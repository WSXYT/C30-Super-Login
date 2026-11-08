namespace C30SuperLogin.Core;

public static class PreLoginPolicy
{
    public static bool IsAutomatedLogin(string[] args) => args.Contains("login") &&
        Array.IndexOf(args, "--trigger") is int i && i >= 0 && i + 1 < args.Length && args[i + 1] == "classisland";
    public static bool ShouldPrompt(Settings settings, string[] args, bool skipPending = false) =>
        IsAutomatedLogin(args) && settings.PreLoginWarningSeconds > 0 && !settings.AutomationPaused && !skipPending;
}
