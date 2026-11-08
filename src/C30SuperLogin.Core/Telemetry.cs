using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace C30SuperLogin.Core;

public enum DiagnosticKind { AppStarted, ProbeCompleted, LoginSubmitted, InjectionFailed, AutomationSkipped, UnhandledError, TestEvent, AccountsSynced, StrategyTransition }
public enum DiagnosticStage { Startup, Discovery, VersionCheck, Hook, Page, Submission, Automation, Application, Accounts, ControlLogin }
public enum DiagnosticOutcome { None, Ready, Submitted, Skipped, Busy, NotInstalled, NotRunning, HelperMissing, UnsupportedVersion, ProfileMissing, ReceiptTimeout, NoControl, ScanLimit, ModuleMissing, ExportMissing, HookFailed, HookTimeout, HookNotExecuted, AccessDenied, ArchitectureMismatch, BridgeError, InvalidCredentials, CredentialError, Cancelled, Win32Error, OperationError, NativeError, SendFailed, AccountsSynced, SavedMissing, SavedAmbiguous, PasswordRequired, NotReady, RestartRequired, MultipleVersions }
public enum LoginMethod { None, Injection, PageControls, Manual }
public enum StrategyState { None, Started, NotSubmitted, Switching, Submitted, SubmissionUncertain, ManualRequired }
public sealed record Diagnostic(DiagnosticKind Kind, DiagnosticStage Stage, string C30Version = "unknown", int Win32Error = 0, double DurationMs = 0, DiagnosticOutcome Outcome = DiagnosticOutcome.None,
    LoginMethod Method = LoginMethod.None, LoginMethod PreviousMethod = LoginMethod.None, StrategyState StrategyState = StrategyState.None, int StrategyStep = 0);
public sealed record Delivery(string Provider, string State);

public sealed class Telemetry : IDisposable
{
    private readonly TelemetrySettings settings;
    private readonly HttpClient http;
    private readonly bool allowTestLoopback;
    private readonly SemaphoreSlim gate = new(1, 1);
    public IReadOnlyList<Delivery> LastDelivery { get; private set; } = [];
    public Telemetry(TelemetrySettings settings, bool allowTestLoopback = false, HttpMessageHandler? handler = null)
    {
        this.settings = settings;
        this.allowTestLoopback = allowTestLoopback;
        http = handler is null ? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(5);
    }
    private string ValidVersion(string value) => Regex.IsMatch(value, @"^\d{1,4}(\.\d{1,5}){1,3}$") ? value : "unknown";
    private Uri Endpoint(string value)
    {
        var uri = new Uri(value, UriKind.Absolute);
        if (uri.Scheme != "https" && !(allowTestLoopback && uri.Scheme == "http" && uri.IsLoopback)) throw new InvalidDataException("Invalid telemetry endpoint");
        if (!string.IsNullOrEmpty(uri.Fragment)) throw new InvalidDataException("Invalid telemetry endpoint");
        return uri;
    }
    public object Properties(Diagnostic item) => new
    {
        app_version = BuildVersion.Current, c30_version = ValidVersion(item.C30Version), adapter_version = "duilib-x86-v1",
        stage = item.Stage.ToString(), result_code = item.Outcome.ToString(), win32_error = item.Win32Error, duration_ms = Math.Round(Math.Clamp(item.DurationMs, 0, 600000), 1),
        architecture = "x86", operating_system = "Windows", authentication_confirmed = false,
        property_allowlist_revision = 1, privacy = "no-account-no-token-no-replay"
        , login_method = item.Method.ToString(), previous_method = item.PreviousMethod.ToString(), strategy_state = item.StrategyState.ToString(), strategy_step = Math.Clamp(item.StrategyStep, 0, 32)
    };
    public async Task<IReadOnlyList<Delivery>> PublishAsync(Diagnostic item, CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            // Each provider is attempted independently; a failure in one never suppresses the other.
            var tasks = new[] { SendPostHog(item, cancellationToken), SendSentry(item, cancellationToken) };
            LastDelivery = await Task.WhenAll(tasks);
            return LastDelivery;
        }
        finally { gate.Release(); }
    }
    private async Task<Delivery> SendPostHog(Diagnostic item, CancellationToken token)
    {
        if (!settings.PostHogEnabled) return new("PostHog", "disabled");
        string key = Environment.GetEnvironmentVariable("C30_POSTHOG_KEY") ?? settings.PostHogKey;
        if (string.IsNullOrWhiteSpace(key)) return new("PostHog", "not_configured");
        try
        {
            var host = Endpoint(Environment.GetEnvironmentVariable("C30_POSTHOG_HOST") ?? settings.PostHogHost);
            if (!string.IsNullOrEmpty(host.UserInfo) || !string.IsNullOrEmpty(host.Query)) throw new InvalidDataException();
            var payload = new { api_key = key, @event = "c30_" + item.Kind.ToString().ToLowerInvariant(),
                properties = new { distinct_id = Regex.IsMatch(settings.InstallationId, "^[a-f0-9]{32}$") ? settings.InstallationId : "anonymous", details = Properties(item),
                    process_person_profile = false, geoip_disable = true }, timestamp = DateTimeOffset.UtcNow };
            // Correct PostHog special property names are written explicitly, not inferred from CLR naming.
            var node = System.Text.Json.Nodes.JsonNode.Parse(Json.Serialize(payload))!;
            node["properties"]!["$process_person_profile"] = false;
            node["properties"]!["$geoip_disable"] = true;
            using var body = new StringContent(node.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync(new Uri(host, "/capture/"), body, token);
            return new("PostHog", response.IsSuccessStatusCode ? "sent" : "http_" + (int)response.StatusCode);
        }
        catch (OperationCanceledException) { return new("PostHog", "timeout_or_cancelled"); }
        catch { return new("PostHog", "transport_or_configuration_error"); }
    }
    private async Task<Delivery> SendSentry(Diagnostic item, CancellationToken token)
    {
        if (!settings.SentryEnabled) return new("Sentry", "disabled");
        string dsn = Environment.GetEnvironmentVariable("C30_SENTRY_DSN") ?? settings.SentryDsn;
        if (string.IsNullOrWhiteSpace(dsn)) return new("Sentry", "not_configured");
        if (item.Kind is not (DiagnosticKind.InjectionFailed or DiagnosticKind.UnhandledError or DiagnosticKind.TestEvent)) return new("Sentry", "not_an_error");
        try
        {
            var uri = Endpoint(dsn);
            string key = uri.UserInfo.Split(':')[0];
            var segments = uri.AbsolutePath.Trim('/').Split('/');
            string project = segments.Last();
            if (!Regex.IsMatch(project, @"^\d+$") || !Regex.IsMatch(key, @"^[a-zA-Z0-9]+$")) throw new InvalidDataException();
            var prefix = segments.Length > 1 ? string.Join('/', segments[..^1]) + "/" : "";
            var target = new UriBuilder(uri) { UserName = "", Password = "", Path = "/" + prefix + "api/" + project + "/envelope/", Query = "" }.Uri;
            string id = Guid.NewGuid().ToString("N");
            var sentryEvent = new
            {
                event_id = id, timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
                platform = "csharp", level = "error", release = "c30-super-login@" + BuildVersion.Current, environment = "production",
                logger = "C30SuperLogin", message = item.Kind.ToString(),
                fingerprint = new[] { item.Kind.ToString(), item.Stage.ToString(), ValidVersion(item.C30Version), item.Win32Error.ToString() },
                tags = new { c30_version = ValidVersion(item.C30Version), adapter = "duilib-x86-v1", stage = item.Stage.ToString() },
                contexts = new { c30 = Properties(item) }
                // Deliberately no user, request, screenshot, breadcrumb text, account or raw exception message.
            };
            string eventJson = Json.Serialize(sentryEvent);
            string envelope = Json.Serialize(new { event_id = id, sent_at = DateTimeOffset.UtcNow }).Replace("\n", "").Replace("\r", "") + "\n"
                + Json.Serialize(new { type = "event", content_type = "application/json", length = Encoding.UTF8.GetByteCount(eventJson) }).Replace("\n", "").Replace("\r", "") + "\n" + eventJson + "\n";
            using var request = new HttpRequestMessage(HttpMethod.Post, target);
            request.Headers.TryAddWithoutValidation("X-Sentry-Auth", $"Sentry sentry_version=7,sentry_client=c30-super-login/0.1.0,sentry_key={key}");
            request.Content = new StringContent(envelope, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/x-sentry-envelope");
            using var response = await http.SendAsync(request, token);
            return new("Sentry", response.IsSuccessStatusCode ? "sent" : "http_" + (int)response.StatusCode);
        }
        catch (OperationCanceledException) { return new("Sentry", "timeout_or_cancelled"); }
        catch { return new("Sentry", "transport_or_configuration_error"); }
    }
    public void Dispose() { http.Dispose(); gate.Dispose(); }
}
