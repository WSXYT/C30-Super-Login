using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using C30SuperLogin.Core;

if (args.Length >= 2 && args[0] == "--control-target")
{
    var parts = args[1].Split(':');
    var target = new TargetProcess(int.Parse(parts[0]), new IntPtr(long.Parse(parts[1])), "fixture", "fixture");
    var root = System.Windows.Automation.AutomationElement.FromHandle(target.Window);
    foreach (System.Windows.Automation.AutomationElement item in root.FindAll(System.Windows.Automation.TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
        if (item.Current.AutomationId.StartsWith("login")) Console.WriteLine($"FIXTURE_CONTROL id={item.Current.AutomationId} password={item.Current.IsPassword} patterns={string.Join(',', item.GetSupportedPatterns().Select(x => x.ProgrammaticName))}");
    bool sent = await ControlLogin.TryAsync(target, new("LOCAL_UIA_USER", "LOCAL_UIA_PASSWORD"), CancellationToken.None);
    Console.WriteLine("CONTROL_FIXTURE submitted=" + sent); return sent ? 0 : 2;
}
int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
var settings = new Settings();
Check(settings.Telemetry.PostHogEnabled && settings.Telemetry.SentryEnabled && settings.QuietSuccess, "default telemetry ON, quiet UI ON");
using (var telemetry = new Telemetry(new()))
{
    string properties = Json.Serialize(telemetry.Properties(new(DiagnosticKind.InjectionFailed, DiagnosticStage.Hook, "1.3.1610.0", 5, 10, DiagnosticOutcome.HookFailed)));
    Check(properties.Contains("HookFailed") && properties.Contains("1.3.1610.0"), "diagnostics include allowlisted failure reason and C30 version");
}
var directory = Path.Combine(Path.GetTempPath(), "C30SL-tests-" + Guid.NewGuid().ToString("N"));
var store = new Store(directory); var profile = Guid.NewGuid();
var credentials = new Credentials("LOCAL_TEST_ACCOUNT", "LOCAL_TEST_PASSWORD");
Check(ControlLogin.MayTry(false, false, false, credentials), "control fallback allowed only before script dispatch with local credentials");
Check(!ControlLogin.MayTry(false, false, true, credentials) && !ControlLogin.MayTry(true, false, false, credentials) &&
    !ControlLogin.MayTry(false, true, false, credentials) && !ControlLogin.MayTry(false, false, false, new("u", "", UseC30Saved: true)),
    "queued requests, probes, imports and C30 saved references never enter control fallback");
store.SaveSecret(profile, credentials);
Check(store.LoadSecret(profile) == credentials, "DPAPI current-user round trip");
Check(!Encoding.UTF8.GetString(File.ReadAllBytes(Directory.GetFiles(directory, "*.credential")[0])).Contains(credentials.Password), "credential file has no plaintext password");
store.SetSkipNext(); Check(store.ConsumeSkip() && !store.ConsumeSkip(), "skip-next consumed exactly once");
settings.Profiles.Add(new(profile, "Fixture")); store.Save(settings); Check(store.Load().Profiles.Single().Id == profile, "settings atomic save/load");
var rule = new CourseRule(Guid.NewGuid(), profile, "SUBJECT_FIXTURE", "Test Subject");
string exported = ClassIsland.Export([rule], @"C:\Fixture\C30SuperLogin.exe");
using (var doc = JsonDocument.Parse(exported))
{
    var action = doc.RootElement[0].GetProperty("ActionSet").GetProperty("Actions")[0];
    Check(action.GetProperty("Id").GetString() == "classisland.os.run" && action.GetProperty("Settings").GetProperty("Args").GetString()!.Contains(profile.ToString()), "ClassIsland native run action uses profile GUID");
}
Check(!exported.Contains(credentials.Username) && !exported.Contains(credentials.Password), "ClassIsland export has no account/password");
string merged = ClassIsland.Merge("[{\"ActionSet\":{\"Guid\":\"unmanaged\",\"Name\":\"Keep me\"}}]", exported);
using (var doc = JsonDocument.Parse(ClassIsland.Merge(merged, exported))) Check(doc.RootElement.GetArrayLength() == 2 && doc.RootElement[0].GetProperty("ActionSet").GetProperty("Name").GetString() == "Keep me", "ClassIsland merge preserves other rules and is idempotent");
Check(!Target.IsSupported(new(0, IntPtr.Zero, "9.9.9.9", directory)), "unknown C30 version rejected before native injection");
Check(Script.Create(false, credentials, "http://127.0.0.1:1/receipt/" + new string('0', 32)).Contains("window.C30SuperLogin.run("), "runtime script uses the tested injection entry");
var accountSettings = new Settings();
var accountStore = new Store(Path.Combine(directory, "account-book"));
var book = new AccountBook(accountStore, accountSettings);
Guid stableId = Guid.NewGuid();
var first = book.Save(stableId, "Local Fixture", credentials.Username, credentials.Password);
Check(first.Id == stableId && new AccountBook(accountStore, accountStore.Load()).Read().Single().Username == credentials.Username, "saved accounts reload from DPAPI storage with stable profile ID");
var duplicate = book.Save(Guid.NewGuid(), "Same Account", credentials.Username, credentials.Password);
Check(duplicate.Id == stableId && accountSettings.Profiles.Count == 1, "same account is merged rather than assigned a duplicate profile ID");
bool rejectedId = false;
try { book.Save(stableId, "Collision", "DIFFERENT_ACCOUNT", credentials.Password); } catch (InvalidDataException) { rejectedId = true; }
Check(rejectedId && accountStore.LoadSecret(stableId).Username == credentials.Username, "new account cannot overwrite another account's profile ID");
var nativeAccounts = new[] { new SavedAccount("LOCAL_UID", credentials.Username, "Local Fixture", true), new SavedAccount("C30_ONLY", "LOCAL_C30_ONLY", "C30 Fixture", true), new SavedAccount("NO_PASSWORD", "LOCAL_NO_PASSWORD", "No Password", false) };
var imported = book.MergeSaved(nativeAccounts);
Check(imported.Added == 2 && imported.Matched == 1 && accountSettings.Profiles.Count == 3 && accountStore.LoadSecret(stableId).Password == credentials.Password, "C30 import merges with local accounts without losing password or stable ID");
var beforeIds = accountSettings.Profiles.Select(x => x.Id).ToArray();
Check(book.MergeSaved(nativeAccounts).Added == 0 && accountSettings.Profiles.Select(x => x.Id).SequenceEqual(beforeIds), "repeated C30 import is idempotent and creates no duplicate records");
var c30Profile = book.Read().Single(x => x.Username == "LOCAL_C30_ONLY"); var c30Secret = accountStore.LoadSecret(c30Profile.Id);
Check(c30Secret.Password == "" && c30Secret.UseC30Saved && c30Profile.CanLogin, "import stores an encrypted C30 reference, not an exported password");
Check(!book.Read().Single(x => x.Username == "LOCAL_NO_PASSWORD").CanLogin, "C30 records without a saved password remain visible and require password input");
string referencedScript = Script.Create(false, c30Secret, "http://127.0.0.1:1/receipt/" + new string('0', 32));
Check(referencedScript.Contains("login_saved") && referencedScript.Contains("C30_ONLY") && !referencedScript.Contains(credentials.Password), "profile-ID login selects the native saved reference without exporting its password");
Check(book.Resolve(stableId.ToString()) == stableId && book.Resolve("not-a-guid") is null && book.Resolve(Guid.NewGuid().ToString()) is null, "profile ID lookup validates existing records");
Guid legacyDuplicate = Guid.NewGuid(); accountSettings.Profiles.Add(new(legacyDuplicate, "Legacy Duplicate")); accountStore.SaveSecret(legacyDuplicate, credentials);
accountSettings.Rules.Add(new(Guid.NewGuid(), legacyDuplicate, "SUBJECT_FIXTURE", "Fixture"));
Check(book.Deduplicate() == 1 && book.Resolve(legacyDuplicate.ToString()) == stableId && accountSettings.Rules.Single().ProfileId == stableId, "legacy duplicates merge with old ID aliases and automation bindings retained");
var badIdResult = await new Coordinator(accountStore, accountSettings, "MISSING_HELPER").RunAsync(Guid.NewGuid(), false, false, CancellationToken.None);
Check(badIdResult.Code == "profile_missing" && !badIdResult.Submitted, "unknown profile ID is rejected before target injection");
await using (var receipt = new ReceiptListener())
{
    using var http = new HttpClient(); await http.GetAsync(receipt.Address + "?event=ready");
    Check(await receipt.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None) == "ready", "actual loopback HTTP receipt round trip");
}
await using (var receipt = new ReceiptListener())
{
    using var http = new HttpClient();
    using var wrong = await http.PostAsync(receipt.Address.Replace("/receipt/", "/wrong/") + "?event=accounts", new StringContent(Json.Serialize(nativeAccounts)));
    Check(wrong.StatusCode == HttpStatusCode.NotFound, "account metadata receiver rejects the wrong nonce route");
    using var response = await http.PostAsync(receipt.Address + "?event=accounts", new StringContent(Json.Serialize(nativeAccounts), Encoding.UTF8, "text/plain"));
    var actual = await receipt.ReadAccountsAsync(TimeSpan.FromSeconds(2), CancellationToken.None);
    Check(response.IsSuccessStatusCode && actual.Count == 3 && await receipt.WaitAsync(TimeSpan.FromSeconds(2), CancellationToken.None) == "accounts", "actual loopback POST reads bounded account metadata");
}
using (var telemetry = new Telemetry(new()))
{
    var results = await telemetry.PublishAsync(new(DiagnosticKind.InjectionFailed, DiagnosticStage.Hook));
    Check(results.All(x => x.State == "not_configured"), "empty credentials never claim an upload");
}
foreach (int posthogStatus in new[] { 200, 503 })
{
    await using var receiver = new CaptureServer(posthogStatus);
    using var telemetry = new Telemetry(new() { PostHogHost = receiver.Host, PostHogKey = "TEST_PROJECT_KEY", SentryDsn = receiver.Host.Replace("http://", "http://testkey@") + "/42" }, allowTestLoopback: true);
    var result = await telemetry.PublishAsync(new(DiagnosticKind.InjectionFailed, DiagnosticStage.Hook, posthogStatus == 200 ? "1.3.1610.0" : credentials.Password, 5, 12.3));
    await receiver.Completed.WaitAsync(TimeSpan.FromSeconds(5));
    Check(result.Single(x => x.Provider == "Sentry").State == "sent" && result.Single(x => x.Provider == "PostHog").State == (posthogStatus == 200 ? "sent" : "http_503"), "independent real HTTP providers, PostHog status " + posthogStatus);
    Check(receiver.Bodies.Count == 2 && receiver.Bodies.All(x => !x.Contains(credentials.Password) && !x.Contains(credentials.Username)) && receiver.Bodies.Any(x => x.Contains("c30_version")), "outbound allowlist strips invalid version and excludes credentials, case " + posthogStatus);
    Check(receiver.Paths.Contains("/capture/") && receiver.Paths.Contains("/api/42/envelope/"), "PostHog capture and Sentry envelope protocol paths, case " + posthogStatus);
}
InstallationChecks.Run(Check);
NumberChecks.Run(Check);
var automaticArgs = new[] { "login", "--trigger", "classisland", "--headless" };
Check(PreLoginPolicy.ShouldPrompt(new(), automaticArgs), "automatic login warns by default before any injection");
Check(!PreLoginPolicy.ShouldPrompt(new(), ["login", "--headless"]), "manual login retains fast path without countdown");
Check(!PreLoginPolicy.ShouldPrompt(new(), automaticArgs, true) && !PreLoginPolicy.ShouldPrompt(new() { AutomationPaused = true }, automaticArgs), "already skipped or paused automation does not show a warning");
Check(!PreLoginPolicy.ShouldPrompt(new() { PreLoginWarningSeconds = 0 }, automaticArgs), "pre-login warning can be disabled without affecting login adapter");
await StrategyChecks.Run(Check);
Console.WriteLine($"RESULT {passed}/{passed} PASS; external_uploads=none; telemetry_receiver=loopback-only");
return 0;

sealed class CaptureServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource stop = new(TimeSpan.FromSeconds(15));
    public readonly List<string> Bodies = [];
    public readonly List<string> Paths = [];
    public string Host { get; }
    public Task Completed { get; }
    public CaptureServer(int posthogStatus, int expectedRequests = 2)
    {
        listener.Start(); Host = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
        Completed = Task.Run(async () =>
        {
            for (int i = 0; i < expectedRequests; i++)
            {
                using var client = await listener.AcceptTcpClientAsync(stop.Token); var stream = client.GetStream();
                var header = new List<byte>(); byte[] one = new byte[1];
                while (header.Count < 16384)
                {
                    if (await stream.ReadAsync(one, stop.Token) == 0) throw new IOException("Incomplete HTTP request"); header.Add(one[0]);
                    if (header.Count >= 4 && Encoding.ASCII.GetString(header.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                }
                string text = Encoding.ASCII.GetString(header.ToArray()); string route = text.Split(' ')[1]; Paths.Add(route);
                string lengthLine = text.Split("\r\n").First(x => x.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                int length = int.Parse(lengthLine.Split(':')[1]); if (length > 65536) throw new IOException("Oversized test request");
                byte[] body = new byte[length]; await stream.ReadExactlyAsync(body, stop.Token); Bodies.Add(Encoding.UTF8.GetString(body));
                int status = route == "/capture/" ? posthogStatus : 200;
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Result\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), stop.Token);
            }
        });
    }
    public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); try { await Completed; } catch (OperationCanceledException) { } stop.Dispose(); }
}
