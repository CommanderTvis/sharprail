using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Resources;

/// <summary>The renderer a tab chose and that renderer's own view state. Choosing another renderer drops the state.</summary>
internal sealed record TabView(string? RendererId = null, object? ViewState = null);

/// <summary>
/// A file document's body: the ranked renderers as a view toggle (when there is a choice) over the selected renderer's
/// view. Views are built on first use and kept while the pane lives, so switching back restores the same control.
/// </summary>
internal sealed class ResourcePane : Grid, IDisposable
{
    private readonly IReadOnlyList<ResourceRenderer> candidates;
    private readonly Func<TabView> read;
    private readonly Action<TabView> write;
    private readonly Dictionary<string, Control> bodies = [];
    private readonly Dictionary<string, ToggleButton> toggles = [];
    private readonly ContentControl host = new();
    private ResourceDescriptor resource;
    private ResourceContent content;
    private readonly string tabId;
    private bool disposed;

    internal ResourcePane(IReadOnlyList<ResourceRenderer> candidates, ResourceDescriptor resource, string tabId, ResourceContent content,
        Func<TabView> read, Action<TabView> write)
    {
        this.candidates = candidates; this.resource = resource; this.tabId = tabId; this.content = content; this.read = read; this.write = write;
        Name = "ResourcePane";
        RowDefinitions = new RowDefinitions("Auto,*");
        if (candidates.Count >= 2)
        {
            var segments = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            foreach (var candidate in candidates)
            {
                var toggle = Segment(ToggleName(candidate.Id), candidate.Label);
                toggle.Click += (_, _) => Select(candidate.Id);
                toggles[candidate.Id] = toggle;
                segments.Children.Add(toggle);
            }
            Ui.Place(this, new Border
            {
                Name = "ResourceViewToggle",
                Height = 32,
                Background = Ui.Header,
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(12, 0),
                Child = segments
            });
        }
        Ui.Place(this, host, 1);
        Show(ResourceRegistry.Select(candidates, read().RendererId));
    }

    internal static string ToggleName(string rendererId) => "ViewToggle_" + rendererId[(rendererId.LastIndexOf('/') + 1)..];

    internal IEnumerable<string> Candidates => candidates.Select(candidate => candidate.Id);
    internal string Selected { get; private set; } = "";
    internal Control? Body => host.Content as Control;
    internal T? Find<T>() where T : class => bodies.Values.OfType<T>().FirstOrDefault();

    /// <summary>Chooses a renderer for the tab; its predecessor's view state does not carry over.</summary>
    internal void Select(string rendererId)
    {
        if (rendererId == Selected) { Check(); return; }
        if (candidates.FirstOrDefault(candidate => candidate.Id == rendererId) is not { } renderer) return;
        write(new(rendererId));
        Show(renderer);
    }

    private void Show(ResourceRenderer renderer)
    {
        Selected = renderer.Id;
        if (!bodies.TryGetValue(renderer.Id, out var body))
        {
            var state = read();
            body = renderer.View!(new(resource, tabId, content, state.RendererId is null || state.RendererId == renderer.Id ? state.ViewState : null,
                value => SaveViewState(renderer.Id, value)));
            bodies[renderer.Id] = body;
        }
        host.Content = body;
        Check();
    }

    private void Check()
    {
        foreach (var (id, toggle) in toggles) toggle.IsChecked = id == Selected;
    }

    // A late report from a renderer the tab has left must not overwrite its successor's state.
    private void SaveViewState(string rendererId, object? state)
    {
        var current = read();
        if (rendererId == Selected && (current.RendererId is null || current.RendererId == rendererId)) write(current with { ViewState = state });
    }

    /// <summary>Shows new content of the same shape, keeping every view that can take it and one the caller already reloaded.</summary>
    internal void Reload(ResourceDescriptor resource, ResourceContent content, Control? current = null)
    {
        this.resource = resource; this.content = content;
        foreach (var (id, body) in bodies.ToArray())
        {
            if (ReferenceEquals(body, current) || body is IResourceBody reloadable && reloadable.Reload(content)) continue;
            bodies.Remove(id);
            if (id == Selected) host.Content = null;
            (body as IDisposable)?.Dispose();
        }
        if (host.Content is null) Show(candidates.First(candidate => candidate.Id == Selected));
    }

    /// <summary>Drops every view except those <paramref name="keep"/> names; the selected one is rebuilt.</summary>
    internal void Reset(Func<Control, bool> keep)
    {
        Capture();
        foreach (var (id, body) in bodies.Where(pair => !keep(pair.Value)).ToArray())
        {
            bodies.Remove(id);
            if (id == Selected) host.Content = null;
            (body as IDisposable)?.Dispose();
        }
        if (host.Content is null) Show(candidates.First(candidate => candidate.Id == Selected));
    }

    private void Capture()
    {
        if (bodies.GetValueOrDefault(Selected) is IResourceBody body) SaveViewState(Selected, body.ViewState);
    }

    public void Dispose()
    {
        if (disposed) return;
        Capture();
        disposed = true;
        host.Content = null;
        foreach (var body in bodies.Values) (body as IDisposable)?.Dispose();
        bodies.Clear();
    }

    internal static ToggleButton Segment(string name, string label)
    {
        var button = new ToggleButton
        {
            Name = name,
            Content = label,
            Height = 20,
            Padding = new Thickness(8, 0),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Foreground = Ui.Muted
        };
        foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed", "PointerOver", "Pressed" })
        {
            button.Resources["ToggleButtonBackground" + state] = Ui.Hover;
            button.Resources["ToggleButtonForeground" + state] = Ui.TextBrush;
        }
        AutomationProperties.SetName(button, label);
        return button;
    }
}