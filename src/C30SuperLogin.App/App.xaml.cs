using System.Text.Json;
using System.Windows;
using C30SuperLogin.Core;

namespace C30SuperLogin.App;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Option(string flag) { int i = Array.IndexOf(e.Args, flag); return i >= 0 && i + 1 < e.Args.Length ? e.Args[i + 1] : null; }
        var store = new Store(Option("--data-dir"));
        Settings settings;
        try { settings = store.Load(); if (!File.Exists(Path.Combine(store.Root, "settings.json"))) store.Save(settings); }
        catch { if (!e.Args.Contains("--headless")) MessageBox.Show("配置文件读取失败，请检查本地 settings.json。原文件已保留。", "C30 Super Login"); Shutdown(2); return; }
        string helper = Path.Combine(AppContext.BaseDirectory, "C30Native.exe");
        try { new AccountBook(store, settings).Deduplicate(); }
        catch { if (!e.Args.Contains("--headless")) MessageBox.Show("账号记录整理失败；原配置和未处理记录已保留，请检查数据目录。", "C30 Super Login"); Shutdown(2); return; }
        if (e.Args.Contains("accounts") && e.Args.Contains("list"))
        {
            string records = Json.Serialize(new AccountBook(store, settings).Read());
            if (Option("--output") is { } output) File.WriteAllText(output, records);
            Console.WriteLine(records); Shutdown(); return;
        }
        if (e.Args.Contains("accounts") && e.Args.Contains("add"))
        {
            try
            {
                // Credentials are accepted on stdin only, never as command-line arguments.
                string input = await Console.In.ReadToEndAsync();
                if (input.Length > 16384) throw new InvalidDataException();
                using var document = JsonDocument.Parse(input);
                string? idText = Option("--id");
                Guid id = idText is null ? Guid.NewGuid() : Guid.Parse(idText);
                var profile = new AccountBook(store, settings).Save(id, document.RootElement.GetProperty("label").GetString() ?? "",
                    document.RootElement.GetProperty("username").GetString() ?? "", document.RootElement.GetProperty("password").GetString() ?? "");
                string result = Json.Serialize(new { code = "account_saved", profile_id = profile.Id });
                File.WriteAllText(Path.Combine(store.Root, "last-account-result.json"), result); Console.WriteLine(result); Shutdown(); return;
            }
            catch
            {
                File.WriteAllText(Path.Combine(store.Root, "last-account-result.json"), Json.Serialize(new { code = "account_save_failed" }));
                Shutdown(2); return;
            }
        }
        if (e.Args.Contains("skip-next")) { store.SetSkipNext(); Shutdown(); return; }
        if (e.Args.Contains("status"))
        {
            var status = Json.Serialize(new { app_version = "0.1.0", c30_version = Target.ReadInstallVersion(settings.InstallRoot),
                process_count = Target.Find(settings.InstallRoot).Select(x => x.Pid).Distinct().Count(),
                posthog_enabled = settings.Telemetry.PostHogEnabled, sentry_enabled = settings.Telemetry.SentryEnabled,
                posthog_configured = !string.IsNullOrEmpty(settings.Telemetry.PostHogKey) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("C30_POSTHOG_KEY")),
                sentry_configured = !string.IsNullOrEmpty(settings.Telemetry.SentryDsn) || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("C30_SENTRY_DSN")) });
            if (Option("--output") is { } path) File.WriteAllText(path, status);
            Console.WriteLine(status); Shutdown(); return;
        }
        if (e.Args.Contains("telemetry-test"))
        {
            using var telemetry = new Telemetry(settings.Telemetry);
            var delivery = await telemetry.PublishAsync(new(DiagnosticKind.TestEvent, DiagnosticStage.Application, Target.ReadInstallVersion(settings.InstallRoot)));
            File.WriteAllText(Path.Combine(store.Root, "last-delivery.json"), Json.Serialize(delivery));
            Console.WriteLine(Json.Serialize(delivery));
            Shutdown(delivery.Any(x => x.State == "sent") ? 0 : 2); return;
        }
        if (e.Args.Contains("login") || e.Args.Contains("probe") || (e.Args.Contains("accounts") && e.Args.Contains("sync")))
        {
            using var telemetry = new Telemetry(settings.Telemetry);
            var coordinator = new Coordinator(store, settings, helper);
            Diagnostic? diagnostic = null;
            coordinator.DiagnosticReady += item => diagnostic = item;
            Guid? profile = new AccountBook(store, settings).Resolve(Option("--id") ?? Option("--profile"));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(35));
            var result = await coordinator.RunAsync(profile, e.Args.Contains("probe"), Option("--trigger") == "classisland", timeout.Token,
                readAccounts: e.Args.Contains("accounts") && e.Args.Contains("sync"));
            File.WriteAllText(Path.Combine(store.Root, "last-operation.json"), Json.Serialize(result));
            Console.WriteLine(Json.Serialize(result));
            // Submission has completed; headless mode waits for its bounded upload attempt before exiting.
            if (diagnostic is not null)
            {
                var delivery = await telemetry.PublishAsync(diagnostic);
                File.WriteAllText(Path.Combine(store.Root, "last-delivery.json"), Json.Serialize(delivery));
                if (!delivery.Any(x => x.State == "sent"))
                {
                    string pending = Path.Combine(store.Root, "pending-diagnostics"); Directory.CreateDirectory(pending);
                    foreach (var stale in Directory.GetFiles(pending, "*.json").OrderBy(File.GetCreationTimeUtc).Take(Math.Max(0, Directory.GetFiles(pending, "*.json").Length - 63))) File.Delete(stale);
                    File.WriteAllText(Path.Combine(pending, Guid.NewGuid().ToString("N") + ".json"), Json.Serialize(diagnostic));
                }
            }
            Shutdown(result.Submitted || result.Code is "ready" or "skipped" or "accounts_synced" ? 0 : 2); return;
        }
        var window = new MainWindow(store, settings, helper);
        MainWindow = window;
        DispatcherUnhandledException += (_, args) => { store.Log("unhandled_error", "Application"); window.RecordUnhandled(); args.Handled = true; };
        window.Show();
    }
}
