using System.Globalization;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>
/// The Claude Code side tool: what a Claude session in this workspace loads (context, settings, capabilities) with the
/// provenance of each part, and the account. It describes one terminal session, the first shown tab running Claude, else
/// what a new session at the workspace root would load. Every change is composed, scoped, reviewed as a diff, then written.
/// </summary>
internal sealed class ClaudeConfigPanel : DockPanel
{
    private enum Surface { Context, Settings, Capabilities, Account }

    private readonly IPluginUIContext context;
    private readonly ClaudeGlyph glyph;
    private readonly string workspaceId;
    private readonly StackPanel scopeBlock = new() { Name = "ClaudeConfigScope", Margin = new Thickness(8, 0, 8, 4) };
    private readonly StackPanel notices = new();
    private readonly ContentControl body = new() { HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel switcher = new() { Orientation = Orientation.Horizontal, Spacing = 8 };
    private Surface surface = Surface.Context;
    private ClaudeConfigSnapshot? snapshot;
    private IReadOnlyList<ClaudeCapability> reached = [];
    private string? error;
    private ClaudeAccountSurface? account;
    private int generation;
    private TerminalTabInfo? session;
    private TerminalTabInfo? activeTab;
    private string settingsQuery = "";
    private IDisposable? watch;
    private string snapshotKey = "", reachedKey = "";
    private (Surface, string, string, string?, string)? rendered;
    private (Surface, string?, string?, string?, string?)? renderedScope;

    public ClaudeConfigPanel(IPluginUIContext context, ClaudeGlyph glyph, string workspaceId)
    {
        this.context = context; this.glyph = glyph; this.workspaceId = workspaceId;
        Name = "ClaudeConfigPanel";
        Background = Ui.Sidebar;
        var header = new DockPanel { Margin = new Thickness(8, 4) };
        var refresh = Ui.IconButton("refresh", "Re-read configuration", () =>
        {
            // On the account surface a press is also the only thing that asks Claude Code for fresh usage numbers.
            if (surface == Surface.Account) account?.Read(refresh: true);
            Load();
        });
        refresh.Name = "ClaudeConfigRefresh";
        refresh.Width = refresh.Height = 24;
        refresh.Padding = new Thickness(4);
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        header.Children.Add(new ScrollViewer { Content = switcher, AllowAutoHide = false, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled });
        SetDock(header, Dock.Top);
        Children.Add(header);
        SetDock(scopeBlock, Dock.Top);
        Children.Add(scopeBlock);
        SetDock(notices, Dock.Top);
        Children.Add(notices);
        Children.Add(new ScrollViewer { Name = "ClaudeConfigBody", Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        RenderSwitcher();
        AttachedToVisualTree += (_, _) => Start();
        DetachedFromVisualTree += (_, _) => { watch?.Dispose(); watch = null; };
    }

    // The session this pane describes: among the terminals the workspace shows, the first running Claude.
    private (TerminalTabInfo? Session, TerminalTabInfo? Active) Shown(PluginHostProjection host)
    {
        var tabs = host.Terminals.GetValueOrDefault(workspaceId) ?? [];
        var shown = (host.ShownTerminalTabKeys.GetValueOrDefault(workspaceId) ?? []).Select(key => tabs.FirstOrDefault(tab => tab.TabKey == key)).OfType<TerminalTabInfo>().ToArray();
        return (shown.FirstOrDefault(tab => tab.Agent?.Kind == "claude"), shown.FirstOrDefault());
    }

    private void Start()
    {
        (session, activeTab) = Shown(context.Host());
        watch ??= context.WatchHost(host =>
        {
            var (found, active) = Shown(host);
            return (found?.TabKey, found?.Title, found?.Agent?.Command, active?.TabKey, active?.Title);
        }, (_, previous) =>
        {
            var previousTab = previous.Item1;
            var previousCommand = previous.Item3;
            (session, activeTab) = Shown(context.Host());
            if (session?.TabKey != previousTab || session?.Agent?.Command != previousCommand) Load();
            RenderScope();
        });
        RenderScope();
        Load();
    }

    // Every read is issued; a reply is applied only if no later read was issued after it, which is ordering, not a gate.
    private void Load()
    {
        var mine = ++generation;
        var tabKey = session?.TabKey;
        _ = Task.Run(async () =>
        {
            ClaudeConfigSnapshot result;
            try { result = await context.RequestAsync(ClaudeCodeContract.ConfigGet, new WorkspaceParams(workspaceId) { TabKey = tabKey }); }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (mine != generation) return;
                    error = ClaudeParts.ErrorText(failure);
                    Render();
                });
                return;
            }
            var key = JsonSerializer.Serialize(result, PluginJson.Options);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (mine != generation) return;
                snapshot = result;
                snapshotKey = key;
                error = null;
                RenderScope();
                Render();
            });
            // What Claude itself reaches beyond the files costs a health check, so it is asked after and never holds the pane.
            try
            {
                var listed = await context.RequestAsync(ClaudeCodeContract.McpList, new WorkspaceParams(workspaceId) { TabKey = tabKey });
                var capabilitiesKey = JsonSerializer.Serialize(listed.Capabilities, PluginJson.Options);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    if (mine != generation) return;
                    reached = listed.Capabilities;
                    reachedKey = capabilitiesKey;
                    if (surface == Surface.Capabilities) Render();
                });
            }
            catch (Exception failure) when (failure is not OperationCanceledException) { }
        });
    }

    private void Reload() => Load();

    private void RenderSwitcher()
    {
        if (switcher.Children.Count > 0)
        {
            foreach (var segment in switcher.Children.OfType<ToggleButton>()) segment.IsChecked = Equals(segment.Tag, surface);
            return;
        }
        switcher.Children.Clear();
        foreach (var (value, label) in new[] { (Surface.Context, "Context"), (Surface.Settings, "Settings"), (Surface.Capabilities, "Capabilities"), (Surface.Account, "Account") })
        {
            var segment = Ui.Segment("ClaudeSurface_" + value.ToString().ToLowerInvariant(), label);
            segment.Tag = value;
            segment.IsChecked = surface == value;
            segment.Click += (_, _) =>
            {
                surface = value;
                RenderSwitcher();
                RenderScope();
                Render();
            };
            switcher.Children.Add(segment);
        }
    }

    private void RenderScope()
    {
        var key = (surface, session?.TabKey, session?.Title, activeTab?.Title, snapshot?.Root);
        if (renderedScope == key) return;
        renderedScope = key;
        scopeBlock.Children.Clear();
        scopeBlock.IsVisible = surface == Surface.Context;
        scopeBlock.Tag = session is not null ? "session" : "workspace";
        if (session is not null)
        {
            scopeBlock.Children.Add(Ui.Text($"Context of Claude in tab {session.Title}", Ui.Muted, 13));
            var cwd = ClaudeParts.Code(TerminalFacts.CwdLabel(snapshot?.Root) ?? "…");
            cwd.Name = "ClaudeConfigScopeCwd";
            ToolTip.SetTip(cwd, snapshot?.Root ?? "Reading the directory…");
            scopeBlock.Children.Add(cwd);
        }
        else
        {
            scopeBlock.Children.Add(Ui.Text(activeTab is not null ? $"No Claude in tab {activeTab.Title}" : "No terminal open", Ui.Muted, 13));
            scopeBlock.Children.Add(Ui.Text("Showing what a new session here would load", Ui.Hint, 12));
        }
    }

    private void OpenSource(string path, IReadOnlyList<string>? keyPath) =>
        _ = context.Editors.OpenAsync(workspaceId, path, keyPath is null ? null : new EditorOpenOptions { KeyPath = keyPath }).AsTask()
            .ContinueWith(_ => { }, TaskScheduler.Default);

    private async void Edit(PendingEdit pending)
    {
        if (ClaudeParts.Owner(this) is not { } owner) return;
        if (await ReviewDialogs.EditAsync(context, owner, workspaceId, session?.TabKey, pending)) Reload();
    }

    private void Render()
    {
        var key = (surface, snapshotKey, reachedKey, error, settingsQuery);
        if (rendered == key) return;
        rendered = key;
        notices.Children.Clear();
        if (error is not null)
        {
            var text = ClaudeParts.Wrapped(error, Ui.Danger, 13);
            text.Name = "ClaudeConfigError";
            text.Margin = new Thickness(8, 4);
            notices.Children.Add(text);
        }
        foreach (var problem in snapshot?.Problems ?? [])
        {
            var row = new DockPanel { Name = "ClaudeConfigProblem", Tag = problem.Severity, Margin = new Thickness(8, 4) };
            var icon = problem.Severity == ClaudeProblemSeverity.Warning ? Ui.Icon("alertWarning", Ui.Warning, 14) : Ui.Icon("alertInfo", Ui.Info, 14);
            icon.VerticalAlignment = VerticalAlignment.Top;
            icon.Margin = new Thickness(0, 2, 4, 0);
            SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            var column = new StackPanel();
            column.Children.Add(Ui.Text(problem.Title, Ui.TextBrush, 13));
            column.Children.Add(ClaudeParts.Wrapped(problem.Detail));
            if (problem.Path is { } path) column.Children.Add(ClaudeParts.SourceButton(path, null, OpenSource));
            row.Children.Add(column);
            notices.Children.Add(row);
        }
        body.Content = snapshot is null
            ? Padded(Ui.Text("Reading configuration…", Ui.Muted, 13))
            : surface switch
            {
                Surface.Context => ContextSurface(snapshot.Context),
                Surface.Settings => SettingsSurface(snapshot),
                Surface.Account => Account(),
                _ => new ClaudeCapabilitiesSurface(this, [.. snapshot.Capabilities, .. reached]).View
            };
    }

    private ClaudeAccountSurface Account()
    {
        if (account is not null) return account;
        account = new ClaudeAccountSurface(context) { Name = "ClaudeAccount", Spacing = 16, Margin = new Thickness(8) };
        account.Read(refresh: false);
        return account;
    }

    private static Control Padded(Control child)
    {
        child.Margin = new Thickness(8);
        return child;
    }

    private static string FormatSize(long bytes) =>
        bytes < 1024 ? $"{bytes} B" : $"{Math.Round(bytes / 1024.0, MidpointRounding.AwayFromZero).ToString("N0", CultureInfo.CurrentCulture)} KB";

    private static Button Offer(string name, string title, string description, Action open)
    {
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(Ui.Text(title, Ui.TextBrush, 13));
        content.Children.Add(ClaudeParts.Wrapped(description));
        var button = new Button
        {
            Name = name,
            Content = content,
            Background = Brushes.Transparent,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => open();
        return button;
    }

    private Control ContextSurface(IReadOnlyList<ClaudeContextLayer> layers)
    {
        if (layers.Count == 0) return Padded(Ui.Text("No instruction files reach this workspace.", Ui.Muted, 13));
        var list = new StackPanel();
        var hasProjectInstructions = layers.Any(layer => layer.Kind == ClaudeContextKind.Instructions && layer.Origin.Scope == ClaudeConfigScope.Project &&
            !layer.Path.Contains("/.claude/", StringComparison.Ordinal) && !layer.Path.Contains("\\.claude\\", StringComparison.Ordinal));
        if (!hasProjectInstructions)
            list.Children.Add(Offer("ClaudeOfferProjectMd", "Add CLAUDE.md", "Shared instructions for everyone on this project. Commit it and it stays with the repo.",
                () => Edit(new(new FileEdit(ClaudeFileTemplate.ProjectInstructions), "Create CLAUDE.md"))));
        if (!layers.Any(layer => layer.Path.EndsWith("CLAUDE.local.md", StringComparison.Ordinal)))
            list.Children.Add(Offer("ClaudeOfferLocalMd", "Add CLAUDE.local.md", "Project instructions that stay on this machine, like which toolchain is installed here.",
                () => Edit(new(new FileEdit(ClaudeFileTemplate.ProjectLocalInstructions), "Create CLAUDE.local.md"))));
        // Size, not an estimated token count: bytes are what is actually known.
        var total = new DockPanel { Name = "ClaudeContextTotal", Margin = new Thickness(8, 4) };
        var amount = ClaudeParts.Code(FormatSize(layers.Where(layer => layer.Lazy != true).Sum(layer => layer.Bytes)), Ui.TextBrush);
        SetDock(amount, Dock.Right);
        total.Children.Add(amount);
        total.Children.Add(Ui.Text("PERSISTENT CONTEXT", Ui.Muted, 11));
        list.Children.Add(new Border { BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(0, 0, 0, 1), Child = total });
        // An @-import hangs under the file that pulled it in: a rule per ancestor level, then a tee or an elbow into the row.
        for (var index = 0; index < layers.Count; index++)
        {
            var layer = layers[index];
            var depth = Math.Min(layer.Depth ?? 0, 4);
            var nextDepth = index + 1 < layers.Count ? layers[index + 1].Depth ?? 0 : 0;
            var row = new DockPanel { Name = "ClaudeContextLayer", Tag = layer.Kind };
            for (var level = 0; level < depth; level++)
            {
                var guide = new Grid { Width = 5, Margin = new Thickness(15, 0, 0, 0), RowDefinitions = new RowDefinitions("*,*") };
                if (level == depth - 1)
                {
                    var last = nextDepth < (layer.Depth ?? 0);
                    guide.Name = "ClaudeContextConnector";
                    guide.Tag = last ? "last" : "tee";
                    var rule = new Border { Width = 1, Background = Ui.BorderBrush, HorizontalAlignment = HorizontalAlignment.Left };
                    if (!last) Grid.SetRowSpan(rule, 2);
                    guide.Children.Add(rule);
                    var arm = new Border { Height = 1, Background = Ui.BorderBrush, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, -8, 0) };
                    guide.Children.Add(arm);
                }
                else guide.Children.Add(new Border { Width = 1, Background = Ui.BorderBrush, HorizontalAlignment = HorizontalAlignment.Left, [Grid.RowSpanProperty] = 2 });
                SetDock(guide, Dock.Left);
                row.Children.Add(guide);
            }
            var line = new DockPanel { Margin = new Thickness(8, 4) };
            var size = ClaudeParts.Code(FormatSize(layer.Bytes));
            SetDock(size, Dock.Right);
            line.Children.Add(size);
            var icon = Ui.Icon("fileText", Ui.Hint, 14);
            icon.VerticalAlignment = VerticalAlignment.Center;
            var glyph = new Grid { Margin = new Thickness(0, -4, 8, -4), RowDefinitions = new RowDefinitions("*,*") };
            Grid.SetRowSpan(icon, 2);
            glyph.Children.Add(icon);
            if (nextDepth > (layer.Depth ?? 0))
                glyph.Children.Add(new Border
                {
                    Name = "ClaudeContextStem",
                    Width = 1,
                    Background = Ui.BorderBrush,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(7, 7, 0, 0),
                    [Grid.RowProperty] = 1
                });
            SetDock(glyph, Dock.Left);
            line.Children.Add(glyph);
            var column = new StackPanel();
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            title.Children.Add(Ui.Text(layer.Label, Ui.TextBrush, 13));
            title.Children.Add(layer.Kind == ClaudeContextKind.SystemPrompt ? ClaudeParts.LaunchFlagChip() : ClaudeParts.ScopeChip(layer.Origin.Scope.Name()));
            if (layer.Lazy == true)
            {
                var lazy = Ui.Text("ON DEMAND", Ui.Hint, 10);
                ToolTip.SetTip(lazy, "Loads only when Claude reads: " + string.Join(", ", layer.PathGlobs ?? []));
                title.Children.Add(lazy);
            }
            column.Children.Add(title);
            column.Children.Add(ClaudeParts.SourceButton(layer.Path, null, OpenSource));
            line.Children.Add(column);
            row.Children.Add(line);
            list.Children.Add(row);
        }
        // Editable whether or not a session here was launched with it; SharpRail stops refreshing it once it is edited.
        if (context.Settings<ClaudeCodeSettings>().AppendSystemPrompt)
        {
            var edit = Ui.Button("Edit SharpRail's instructions", () => _ = ClaudeParts.EditSystemPromptAsync(context, workspaceId), "pencil");
            edit.Name = "ClaudeSystemPromptEditPane";
            edit.Margin = new Thickness(8);
            edit.HorizontalAlignment = HorizontalAlignment.Left;
            ToolTip.SetTip(edit, "The system prompt sessions SharpRail starts append. Your edits are kept.");
            list.Children.Add(edit);
        }
        return list;
    }

    private Control SettingsSurface(ClaudeConfigSnapshot current)
    {
        var view = new DockPanel();
        var rows = new StackPanel();
        void Fill()
        {
            rows.Children.Clear();
            var shown = current.Settings.Where(entry => entry.Key.Contains(settingsQuery, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var entry in shown) rows.Children.Add(SettingRow(entry, current.KnownSettingKeys));
            if (shown.Length == 0) rows.Children.Add(Padded(Ui.Text("No keys match.", Ui.Muted, 13)));
        }
        var toolbar = ScopedSetting.SettingsToolbar("Claude", settingsQuery, query => { settingsQuery = query; Fill(); },
            () => Compose("", null, current.KnownSettingKeys));
        SetDock(toolbar, Dock.Top);
        view.Children.Add(toolbar);
        view.Children.Add(rows);
        Fill();
        return view;
    }

    private async void Compose(string key, JsonElement? value, IReadOnlyList<string> knownKeys)
    {
        if (ClaudeParts.Owner(this) is not { } owner) return;
        var composed = await SettingValueDialog.ShowAsync(owner,
            new("Claude", key, value, knownKeys, "Review the change") { KeyPlaceholder = "permissions.defaultMode" });
        if (composed is not { } result) return;
        Edit(new(new SettingEdit(result.Key, result.Value), key.Length == 0 ? $"Add \"{result.Key}\"" : $"Change \"{result.Key}\""));
    }

    private Control SettingRow(ClaudeSettingValue entry, IReadOnlyList<string> knownKeys)
    {
        var text = Json(entry.Value);
        var editable = SettingValueDialog.ShapeOf(entry.Value) is not null;
        var valueText = ClaudeParts.Code(text, Ui.Accent);
        valueText.TextWrapping = TextWrapping.Wrap;
        valueText.TextTrimming = TextTrimming.CharacterEllipsis;
        valueText.MaxLines = 4;
        Control value;
        if (editable)
        {
            var change = new Button
            {
                Name = "ClaudeSettingChange",
                Content = valueText,
                Padding = new Thickness(0),
                MinHeight = 0,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                HorizontalContentAlignment = HorizontalAlignment.Left
            };
            change.Classes.Add("quiet");
            ToolTip.SetTip(change, "Edit");
            change.Click += (_, _) => Compose(entry.Key, entry.Value, knownKeys);
            value = change;
        }
        else
        {
            ToolTip.SetTip(valueText, text);
            value = valueText;
        }
        var actions = new List<Control>();
        if (!editable)
        {
            var note = Ui.Text("EDIT AS A FILE", Ui.Hint, 10);
            note.Name = "ClaudeSettingUneditable";
            ToolTip.SetTip(note, "Only text, numbers, on/off and lists of text are editable here");
            actions.Add(note);
        }
        actions.Add(ScopedSetting.RowAction("ClaudeSettingRemove", "Remove", () => Edit(new(new SettingEdit(entry.Key), $"Remove \"{entry.Key}\"")), danger: true));
        return ScopedSetting.SettingRow("Claude", entry.Key, value, new(entry.Origin.Scope.Name(), entry.Origin.Path),
            [.. entry.Shadowed.Select(shadow => new ScopedSettingShadow(shadow.Origin.Scope.Name(), shadow.Origin.Path, Json(shadow.Value)))],
            path => OpenSource(path, entry.Origin.KeyPath), actions, entry.DocsUrl, "What this key does, in Claude Code's reference");
    }

    private static string Json(JsonElement value) => JsonSerializer.Serialize(value, new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    // What the capabilities surface asks of the pane: open a source, review an edit, run one of Claude's commands.
    internal void Open(string path, IReadOnlyList<string>? keyPath) => OpenSource(path, keyPath);
    internal void Review(PendingEdit pending) => Edit(pending);

    internal async void Uninstall(PluginUninstallTarget target)
    {
        if (ClaudeParts.Owner(this) is { } owner && await ReviewDialogs.UninstallAsync(context, owner, workspaceId, session?.TabKey, target)) Reload();
    }

    internal async void Move(PluginMoveTarget target)
    {
        if (ClaudeParts.Owner(this) is { } owner && await ReviewDialogs.MoveAsync(context, owner, workspaceId, session?.TabKey, target)) Reload();
    }

    internal async void Marketplace(ClaudeMarketplaceAction action)
    {
        if (ClaudeParts.Owner(this) is { } owner && await ReviewDialogs.MarketplaceAsync(context, owner, workspaceId, session?.TabKey, action)) Reload();
    }

    internal async void Add(string kind)
    {
        if (ClaudeParts.Owner(this) is not { } owner) return;
        var pending = kind switch
        {
            "mcp" => await ComposeDialogs.McpServerAsync(owner),
            "skill" => await ComposeDialogs.SkillAsync(owner),
            "hook" => await ComposeDialogs.HookAsync(owner),
            _ => await ComposeDialogs.PluginAsync(owner)
        };
        if (pending is not null) Edit(pending);
    }
}