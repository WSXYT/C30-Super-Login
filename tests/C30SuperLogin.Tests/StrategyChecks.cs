using C30SuperLogin.Core;

internal static class StrategyChecks
{
    public static async Task Run(Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetTempPath(), "C30SL-strategy-tests-" + Guid.NewGuid().ToString("N"));
        var store = new Store(root); var settings = new Settings { InstallRoot = Path.Combine(root, "NO_INSTALLATION"), AutoDetectInstall = false, LaunchC30 = false };
        string helper = Path.Combine(root, "fixture-helper-not-executable"); File.WriteAllText(helper, "never executed");
        var profile = new AccountBook(store, settings).Save(null, "LOCAL strategy fixture", "LOCAL_STRATEGY_USER", "LOCAL_STRATEGY_PASSWORD");
        async Task<(OperationResult Result, List<Diagnostic> Events)> Run(FakeBackend backend) {
            var events = new List<Diagnostic>(); var coordinator = new Coordinator(store, settings, helper, backend);
            coordinator.DiagnosticReady += events.Add;
            var result = await coordinator.RunAsync(profile.Id, false, false, CancellationToken.None);
            return (result, events.Where(x => x.Kind == DiagnosticKind.StrategyTransition).ToList());
        }
        var success = new FakeBackend(); var flow = await Run(success);
        check(flow.Result.Submitted && success.InjectionCalls == 1 && success.ControlCalls == 1, "unsubmitted injection falls through to page controls exactly once");
        check(flow.Events.Select(x => x.StrategyState).SequenceEqual(new[] { StrategyState.Started, StrategyState.NotSubmitted, StrategyState.Switching, StrategyState.Submitted }), "all fallback transitions are emitted in order, not only the final result");
        check(flow.Events[2].Method == LoginMethod.PageControls && flow.Events[2].PreviousMethod == LoginMethod.Injection && flow.Events[2].Outcome == DiagnosticOutcome.AccessDenied,
            "transition reports exact method, predecessor and typed injection failure reason");
        string serialized = Json.Serialize(flow.Events);
        check(!serialized.Contains("LOCAL_STRATEGY") && flow.Events.All(x => x.C30Version == "1.3.1610.0") && flow.Events.Select(x => x.StrategyStep).SequenceEqual(new[] { 1, 2, 3, 4 }), "strategy events carry C30 version, duration and sequence but no credentials");
        var failed = await Run(new() { Control = new(false) });
        check(!failed.Result.Submitted && failed.Events.Last().Method == LoginMethod.Manual && failed.Events.Last().StrategyState == StrategyState.ManualRequired, "both automatic methods failing requests manual handoff without fake success");
        var queued = new FakeBackend { Queued = true }; var uncertain = await Run(queued);
        check(uncertain.Result.Code == "receipt_timeout" && queued.ControlCalls == 0 && uncertain.Events.Last().StrategyState == StrategyState.SubmissionUncertain, "queued injection with missing receipt stops fallback and prevents double submission");
        var ambiguous = new FakeBackend { Control = new(false, true), TargetCount = 2 }; var controlTimeout = await Run(ambiguous);
        check(controlTimeout.Result.Code == "receipt_timeout" && ambiguous.ControlCalls == 1, "uncertain control submission stops before any second window is tried");
        check(File.ReadAllLines(Path.Combine(root, "strategies.jsonl")).Length >= 4, "strategy transitions are durably recorded locally before delivery");
        await using var receiver = new CaptureServer(200, 1);
        using var telemetry = new Telemetry(new() { PostHogHost = receiver.Host, PostHogKey = "fixture", SentryDsn = receiver.Host + "/1" }, allowTestLoopback: true);
        var delivery = await telemetry.PublishAsync(flow.Events[2]); await receiver.Completed;
        check(delivery[0].State == "sent" && delivery[1].State == "not_an_error" && receiver.Bodies[0].Contains("PageControls") && receiver.Bodies[0].Contains("AccessDenied"), "real loopback PostHog request includes fallback; transition does not create a Sentry error");
    }
    private sealed class FakeBackend : ILoginBackend
    {
        public int InjectionCalls, ControlCalls, TargetCount = 1;
        public bool Queued;
        public ControlLogin.Result Control = new(true);
        public IReadOnlyList<TargetProcess> Find(string root, Action<int> queryError) => Enumerable.Range(1, TargetCount).Select(x => new TargetProcess(x, new IntPtr(x), "1.3.1610.0", root)).ToArray();
        public bool IsSupported(TargetProcess target) => true;
        public Task<NativeResult> InjectAsync(TargetProcess target, string script, CancellationToken token) { InjectionCalls++; return Task.FromResult(new NativeResult(Queued ? "queued" : "access_denied", Queued ? 0 : 5, 1, Queued ? 1U : 0U, 0)); }
        public Task<ControlLogin.Result> ControlAsync(TargetProcess target, Credentials credentials, CancellationToken token) { ControlCalls++; return Task.FromResult(Control); }
    }
}
