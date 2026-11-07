using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Threading;
using Wpf.Ui.Controls;

namespace Contain.Desktop.Controls;

/// <summary>
/// A WPF UI navigation item whose Click handler performs an application action.
/// The library's 4.3.0 selection peer requires TargetPageType; these entries use
/// the shell's existing page instances or a file dialog instead. Expose the same
/// Click path to assistive technology that mouse and keyboard activation use.
/// </summary>
public sealed class ActionNavigationItem : NavigationViewItem
{
    protected override AutomationPeer OnCreateAutomationPeer() => new ActionNavigationItemPeer(this);

    private void InvokeAction()
    {
        // An accepted request can be queued just before the shell disables
        // navigation for shutdown. Do not execute an action after that boundary.
        if (IsEnabled) OnClick();
    }

    private sealed class ActionNavigationItemPeer(ActionNavigationItem owner)
        : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(ActionNavigationItem);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;

        protected override string GetNameCore()
        {
            var name = base.GetNameCore();
            return string.IsNullOrEmpty(name) ? owner.Content as string ?? "" : name;
        }

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);

        void IInvokeProvider.Invoke()
        {
            if (owner.Dispatcher.HasShutdownStarted) throw new ElementNotAvailableException();
            owner.Dispatcher.Invoke(() =>
            {
                if (!owner.IsEnabled) throw new ElementNotEnabledException();
                // Return to UI Automation before the handler may show a native
                // modal chooser. The queued action uses the normal protected
                // OnClick implementation, not the library's null-target route.
                _ = owner.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(owner.InvokeAction));
            });
        }
    }
}
