using System.Text.Json;

namespace C30SuperLogin.Core;

public sealed class Settings
{
    public int SchemaVersion { get; set; } = 1;
    public string InstallRoot { get; set; } = @"D:\Program Files (x86)\Datedu\teach";
    public bool AutoDetectInstall { get; set; } = true;
    public bool QuietSuccess { get; set; } = true;
    public bool LaunchC30 { get; set; } = true;
    public string Theme { get; set; } = "System";
    public int OnboardingVersion { get; set; }
    public RuntimeMode RuntimeMode { get; set; } = RuntimeMode.Resident;
    // Retained for old settings files; connecting to C30 requires elevation.
    public bool UseAdministrator { get => true; set { } }
    public bool AutomationPaused { get; set; }
    public int PreLoginWarningSeconds { get; set; } = 5;
    public UpdateSettings Updates { get; set; } = new();
    public List<AccountProfile> Profiles { get; set; } = [];
    public Dictionary<Guid, Guid> ProfileAliases { get; set; } = [];
    public Dictionary<int, Guid> ProfileNumberAliases { get; set; } = [];
    public long NextProfileNumber { get; set; } = 1;
    public List<CourseRule> Rules { get; set; } = [];
    public TelemetrySettings Telemetry { get; set; } = new();
}
public enum RuntimeMode { Resident, OnDemand }
public sealed record AccountProfile(Guid Id, string Label, int Number = 0);
public sealed class UpdateSettings
{
    public bool AutoCheck { get; set; } = true;
    public string FeedUrl { get; set; } = "";
}
public sealed record Credentials(string Username, string Password, string C30Uid = "", bool UseC30Saved = false, bool SavedPasswordAvailable = false);
public sealed record SavedAccount(string Uid, string Username, string DisplayName, bool HasSavedPassword);
public sealed record AccountRecord(Guid Id, string Label, string Username, string Source, bool CanLogin, int Number = 0);
public sealed record AccountMerge(int Added, int Matched);
public sealed record CourseRule(Guid Id, Guid ProfileId, string SubjectId, string SubjectName, int LeadSeconds = 20, bool Enabled = true);
public sealed class TelemetrySettings
{
    // Explicit product-owner requirement: default ON; missing destinations are reported, never fabricated.
    public bool PostHogEnabled { get; set; } = true;
    public bool SentryEnabled { get; set; } = true;
    public string PostHogHost { get; set; } = "https://us.i.posthog.com";
    public string PostHogKey { get; set; } = "";
    public string SentryDsn { get; set; } = "";
    public string InstallationId { get; set; } = Guid.NewGuid().ToString("N");
}
public sealed record OperationResult(string Code, string Message, bool Submitted = false, double ElapsedMs = 0, string C30Version = "unknown", int Win32Error = 0, DiagnosticStage Stage = DiagnosticStage.Page);
public sealed record NativeResult(string Code, int Win32Error, uint Candidates, uint Queued, uint ScannedBytes);
public sealed record ActivationSnapshot(bool Authorized, string Time, string Tips);
public sealed record ActivationResult(string Code, string Message, bool? Authorized = null, string Time = "", string Tips = "", string C30Version = "unknown", int Win32Error = 0);
public sealed record Subject(string Id, string Name);
public static class Json
{
    public static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
