using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace C30SuperLogin.Core;

public sealed record InstallationSnapshot(string Root, string LatestInstalledVersion, string? LatestDirectory,
    string LastStartedVersion, string LauncherNewVersion);

/// <summary>Read-only discovery. A launcher's advertised version is not proof of an installed build.</summary>
public static class Installation
{
    public static InstallationSnapshot Discover(Settings settings, IEnumerable<string>? candidates = null, IEnumerable<string>? runningImages = null,
        bool forceAutoDetect = false)
    {
        bool searchCandidates = settings.AutoDetectInstall || forceAutoDetect;
        string configured = Normalize(settings.InstallRoot);
        var roots = new List<string>();
        void Add(string path) { string normalized = Normalize(path); if (!roots.Contains(normalized, StringComparer.OrdinalIgnoreCase)) roots.Add(normalized); }
        // A running installation wins, including after an installer changes its parent path.
        if (searchCandidates)
            foreach (var path in runningImages ?? Target.RunningImages()) {
                string parent = Normalize(Path.GetDirectoryName(path) ?? path);
                if (File.Exists(Path.Combine(parent, "TeachLauncher.exe"))) Add(parent);
            }
        Add(configured);
        if (searchCandidates) foreach (var path in candidates ?? CandidateRoots()) Add(path);
        var snapshots = roots.Select(Inspect).ToList();
        return snapshots.FirstOrDefault(x => x.LatestDirectory is not null) ?? Inspect(configured);
    }

    public static string Normalize(string path)
    {
        string original;
        try { original = Path.GetFullPath(path); } catch (Exception e) when (e is ArgumentException or NotSupportedException) { return path; }
        if (!Directory.Exists(original)) return original;
        var current = new DirectoryInfo(original);
        for (int i = 0; current is not null && i < 6; i++, current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "TeachLauncher.exe")) || File.Exists(Path.Combine(current.FullName, "AppData", "launcher.ini"))) return current.FullName;
        return original;
    }

    public static InstallationSnapshot Inspect(string root)
    {
        string started = "unknown", advertised = "unknown";
        try {
            string ini = Path.Combine(root, "AppData", "launcher.ini");
            if (File.Exists(ini)) {
                string text = File.ReadAllText(ini);
                started = ReadIni(text, "last_start_verion"); advertised = ReadIni(text, "new_verion");
            }
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        var builds = new List<(Version Version, string Directory)>();
        // Search only the install root and complete numeric-version trees, never the whole disk.
        var pending = new Queue<(string Path, int Depth, Version? FolderVersion)>();
        pending.Enqueue((root, 0, null));
        int inspected = 0;
        while (pending.Count > 0 && inspected++ < 512) {
            var item = pending.Dequeue();
            try {
                if (!Directory.Exists(item.Path) || (File.GetAttributes(item.Path) & FileAttributes.ReparsePoint) != 0) continue;
                string exe = Path.Combine(item.Path, "teach.exe");
                if (File.Exists(exe) && File.Exists(Path.Combine(item.Path, "DuiLib.dll"))) {
                    string? fileVersion = FileVersionInfo.GetVersionInfo(exe).FileVersion;
                    Version? version = Version.TryParse(fileVersion, out var parsed) ? parsed : item.FolderVersion;
                    if (version is not null) builds.Add((version, item.Path));
                }
                if (item.Depth >= 4) continue;
                foreach (string child in Directory.EnumerateDirectories(item.Path).Order(StringComparer.OrdinalIgnoreCase)) {
                    string name = Path.GetFileName(child);
                    Version? folder = Version.TryParse(name, out var parsed) ? parsed : item.FolderVersion;
                    if (item.Depth == 0 && folder is null && name is not ("teach" or "teachingtools")) continue;
                    pending.Enqueue((child, item.Depth + 1, folder));
                }
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        }
        var latest = builds.OrderByDescending(x => x.Version).ThenBy(x => x.Directory, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
        return new(root, latest.Version?.ToString() ?? "unknown", latest.Directory, started, advertised);
    }

    private static string ReadIni(string text, string key) {
        var match = Regex.Match(text, @"(?im)^\s*" + Regex.Escape(key) + @"\s*=\s*(\d+(?:\.\d+){1,3})\s*$");
        return match.Success && Version.TryParse(match.Groups[1].Value, out _) ? match.Groups[1].Value : "unknown";
    }
    public static bool NeedsRestart(string runningVersion, string installedVersion) =>
        Version.TryParse(runningVersion, out var running) && Version.TryParse(installedVersion, out var installed) && installed > running;

    private static IEnumerable<string> CandidateRoots()
    {
        var paths = new List<string>();
        foreach (var directory in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) })
            if (!string.IsNullOrWhiteSpace(directory)) paths.Add(Path.Combine(directory, "Datedu", "teach"));
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 }) {
                try {
                    using var registry = RegistryKey.OpenBaseKey(hive, view);
                    using var uninstall = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                    if (uninstall is null) continue;
                    foreach (string name in uninstall.GetSubKeyNames()) {
                        using var entry = uninstall.OpenSubKey(name);
                        if (Regex.IsMatch(entry?.GetValue("DisplayName") as string ?? "", "C30|Datedu|智能教学", RegexOptions.IgnoreCase)
                            && entry?.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location)) paths.Add(location);
                    }
                } catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
            }
        return paths;
    }
}
