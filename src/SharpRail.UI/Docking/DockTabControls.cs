using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Docking;

/// <summary>
/// One group's body. It is also the region's error boundary: a body that fails to build, measure or arrange
/// is replaced by a notice here, so sibling groups and the window carry on.
/// </summary>
internal sealed class DockPanel : Border
{
    /// <summary>Mounts the body <paramref name="build"/> returns; <paramref name="keep"/> leaves an unchanged body attached.</summary>
    internal void Mount(Func<Control> build, bool keep = false)
    {
        Control content;
        try { content = build(); }
        catch (Exception error) { Fail(error); return; }
        if (keep && ReferenceEquals(Child, content)) return;
        Child = null; Child = content;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        try { return base.MeasureOverride(availableSize); }
        catch (Exception error) { Fail(error); return base.MeasureOverride(availableSize); }
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        try { return base.ArrangeOverride(finalSize); }
        catch (Exception error) { Fail(error); InvalidateMeasure(); return finalSize; }
    }

    private void Fail(Exception error)
    {
        Console.Error.WriteLine(error);
        var message = Ui.Text(error.Message.Length > 0 ? error.Message : "An unexpected error occurred while showing this view.", size: 12);
        message.TextWrapping = TextWrapping.Wrap; message.TextAlignment = TextAlignment.Center;
        message.MaxWidth = 448;
        var notice = new StackPanel
        {
            Name = "RegionError",
            Spacing = 8,
            Margin = new Thickness(16),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Ui.Icon("alertWarning", Ui.Danger, 24), Ui.Text("This view hit an error", Ui.TextBrush), message }
        };
        foreach (var child in notice.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
        AutomationProperties.SetLiveSetting(notice, AutomationLiveSetting.Assertive);
        Child = null; Child = notice;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new PanelPeer(this);
    private sealed class PanelPeer(DockPanel owner) : ControlAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
    }
}

internal sealed class DockTabStrip(LayoutSession session, string group) : StackPanel
{
    internal string? Selected => session.Selected(group)?.Id;
    protected override AutomationPeer OnCreateAutomationPeer() => new StripPeer(this);

    private sealed class StripPeer(DockTabStrip owner) : ControlAutomationPeer(owner), ISelectionProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Tab;
        public bool CanSelectMultiple => false;
        public bool IsSelectionRequired => owner.GetLogicalDescendants().OfType<DockTabButton>().Any();
        public IReadOnlyList<AutomationPeer> GetSelection() => owner.GetLogicalDescendants().OfType<DockTabButton>()
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