using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace C30SuperLogin.Core;

public sealed record TargetProcess(int Pid, IntPtr Window, string Version, string Directory);
public static class Target
{
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
    private delegate bool EnumProc(IntPtr hwnd, IntPtr parameter);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref int size);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    private static string ImagePath(int pid)
    {
        // Limited query avoids unnecessary VM_READ rights and cross-bitness module enumeration.
        IntPtr handle = OpenProcess(0x1000, false, pid);
        if (handle == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var path = new StringBuilder(32768); int size = path.Capacity;
            if (!QueryFullProcessImageName(handle, 0, path, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            return path.ToString();
        }
        finally { CloseHandle(handle); }
    }
    public static IReadOnlyList<string> RunningImages()
    {
        var paths = new List<string>();
        foreach (var process in Process.GetProcessesByName("teach")) using (process) {
            try { paths.Add(ImagePath(process.Id)); }
            catch (System.ComponentModel.Win32Exception) { }
            catch (InvalidOperationException) { }
        }
        return paths;
    }
    public static IReadOnlyList<TargetProcess> Find(string root, Action<int>? queryError = null)
    {
        string canonical = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var targets = new List<TargetProcess>();
        foreach (var process in Process.GetProcessesByName("teach"))
        {
            using (process)
            {
                try
                {
                    string path = ImagePath(process.Id);
                    if (!Path.GetFullPath(path).StartsWith(canonical, StringComparison.OrdinalIgnoreCase)) continue;
                    string directory = Path.GetDirectoryName(path)!;
                    var module = Path.Combine(directory, "DuiLib.dll");
                    if (!File.Exists(module)) continue;
                    EnumWindows((hwnd, _) =>
                    {
                        GetWindowThreadProcessId(hwnd, out uint pid);
                        if (pid == process.Id && IsWindowVisible(hwnd)) targets.Add(new(process.Id, hwnd,
                            FileVersionInfo.GetVersionInfo(path).FileVersion ?? "unknown", directory));
                        return true;
                    }, IntPtr.Zero);
                }
                catch (System.ComponentModel.Win32Exception e) { queryError?.Invoke(e.NativeErrorCode); }
                catch (InvalidOperationException) { }
            }
        }
        return targets;
    }
    public static bool IsSupported(TargetProcess target)
    {
        // Exact observed initial adapter identity; never reuse offsets on an unrecognized version.
        if (target.Version != "1.3.1610.0") return false;
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(target.Directory, "DuiLib.dll"))));
        return hash == AdapterIdentity.DuiLibSha256;
    }
    public static string ReadInstallVersion(string root)
    {
        return Installation.Inspect(Installation.Normalize(root)).LatestInstalledVersion;
    }
    public static string CurrentVersion(Settings settings)
    {
        var installation = Installation.Discover(settings);
        return Find(installation.Root).FirstOrDefault()?.Version ?? installation.LatestInstalledVersion;
    }
}
public static class Script
{
    public static string Source
    {
        get { using var stream = typeof(Script).Assembly.GetManifestResourceStream("C30SuperLogin.Core.Injection.login.js")!; using var reader = new StreamReader(stream); return reader.ReadToEnd(); }
    }
    public static string Create(bool probe, Credentials? credentials, string receipt, string? requestId = null, bool readAccounts = false) => Source + "\nwindow.C30SuperLogin.run(" +
        JsonSerializer.Serialize(new { operation = readAccounts ? "list_saved" : probe ? "probe" : credentials?.UseC30Saved == true ? "login_saved" : "login", username = credentials?.Username, password = credentials?.UseC30Saved == true ? null : credentials?.Password, savedUid = credentials?.C30Uid,
            receipt, requestId = requestId ?? Guid.NewGuid().ToString("N") }) + ");";
    public static string CreateActivation(string operation, string receipt) => Source + "\nwindow.C30SuperLogin.run(" +
        JsonSerializer.Serialize(new { operation, receipt }) + ");";
}
public sealed class NativeTransport(string helperPath)
{
    public async Task<NativeResult> ExecuteAsync(TargetProcess target, string script, CancellationToken token)
    {
        var start = new ProcessStartInfo(helperPath) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8 };
        start.ArgumentList.Add("--pid"); start.ArgumentList.Add(target.Pid.ToString());
        start.ArgumentList.Add("--hwnd"); start.ArgumentList.Add(target.Window.ToInt64().ToString("X"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Native helper did not start");
        try
        {
            await process.StandardInput.WriteAsync(script.AsMemory(), token); process.StandardInput.Close();
            Task<string> output = process.StandardOutput.ReadToEndAsync(token);
            Task<string> error = process.StandardError.ReadToEndAsync(token);
            await process.WaitForExitAsync(token);
            string json = await output; _ = await error;
            return JsonSerializer.Deserialize<NativeResult>(json, Json.Options) ?? new("invalid_response", 0, 0, 0, 0);
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(CancellationToken.None); } }
    }
}
