using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using FluentAvalonia.UI.Windowing;

namespace C30SuperLogin.Desktop;

internal sealed class PreLoginWindow : AppWindow
{
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly TextBlock countdown = new() { FontSize = 14 };
    private readonly int warningSeconds;
    private DateTimeOffset deadline;
    private bool completed, deferred;
    public event Action<bool>? Decision;
    public PreLoginWindow(int seconds)
    {
        warningSeconds = Math.Clamp(seconds, 1, 60);
        Title = "C30 登录提醒"; Width = 460; Height = 255; CanResize = false;
        ShowActivated = false; Topmost = true; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        TitleBar.Height = 36;
        var title = new TextBlock { Text = "即将自动登录 C30", FontSize = 22 };
        AutomationProperties.SetAutomationId(countdown, "WarningCountdown");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void Button(string text, string id, Action action, bool accent = false) {
            var button = new Button { Content = text }; if (accent) button.Classes.Add("accent");
            AutomationProperties.SetAutomationId(button, id); button.Click += (_, _) => action(); buttons.Children.Add(button);
        }
        Button("跳过本次", "WarningSkip", () => Complete(false));
        Button("推迟 1 分钟", "WarningDefer", () => { deferred = true; deadline = DateTimeOffset.UtcNow.AddMinutes(1); Hide(); });
        Button("立即登录", "WarningNow", () => Complete(true), true);
        Content = new StackPanel { Spacing = 16, Margin = new Thickness(24, 12, 24, 20), Children = { title, countdown, buttons } };
        Opened += (_, _) => { if (!deferred) deadline = DateTimeOffset.UtcNow.AddSeconds(warningSeconds); Tick(); timer.Start(); };
        timer.Tick += (_, _) => Tick();
        Closing += (_, e) => { if (!completed) { e.Cancel = true; Complete(false); } };
    }
    private void Tick()
    {
        if (completed) return;
        if (deferred) {
            if (DateTimeOffset.UtcNow < deadline) return;
            deferred = false; deadline = DateTimeOffset.UtcNow.AddSeconds(warningSeconds); Show();
        }
        int remaining = (int)Math.Ceiling((deadline - DateTimeOffset.UtcNow).TotalSeconds);
        countdown.Text = $"{Math.Max(remaining, 0)} 秒后继续；现在不方便，可以跳过或推迟。";
        if (remaining <= 0) Complete(true);
    }
    private void Complete(bool proceed)
    {
        if (completed) return;
        completed = true; timer.Stop(); Hide(); Decision?.Invoke(proceed);
    }
}
