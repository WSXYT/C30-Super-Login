using System.Diagnostics;

namespace C30SuperLogin.Core;

// Queries use C30's own CEF bridge. Local activation is an explicit, manual and
// process-scoped operation: it changes only the running process and never writes the
// installation, registry, license files or cloud device state.
public sealed class ActivationClient
{
    private readonly Settings settings;
    private readonly ILoginBackend backend;

    public ActivationClient(Settings settings, string helperPath, ILoginBackend? testBackend = null)
    {
        this.settings = settings;
        backend = testBackend ?? new WindowsLoginBackend(helperPath);
    }

    public Task<ActivationResult> QueryAsync(CancellationToken token) => ExecuteAsync("activation_status", token);
    public Task<ActivationResult> OpenOfficialDialogAsync(CancellationToken token) => ExecuteAsync("activation_open", token);
    public async Task<ActivationResult> ApplyLocalAsync(CancellationToken token)
    {
        var installation = Installation.Discover(settings);
        int discoveryError = 0;
        IReadOnlyList<TargetProcess> targets;
        try { targets = backend.Find(installation.Root, error => discoveryError = error); }
        catch (System.ComponentModel.Win32Exception e) { return new("access_denied", "读取 C30 进程信息失败，请使用管理员权限。", Win32Error: e.NativeErrorCode); }
        catch { return new("activation_error", "定位 C30 失败，请稍后重试。", Win32Error: discoveryError); }
        if (targets.Count == 0) return new("not_running", "没有找到正在运行的 C30，请先打开 C30。", Win32Error: discoveryError);
        if (targets.Select(x => x.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            return new("multiple_versions", "同时运行了多个 C30 版本，请先关闭旧版本。", C30Version: targets[0].Version);
        var target = targets.FirstOrDefault(Target.IsSupported);
        if (target is null) return new("unsupported_version", "当前 C30 版本或模块与本地激活补丁不匹配。", C30Version: targets[0].Version);

        token.ThrowIfCancellationRequested();
        try { LocalActivation.Apply(target); }
        catch (NotSupportedException) { return new("unsupported_version", "teach_core.dll 与已验证版本不一致，已停止操作。", C30Version: target.Version); }
        catch (System.ComponentModel.Win32Exception e) { return new("access_denied", "写入 C30 运行内存失败，请使用管理员权限。", C30Version: target.Version, Win32Error: e.NativeErrorCode); }
        catch (Exception e) { return new("activation_patch_failed", "本地激活失败：" + e.Message, C30Version: target.Version); }

        ActivationSnapshot? snapshot = null;
        for (int attempt = 0; attempt < 3 && snapshot is null; attempt++)
        {
            snapshot = await C30StatusReader.TryReadAsync(token);
            if (snapshot is null) await Task.Delay(150, token);
        }
        return snapshot is { Authorized: true }
            ? new("activation_local", "C30 本地授权已生效；关闭或重启 C30 后失效。",
                true, snapshot.Time, snapshot.Tips, target.Version)
            : new("activation_local", "本地授权补丁已写入；关闭或重启 C30 后失效。",
                true, snapshot?.Time ?? "", snapshot?.Tips ?? "", target.Version);
    }

    private async Task<ActivationResult> ExecuteAsync(string operation, CancellationToken token)
    {
        if (operation == "activation_status")
        {
            var direct = await C30StatusReader.TryReadAsync(token);
            if (direct is not null)
            {
                string message = direct.Authorized
                    ? "C30 已授权。"
                    : string.IsNullOrWhiteSpace(direct.Tips) ? "C30 当前未授权。" : "C30 当前未授权：" + direct.Tips;
                return new("activation_status", message, direct.Authorized, direct.Time, direct.Tips,
                    Target.CurrentVersion(settings));
            }
        }

        var installation = Installation.Discover(settings);
        int discoveryError = 0;
        IReadOnlyList<TargetProcess> targets;
        try { targets = backend.Find(installation.Root, error => discoveryError = error); }
        catch (System.ComponentModel.Win32Exception e) { return new("access_denied", "读取 C30 进程信息失败，请确认运行权限一致。", Win32Error: e.NativeErrorCode); }
        catch { return new("activation_error", "定位 C30 失败，请稍后重试。", Win32Error: discoveryError); }

        if (targets.Count == 0)
            return discoveryError == 0
                ? new("not_running", "没有找到正在运行的 C30，请先打开 C30。")
                : new("access_denied", "读取 C30 进程信息失败，请确认运行权限一致。", Win32Error: discoveryError);

        string version = targets[0].Version;
        if (targets.Select(x => x.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
            return new("multiple_versions", "同时运行了多个 C30 版本，请先关闭旧版本。", C30Version: version);

        var supported = targets.Where(backend.IsSupported).Take(8).ToList();
        if (supported.Count == 0)
            return new("unsupported_version", "当前 C30 版本或模块与已验证适配不一致。", C30Version: version);

        await using var receipt = new ReceiptListener();
        NativeResult native;
        try { native = await backend.InjectAsync(supported[0], Script.CreateActivation(operation, receipt.Address), token); }
        catch (OperationCanceledException) { throw; }
        catch (System.ComponentModel.Win32Exception e) { return new("win32_error", "连接 C30 失败，请确认运行权限一致。", C30Version: version, Win32Error: e.NativeErrorCode); }
        catch { return new("activation_error", "连接 C30 失败，请稍后重试。", C30Version: version); }
        if (native.Queued == 0)
            return new(native.Code == "" ? "not_submitted" : native.Code, "C30 没有接受本次请求。", C30Version: version, Win32Error: native.Win32Error);

        if (operation == "activation_status")
        {
            try
            {
                var snapshot = await receipt.ReadActivationAsync(TimeSpan.FromSeconds(3), token);
                string message = snapshot.Authorized
                    ? "C30 已授权。"
                    : string.IsNullOrWhiteSpace(snapshot.Tips) ? "C30 当前未授权。" : "C30 当前未授权：" + snapshot.Tips;
                return new("activation_status", message, snapshot.Authorized, snapshot.Time, snapshot.Tips, version);
            }
            catch (TimeoutException)
            {
                string receiptCode = await receipt.WaitAsync(TimeSpan.FromSeconds(1), token);
                return receiptCode == "activation_not_ready"
                    ? new("activation_not_ready", "当前 C30 页面没有提供授权查询接口，请先进入主界面。", C30Version: version)
                    : new("activation_unknown", "C30 没有返回授权状态，请稍后重试。", C30Version: version);
            }
        }

        try
        {
            string result = await receipt.WaitAsync(TimeSpan.FromSeconds(3), token);
            return result switch
            {
                "activation_requested" => new("activation_requested", "已请求 C30 官方激活窗口，请在 C30 中完成激活。", C30Version: version),
                "activation_not_ready" => new("activation_not_ready", "当前 C30 页面还没有准备好官方激活接口，请先进入主界面。", C30Version: version),
                "bridge_error" => new("bridge_error", "C30 官方激活接口调用失败。", C30Version: version),
                _ => new("activation_unknown", "C30 没有确认激活窗口请求，请查看 C30。", C30Version: version)
            };
        }
        catch (TimeoutException) { return new("activation_unknown", "C30 没有确认激活窗口请求，请查看 C30。", C30Version: version); }
    }
}
