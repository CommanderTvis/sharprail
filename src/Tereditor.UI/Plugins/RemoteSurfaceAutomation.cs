using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.VisualTree;

using Tereditor.Plugins.Isolation;

namespace Tereditor.UI.Plugins;

/// <summary>
/// The accessibility tree of one top-level of a plugin's surface (the surface, a popup or a dialog) as the plugin's
/// process described it, shown through the app's own accessibility: the control that shows the top-level has a peer whose
/// children are the tree's root's children, and each node is a peer that answers from the node and sends the action a
/// client asks of it (invoke, focus, set value, expand, collapse, toggle) back to the plugin. On the UI thread only.
/// </summary>
internal sealed class RemoteTree(Control host, Action<SurfaceInputKind, int, string> send)
{
    private readonly Dictionary<int, SurfaceNode> nodes = [];
    private readonly Dictionary<int, NodePeer> peers = [];
    private int root;

    /// <summary>The nodes described now, for the checks.</summary>
    internal IReadOnlyCollection<SurfaceNode> Nodes => nodes.Values;

    /// <summary>Applies a change the host checked: nodes replace whole, removed ones go, a reset starts over.</summary>
    public void Apply(SurfaceAutomation change)
    {
        if (change.Reset)
        {
            nodes.Clear();
            peers.Clear();
        }
        foreach (var gone in change.Removed)
        {
            nodes.Remove(gone);
            peers.Remove(gone);
        }
        foreach (var node in change.Nodes)
        {
            nodes[node.Id] = node;
            // A node that gained or lost an action is a peer of another kind.
            if (peers.TryGetValue(node.Id, out var held) && held.GetType() != KindOf(node)) peers.Remove(node.Id);
        }
        root = change.Root;
        Changed();
    }

    /// <summary>The top-level closed: nothing is described any more.</summary>
    public void Clear()
    {
        if (nodes.Count == 0 && peers.Count == 0) return;
        nodes.Clear();
        peers.Clear();
        root = 0;
        Changed();
    }

    private void Changed() => (ControlAutomationPeer.FromElement(host) as HostPeer)?.Refresh();

    /// <summary>The peers of the root's children, which the host control's peer shows as its own children.</summary>
    internal IReadOnlyList<AutomationPeer> Children(AutomationPeer parent) =>
        nodes.TryGetValue(root, out var top) ? [.. top.Children.Where(nodes.ContainsKey).Select(child => Peer(child, parent))] : [];

    private NodePeer Peer(int id, AutomationPeer parent)
    {
        var node = nodes[id];
        if (!peers.TryGetValue(id, out var peer))
        {
            var kind = KindOf(node);
            peers[id] = peer = kind == typeof(InvokeExpandPeer) ? new InvokeExpandPeer(this, id) :
                kind == typeof(ValueExpandPeer) ? new ValueExpandPeer(this, id) :
                kind == typeof(ExpandPeer) ? new ExpandPeer(this, id) :
                kind == typeof(InvokePeer) ? new InvokePeer(this, id) :
                kind == typeof(TogglePeer) ? new TogglePeer(this, id) :
                kind == typeof(ValuePeer) ? new ValuePeer(this, id) : new NodePeer(this, id);
        }
        peer.Parent = parent;
        return peer;
    }

    // A peer offers the providers its node's actions ask for; a node with several actions gets the closest peer kind.
    private static Type KindOf(SurfaceNode node)
    {
        var (invoke, toggle, value, expand) = (node.States.HasFlag(SurfaceNodeStates.Invoke), node.States.HasFlag(SurfaceNodeStates.Toggle),
            node.States.HasFlag(SurfaceNodeStates.Value), node.States.HasFlag(SurfaceNodeStates.ExpandCollapse));
        return expand && invoke ? typeof(InvokeExpandPeer) : expand && value ? typeof(ValueExpandPeer) : expand ? typeof(ExpandPeer) :
            invoke ? typeof(InvokePeer) : toggle ? typeof(TogglePeer) : value ? typeof(ValuePeer) : typeof(NodePeer);
    }

    private void Act(SurfaceInputKind kind, int node, string text) => send(kind, node, text);

    private SurfaceNode? Get(int id) => nodes.GetValueOrDefault(id);

    private Rect Place(SurfaceNode node)
    {
        var origin = TopLevel.GetTopLevel(host) is { } top && host.TranslatePoint(default, top) is { } corner ? corner : default;
        return new Rect(origin.X + node.X, origin.Y + node.Y, node.Width, node.Height);
    }

    internal class NodePeer(RemoteTree tree, int id) : AutomationPeer
    {
        internal AutomationPeer? Parent { get; set; }
        protected RemoteTree Tree => tree;
        protected int Id => id;
        protected SurfaceNode? Node => tree.Get(id);
        protected bool Has(SurfaceNodeStates state) => Node?.States.HasFlag(state) == true;

        protected override string GetClassNameCore() => "PluginNode";
        protected override AutomationControlType GetAutomationControlTypeCore() => Node is { } node ? (AutomationControlType)node.Role : AutomationControlType.Custom;
        protected override string? GetNameCore() => Node?.Name;
        protected override string? GetAutomationIdCore() => Node?.AutomationId;
        protected override Rect GetBoundingRectangleCore() => Node is { } node ? tree.Place(node) : default;
        protected override bool IsEnabledCore() => Has(SurfaceNodeStates.Enabled);
        protected override bool IsKeyboardFocusableCore() => Has(SurfaceNodeStates.Focusable);
        protected override bool HasKeyboardFocusCore() => Has(SurfaceNodeStates.Focused);
        protected override bool IsOffscreenCore() => Has(SurfaceNodeStates.Offscreen);
        protected override void SetFocusCore() => tree.Act(SurfaceInputKind.SetFocus, id, "");
        protected override AutomationPeer? GetParentCore() => Parent;
        protected override IReadOnlyList<AutomationPeer> GetOrCreateChildrenCore() =>
            Node is { } node ? [.. node.Children.Where(tree.nodes.ContainsKey).Select(child => tree.Peer(child, this))] : [];
        protected override bool TrySetParent(AutomationPeer? parent)
        {
            Parent = parent;
            return true;
        }
        protected override string? GetAcceleratorKeyCore() => null;
        protected override string? GetAccessKeyCore() => null;
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override void BringIntoViewCore() { }
        protected override bool ShowContextMenuCore() => false;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
    }

    private sealed class InvokePeer(RemoteTree tree, int id) : NodePeer(tree, id), IInvokeProvider
    {
        public void Invoke() => Tree.Act(SurfaceInputKind.Invoke, Id, "");
    }

    private sealed class TogglePeer(RemoteTree tree, int id) : NodePeer(tree, id), IToggleProvider
    {
        public ToggleState ToggleState => Has(SurfaceNodeStates.Checked) ? ToggleState.On : Has(SurfaceNodeStates.Indeterminate) ? ToggleState.Indeterminate : ToggleState.Off;
        public void Toggle() => Tree.Act(SurfaceInputKind.Toggle, Id, "");
    }

    private sealed class ValuePeer(RemoteTree tree, int id) : NodePeer(tree, id), IValueProvider
    {
        public bool IsReadOnly => Has(SurfaceNodeStates.ReadOnly);
        public string? Value => Node?.Value;
        public void SetValue(string? value) => Tree.Act(SurfaceInputKind.SetValue, Id, value ?? "");
    }

    private class ExpandPeer(RemoteTree tree, int id) : NodePeer(tree, id), IExpandCollapseProvider
    {
        public ExpandCollapseState ExpandCollapseState => Has(SurfaceNodeStates.Expanded) ? ExpandCollapseState.Expanded :
            Has(SurfaceNodeStates.Collapsed) ? ExpandCollapseState.Collapsed : ExpandCollapseState.LeafNode;
        public bool ShowsMenu => false;
        public void Expand() => Tree.Act(SurfaceInputKind.Expand, Id, "");
        public void Collapse() => Tree.Act(SurfaceInputKind.Collapse, Id, "");
    }

    private sealed class InvokeExpandPeer(RemoteTree tree, int id) : ExpandPeer(tree, id), IInvokeProvider
    {
        public void Invoke() => Tree.Act(SurfaceInputKind.Invoke, Id, "");
    }

    private sealed class ValueExpandPeer(RemoteTree tree, int id) : ExpandPeer(tree, id), IValueProvider
    {
        public bool IsReadOnly => Has(SurfaceNodeStates.ReadOnly);
        public string? Value => Node?.Value;
        public void SetValue(string? value) => Tree.Act(SurfaceInputKind.SetValue, Id, value ?? "");
    }

    /// <summary>The peer of the control that shows a top-level: a pane whose children are the plugin's nodes.</summary>
    internal sealed class HostPeer(Control owner, RemoteTree tree, Func<string> name) : ControlAutomationPeer(owner)
    {
        internal void Refresh() => InvalidateChildren();

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Pane;
        protected override string? GetNameCore() => name();
        protected override IReadOnlyList<AutomationPeer> GetChildrenCore()
        {
            var own = base.GetChildrenCore() ?? [];
            return [.. own, .. tree.Children(this)];
        }
    }
}