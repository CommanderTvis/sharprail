using System.Collections.ObjectModel;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.UI.Plugins;
using SharpRail.UI.State;

namespace SharpRail.UI.Panels;

/// <summary>
/// Settings › Plugins: one card per roster entry, grouped under a heading per category, with its switch, Retry on a failed
/// card, Rescan, and the extra plugin roots. The card list is virtualized; cards are keyed by plugin id and updated in place when the roster changes;
/// nothing changes locally until the host's snapshot arrives.
/// </summary>
public sealed class PluginsSettings : StackPanel
{
    private readonly SharedState state;
    private readonly IPluginService? service;
    private readonly Window owner;
    private readonly ObservableCollection<PluginCardModel> cards = [];
    private readonly Dictionary<string, PluginCardModel> models = new(StringComparer.Ordinal);
    private readonly PluginCardList rows;
    private readonly Control none = new Border { Child = Ui.Text("No plugins are installed.", Ui.Hint, 12), IsVisible = false };
    private readonly StackPanel paths = new() { Name = "PluginPaths", Spacing = 4 };
    private readonly TextBlock error = new() { Name = "PluginsError", IsVisible = false, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Danger, FontSize = 12 };
    private string pathsSignature = "\0";

    public PluginsSettings(SharedState state, IPluginService? service, Window owner)
    {
        this.state = state; this.service = service; this.owner = owner;
        Name = "PluginsSettings";
        Spacing = 16;
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        var intro = new StackPanel { Spacing = 4 };
        intro.Children.Add(new TextBlock { Text = "Plugins", Classes = { "settings-heading" } });
        intro.Children.Add(new TextBlock
        {
            Text = state.Current.Plugins.Count == 0 && service is null ? "This host has no plugin runtime." :
                "Turn plugins on or off, add directories for external ones, and retry a failed load. Plugins run on the host this window is connected to.",
            Classes = { "settings-description" }
        });
        Ui.Place(header, intro);
        var rescan = Ui.Button("Rescan", () => _ = Run("Couldn't rescan plugin directories", async () => { if (service is not null) await service.RescanAsync(); }), "refresh");
        rescan.Name = "PluginsRescan";
        rescan.VerticalAlignment = VerticalAlignment.Top;
        rescan.IsEnabled = service is not null;
        Ui.Place(header, rescan, 0, 1);
        Children.Add(header);
        // A long roster (one plugin per language server) realizes only the cards near the viewport and recycles them.
        rows = new PluginCardList(() => new PluginCard(
            entry => _ = Run($"Couldn't retry {entry.Label}", async () => { if (this.service is not null) await this.service.RetryAsync(entry.Id); }),
            entry => _ = Toggle(entry)))
        {
            Name = "PluginRows",
            ItemsSource = cards,
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel())
        };
        Children.Add(rows);
        Children.Add(none);
        Children.Add(PathsEditor());
        Children.Add(error);
        Refresh();
        state.Changed += Changed;
        DetachedFromVisualTree += (_, _) => state.Changed -= Changed;
    }

    private void Changed(HostState previous, HostState next)
    {
        // Terminal activity and other unrelated broadcasts leave the roster and the roots as they were.
        if (previous.Plugins.SequenceEqual(next.Plugins) && previous.PluginPaths.SequenceEqual(next.PluginPaths)) return;
        Refresh();
    }

    /// <summary>
    /// Updates cards in place: each plugin keeps one model for the page's life and the list moves, adds or removes only
    /// what changed, so a realized card keeps its control, focus, pointer and the scroll position. Cards sit under one
    /// heading per manifest category: <see cref="PluginCategories.Order"/> first, other names alphabetically, "Other" last,
    /// and plugins by label inside each.
    /// </summary>
    public void Refresh()
    {
        var roster = state.Current.Plugins;
        var desired = new List<PluginCardModel>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in roster.GroupBy(Category).OrderBy(group => CategoryRank(group.Key)).ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = true;
            foreach (var entry in group.OrderBy(entry => entry.Label, StringComparer.OrdinalIgnoreCase).ThenBy(entry => entry.Id, StringComparer.Ordinal))
            {
                ids.Add(entry.Id);
                var heading = first ? group.Key : null;
                first = false;
                var dependents = entry.Status != PluginStatus.Disabled ? PluginRegistry.ActiveDependents(entry.Id, roster).Select(id => Label(id, roster)).ToArray() : [];
                var signature = System.Text.Json.JsonSerializer.Serialize(entry, PluginJson.Options) + "\n" + string.Join(",", dependents) + "\n" + heading;
                if (models.TryGetValue(entry.Id, out var model)) model.Update(entry, dependents, signature, heading);
                else models[entry.Id] = model = new PluginCardModel(entry, dependents, signature, heading);
                desired.Add(model);
            }
        }
        foreach (var id in models.Keys.Where(id => !ids.Contains(id)).ToArray()) models.Remove(id);
        for (var index = cards.Count - 1; index >= 0; index--)
            if (!ids.Contains(cards[index].Entry.Id)) cards.RemoveAt(index);
        for (var index = 0; index < desired.Count; index++)
        {
            if (index < cards.Count && ReferenceEquals(cards[index], desired[index])) continue;
            var at = cards.IndexOf(desired[index]);
            if (at < 0) cards.Insert(index, desired[index]);
            else cards.Move(at, index);
        }
        none.IsVisible = roster.Count == 0;
        RefreshPaths();
    }

    private static string Category(PluginRosterEntry entry) => entry.Category is { Length: > 0 } category ? category.Trim() : PluginCategories.Other;

    private static int CategoryRank(string category)
    {
        var index = PluginCategories.Order.ToList().IndexOf(category);
        return index >= 0 ? index : category == PluginCategories.Other ? int.MaxValue : PluginCategories.Order.Count;
    }

    /// <summary>The list as shown, top to bottom: <c>PluginCategory_&lt;name&gt;</c> for a heading and <c>PluginRow_&lt;id&gt;</c> for a card.</summary>
    internal IReadOnlyList<string> Listing() => [.. cards.SelectMany(model => model.Heading is { } heading ? new[] { "PluginCategory_" + heading, "PluginRow_" + model.Entry.Id } : ["PluginRow_" + model.Entry.Id])];

    /// <summary>The card showing <paramref name="id"/>, scrolled into view so the virtualized list realizes it; null when the roster has no such plugin.</summary>
    public Border? Card(string id)
    {
        var index = cards.ToList().FindIndex(model => model.Entry.Id == id);
        if (index < 0) return null;
        rows.ScrollIntoView(index);
        (TopLevel.GetTopLevel(this) as Layoutable ?? this).UpdateLayout();
        return rows.GetRealizedContainers().OfType<PluginCard>().FirstOrDefault(card => card.Model?.Entry.Id == id);
    }

    private static string Label(string id, IReadOnlyList<PluginRosterEntry> roster) => roster.FirstOrDefault(entry => entry.Id == id)?.Label ?? id;

    /// <summary>Disabling is immediate (the host cascades dependents off); enabling asks first when disabled dependencies must come along.</summary>
    public async Task Toggle(PluginRosterEntry entry)
    {
        var roster = state.Current.Plugins;
        if (entry.Status != PluginStatus.Disabled)
        {
            await Run($"Couldn't disable {entry.Label}", () => state.ChangeAsync(HostStateChange.PluginEnabled(entry.Id, false)));
            return;
        }
        var dependencies = PluginRegistry.ToEnable(entry.Id, roster);
        if (dependencies.Count > 0)
        {
            var names = string.Join(", ", dependencies.Select(id => Label(id, roster)));
            if (!await Dialogs.Confirm(owner, $"Also turn on {names}?", $"{entry.Label} depends on {names}, currently off.", "Enable", "PluginEnableDependencies"))
                return;
        }
        await Run($"Couldn't enable {entry.Label}", () => state.ChangeAsync([.. new[] { entry.Id }.Concat(dependencies).Select(id => HostStateChange.PluginEnabled(id, true))]));
    }

    private Control PathsEditor()
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "External plugin directories", Classes = { "settings-label" } });
        panel.Children.Add(paths);
        var draft = new TextBox { Name = "PluginPathInput", PlaceholderText = "/absolute/path/to/plugins", Width = 360, FontFamily = Ui.CodeFont };
        var add = Ui.Button("Add", () => { }, "add");
        add.Name = "PluginPathAdd";
        void Add()
        {
            var path = draft.Text?.Trim() ?? "";
            if (path.Length == 0 || state.Current.PluginPaths.Contains(path)) return;
            _ = SavePaths([.. state.Current.PluginPaths, path]);
            draft.Text = "";
        }
        add.Click += (_, _) => Add();
        draft.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { Add(); e.Handled = true; } };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(draft); row.Children.Add(add);
        panel.Children.Add(row);
        return panel;
    }

    private void RefreshPaths()
    {
        var current = state.Current.PluginPaths;
        var signature = string.Join("\n", current);
        if (signature == pathsSignature) return;
        pathsSignature = signature;
        paths.Children.Clear();
        foreach (var path in current)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            var label = Ui.Text(path, Ui.TextBrush, 12);
            label.FontFamily = Ui.CodeFont;
            Ui.Place(line, label);
            var remove = Ui.IconButton("trash", "Remove " + path, () => _ = SavePaths([.. state.Current.PluginPaths.Where(candidate => candidate != path)]));
            remove.Name = "PluginPathRemove";
            Ui.Place(line, remove, 0, 1);
            paths.Children.Add(new Border { Name = "PluginPath", Child = line, Padding = new Thickness(8, 0), BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) });
        }
    }

    private Task SavePaths(IReadOnlyList<string> next) => Run("Couldn't update the plugin paths", () => state.ChangeAsync(HostStateChange.PluginPaths(next)));

    private async Task Run(string failure, Func<Task> action)
    {
        try { await action(); error.IsVisible = false; }
        catch (Exception problem) when (problem is not OperationCanceledException)
        {
            error.Text = failure + ": " + problem.Message;
            error.IsVisible = true;
        }
    }
}

/// <summary>The cards are the list's own containers, so the virtualizing panel recycles a whole card rather than rebuilding its template.</summary>
internal sealed class PluginCardList(Func<PluginCard> create) : ItemsControl
{
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) => NeedsContainer<PluginCard>(item, out recycleKey);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => create();
}
