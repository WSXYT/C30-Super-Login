using C30SuperLogin.Core;
using System.Reflection;
using Velopack;

namespace C30SuperLogin.Desktop;

/// <summary>Official Velopack API. Downloading never implies permission to restart.</summary>
public sealed class AppUpdates(Store store, Settings settings, UpdateManager? testManager = null)
{
    private UpdateManager? manager;
    private UpdateInfo? available;
    private bool running, downloaded;
    private FileStream? warningGate, operationGate;
    public string State { get; private set; } = "idle";
    public string? AvailableVersion => available?.TargetFullRelease.Version.ToString();
    public event Action<string>? Changed;
    private void Set(string state) { State = state; Changed?.Invoke(state); }
    public static bool IsValidFeed(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    public async Task CheckAsync(bool manual = false)
    {
        if (running) return;
        if (!manual && !settings.Updates.AutoCheck) { Set("disabled"); return; }
        string embedded = typeof(AppUpdates).Assembly.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>().FirstOrDefault(x => x.Key == "C30UpdateUrl")?.Value ?? "";
        string feed = Environment.GetEnvironmentVariable("C30_UPDATE_URL") ?? (string.IsNullOrWhiteSpace(settings.Updates.FeedUrl) ? embedded : settings.Updates.FeedUrl);
        if (testManager is null && string.IsNullOrWhiteSpace(feed)) { Set("not_configured"); return; }
        if (testManager is null && !IsValidFeed(feed)) { Set("invalid_source"); return; }
        running = true; downloaded = false; available = null;
        try {
            manager = testManager ?? new UpdateManager(feed);
            if (!manager.IsInstalled) { Set("portable"); return; }
            Set("checking"); available = await manager.CheckForUpdatesAsync();
            Set(available is null ? "up_to_date" : "available");
        } catch { Set("check_failed"); }
        finally { running = false; }
    }
    public async Task DownloadAsync(CancellationToken token = default)
    {
        if (running || manager is null || available is null) return;
        running = true;
        try {
            Set("downloading"); await manager.DownloadUpdatesAsync(available, cancelToken: token);
            downloaded = true; Set("downloaded");
        } catch (OperationCanceledException) { Set("download_cancelled"); }
        catch { Set("download_failed"); }
        finally { running = false; }
    }
    public bool ApplyAndRestart(bool locallyBusy)
    {
        if (running || locallyBusy) { Set("busy"); return false; }
        if (manager is null || available is null || !downloaded) { Set("not_ready"); return false; }
        try {
            // Hold both locks through process exit. No restart while another instance is warning/logging in.
            warningGate = new FileStream(Path.Combine(store.Root, "prelogin.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            operationGate = new FileStream(Path.Combine(store.Root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            manager.ApplyUpdatesAndRestart(available.TargetFullRelease, ["--data-dir", store.Root]);
            return true;
        } catch (IOException) { ReleaseGates(); Set("busy"); return false; }
        catch { ReleaseGates(); Set("apply_failed"); return false; }
    }
    private void ReleaseGates() { operationGate?.Dispose(); warningGate?.Dispose(); operationGate = warningGate = null; }
}
