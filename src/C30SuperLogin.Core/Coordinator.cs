using System.Diagnostics;

namespace C30SuperLogin.Core;

public static class AdapterIdentity
{
    public const string DuiLibSha256 = "15C4E6674A3BF0BB746449459E3E0A03741F933840438DEE3C68F2C2B6433980";
}
public sealed class Coordinator(Store store, Settings settings, string helperPath, ILoginBackend? testBackend = null)
{
    private readonly ILoginBackend backend = testBackend ?? new WindowsLoginBackend(helperPath);
    public event Action<string>? Progress;
    public event Action<Diagnostic>? DiagnosticReady;
    public async Task<OperationResult> RunAsync(Guid? profileId, bool probe, bool automated, CancellationToken token, bool readAccounts = false)
    {
        var watch = Stopwatch.StartNew();
        var installation = Installation.Discover(settings);
        string installRoot = installation.Root;
        string version = installation.LatestInstalledVersion;
        int strategyStep = 0;
        void Strategy(LoginMethod method, LoginMethod previous, StrategyState state, DiagnosticOutcome outcome = DiagnosticOutcome.None, int error = 0)
        {
            var diagnostic = new Diagnostic(DiagnosticKind.StrategyTransition, method == LoginMethod.Injection ? DiagnosticStage.Hook : DiagnosticStage.ControlLogin,
                version, error, watch.Elapsed.TotalMilliseconds, outcome, method, previous, state, ++strategyStep);
            store.LogStrategy(diagnostic); DiagnosticReady?.Invoke(diagnostic);
        }
        OperationResult Finish(string code, string message, bool submitted = false, int error = 0, DiagnosticStage stage = DiagnosticStage.Page)
        {
            var result = new OperationResult(code, message, submitted, watch.Elapsed.TotalMilliseconds, version, error, stage);
            store.Log(code, stage.ToString(), version);
            DiagnosticReady?.Invoke(new(submitted ? DiagnosticKind.LoginSubmitted : code == "accounts_synced" ? DiagnosticKind.AccountsSynced : code == "ready" ? DiagnosticKind.ProbeCompleted :
                code == "skipped" ? DiagnosticKind.AutomationSkipped : DiagnosticKind.InjectionFailed, stage, version, error, result.ElapsedMs, Outcome(code)));
            return result;
        }
        if (automated && (settings.AutomationPaused || store.ConsumeSkip())) return Finish("skipped", "本次自动化已跳过", stage: DiagnosticStage.Automation);
        if (!probe && !readAccounts && (profileId is null || !settings.Profiles.Any(x => x.Id == profileId))) return Finish("profile_missing", "此档案 ID 不存在，请先读取或新增账号档案", stage: DiagnosticStage.Submission);
        FileStream operation;
        try { operation = new FileStream(Path.Combine(store.Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return Finish("busy", "已有登录任务正在执行", stage: DiagnosticStage.Discovery); }
        using (operation)
        {
            try
            {
                Progress?.Invoke("正在定位 C30 进程");
                int discoveryError = 0;
                var targets = backend.Find(installRoot, error => discoveryError = error);
                if (targets.Count == 0 && discoveryError != 0) return Finish("access_denied", "读取 C30 进程信息失败；请确认两者运行权限一致", error: discoveryError, stage: DiagnosticStage.Discovery);
                if (targets.Count == 0 && settings.LaunchC30)
                {
                    string launcher = Path.Combine(installRoot, "TeachLauncher.exe");
                    if (!File.Exists(launcher)) return Finish("not_installed", "请检查 C30 安装目录", stage: DiagnosticStage.Discovery);
                    // Do not block a headless/ClassIsland task on a UAC consent dialog.
                    // ERROR_ELEVATION_REQUIRED is reported with its real Windows error code.
                    using var launched = Process.Start(new ProcessStartInfo(launcher) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = installRoot, WindowStyle = ProcessWindowStyle.Minimized });
                    for (int i = 0; i < 32 && targets.Count == 0; i++)
                    {
                        await Task.Delay(250, token); targets = backend.Find(installRoot, error => discoveryError = error);
                    }
                }
                if (targets.Count == 0) return Finish("not_running", "没有找到 C30 教学窗口，请先打开 C30", stage: DiagnosticStage.Discovery);
                version = targets[0].Version; // Report the actual running build, including unsupported builds.
                if (targets.Select(x => x.Version).Distinct().Count() > 1) return Finish("multiple_versions", "同时运行了多个 C30 版本，请先关闭旧版本", stage: DiagnosticStage.VersionCheck);
                installation = Installation.Inspect(installRoot); // Re-read after a possible launcher update.
                if (Installation.NeedsRestart(version, installation.LatestInstalledVersion)) return Finish("restart_required", "检测到已安装的新版本，请保存课堂内容并重启 C30", stage: DiagnosticStage.VersionCheck);
                if (!File.Exists(helperPath)) return Finish("helper_missing", "缺少 x86 注入辅助程序，请重新构建或完整解压", stage: DiagnosticStage.Hook);
                var supported = targets.Where(backend.IsSupported).ToList();
                if (supported.Count == 0) return Finish("unsupported_version", "当前 C30 模块与已验证适配不一致，已停止注入", stage: DiagnosticStage.VersionCheck);
                version = supported[0].Version;
                Credentials? credentials = null;
                if (!probe && !readAccounts)
                {
                    if (profileId is null || !settings.Profiles.Any(x => x.Id == profileId)) return Finish("profile_missing", "请先保存并选择账号档案", stage: DiagnosticStage.Submission);
                    credentials = store.LoadSecret(profileId.Value);
                }
                Progress?.Invoke(readAccounts ? "正在读取 C30 已保存账号（不导出密码）" : probe ? "正在注入并验证登录页面" : "正在注入并提交登录");
                NativeResult? last = null;
                bool anyQueued = false;
                Strategy(LoginMethod.Injection, LoginMethod.None, StrategyState.Started);
                foreach (var target in supported.Take(8))
                {
                    token.ThrowIfCancellationRequested();
                    await using var receipt = new ReceiptListener();
                    string script = Script.Create(probe, credentials, receipt.Address, readAccounts: readAccounts);
                    last = await backend.InjectAsync(target, script, token);
                    if (last.Queued == 0) continue;
                    anyQueued = true;
                    string outcome;
                    try { outcome = await receipt.WaitAsync(TimeSpan.FromSeconds(2), token); }
                    catch (TimeoutException) {
                        if (!probe && !readAccounts) {
                            Strategy(LoginMethod.Injection, LoginMethod.None, StrategyState.SubmissionUncertain, DiagnosticOutcome.ReceiptTimeout);
                            return Finish("receipt_timeout", "没有收到确认，请先查看 C30，避免重复登录", stage: DiagnosticStage.Page);
                        }
                        continue;
                    }
                    if (outcome == "accounts" && readAccounts)
                    {
                        var accounts = await receipt.ReadAccountsAsync(TimeSpan.FromSeconds(1), token);
                        var merged = new AccountBook(store, settings).MergeSaved(accounts);
                        return Finish("accounts_synced", $"已读取 {accounts.Count} 条 C30 记录：新增 {merged.Added}，匹配合并 {merged.Matched}；未导出密码", stage: DiagnosticStage.Accounts);
                    }
                    if (outcome == "ready") return Finish("ready", "已连接真实 C30 登录页；注入与页面回执正常");
                    if (outcome == "submitted") {
                        Strategy(LoginMethod.Injection, LoginMethod.None, StrategyState.Submitted, DiagnosticOutcome.Submitted);
                        return Finish("submitted", "已提交至 C30；认证结果以 C30 显示为准", true, stage: DiagnosticStage.Submission);
                    }
                    if (outcome == "busy") return Finish("busy", "C30 正在登录，请等待其返回结果");
                    if (outcome is "bridge_error" or "invalid_credentials") return Finish(outcome, "登录桥返回错误，请检查档案或诊断记录", stage: DiagnosticStage.Submission);
                    if (outcome == "password_required") return Finish(outcome, "C30 未记住此账号的密码，请在档案中补充密码后登录", stage: DiagnosticStage.Submission);
                    if (outcome is "saved_missing" or "saved_ambiguous") return Finish(outcome, "C30 原记录已变动或存在歧义，请重新读取账号记录", stage: DiagnosticStage.Accounts);
                    if (outcome == "not_ready") return Finish(outcome, "C30 登录页或账号列表尚未就绪，请稍后重新读取", stage: DiagnosticStage.Accounts);
                }
                if (ControlLogin.MayTry(probe, readAccounts, anyQueued, credentials)) {
                    Strategy(LoginMethod.Injection, LoginMethod.None, StrategyState.NotSubmitted, Outcome(last?.Code ?? "no_control"), last?.Win32Error ?? 0);
                    Strategy(LoginMethod.PageControls, LoginMethod.Injection, StrategyState.Switching, Outcome(last?.Code ?? "no_control"), last?.Win32Error ?? 0);
                    Progress?.Invoke("正在尝试备用登录");
                    foreach (var target in supported.Take(4)) {
                        var control = await backend.ControlAsync(target, credentials!, token);
                        if (control.Submitted) {
                            Strategy(LoginMethod.PageControls, LoginMethod.Injection, StrategyState.Submitted, DiagnosticOutcome.Submitted);
                            return Finish("submitted", "已点击登录，结果请看 C30。", true, stage: DiagnosticStage.ControlLogin);
                        }
                        if (control.Indeterminate) {
                            Strategy(LoginMethod.PageControls, LoginMethod.Injection, StrategyState.SubmissionUncertain, DiagnosticOutcome.ReceiptTimeout);
                            return Finish("receipt_timeout", "控件登录结果未确认，请先查看 C30，避免重复登录", stage: DiagnosticStage.ControlLogin);
                        }
                    }
                    Strategy(LoginMethod.PageControls, LoginMethod.Injection, StrategyState.NotSubmitted, DiagnosticOutcome.NoControl);
                    Strategy(LoginMethod.Manual, LoginMethod.PageControls, StrategyState.ManualRequired, DiagnosticOutcome.NoControl);
                }
                return Finish(anyQueued ? "receipt_timeout" : last?.Code ?? "no_control",
                    last?.Win32Error == 5 ? "Windows 拒绝注入消息（错误码 5）；请确认运行权限一致，或在 C30 手动登录" : "没有完成自动登录，请在 C30 手动登录；本次不会继续重试", error: last?.Win32Error ?? 0, stage: anyQueued ? DiagnosticStage.Page : DiagnosticStage.Hook);
            }
            catch (OperationCanceledException) { return Finish("cancelled", "操作已取消"); }
            catch (System.Security.Cryptography.CryptographicException) { return Finish("credential_error", "当前 Windows 用户未能解密档案，请重新保存账号", stage: DiagnosticStage.Submission); }
            catch (System.ComponentModel.Win32Exception e) { return Finish("win32_error", "Windows 操作失败；错误码 " + e.NativeErrorCode, error: e.NativeErrorCode, stage: DiagnosticStage.Hook); }
            catch { return Finish("operation_error", "操作失败，详情已写入脱敏诊断记录", stage: DiagnosticStage.Application); }
        }
    }
    // Only known enum values enter telemetry, never arbitrary native output or exception text.
    private static DiagnosticOutcome Outcome(string code)
    {
        string normalized = code.Replace("_", "");
        return Enum.TryParse<DiagnosticOutcome>(normalized, true, out var value) && Enum.IsDefined(value)
            ? value : DiagnosticOutcome.NativeError;
    }
}
