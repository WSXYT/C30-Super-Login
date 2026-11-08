using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Threading;
using FluentAvalonia.UI.Controls;

namespace C30SuperLogin.Desktop;

// FluentAvalonia 2.5 uses a ListBoxItem peer even though NavigationView owns no
// ListBox selection container. Expose a real navigation action to accessibility.
internal sealed class NavigationItem : NavigationViewItem
{
    public required Action Navigate { get; init; }
    protected override Type StyleKeyOverride => typeof(NavigationViewItem);
    protected override AutomationPeer OnCreateAutomationPeer() => new NavigationPeer(this);
    private sealed class NavigationPeer(NavigationItem owner) : ControlAutomationPeer(owner), IInvokeProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        public void Invoke() => Dispatcher.UIThread.Post(owner.Navigate);
    }
}
