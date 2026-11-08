using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace C30SuperLogin.Core;

public sealed class Store
{
    public string Root { get; }
    public Store(string? root = null)
    {
        Root = root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "C30SuperLogin");
        Directory.CreateDirectory(Root);
    }
    public Settings Load() => File.Exists(Path.Combine(Root, "settings.json"))
        ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path.Combine(Root, "settings.json")), Json.Options) ?? new() : new();
    public void Save(Settings settings)
    {
        var path = Path.Combine(Root, "settings.json");
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, Json.Serialize(settings), new UTF8Encoding(false));
        File.Move(temporary, path, true);
    }
    public void SetSkipNext() => File.WriteAllText(Path.Combine(Root, "skip-next"), "1");
    public bool ConsumeSkip()
    {
        string source = Path.Combine(Root, "skip-next"), claim = source + "." + Guid.NewGuid().ToString("N");
        try { File.Move(source, claim); File.Delete(claim); return true; }
        catch (FileNotFoundException) { return false; }
    }
    public void SaveSecret(Guid profile, Credentials credentials)
    {
        var plain = Encoding.UTF8.GetBytes(Json.Serialize(credentials));
        try { File.WriteAllBytes(SecretPath(profile), Dpapi.Transform(plain, true)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public Credentials LoadSecret(Guid profile)
    {
        var plain = Dpapi.Transform(File.ReadAllBytes(SecretPath(profile)), false);
        try { return JsonSerializer.Deserialize<Credentials>(plain, Json.Options) ?? throw new InvalidDataException("Invalid profile"); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
    public void DeleteSecret(Guid profile) => File.Delete(SecretPath(profile));
    private string SecretPath(Guid id) => Path.Combine(Root, id.ToString("N") + ".credential");
    public void Log(string code, string stage, string version = "unknown")
    {
        // Strict allowlist, no arbitrary exception messages, commands, URLs or account data.
        var path = Path.Combine(Root, "events.jsonl");
        if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Move(path, path + ".previous", true);
        File.AppendAllText(path, System.Text.Json.JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, code, stage, c30_version = version }) + "\n");
    }
    public void LogStrategy(Diagnostic item)
    {
        var path = Path.Combine(Root, "strategies.jsonl");
        if (File.Exists(path) && new FileInfo(path).Length > 512 * 1024) File.Move(path, path + ".previous", true);
        File.AppendAllText(path, System.Text.Json.JsonSerializer.Serialize(new { time = DateTimeOffset.UtcNow, item }) + "\n");
    }
}
internal static class Dpapi
{
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref Blob input, string description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    public static byte[] Transform(byte[] input, bool protect)
    {
        var data = new Blob { Length = input.Length, Data = Marshal.AllocHGlobal(input.Length) };
        Marshal.Copy(input, 0, data.Data, input.Length);
        Blob output = default;
        try
        {
            bool ok = protect ? CryptProtectData(ref data, "C30 Super Login", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref data, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var result = new byte[output.Length]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        }
        finally
        {
            for (var i = 0; i < data.Length; i++) Marshal.WriteByte(data.Data, i, 0);
            Marshal.FreeHGlobal(data.Data);
            if (output.Data != IntPtr.Zero)
            {
                for (var i = 0; i < output.Length; i++) Marshal.WriteByte(output.Data, i, 0);
                LocalFree(output.Data);
            }
        }
    }
}
