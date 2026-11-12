using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

using SharpRail.Plugins.Isolation;

namespace SharpRail.Plugins.Runner;

/// <summary>
/// The accessibility tree of one top-level of a surface (the surface, a popup or a dialog), read from the automation
/// peers Avalonia makes for its controls: each peer gets a number that is its own for as long as the peer is in the tree,
/// the nodes are sent as a change against what the client holds, and an accessibility action from the client acts on the
/// peer of a node the client was told of. On the UI thread only.
/// </summary>
internal sealed class SurfaceAutomationFeed(TopLevel top)
{
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(100);

    private readonly Dictionary<AutomationPeer, int> ids = [];
    private readonly Dictionary<int, AutomationPeer> peers = [];
    private Dictionary<int, SurfaceNode> sent = [];
    private int sentRoot;
    private bool told;
    private long readAt = long.MinValue;
    private int last;

    /// <summary>What changed in the tree since the client was last told, or null when nothing did or it was read a moment ago.</summary>
    public SurfaceAutomation? Changes(int surface, int popup)
    {
        var now = Environment.TickCount64;
        if (told && now - readAt < Every.TotalMilliseconds) return null;
        readAt = now;
        var (root, nodes) = Read();
        // Peers that left the tree lose their numbers.
        foreach (var gone in peers.Where(pair => !nodes.ContainsKey(pair.Key)).Select(pair => pair.Key).ToArray())
        {
            ids.Remove(peers[gone]);
            peers.Remove(gone);
        }
        var removed = sent.Keys.Where(id => !nodes.ContainsKey(id)).ToList();
        var changed = nodes.Values.Where(node => !sent.TryGetValue(node.Id, out var held) || !Same(held, node)).ToList();
        if (told && root == sentRoot && removed.Count == 0 && changed.Count == 0) return null;
        var reset = !told;
        (sent, sentRoot, told) = (nodes, root, true);
        return new SurfaceAutomation(surface, popup, root, reset, reset ? [.. nodes.Values] : changed, reset ? [] : removed);
    }

    /// <summary>The tree as it reads now, one tab-separated line per node, for the checks.</summary>
    public List<string> Lines()
    {
        var (_, nodes) = Read();
        return [.. nodes.Values.Select(node => string.Join('\t', node.Id, node.Role, node.Name, node.Value, node.AutomationId, (int)node.States,
            FormattableString.Invariant($"{node.X:0.##},{node.Y:0.##},{node.Width:0.##},{node.Height:0.##}"), string.Join(',', node.Children)))];
    }

    /// <summary>Applies an accessibility action to the node it names, if the client was told of it and the peer offers the action.</summary>
    public void Act(SurfaceInput input)
    {
        if (!told || !sent.ContainsKey(input.Node) || !peers.TryGetValue(input.Node, out var peer)) return;
        try
        {
            if (!peer.IsEnabled()) return;
            switch (input.Kind)
            {
                case SurfaceInputKind.Invoke: (peer as IInvokeProvider)?.Invoke(); break;
                case SurfaceInputKind.SetFocus: peer.SetFocus(); break;
                case SurfaceInputKind.SetValue:
                    if (peer is IValueProvider { IsReadOnly: false } value && input.Text.Length <= SurfaceLimits.MostNodeText) value.SetValue(input.Text);
                    break;
                case SurfaceInputKind.Expand: (peer as IExpandCollapseProvider)?.Expand(); break;
                case SurfaceInputKind.Collapse: (peer as IExpandCollapseProvider)?.Collapse(); break;
                case SurfaceInputKind.Toggle: (peer as IToggleProvider)?.Toggle(); break;
            }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Console.Error.WriteLine("[error] An accessibility action failed in the plugin: " + error.Message);
        }
    }

    // Breadth first from the top-level's own peer, within the caps on nodes and depth; a peer reached twice counts once.
    private (int Root, Dictionary<int, SurfaceNode> Nodes) Read()
    {
        var nodes = new Dictionary<int, SurfaceNode>();
        if (ControlAutomationPeer.CreatePeerForElement(top) is not { } rootPeer) return (0, nodes);
        var seen = new HashSet<AutomationPeer> { rootPeer };
        var queue = new Queue<(AutomationPeer Peer, int Depth)>();
        queue.Enqueue((rootPeer, 0));
        var root = Id(rootPeer);
        while (queue.TryDequeue(out var item))
        {
            var (peer, depth) = item;
            var children = new List<int>();
            if (depth < SurfaceLimits.MostNodeDepth)
                foreach (var child in Safe<IReadOnlyList<AutomationPeer>?>(() => peer.GetChildren(), null) ?? [])
                    if (nodes.Count + queue.Count + 1 < SurfaceLimits.MostNodes && seen.Add(child))
                    {
                        children.Add(Id(child));
                        queue.Enqueue((child, depth + 1));
                    }
            nodes[Id(peer)] = Describe(peer, children);
        }
        return (root, nodes);
    }

    private int Id(AutomationPeer peer)
    {
        if (ids.TryGetValue(peer, out var id)) return id;
        ids[peer] = id = ++last;
        peers[id] = peer;
        return id;
    }

    private SurfaceNode Describe(AutomationPeer peer, List<int> children)
    {
        var states = SurfaceNodeStates.None;
        if (Safe(peer.IsEnabled, false)) states |= SurfaceNodeStates.Enabled;
        if (Safe(peer.IsKeyboardFocusable, false)) states |= SurfaceNodeStates.Focusable;
        if (Safe(peer.HasKeyboardFocus, false)) states |= SurfaceNodeStates.Focused;
        if (Safe(peer.IsOffscreen, false)) states |= SurfaceNodeStates.Offscreen;
        var value = "";
        if (peer is IInvokeProvider) states |= SurfaceNodeStates.Invoke;
        if (peer is IToggleProvider toggle)
        {
            states |= SurfaceNodeStates.Toggle;
            switch (Safe(() => toggle.ToggleState, ToggleState.Off))
            {
                case ToggleState.On: states |= SurfaceNodeStates.Checked; break;
                case ToggleState.Indeterminate: states |= SurfaceNodeStates.Indeterminate; break;
            }
        }
        if (peer is IExpandCollapseProvider expand)
        {
            states |= SurfaceNodeStates.ExpandCollapse;
            switch (Safe(() => expand.ExpandCollapseState, ExpandCollapseState.LeafNode))
            {
                case ExpandCollapseState.Expanded: states |= SurfaceNodeStates.Expanded; break;
                case ExpandCollapseState.Collapsed: states |= SurfaceNodeStates.Collapsed; break;
            }
        }
        if (peer is IValueProvider text)
        {
            states |= SurfaceNodeStates.Value;
            if (Safe(() => text.IsReadOnly, true)) states |= SurfaceNodeStates.ReadOnly;
            value = Safe(() => text.Value, "") ?? "";
        }
        if (peer is ISelectionItemProvider item && Safe(() => item.IsSelected, false)) states |= SurfaceNodeStates.Selected;
        var bounds = Safe(peer.GetBoundingRectangle, default);
        double Side(double number) => double.IsFinite(number) ? Math.Clamp(number, -SurfaceLimits.MostSide, SurfaceLimits.MostSide) : 0;
        double Extent(double number) => double.IsFinite(number) ? Math.Clamp(number, 0, SurfaceLimits.MostSide) : 0;
        var role = (int)Safe(peer.GetAutomationControlType, AutomationControlType.Custom);
        return new SurfaceNode(Id(peer), role is >= 0 and <= SurfaceLimits.MostRole ? role : (int)AutomationControlType.Custom,
            Plain(Safe(peer.GetName, ""), SurfaceLimits.MostNodeText), Plain(value, SurfaceLimits.MostNodeText), Plain(Safe(peer.GetAutomationId, ""), SurfaceLimits.MostAutomationId),
            states, Side(bounds.X), Side(bounds.Y), Extent(bounds.Width), Extent(bounds.Height), children);
    }

    // Text as a client takes it: no control characters, within the cap and not cut inside a surrogate pair.
    private static string Plain(string? text, int most)
    {
        var plain = new string((text ?? "").Where(character => !char.IsControl(character)).ToArray());
        if (plain.Length <= most) return plain;
        return plain[..(char.IsHighSurrogate(plain[most - 1]) ? most - 1 : most)];
    }

    // A plugin's own peer may throw; the node then reads as the fallback rather than the surface stopping.
    private static T Safe<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch (Exception error) when (error is not OutOfMemoryException) { return fallback; }
    }

    private static bool Same(SurfaceNode a, SurfaceNode b) =>
        (a.Id, a.Role, a.Name, a.Value, a.AutomationId, a.States, a.X, a.Y, a.Width, a.Height) == (b.Id, b.Role, b.Name, b.Value, b.AutomationId, b.States, b.X, b.Y, b.Width, b.Height) &&
        a.Children.SequenceEqual(b.Children);
}