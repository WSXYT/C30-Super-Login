using System.Windows.Automation;
namespace C30SuperLogin.Core;
public static class ControlLogin
{
    public sealed record Result(bool Submitted, bool Indeterminate = false);
    public static bool MayTry(bool probe, bool readingAccounts, bool scriptQueued, Credentials? credentials) =>
        !probe && !readingAccounts && !scriptQueued && credentials is { UseC30Saved: false, Password.Length: > 0 };
    public static async Task<bool> TryAsync(TargetProcess target, Credentials credentials, CancellationToken token) =>
        (await ExecuteAsync(target, credentials, token)).Submitted;
    public static async Task<Result> ExecuteAsync(TargetProcess target, Credentials credentials, CancellationToken token)
    {
        int submitStarted = 0;
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token); bounded.CancelAfter(2500);
        var work = Task.Run(() => {
            try {
                var root = AutomationElement.FromHandle(target.Window);
                if (root.Current.ProcessId != target.Pid) return new Result(false);
                var nodes = root.FindAll(TreeScope.Descendants, Condition.TrueCondition).Cast<AutomationElement>().Take(512).ToArray();
                AutomationElement? Single(string id) { var matches = nodes.Where(x => x.Current.AutomationId == id).ToArray(); return matches.Length == 1 ? matches[0] : null; }
                var username = Single("loginname"); var password = Single("loginpwd"); var submit = Single("loginbtn");
                if (username is null || password is null || submit is null || !password.Current.IsPassword ||
                    !username.TryGetCurrentPattern(ValuePattern.Pattern, out var userPattern) ||
                    !password.TryGetCurrentPattern(ValuePattern.Pattern, out var passPattern) ||
                    !submit.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) return new Result(false);
                bounded.Token.ThrowIfCancellationRequested(); ((ValuePattern)userPattern).SetValue(credentials.Username);
                bounded.Token.ThrowIfCancellationRequested(); ((ValuePattern)passPattern).SetValue(credentials.Password);
                // Page callbacks or concurrent edits may replace the username while the
                // password is being filled. Read back only the non-secret account field;
                // never read the password, and do not submit an unexpected account.
                if (!string.Equals(((ValuePattern)userPattern).Current.Value, credentials.Username, StringComparison.Ordinal)) return new Result(false);
                bounded.Token.ThrowIfCancellationRequested(); Interlocked.Exchange(ref submitStarted, 1); ((InvokePattern)invoke).Invoke(); return new Result(true);
            } catch { return new Result(false, Volatile.Read(ref submitStarted) != 0); }
        }, CancellationToken.None);
        try { return await work.WaitAsync(bounded.Token); } catch (OperationCanceledException) { return new(false, Volatile.Read(ref submitStarted) != 0); }
    }
}
