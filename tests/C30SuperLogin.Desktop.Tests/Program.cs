using C30SuperLogin.Core;
using C30SuperLogin.Desktop;
using Velopack;
using Velopack.Locators;
using Velopack.Sources;

if (args.Contains("--describe")) {
    foreach (var ctor in typeof(TestVelopackLocator).GetConstructors()) Console.WriteLine(string.Join(", ", ctor.GetParameters().Select(x => x.ParameterType.Name + " " + x.Name + "=" + x.DefaultValue)));
    return 0;
}
int passed = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAIL " + label); passed++; Console.WriteLine("PASS " + label); }
string root = Path.Combine(Path.GetTempPath(), "C30SL-updates-tests-" + Guid.NewGuid().ToString("N"));
var store = new Store(root);
var settings = new Settings();
Environment.SetEnvironmentVariable("C30_UPDATE_URL", null);
var client = new AppUpdates(store, settings);
await client.CheckAsync(); Check(client.State == "not_configured", "empty feed reports unconfigured, not fake latest-version success");
settings.Updates.AutoCheck = false;
await client.CheckAsync(); Check(client.State == "disabled", "automatic update check respects persisted opt-out");
settings.Updates.FeedUrl = "http://127.0.0.1/untrusted";
await client.CheckAsync(true); Check(client.State == "invalid_source", "production updater rejects plain HTTP before any network request");
Check(!AppUpdates.IsValidFeed("https://user:password@example.test/updates") && !AppUpdates.IsValidFeed("https://example.test/feed#fragment") && AppUpdates.IsValidFeed("https://example.test/updates"), "only HTTPS deployment feeds without embedded credentials or fragments are accepted");
Check(!client.ApplyAndRestart(false) && client.State == "not_ready", "no restart without a fully downloaded update");
Check(!client.ApplyAndRestart(true) && client.State == "busy", "local login activity blocks restart before updater invocation");
await RuntimeChecks.Run(Check);
if (args.Length == 2 && args[0] == "--local-feed") {
    string feed = Path.GetFullPath(args[1]);
    var locator = new TestVelopackLocator("C30SuperLogin", "0.0.1", Path.Combine(root, "packages"));
    var manager = new UpdateManager(new SimpleFileSource(new DirectoryInfo(feed)), locator: locator);
    var local = new AppUpdates(store, new Settings(), manager);
    await local.CheckAsync(true); Check(local.State == "available", "official SDK TestVelopackLocator detects a newer package from the local feed");
    await local.DownloadAsync(); Check(local.State == "downloaded", "official SDK downloads and verifies the local update without installing it");
    using (var gate = new FileStream(Path.Combine(root, "operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        Check(!local.ApplyAndRestart(false) && local.State == "busy", "another process holding the login lock prevents update apply");
    using (var gate = new FileStream(Path.Combine(root, "prelogin.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        Check(!local.ApplyAndRestart(false) && local.State == "busy", "a pending pre-login warning or deferral prevents update apply");
}
Console.WriteLine($"RESULT {passed}/{passed} PASS; external_uploads=none; actual_install_or_restart=not_attempted");
return 0;
