using System.Text;
using System.Text.Json;
using C30SuperLogin.Core;
using C30SuperLogin.Desktop;

internal static class RuntimeChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "C30SL-runtime-tests-" + Guid.NewGuid().ToString("N"));
        var store = new Store(root);
        var settings = new Settings { InstallRoot = Path.Combine(root, "NO_C30"), AutoDetectInstall = false, LaunchC30 = false };
        check(settings.RuntimeMode == RuntimeMode.Resident && settings.UseAdministrator && settings.OnboardingVersion == 0,
            "new users enter OOBE; resident/admin preferences default to recommended values");
        settings.RuntimeMode = RuntimeMode.OnDemand; settings.OnboardingVersion = 1; store.Save(settings);
        check(store.Load().RuntimeMode == RuntimeMode.OnDemand && store.Load().OnboardingVersion == 1,
            "on-demand choice and OOBE completion survive reload");
        check(!RuntimeAccess.RequiresAdministrator(settings), "isolated nonexistent installation never requests UAC in tests");
        settings.UseAdministrator = false; store.Save(settings);
        var legacy = JsonSerializer.Deserialize<Settings>("{\"UseAdministrator\":false}", Json.Options)!;
        check(settings.UseAdministrator && store.Load().UseAdministrator && legacy.UseAdministrator,
            "administrator access is required; legacy false settings cannot disable it");
        check(RuntimeAccess.PipeName(root) == RuntimeAccess.PipeName(root) && RuntimeAccess.PipeName(root) != RuntimeAccess.PipeName(root + "-other"),
            "broker endpoint is scoped to user, Windows session and data directory");
        var profile = new AccountBook(store, settings).Save(null, "LOCAL fixture", "LOCAL_RUNTIME_USER", "LOCAL_SYNTHETIC_PASSWORD");
        var request = RuntimeAccess.Request(["login", "--id", "1", "--trigger", "classisland"], store, settings)!;
        check(request.Number == 1 && request.Automated && RuntimeAccess.Valid(request)
            && RuntimeAccess.Request(["login", "--profile", profile.Id.ToString()], store, settings)!.Number == 1,
            "numeric and legacy GUID CLI calls become the same bounded account request");
        check(RuntimeAccess.Request(["login", "--id", "999"], store, settings) is null,
            "unknown account is rejected before requesting elevation");
        check(new[] { new AdminRequest(Guid.Empty, "login", 1), new(Guid.NewGuid(), "shell", 1), new(Guid.NewGuid(), "login", 0),
            new(Guid.NewGuid(), "probe", 1), new(Guid.NewGuid(), "sync", 0, true) }.All(x => !RuntimeAccess.Valid(x)),
            "administrator broker refuses arbitrary commands, invalid IDs and invalid operation combinations");
        var localActivation = RuntimeAccess.Request(["activation", "local"], store, settings)!;
        var activationArgs = RuntimeAccess.Arguments(localActivation, root);
        check(RuntimeAccess.Valid(localActivation) && localActivation.Operation == "activation_local" && !localActivation.Automated
            && activationArgs[0] == "activation" && activationArgs[1] == "local" && activationArgs.Contains("--elevated-operation"),
            "manual local activation is a bounded elevated operation without arbitrary command data");
        var args = RuntimeAccess.Arguments(request, root);
        check(args[0] == "login" && args[1] == "--id" && args[2] == "1" && args.Contains("--elevated-operation")
            && args.Contains(request.RequestId.ToString("N")) && !string.Join(' ', args).Contains("LOCAL_SYNTHETIC_PASSWORD"),
            "elevation forwards a short ID and result nonce, never account credentials or scripts");
        bool oversized = false;
        try { await AdminWire.Read<AdminRequest>(new MemoryStream(BitConverter.GetBytes(4097)), CancellationToken.None); }
        catch (InvalidDataException) { oversized = true; }
        check(oversized, "broker rejects oversized frames before allocating a payload");
        bool extraField = false;
        try { JsonSerializer.Deserialize<AdminRequest>("{\"RequestId\":\"" + request.RequestId + "\",\"Operation\":\"login\",\"Number\":1,\"Command\":\"arbitrary.exe\"}", AdminWire.Options); }
        catch (JsonException) { extraField = true; }
        check(extraField, "broker rejects unknown JSON fields instead of accepting command or credential payloads");
        string name = "C30SL-owned-pipe-test-" + Guid.NewGuid().ToString("N"); int calls = 0;
        // Owned normal-integrity transport fixture; production uses OS token checks.
        // These injected delegates are test assembly calls, not command-line switches.
        using (var server = new AdminPipeServer(name, _ => { Interlocked.Increment(ref calls); return Task.FromResult(new AdminReply(0, new("fixture_only", "Owned fixture", true))); }, _ => true)) {
            var first = await AdminPipeClient.SendAsync(name, request, 2000, _ => true);
            var repeated = await AdminPipeClient.SendAsync(name, request, 2000, _ => true);
            check(first?.ExitCode == 0 && repeated?.Result.Code == "duplicate_admin_request" && calls == 1,
                "actual owned named pipe round trip deduplicates the same request identity");
            var invalid = await AdminPipeClient.SendAsync(name, new(Guid.NewGuid(), "shell", 1), 2000, _ => true);
            check(invalid?.Result.Code == "invalid_admin_request" && calls == 1, "actual pipe rejects an unknown operation without executing it");
            var rejected = await AdminPipeClient.SendAsync(name, request with { RequestId = Guid.NewGuid() }, 2000, _ => false);
            check(rejected?.Result.Code == "admin_request_unconfirmed" && calls == 1, "client peer rejection does not dispatch or retry a request");
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        name += "-busy";
        using (var server = new AdminPipeServer(name, async _ => { entered.TrySetResult(); await release.Task; return new(0, new("fixture_only", "Owned fixture")); }, _ => true)) {
            var first = AdminPipeClient.SendAsync(name, request, 2000, _ => true);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var second = await AdminPipeClient.SendAsync(name, request with { RequestId = Guid.NewGuid() }, 2000, _ => true);
            release.TrySetResult(); await first;
            check(second?.Result.Code == "busy", "a concurrent broker request is rejected rather than queued for a second login");
        }
        name += "-broken"; calls = 0;
        using (var server = new AdminPipeServer(name, _ => { calls++; throw new IOException("owned fixture disconnect"); }, _ => true)) {
            var reply = await AdminPipeClient.SendAsync(name, request, 2000, _ => true);
            check(reply?.Result.Code == "admin_request_unconfirmed" && calls == 1,
                "disconnect after dispatch is indeterminate and does not trigger another submission");
        }
        check(!File.Exists(RuntimeAccess.ReplyPath(root, Guid.NewGuid())), "per-request result nonces cannot accidentally read a previous operation result");
        Console.WriteLine("RUNTIME_FIXTURE: real elevation/UAC and medium-to-high integrity handshake not attempted; no startup tasks registered");
    }
}
