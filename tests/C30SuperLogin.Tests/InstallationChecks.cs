using C30SuperLogin.Core;

internal static class InstallationChecks
{
    public static void Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "C30SL-install-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "AppData"));
        File.WriteAllText(Path.Combine(root, "TeachLauncher.exe"), "owned synthetic fixture; never executed");
        File.WriteAllText(Path.Combine(root, "AppData", "launcher.ini"), "[launcher]\nlast_start_verion=1.3.9.0\nnew_verion=99.0.0.0\n");
        string Build(string version, string suffix = "teach/teachingtools", bool complete = true) {
            string directory = Path.Combine(root, version, suffix.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(directory); File.WriteAllText(Path.Combine(directory, "teach.exe"), "owned fixture; never executed");
            if (complete) File.WriteAllText(Path.Combine(directory, "DuiLib.dll"), "owned fixture; never loaded");
            return directory;
        }
        Build("1.3.9.0"); string newer = Build("1.3.10.0"); string partial = Build("1.3.11.0", "new-layout/bin", false);
        var settings = new Settings { InstallRoot = root, AutoDetectInstall = false };
        var result = Installation.Discover(settings, [], []);
        check(result.LatestInstalledVersion == "1.3.10.0" && result.LatestDirectory == newer, "installed versions use numeric ordering; incomplete upgrade excluded");
        check(result.LastStartedVersion == "1.3.9.0" && result.LauncherNewVersion == "99.0.0.0", "launcher hints remain separate from confirmed installed version");
        File.WriteAllText(Path.Combine(partial, "DuiLib.dll"), "owned fixture; never loaded");
        check(Installation.Discover(settings, [], []).LatestInstalledVersion == "1.3.11.0", "next discovery sees newly completed version in changed child layout without restart");
        check(Installation.Normalize(partial) == root, "version-specific configured path normalizes to stable launcher root");
        File.Delete(Path.Combine(partial, "teach.exe"));
        check(Installation.Discover(settings, [], []).LatestInstalledVersion == "1.3.10.0", "removed or rolled-back version is not cached");
        check(Installation.NeedsRestart("1.3.9.0", "1.3.10.0") && !Installation.NeedsRestart("1.3.10.0", "1.3.9.0"), "old running version requires restart, never switches process or reuses old adapter silently");
        string missing = Path.Combine(root, "NO_INSTALLATION");
        var automatic = new Settings { InstallRoot = missing };
        check(Installation.Discover(automatic, [root], []).Root == root, "automatic detection falls back to recognized candidate root");
        check(Installation.Discover(automatic, [], [Path.Combine(newer, "teach.exe")]).Root == root, "actual running executable locates a relocated installation root");
        automatic.AutoDetectInstall = false;
        check(Installation.Discover(automatic, [root], [Path.Combine(newer, "teach.exe")]).LatestDirectory is null, "manual or isolated test root does not fall through to real installations");
        var explicitDetection = Installation.Discover(automatic, [root], [], forceAutoDetect: true);
        check(explicitDetection.Root == root && explicitDetection.LatestInstalledVersion == "1.3.10.0", "explicit one-shot detection searches candidate roots while preserving auto-detect preference");
        check(!automatic.AutoDetectInstall && automatic.InstallRoot == missing, "read-only discovery does not overwrite a manual path when the caller decides whether to save");
        check(new Settings().AutoDetectInstall && new TelemetrySettings().PostHogEnabled && new TelemetrySettings().SentryEnabled, "automatic installation detection and both telemetry providers default ON");
        var optedOut = System.Text.Json.JsonSerializer.Deserialize<Settings>("{\"Telemetry\":{\"PostHogEnabled\":false,\"SentryEnabled\":false}}", Json.Options)!;
        check(!optedOut.Telemetry.PostHogEnabled && !optedOut.Telemetry.SentryEnabled, "explicit telemetry opt-out survives loading; defaults do not overwrite user choice");
        Console.WriteLine("INSTALLATION_FIXTURE root=" + root + "; real_C30_actions=none");
    }
}
