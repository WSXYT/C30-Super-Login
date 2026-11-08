using System.Text.Json;
using C30SuperLogin.Core;
namespace C30SuperLogin.Desktop;
public static class Commands {
 public static async Task<int> Run(string[] args) {
        string? Option(string flag) { int i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        var store = new Store(Option("--data-dir"));
        Settings settings;
        try { settings = store.Load(); if (!File.Exists(Path.Combine(store.Root, "settings.json"))) store.Save(settings); }
        catch { return 2; }
        string helper = Path.Combine(AppContext.BaseDirectory, "C30Native.exe");
        try { new AccountBook(store, settings).Deduplicate(); }
        catch { return 2; }
        if (args.Contains("detect-install"))
        {
            string? candidate = Option("--candidate");
            IEnumerable<string>? candidates = candidate is null ? null : [candidate];
            IEnumerable<string>? runningImages = candidate is null ? null : [];
            var installation = Installation.Discover(settings, candidates, runningImages, forceAutoDetect: true);
            if (installation.LatestDirectory is null)
            {
                string notFound = Json.Serialize(new { code = "installation_not_found", install_root = settings.InstallRoot });
                if (Option("--output") is { } failureOutput) File.WriteAllText(failureOutput, notFound);
                Console.WriteLine(notFound);
                return 2;
            }
            settings.InstallRoot = installation.Root;
            settings.AutoDetectInstall = true;
            try { store.Save(settings); }
            catch { Console.WriteLine(Json.Serialize(new { code = "settings_save_failed", install_root = settings.InstallRoot })); return 2; }
            string detected = Json.Serialize(new { code = "installation_detected", install_root = installation.Root,
                latest_installed_version = installation.LatestInstalledVersion, latest_installed_directory = installation.LatestDirectory });
            if (Option("--output") is { } output) File.WriteAllText(output, detected);
            Console.WriteLine(detected); return 0;
        }
        if (args.Contains("accounts") && args.Contains("list"))
        {
            string records = Json.Serialize(new AccountBook(store, settings).Read());
            if (Option("--output") is { } output) File.WriteAllText(output, records);
            Console.WriteLine(records); return 0;
        }
        if (args.Contains("accounts") && args.Contains("add"))
        {
            try
            {
                // Credentials are accepted on stdin only, never as command-line arguments.
                string input = await Console.In.ReadToEndAsync();
                if (input.Length > 16384) throw new InvalidDataException();
                using var document = JsonDocument.Parse(input);
                string? idText = Option("--id");
                Guid id = idText is null ? Guid.NewGuid() : new AccountBook(store, settings).Resolve(idText) ?? Guid.Parse(idText);
                var profile = new AccountBook(store, settings).Save(id, document.RootElement.GetProperty("label").GetString() ?? "",
                    document.RootElement.GetProperty("username").GetString() ?? "", document.RootElement.GetProperty("password").GetString() ?? "");
                string result = Json.Serialize(new { code = "account_saved", id = profile.Number, profile_id = profile.Id });
                File.WriteAllText(Path.Combine(store.Root, "last-account-result.json"), result); Console.WriteLine(result); return 0;
            }
            catch
            {
                File.WriteAllText(Path.Combine(store.Root, "last-account-result.json"), Json.Serialize(new { code = "account_save_failed" }));
                return 2;
            }
        }
        if (args.Contains("skip-next")) { store.SetSkipNext(); return 0; }
        if (args.Contains("status") && !args.Contains("activation"))
        {
            var installation = Installation.Discover(settings);
            var running = Target.Find(installation.Root);
            var status = Json.Serialize(new { app_version = BuildVersion.Current, c30_version = running.FirstOrDefault()?.Version ?? installation.LatestInstalledVersion,
                install_root = installation.Root, auto_detect_install = settings.AutoDetectInstall,
                latest_installed_version = installation.LatestInstalledVersion, latest_installed_directory = installation.LatestDirectory,
                running_versions = running.Select(x => x.Version).Distinct().ToArray(),
                launcher_last_started_version = installation.LastStartedVersion, launcher_advertised_version = installation.LauncherNewVersion,
                process_count = running.Select(x => x.Pid).Distinct().Count(),
                posthog_enabled = settings.Telemetry.PostHogEnabled, sentry_enabled = settings.Telemetry.SentryEnabled,
                posthog_configured = !string.IsNullOrEmpty(settings.Telemetry.PostHogKey) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("C30_POSTHOG_KEY")),
                sentry_configured = !string.IsNullOrEmpty(settings.Telemetry.SentryDsn) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("C30_SENTRY_DSN")) });
            if (Option("--output") is { } path) File.WriteAllText(path, status);
            Console.WriteLine(status); return 0;
        }
        if (args.Contains("telemetry-test"))
        {
            using var telemetry = new Telemetry(settings.Telemetry);
            var delivery = await telemetry.PublishAsync(new(DiagnosticKind.TestEvent, DiagnosticStage.Application, Target.CurrentVersion(settings)));
            File.WriteAllText(Path.Combine(store.Root, "last-delivery.json"), Json.Serialize(delivery));
            Console.WriteLine(Json.Serialize(delivery));
            return delivery.Any(x => x.State == "sent") ? 0 : 2;
        }
        if (args.Contains("activation"))
        {
            if (!args.Contains("status") && !args.Contains("open") && !args.Contains("local")) return 64;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var activation = new ActivationClient(settings, helper);
            ActivationResult result = args.Contains("local")
                ? await activation.ApplyLocalAsync(timeout.Token)
                : args.Contains("status") ? await activation.QueryAsync(timeout.Token) : await activation.OpenOfficialDialogAsync(timeout.Token);
            File.WriteAllText(Path.Combine(store.Root, "last-activation.json"), Json.Serialize(result));
            RuntimeAccess.WriteActivationReply(args, store, result);
            Console.WriteLine(Json.Serialize(result));
            return RuntimeAccess.ActivationExitCode(result);
        }
        if (args.Contains("login") || args.Contains("probe") || (args.Contains("accounts") && args.Contains("sync")))
        {
            using var telemetry = new Telemetry(settings.Telemetry);
            var coordinator = new Coordinator(store, settings, helper);
            List<Diagnostic> diagnostics = [];
            coordinator.DiagnosticReady += diagnostics.Add;
            Guid? profile = new AccountBook(store, settings).Resolve(Option("--id") ?? Option("--profile"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var result = await coordinator.RunAsync(profile, args.Contains("probe"), Option("--trigger") == "classisland", timeout.Token,
                readAccounts: args.Contains("accounts") && args.Contains("sync"));
            File.WriteAllText(Path.Combine(store.Root, "last-operation.json"), Json.Serialize(result));
            RuntimeAccess.WriteReply(args, store, result);
            Console.WriteLine(Json.Serialize(result));
            // Submission has completed; headless mode waits for its bounded upload attempt before exiting.
            using var sendTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach (var diagnostic in diagnostics)
            {
                IReadOnlyList<Delivery> delivery;
                try { delivery = await telemetry.PublishAsync(diagnostic, sendTimeout.Token); }
                catch (OperationCanceledException) { delivery = [new("PostHog", "deferred"), new("Sentry", "deferred")]; }
                File.WriteAllText(Path.Combine(store.Root, "last-delivery.json"), Json.Serialize(delivery));
                if (!delivery.Any(x => x.State == "sent"))
                {
                    string pending = Path.Combine(store.Root, "pending-diagnostics"); Directory.CreateDirectory(pending);
                    foreach (var stale in Directory.GetFiles(pending, "*.json").OrderBy(File.GetCreationTimeUtc).Take(Math.Max(0, Directory.GetFiles(pending, "*.json").Length - 63))) File.Delete(stale);
                    File.WriteAllText(Path.Combine(pending, Guid.NewGuid().ToString("N") + ".json"), Json.Serialize(diagnostic));
                }
            }
            return result.Submitted || result.Code is "ready" or "skipped" or "accounts_synced" ? 0 : 2;
        }

return 64;
 }
}
