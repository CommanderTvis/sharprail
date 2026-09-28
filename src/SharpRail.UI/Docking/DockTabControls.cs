using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace SharpRail.UI.Docking;

internal sealed class DockTabStrip(LayoutSession session, string group) : StackPanel
{
    internal string? Selected => session.Selected(group)?.Id;
    protected override AutomationPeer OnCreateAutomationPeer() => new StripPeer(this);

    private sealed class StripPeer(DockTabStrip owner) : ControlAutomationPeer(owner), ISelectionProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;
        public bool CanSelectMultiple => false;
        public bool IsSelectionRequired => owner.Children.Count > 0;
        public IReadOnlyList<AutomationPeer> GetSelection() => owner.Children.OfType<DockTabButton>()
            .Where(button => button.IsSelected).Select(GetOrCreate).ToArray();
    }
}

internal sealed class DockTabButton(DockTabStrip strip, string id, Action select) : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    internal bool IsSelected => strip.Selected == id;
    private DockTabStrip Strip => strip;
    private void SelectTab() => select();
    protected override AutomationPeer OnCreateAutomationPeer() => new TabPeer(this);

    private sealed class TabPeer(DockTabButton owner) : ButtonAutomationPeer(owner), ISelectionItemProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.TabItem;
        public bool IsSelected => owner.IsSelected;
        public ISelectionProvider SelectionContainer => (ISelectionProvider)GetOrCreate(owner.Strip);
        public void Select() => owner.SelectTab();
        public void AddToSelection() => Select();
        public void RemoveFromSelection()
        {
            if (IsSelected) throw new InvalidOperationException("A pane must retain its selected tab.");
        }
    }
}
