using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.UI.Plugins;
using SharpRail.UI.State;

namespace SharpRail.UI.Panels;

/// <summary>
/// Settings › Plugins: one row per roster entry with its switch, Retry on a failed row, Rescan, and the extra plugin
/// roots. Rows are keyed by plugin id and updated in place when the roster changes; nothing changes locally until
/// the host's snapshot arrives.
/// </summary>
public sealed class PluginsSettings : StackPanel
{
    private readonly SharedState state;
    private readonly IPluginService? service;
    private readonly Window owner;
    private readonly StackPanel rows = new() { Name = "PluginRows", Spacing = 8 };
    private readonly StackPanel paths = new() { Name = "PluginPaths", Spacing = 4 };
    private readonly TextBlock error = new() { Name = "PluginsError", IsVisible = false, TextWrapping = TextWrapping.Wrap, Foreground = Ui.Danger, FontSize = 12 };
    private readonly Dictionary<string, (Border Row, string Signature)> rendered = [];
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
        Children.Add(rows);
        Children.Add(PathsEditor());
        Children.Add(error);
        Refresh();
        state.Changed += Changed;
        DetachedFromVisualTree += (_, _) => state.Changed -= Changed;
    }

    private void Changed(HostState previous, HostState next) => Refresh();

    /// <summary>Updates rows in place: a row whose roster entry is unchanged keeps its control, focus and pointer.</summary>
    public void Refresh()
    {
        var roster = state.Current.Plugins;
        foreach (var id in rendered.Keys.Where(id => roster.All(entry => entry.Id != id)).ToArray())
        {
            rows.Children.Remove(rendered[id].Row);
            rendered.Remove(id);
        }
        for (var index = 0; index < roster.Count; index++)
        {
            var entry = roster[index];
            var signature = Signature(entry, roster);
            if (!rendered.TryGetValue(entry.Id, out var current) || current.Signature != signature)
            {
                var row = Row(entry, roster);
                if (current.Row is not null) rows.Children.Remove(current.Row);
                rendered[entry.Id] = (row, signature);
                current = rendered[entry.Id];
            }
            if (rows.Children.IndexOf(current.Row) != index)
            {
                rows.Children.Remove(current.Row);
                rows.Children.Insert(Math.Min(index, rows.Children.Count), current.Row);
            }
        }
        if (rows.Children.Count == 0 && !rendered.ContainsKey(""))
        {
            var none = new Border { Child = Ui.Text("No plugins are installed.", Ui.Hint, 12) };
            rendered[""] = (none, "");
            rows.Children.Add(none);
        }
        else if (roster.Count > 0 && rendered.Remove("", out var empty)) rows.Children.Remove(empty.Row);
        RefreshPaths();
    }

    private static string Signature(PluginRosterEntry entry, IReadOnlyList<PluginRosterEntry> roster) =>
        System.Text.Json.JsonSerializer.Serialize(entry, PluginJson.Options) + "\n" +
        string.Join(",", PluginRegistry.ActiveDependents(entry.Id, roster).Select(id => Label(id, roster)));

    private static string Label(string id, IReadOnlyList<PluginRosterEntry> roster) => roster.FirstOrDefault(entry => entry.Id == id)?.Label ?? id;

    private Border Row(PluginRosterEntry entry, IReadOnlyList<PluginRosterEntry> roster)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12 };
        var icon = PluginIcons.Resolve(entry.Icon, entry, Ui.Muted, 16);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, 2, 0, 0);
        Ui.Place(grid, icon);
        var text = new StackPanel { Spacing = 2 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(Ui.Text(entry.Label, Ui.TextBrush));
        if (entry.Origin == PluginOrigin.External)
        {
            var version = Ui.Text("v" + entry.Version, Ui.Hint, 12);
            version.Name = "PluginVersion";
            title.Children.Add(version);
        }
        var origin = new Border
        {
            Name = "PluginOrigin",
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 0),
            Child = Ui.Text(entry.Origin == PluginOrigin.External ? "external" : "builtin", Ui.Hint, 11)
        };
        title.Children.Add(origin);
        var status = Ui.Text(entry.Status.ToString().ToLowerInvariant(), entry.Status is PluginStatus.Failed or PluginStatus.Refused ? Ui.Danger : Ui.Hint, 11);
        status.Name = "PluginStatus";
        title.Children.Add(status);
        text.Children.Add(title);
        if (entry.Description is { Length: > 0 } description) text.Children.Add(Wrapped(description, Ui.TextBrush, "PluginDescription"));
        if (Summary(entry) is { } summary) text.Children.Add(Wrapped(summary, Ui.Muted, "PluginContributions"));
        if (entry.Status is PluginStatus.Failed or PluginStatus.Refused)
            text.Children.Add(Wrapped(entry.Reason ?? entry.Status.ToString().ToLowerInvariant(), Ui.Danger, "PluginReason"));
        var enabled = entry.Status != PluginStatus.Disabled;
        var dependents = enabled ? PluginRegistry.ActiveDependents(entry.Id, roster) : [];
        if (dependents.Count > 0)
            text.Children.Add(Wrapped($"Also used by {string.Join(", ", dependents.Select(id => Label(id, roster)))}. Disabling turns them off too.", Ui.Muted, "PluginDependents"));
        Ui.Place(grid, text, 0, 1);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        if (entry.Status == PluginStatus.Failed)
        {
            var retry = Ui.Button("Retry", () => _ = Run($"Couldn't retry {entry.Label}", async () => { if (service is not null) await service.RetryAsync(entry.Id); }));
            retry.Name = "PluginRetry";
            actions.Children.Add(retry);
        }
        var toggle = new ToggleSwitch { Name = "PluginToggle", IsChecked = enabled, OnContent = null, OffContent = null, MinWidth = 0 };
        AutomationProperties.SetName(toggle, entry.Label + " enabled");
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (toggle.IsChecked == enabled) return;
            toggle.IsChecked = enabled;
            _ = Toggle(entry);
        };
        actions.Children.Add(toggle);
        Ui.Place(grid, actions, 0, 2);
        return new Border
        {
            Name = "PluginRow_" + entry.Id,
            Tag = entry.Id,
            Child = grid,
            Padding = new Thickness(12, 8),
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            BorderBrush = Ui.BorderBrush,
            Background = Ui.Elevated
        };
    }

    private static TextBlock Wrapped(string value, IBrush color, string name)
    {
        var text = Ui.Text(value, color, 12);
        text.Name = name;
        text.TextWrapping = TextWrapping.Wrap;
        text.TextTrimming = TextTrimming.None;
        return text;
    }

    private static string? Summary(PluginRosterEntry entry)
    {
        var parts = new List<string>();
        var tools = entry.Contributes.SideTools.Count;
        if (tools > 0) parts.Add($"{tools} side tool{(tools == 1 ? "" : "s")}");
        var viewers = entry.Contributes.FileViewers.Count;
        if (viewers > 0) parts.Add($"{viewers} file viewer{(viewers == 1 ? "" : "s")}");
        return parts.Count > 0 ? string.Join(", ", parts) : null;
    }

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