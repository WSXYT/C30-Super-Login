using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using FluentAvalonia.Styling;
using C30SuperLogin.Core;
namespace C30SuperLogin.Desktop;
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        Velopack.VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        int owner = Array.IndexOf(args, "--expected-user-sid");
        if (owner >= 0 && (owner + 1 >= args.Length || args[owner + 1] != System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value)) return 2;
        if ((args.Contains("--admin-host") || args.Contains("--elevated-operation")) && !RuntimeAccess.IsAdministrator) return 2;
        bool directActivationStatus = args.Contains("activation") && args.Contains("status");
        if (!args.Contains("--elevated-operation") && !directActivationStatus &&
            (args.Contains("login") || args.Contains("probe") || args.Contains("activation") || args.Contains("accounts") && args.Contains("sync"))) {
            try {
                int directory = Array.IndexOf(args, "--data-dir"); var store = new Store(directory >= 0 && directory + 1 < args.Length ? args[directory + 1] : null);
                var routed = RuntimeAccess.RouteAsync(args, store, store.Load()).GetAwaiter().GetResult();
                if (routed is not null) { File.WriteAllText(Path.Combine(store.Root, "last-operation.json"), Json.Serialize(routed.Result)); Console.WriteLine(Json.Serialize(routed.Result)); return routed.ExitCode; }
            } catch { return 2; }
        }
        bool showWarning = false;
        if (PreLoginPolicy.IsAutomatedLogin(args)) {
            try {
                int i = Array.IndexOf(args, "--data-dir"); var store = new Store(i >= 0 && i + 1 < args.Length ? args[i + 1] : null);
                showWarning = PreLoginPolicy.ShouldPrompt(store.Load(), args, File.Exists(Path.Combine(store.Root, "skip-next")));
            } catch { return 2; }
        }
        if (!showWarning && args.Any(x => x is "accounts" or "login" or "probe" or "activation" or "status" or "detect-install" or "skip-next" or "telemetry-test"))
        { try { return Commands.Run(args).GetAwaiter().GetResult(); } catch { return 2; } }
        return AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
    }
}
public sealed class App : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentAvaloniaTheme { PreferSystemTheme = true, PreferUserAccentColor = true, UseSystemFontOnWindows = true });
        Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://C30SuperLogin/")) { Source = new Uri("avares://C30SuperLogin/Design.axaml") });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            string[] args = desktop.Args ?? []; int index = Array.IndexOf(args, "--data-dir");
            var store = new Store(index >= 0 && index + 1 < args.Length ? args[index + 1] : null);
            var settings = store.Load(); new AccountBook(store, settings).Deduplicate(); store.Save(settings);
            ApplyTheme(settings.Theme);
            if (PreLoginPolicy.IsAutomatedLogin(args)) {
                FileStream gate;
                try { gate = new FileStream(Path.Combine(store.Root, "prelogin.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { Avalonia.Threading.Dispatcher.UIThread.Post(() => desktop.Shutdown(2)); base.OnFrameworkInitializationCompleted(); return; }
                var prompt = new PreLoginWindow(settings.PreLoginWarningSeconds);
                prompt.Decision += async proceed => {
                    try { if (!proceed) store.SetSkipNext(); int result = await Commands.Run(args); desktop.Shutdown(result); }
                    catch { desktop.Shutdown(2); }
                    finally { gate.Dispose(); }
                };
                desktop.MainWindow = prompt;
            } else if (!args.Contains("--admin-host") && settings.OnboardingVersion < 1) {
                var onboarding = new OnboardingWindow(store, settings);
                onboarding.Completed += () => { var main = new MainWindow(store, settings); desktop.MainWindow = main; main.Show(); };
                desktop.MainWindow = onboarding;
            } else desktop.MainWindow = new MainWindow(store, settings, args.Contains("--background"));
        }
        base.OnFrameworkInitializationCompleted();
    }
    public static void ApplyTheme(string theme) { if (Current is { } app) app.RequestedThemeVariant = theme switch { "Light" => ThemeVariant.Light, "Dark" => ThemeVariant.Dark, _ => ThemeVariant.Default }; }
    internal static async Task<AdminReply> ExecuteAdminRequest(AdminRequest request, Store store)
    {
        if (!RuntimeAccess.Valid(request)) return new(2, new("invalid_admin_request", "无效后台请求。"));
        string[] args = RuntimeAccess.Arguments(request, store.Root);
        if (request.Operation is "activation_status" or "activation_open" or "activation_local")
        {
            var activationSettings = store.Load();
            var client = new ActivationClient(activationSettings, Path.Combine(AppContext.BaseDirectory, "C30Native.exe"));
            var result = request.Operation switch
            {
                "activation_status" => await client.QueryAsync(CancellationToken.None),
                "activation_local" => await client.ApplyLocalAsync(CancellationToken.None),
                _ => await client.OpenOfficialDialogAsync(CancellationToken.None)
            };
            return RuntimeAccess.ToAdminReply(result);
        }
        var settings = store.Load();
        if (settings.RuntimeMode != RuntimeMode.Resident || !settings.UseAdministrator) return new(2, new("runtime_mode_changed", "运行方式已更改，请按新的方式调用。"));
        FileStream? gate = null;
        try {
            if (PreLoginPolicy.ShouldPrompt(settings, args, File.Exists(Path.Combine(store.Root, "skip-next")))) {
                try { gate = new FileStream(Path.Combine(store.Root, "prelogin.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException) { return new(2, new("busy", "已有课前提醒正在进行。")); }
                var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var warning = new PreLoginWindow(settings.PreLoginWarningSeconds);
                warning.Decision += proceed => { warning.Close(); decision.TrySetResult(proceed); };
                warning.Show();
                if (!await decision.Task) store.SetSkipNext();
            }
            int exit = await Commands.Run(args);
            string path = RuntimeAccess.ReplyPath(store.Root, request.RequestId);
            return new(exit, File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<OperationResult>(File.ReadAllText(path), Json.Options) ?? new("operation_failed", "本次操作没有返回结果。") : new("operation_failed", "本次操作没有返回结果。"));
        } finally { gate?.Dispose(); }
    }
}
