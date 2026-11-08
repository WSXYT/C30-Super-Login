using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using C30SuperLogin.Core;
using Microsoft.Win32.SafeHandles;

namespace C30SuperLogin.Desktop;

public sealed record AdminRequest(Guid RequestId, string Operation, int Number = 0, bool Automated = false);
public sealed record AdminReply(int ExitCode, OperationResult Result, ActivationResult? Activation = null);

public static class RuntimeAccess
{
    public static bool IsAdministrator => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static bool BrokerBusy => server?.Busy == true;
    private static AdminPipeServer? server;
    private static FileStream? hostLease;
    public static bool BrokerRunning => server is not null;
    public static bool RequiresAdministrator(Settings settings) => settings.UseAdministrator && !IsAdministrator &&
        Installation.Discover(settings).LatestInstalledVersion != "unknown";
    public static string PipeName(string root) => "C30SL-admin-v1-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        WindowsIdentity.GetCurrent().User!.Value + ":" + Process.GetCurrentProcess().SessionId + ":" + Path.GetFullPath(root).ToUpperInvariant())))[..32];
    public static bool Valid(AdminRequest request) => request.RequestId != Guid.Empty &&
        ((request.Operation == "login" && request.Number > 0) ||
         (request.Operation is "probe" or "sync" && request.Number == 0 && !request.Automated) ||
         (request.Operation is "activation_status" or "activation_open" or "activation_local" && request.Number == 0 && !request.Automated));
    public static string[] Arguments(AdminRequest request, string root) =>
        (request.Operation switch {
            "login" => new[] { "login", "--id", request.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            "sync" => new[] { "accounts", "sync" },
            "activation_status" => new[] { "activation", "status" },
            "activation_open" => new[] { "activation", "open" },
            "activation_local" => new[] { "activation", "local" },
            _ => new[] { "probe" }
        }).Concat(request.Automated ? new[] { "--trigger", "classisland" } : [])
          .Concat(new[] { "--headless", "--data-dir", root, "--elevated-operation", "--admin-result", request.RequestId.ToString("N") }).ToArray();
    public static AdminRequest? Request(string[] args, Store store, Settings settings)
    {
        if (args.Contains("probe")) return new(Guid.NewGuid(), "probe");
        if (args.Contains("accounts") && args.Contains("sync")) return new(Guid.NewGuid(), "sync");
        if (args.Contains("activation"))
        {
            if (args.Contains("status")) return new(Guid.NewGuid(), "activation_status");
            if (args.Contains("open")) return new(Guid.NewGuid(), "activation_open");
            if (args.Contains("local")) return new(Guid.NewGuid(), "activation_local");
            return null;
        }
        if (!args.Contains("login")) return null;
        int i = Array.IndexOf(args, "--id"); if (i < 0) i = Array.IndexOf(args, "--profile");
        Guid? id = new AccountBook(store, settings).Resolve(i >= 0 && i + 1 < args.Length ? args[i + 1] : null);
        var profile = settings.Profiles.SingleOrDefault(x => x.Id == id);
        int trigger = Array.IndexOf(args, "--trigger");
        return profile is null ? null : new(Guid.NewGuid(), "login", profile.Number,
            trigger >= 0 && trigger + 1 < args.Length && args[trigger + 1] == "classisland");
    }
    public static void StartBroker(string root, Func<AdminRequest, Task<AdminReply>> execute)
    {
        if (!IsAdministrator || server is not null) return;
        try { hostLease = new FileStream(Path.Combine(root, "admin-host.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { return; }
        try { server = new(PipeName(root), execute); } catch { hostLease.Dispose(); hostLease = null; throw; }
    }
    public static void StopBroker() { server?.Dispose(); server = null; hostLease?.Dispose(); hostLease = null; }
    private static AdminReply Failure(string code, string message) => new(2, new(code, message));
    public static int ActivationExitCode(ActivationResult result) => result.Code is "activation_status" or "activation_requested" or "activation_local" ? 0 : 2;
    public static AdminReply ToAdminReply(ActivationResult result) => new(ActivationExitCode(result),
        new(result.Code, result.Message, result.Code == "activation_requested", 0, result.C30Version, result.Win32Error), result);
    public static async Task<AdminReply?> RouteAsync(string[] args, Store store, Settings settings)
    {
        if (!RequiresAdministrator(settings)) return null;
        var request = Request(args, store, settings); if (request is null) return null;
        try {
            if (settings.RuntimeMode == RuntimeMode.Resident) {
                var existing = await AdminPipeClient.SendAsync(PipeName(store.Root), request, 350);
                if (existing is not null) return existing; // Never re-submit after a connected request fails.
                if (!StartElevated(["--admin-host", "--background", "--data-dir", store.Root]))
                    return Failure("elevation_cancelled", "已取消管理员授权，本次没有登录。");
                for (int n = 0; n < 60; n++) {
                    var reply = await AdminPipeClient.SendAsync(PipeName(store.Root), request, 250);
                    if (reply is not null) return reply;
                    await Task.Delay(250);
                }
                return Failure("administrator_required", "管理员后台还没就绪，请打开助手完成首次设置。");
            }
            using var process = LaunchElevated(Arguments(request, store.Root));
            if (process is null) return Failure("elevation_cancelled", "已取消管理员授权，本次没有登录。");
            // The on-demand child owns the whole operation and exits afterwards.
            await process.WaitForExitAsync();
            if (process.ExitCode == 1223) return Failure("elevation_cancelled", "已取消管理员授权，本次没有登录。");
             if (request.Operation is "activation_status" or "activation_open" or "activation_local")
             {
                 string activationPath = ActivationReplyPath(store.Root, request.RequestId);
                 if (File.Exists(activationPath))
                 {
                     var activation = JsonSerializer.Deserialize<ActivationResult>(File.ReadAllText(activationPath), Json.Options);
                     if (activation is not null) return ToAdminReply(activation);
                 }
                 return Failure("operation_failed", "激活请求没有返回结果。");
             }
             var resultPath = ReplyPath(store.Root, request.RequestId);
            return new(process.ExitCode, File.Exists(resultPath)
                ? JsonSerializer.Deserialize<OperationResult>(File.ReadAllText(resultPath), Json.Options) ?? new("operation_failed", "本次操作没有返回结果。")
                : new("operation_failed", "本次操作没有返回结果。"));
        } catch (System.ComponentModel.Win32Exception e) when (e.NativeErrorCode == 1223) {
            return Failure("elevation_cancelled", "已取消管理员授权，本次没有登录。");
        } catch { return Failure("admin_request_unconfirmed", "管理员请求未确认，请查看 C30；为避免重复，本次不再重试。"); }
    }
    public static bool StartElevated(string[] args) { using var process = LaunchElevated(args); return process is not null; }
    public static string ReplyPath(string root, Guid request) => Path.Combine(root, "admin-replies", request.ToString("N") + ".json");
    public static string ActivationReplyPath(string root, Guid request) => Path.Combine(root, "admin-replies", request.ToString("N") + ".activation.json");
    public static void WriteReply(string[] args, Store store, OperationResult result)
    {
        int i = Array.IndexOf(args, "--admin-result");
        if (!args.Contains("--elevated-operation") || !IsAdministrator || i < 0 || i + 1 >= args.Length || !Guid.TryParseExact(args[i + 1], "N", out var id)) return;
        string path = ReplyPath(store.Root, id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (string old in Directory.GetFiles(Path.GetDirectoryName(path)!, "*.json").OrderBy(File.GetCreationTimeUtc).Take(Math.Max(0, Directory.GetFiles(Path.GetDirectoryName(path)!, "*.json").Length - 63))) File.Delete(old);
        File.WriteAllText(path, Json.Serialize(result));
    }
    public static void WriteActivationReply(string[] args, Store store, ActivationResult result)
    {
        int i = Array.IndexOf(args, "--admin-result");
        if (!args.Contains("--elevated-operation") || !IsAdministrator || i < 0 || i + 1 >= args.Length || !Guid.TryParseExact(args[i + 1], "N", out var id)) return;
        string path = ActivationReplyPath(store.Root, id); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Json.Serialize(result));
    }
    private static Process? LaunchElevated(string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", WorkingDirectory = AppContext.BaseDirectory };
        foreach (string arg in args) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("--expected-user-sid"); start.ArgumentList.Add(WindowsIdentity.GetCurrent().User!.Value);
        return Process.Start(start);
    }
}

internal static class AdminWire
{
    public static readonly JsonSerializerOptions Options = new() { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public static async Task<T> Read<T>(Stream stream, CancellationToken token)
    {
        byte[] length = new byte[4]; await stream.ReadExactlyAsync(length, token);
        int count = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(length);
        if (count is < 1 or > 4096) throw new InvalidDataException("Invalid broker frame");
        byte[] data = new byte[count]; await stream.ReadExactlyAsync(data, token);
        return JsonSerializer.Deserialize<T>(data, Options) ?? throw new InvalidDataException("Empty broker frame");
    }
    public static async Task Write<T>(Stream stream, T value, CancellationToken token)
    {
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, Options);
        if (data.Length > 4096) throw new InvalidDataException("Oversized broker frame");
        byte[] length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, data.Length);
        await stream.WriteAsync(length, token); await stream.WriteAsync(data, token); await stream.FlushAsync(token);
    }
}

internal static class AdminPipeClient
{
    public static async Task<AdminReply?> SendAsync(string name, AdminRequest request, int connectMs, Func<SafePipeHandle, bool>? testPeer = null)
    {
        using var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try { await pipe.ConnectAsync(connectMs); } catch (TimeoutException) { return null; }
        // Once connected, an error is indeterminate; the caller must not try a second method.
        try {
            if (!(testPeer?.Invoke(pipe.SafePipeHandle) ?? PipePeer.Server(pipe.SafePipeHandle))) throw new UnauthorizedAccessException();
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            await AdminWire.Write(pipe, request, timeout.Token);
            return await AdminWire.Read<AdminReply>(pipe, timeout.Token);
        } catch { return new(2, new("admin_request_unconfirmed", "后台请求未确认，本次不再重复提交。")); }
    }
}

internal sealed class AdminPipeServer : IDisposable
{
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim gate = new(1);
    private readonly HashSet<Guid> handled = [];
    private readonly Task loop;
    private int busy;
    public bool Busy => Volatile.Read(ref busy) != 0;
    public AdminPipeServer(string name, Func<AdminRequest, Task<AdminReply>> execute, Func<SafePipeHandle, bool>? testPeer = null)
    {
        loop = Task.Run(async () => {
            while (!stop.IsCancellationRequested) {
                var pipe = new NamedPipeServerStream(name, PipeDirection.InOut, 4, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(stop.Token); }
                catch { pipe.Dispose(); if (stop.IsCancellationRequested) return; continue; }
                _ = Handle(pipe, execute, testPeer);
            }
        });
    }
    private async Task Handle(NamedPipeServerStream pipe, Func<AdminRequest, Task<AdminReply>> execute, Func<SafePipeHandle, bool>? testPeer)
    {
        using (pipe) {
            bool acquired = false;
            try {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token); readTimeout.CancelAfter(2000);
                if (!(testPeer?.Invoke(pipe.SafePipeHandle) ?? PipePeer.Client(pipe.SafePipeHandle))) return;
                var request = await AdminWire.Read<AdminRequest>(pipe, readTimeout.Token);
                AdminReply reply;
                if (!RuntimeAccess.Valid(request)) reply = new(2, new("invalid_admin_request", "后台只接受已有账号的登录、连接检查或授权检查。"));
                else if (!(acquired = gate.Wait(0))) reply = new(2, new("busy", "已有登录任务正在执行。"));
                else if (handled.Contains(request.RequestId)) reply = new(2, new("duplicate_admin_request", "这个请求已经处理，不再重复提交。"));
                else if (handled.Count >= 4096) reply = new(2, new("admin_host_restart_required", "请重启管理员后台。"));
                else {
                    handled.Add(request.RequestId);
                    Interlocked.Exchange(ref busy, 1);
                    // Bound memory without evicting request identities during this host session.
                    reply = await execute(request);
                }
                using var writeTimeout = new CancellationTokenSource(2000);
                await AdminWire.Write(pipe, reply, writeTimeout.Token);
            } catch { /* Broken or untrusted frames never become login instructions. */ }
            finally { if (acquired) { Interlocked.Exchange(ref busy, 0); gate.Release(); } }
        }
    }
    public void Dispose() => stop.Cancel(); // An accepted operation is not cancelled or re-dispatched.
}

internal static class PipePeer
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe, out uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int kind, out int value, int size, out int returned);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    public static bool Server(SafePipeHandle pipe) => GetNamedPipeServerProcessId(pipe, out uint pid) && Matches(pid, true);
    public static bool Client(SafePipeHandle pipe) => GetNamedPipeClientProcessId(pipe, out uint pid) && Matches(pid, false);
    private static bool Matches(uint pid, bool elevated)
    {
        IntPtr process = OpenProcess(0x1000, false, pid), token = IntPtr.Zero;
        if (process == IntPtr.Zero) return false;
        try {
            if (!OpenProcessToken(process, 8, out token)) return false;
            using var identity = new WindowsIdentity(token);
            using var peer = Process.GetProcessById((int)pid);
            if (identity.User != WindowsIdentity.GetCurrent().User || peer.SessionId != Process.GetCurrentProcess().SessionId) return false;
            return !elevated || GetTokenInformation(token, 20, out int value, sizeof(int), out _) && value != 0;
        } finally { if (token != IntPtr.Zero) CloseHandle(token); CloseHandle(process); }
    }
}
