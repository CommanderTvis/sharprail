using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Acp.UI;

/// <summary>
/// Settings › ACP: one scrolling list. Installed agents (registry installs and custom ones) are pinned to the top by name, each with its update,
/// configure and remove actions, and the registry agents that are not installed follow under a Registry label. A search filters both groups.
/// </summary>
internal sealed partial class AcpSettingsView : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly TextBlock defaultNote;
    private readonly StackPanel list;
    private readonly TextBlock status;
    private readonly TextBox search;
    private readonly Control page;
    private AcpRegistryView? registry;
    private string? registryError;
    private IReadOnlyList<AcpAgentEntry> configured = [];
    private IReadOnlyDictionary<string, string> problems = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> icons = new Dictionary<string, string>();
    private IDisposable? settingsWatch;
    // The installed agent whose settings are open under its row, and that settings pane (kept across list refreshes so typing is not interrupted).
    private string? expandedId;
    private Border? expanded;

    internal AcpSettingsView(IPluginUIContext context)
    {
        AvaloniaXamlLoader.Load(this);
        this.context = context;
        defaultNote = this.FindControl<TextBlock>("AcpDefaultNote")!;
        list = new StackPanel { Name = "AcpList", Spacing = 0 };
        status = new TextBlock { Name = "AcpRegistryStatus", FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
        search = new TextBox { Name = "AcpSearch", PlaceholderText = "Search agents", Width = 260, FontSize = 12 };
        Avalonia.Automation.AutomationProperties.SetName(search, "search agents");
        search.TextChanged += (_, _) => RenderList();
        page = BuildPage();
        this.FindControl<Border>("AcpListHost")!.Child = page;
        DetachedFromVisualTree += (_, _) => { settingsWatch?.Dispose(); settingsWatch = null; };
        AttachedToVisualTree += (_, _) =>
        {
            settingsWatch ??= context.OnSettings<AcpSettings>(_ => RenderAll(false));
            _ = RunAsync(() => context.RequestAsync(AcpContract.Agents, new AcpNoParams()).AsTask(), agents => Apply(agents, true), "Couldn't list the agents");
            if (registry is null) LoadRegistry(false);
        };
        RenderAll(true);
    }

    private async Task RunAsync<T>(Func<Task<T>> request, Action<T> apply, string failure, Action<string>? failed = null)
    {
        try
        {
            var result = await Task.Run(request);
            await Dispatcher.UIThread.InvokeAsync(() => apply(result));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (failed is null) context.Notify(PluginNotificationKind.Error, failure, error.Message);
            else await Dispatcher.UIThread.InvokeAsync(() => failed(error.Message));
        }
    }

    private static TextBlock Text(string text, bool detail = false, bool code = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = detail || code ? 12 : 13, Foreground = detail ? Ui.Muted : Ui.TextBrush };
        if (code) block.FontFamily = Ui.CodeFont;
        return block;
    }

    private static Control Quiet(string name, string label, Action click)
    {
        var button = new Button { Name = name, Content = label, Classes = { "link" }, Padding = new Thickness(0, 2) };
        button.Click += (_, _) => click();
        return button;
    }

    private static Control StatusIcon(string icon, IBrush color, string description)
    {
        var mark = Ui.Icon(icon, color);
        ToolTip.SetTip(mark, description);
        Avalonia.Automation.AutomationProperties.SetName(mark, description);
        return mark;
    }

    private Control AcpIconFor(string id) => AcpGlyph.For(icons.GetValueOrDefault(id), 16);

    /// <summary>A new list from the host. <paramref name="rebuildDetail"/> is false after a field was saved, so the pane being typed in is left alone.</summary>
    private void Apply(AcpAgentList agents, bool rebuildDetail)
    {
        configured = agents.Agents;
        problems = agents.Problems;
        RenderAll(rebuildDetail);
    }

    private void RenderAll(bool rebuildDetail)
    {
        var defaultId = context.Settings<AcpSettings>().DefaultAgent;
        defaultNote.IsVisible = AcpLauncher.DefaultIsMissing(defaultId, configured);
        defaultNote.Text = defaultNote.IsVisible ? "The agent chosen as default is no longer in the list. The New ACP chat button asks until you choose another." : "";
        if (expandedId is not null && configured.All(agent => agent.Id != expandedId)) expandedId = null;
        if (rebuildDetail || expanded is null || expandedId is null) expanded = null;
        if (expanded is null && expandedId is not null && configured.FirstOrDefault(agent => agent.Id == expandedId) is { } entry)
            expanded = new Border { Name = "AcpAgentSettings_" + entry.Id, Padding = new Thickness(46, 4, 8, 12), Child = BuildDetail(entry) };
        RenderList();
    }

    /// <summary>Saves an edited agent; the list refreshes and the pane stays as typed.</summary>
    private void Save(AcpAgentEntry entry) =>
        _ = RunAsync(() => context.RequestAsync(AcpContract.AddAgent, entry).AsTask(), agents => Apply(agents, false), "Couldn't save the agent");

    private Control BuildDetail(AcpAgentEntry entry)
    {
        var detail = new AcpAgentDetail(entry, AcpRows.RegistryAgent(entry, registry), problems.GetValueOrDefault(entry.Id));
        var defaultId = context.Settings<AcpSettings>().DefaultAgent;
        var panel = new StackPanel { Name = "AcpAgentDetail_" + entry.Id, Spacing = 8 };
        var current = entry;
        var runs = Text(detail.CommandLine, code: true);
        runs.Name = "AcpRuns";
        void Edit(Func<AcpAgentEntry, AcpAgentEntry> change)
        {
            current = change(current);
            runs.Text = AcpEntryText.CommandLine(current);
            Save(current);
        }

        if (detail.IsRegistry) panel.Children.Add(new TextBlock { Text = entry.Name, FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBrush });
        else
        {
            var name = new TextBox { Name = "AcpDetailName", Text = entry.Name, FontSize = 14, FontWeight = FontWeight.SemiBold };
            name.LostFocus += (_, _) => { if (!string.IsNullOrWhiteSpace(name.Text) && name.Text.Trim() != current.Name) Edit(item => item with { Name = name.Text.Trim() }); };
            panel.Children.Add(name);
        }
        panel.Children.Add(Text(string.Join(" · ", AcpRows.Agents([entry], registry, problems, defaultId)[0].Tags), detail: true));
        if (detail.Description.Length > 0) panel.Children.Add(Text(detail.Description));
        panel.Children.Add(Text(detail.Version, detail: true));
        panel.Children.Add(Text(detail.SourceLine, detail: true));
        var links = new WrapPanel { Name = "AcpLinks" };
        if (Link(detail.Website, "Website ↗") is { } website) links.Children.Add(website);
        if (Link(detail.Repository, "Source repository ↗") is { } repository) links.Children.Add(repository);
        if (links.Children.Count > 0) panel.Children.Add(links);
        if (detail.License is { } license) panel.Children.Add(Text("License: " + license, detail: true));
        var state = Text(detail.InstallStatus, detail: detail.Problem is null);
        if (detail.Problem is not null) state.Foreground = Ui.Danger;
        state.Name = "AcpInstallStatus";
        panel.Children.Add(state);
        if (detail.IsRegistry)
        {
            panel.Children.Add(Text("Location: " + detail.Location, detail: true));
            var checks = Text("Checked: " + detail.Verification, detail: true);
            checks.Name = "AcpVerification";
            panel.Children.Add(checks);
            panel.Children.Add(TrustNotice("AcpTrustNote"));
        }

        var enabled = new CheckBox { Name = "AcpEnabled", Content = "Offer this agent in the New ACP chat button", IsChecked = entry.Enabled };
        enabled.IsCheckedChanged += (_, _) => Edit(item => item with { Enabled = enabled.IsChecked == true });
        panel.Children.Add(enabled);
        var isDefault = new CheckBox { Name = "AcpIsDefault", Content = "Start this agent when the New ACP chat button is clicked", IsChecked = defaultId == entry.Id };
        isDefault.IsCheckedChanged += (_, _) =>
            _ = RunAsync(async () => { await context.UpdateSettingsAsync(new AcpSettings { DefaultAgent = isDefault.IsChecked == true ? entry.Id : "" }); return 0; }, _ => { }, "Couldn't save the default agent");
        panel.Children.Add(isDefault);

        if (detail.IsRegistry)
        {
            var useDefaults = new CheckBox { Name = "AcpUseDefaultArgs", Content = "Use default arguments", IsChecked = entry.UseDefaultArgs };
            var program = Field(panel, "Executable override", entry.CommandOverride, "Automatic", "AcpCommandOverride", false, text => Edit(item => item with { CommandOverride = text.Trim() }));
            panel.Children.Add(useDefaults);
            var arguments = Field(panel, "Arguments (one per line)", string.Join('\n', entry.ArgsOverride), "", "AcpArguments", true, text => Edit(item => item with { ArgsOverride = AcpEntryText.ParseLines(text) }));
            arguments.IsEnabled = !entry.UseDefaultArgs;
            useDefaults.IsCheckedChanged += (_, _) =>
            {
                arguments.IsEnabled = useDefaults.IsChecked != true;
                Edit(item => item with { UseDefaultArgs = useDefaults.IsChecked == true });
            };
            _ = program;
        }
        else
        {
            Field(panel, "Command", entry.Command, "Command, such as /usr/local/bin/my-agent", "AcpCommand", false, text => { if (text.Trim().Length > 0) Edit(item => item with { Command = text.Trim() }); });
            Field(panel, "Arguments (one per line)", string.Join('\n', entry.Args), "", "AcpArguments", true, text => Edit(item => item with { Args = AcpEntryText.ParseLines(text) }));
        }
        Field(panel, "Environment (one NAME=value per line)", AcpEntryText.FormatEnvironment(entry.Env), "", "AcpEnvironment", true, text => Edit(item => item with { Env = AcpEntryText.ParseEnvironment(text) }));
        Field(panel, "Working directory (blank: the workspace)", entry.Cwd, "", "AcpDirectory", false, text => Edit(item => item with { Cwd = text.Trim() }));
        panel.Children.Add(Text("Runs:", detail: true));
        panel.Children.Add(runs);

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        if (detail.CanUpdate) actions.Children.Add(Ui.Button("Update to " + detail.UpdateTo, () => Install(entry.Id)));
        if (detail.CanReinstall) actions.Children.Add(Ui.Button("Reinstall", () => Install(entry.Id)));
        var remove = Ui.Button("Remove", () => _ = RemoveAsync(entry));
        remove.Name = "AcpRemove";
        actions.Children.Add(remove);
        panel.Children.Add(actions);
        return panel;
    }

    private static TextBox Field(Panel panel, string label, string value, string placeholder, string name, bool multiline, Action<string> save)
    {
        panel.Children.Add(Text(label, detail: true));
        var box = new TextBox { Name = name, Text = value, PlaceholderText = placeholder, FontFamily = Ui.CodeFont, FontSize = 12, AcceptsReturn = multiline, MinHeight = multiline ? 48 : 0 };
        Avalonia.Automation.AutomationProperties.SetName(box, label);
        box.LostFocus += (_, _) => save(box.Text ?? "");
        panel.Children.Add(box);
        return box;
    }

    /// <summary>The permanent trust notice: registry agents are third-party programs, and a checksum does not make one safe.</summary>
    private static Control TrustNotice(string name) =>
        new Border { Name = name, Background = Ui.WarningWash, CornerRadius = new CornerRadius(8), Padding = new Thickness(10, 6), Child = new TextBlock { Text = AcpInstallPlan.TrustNote, FontSize = 12, Foreground = Ui.TextBrush, TextWrapping = TextWrapping.Wrap } };

    private static Control? Link(string? url, string label)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        var button = new Button { Content = label, Classes = { "link" }, Margin = new Thickness(0, 0, 16, 0) };
        ToolTip.SetTip(button, url);
        button.Click += (_, _) => _ = TopLevel.GetTopLevel(button)?.Launcher.LaunchUriAsync(uri);
        return button;
    }

    private async Task RemoveAsync(AcpAgentEntry entry)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner || !await AcpInstallConfirm.ShowRemoveAsync(owner, entry)) return;
        await RunAsync(() => context.RequestAsync(AcpContract.RemoveAgent, new AcpChooseParams("", "", entry.Id)).AsTask(), agents => Apply(agents, true), "Couldn't remove the agent");
    }

    private void Install(string agentId) =>
        _ = RunAsync(() => context.RequestAsync(AcpContract.InstallPlan, new AcpInstallPlanParams(agentId)).AsTask(), ShowPlan, "Couldn't prepare the install");

    /// <summary>The Add custom agent popup, in the overlay. The new agent is selected in the list once the host has saved it.</summary>
    private async void AddCustom()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (await AcpCustomForm.ShowAsync(owner, configured) is not { } entry) return;
        await RunAsync(() => context.RequestAsync(AcpContract.AddAgent, entry).AsTask(), agents => Apply(agents, true), "Couldn't add the agent");
    }

    private Control BuildPage()
    {
        var panel = new StackPanel { Name = "AcpBrowser", Spacing = 8 };
        panel.Children.Add(TrustNotice("AcpBrowserTrust"));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(search);
        var refresh = new Button { Name = "AcpRefresh", Content = "Refresh" };
        Avalonia.Automation.AutomationProperties.SetName(refresh, "refresh registry");
        refresh.Click += (_, _) => LoadRegistry(true);
        controls.Children.Add(refresh);
        var add = new Button { Name = "AcpAddCustom", Content = "Add custom agent…" };
        Avalonia.Automation.AutomationProperties.SetName(add, "add custom agent");
        add.Click += (_, _) => AddCustom();
        controls.Children.Add(add);
        panel.Children.Add(controls);
        panel.Children.Add(status);
        panel.Children.Add(list);
        return panel;
    }

    private void LoadRegistry(bool refresh)
    {
        registryError = null;
        status.Text = refresh ? "Refreshing…" : "Loading the registry…";
        _ = RunAsync(() => context.RequestAsync(AcpContract.Registry, new AcpRegistryParams(refresh)).AsTask(), view => { registry = view; RenderAll(false); LoadIcons(refresh); }, "Couldn't read the registry",
            message => { registryError = message; RenderList(); });
    }

    /// <summary>Asks the host for the icons after the list is on screen. Rows show the generic glyph meanwhile, and keep it when no icon comes.</summary>
    private void LoadIcons(bool refresh) =>
        _ = Task.Run(async () =>
        {
            AcpIconSet set;
            try { set = await context.RequestAsync(AcpContract.Icons, new AcpIconParams(refresh, false)); }
            catch (Exception error) when (error is not OperationCanceledException) { return; }
            await Dispatcher.UIThread.InvokeAsync(() => { icons = set.Svgs; RenderAll(false); });
        });

    private static Control Label(string name, string text) =>
        new TextBlock { Name = name, Text = text, FontSize = 12, Foreground = Ui.Muted, Margin = new Thickness(2, 12, 0, 4) };

    private void RenderList()
    {
        // The open settings pane is moved, not rebuilt, so a field being typed in keeps its focus.
        if (expanded?.Parent is Panel old) old.Children.Remove(expanded);
        list.Children.Clear();
        if (registry is not null)
            status.Text = registry.Source switch
            {
                "network" => "Fetched " + registry.FetchedAt,
                "cache" => "Saved copy from " + registry.FetchedAt,
                _ => "Built-in list"
            } + (registry.Note is { } note ? ". " + note : "");
        else if (registryError is not null) status.Text = "";
        var defaultId = context.Settings<AcpSettings>().DefaultAgent;
        var combined = AcpRows.Combined(configured, registry, problems, defaultId, search.Text);
        if (registryError is not null && registry is null)
            list.Children.Add(Text("The registry could not be read (" + registryError + "). Installed agents are listed below; press Refresh to try again.", detail: true));
        if (combined.Installed.Count > 0) list.Children.Add(Label("AcpInstalledLabel", "Installed"));
        foreach (var row in combined.Installed)
        {
            list.Children.Add(InstalledRow(row));
            if (expanded is not null && row.Entry.Id == expandedId) list.Children.Add(expanded);
        }
        if (combined.Installed.Count == 0 && string.IsNullOrWhiteSpace(search.Text))
            list.Children.Add(Text("No agents installed. Install one from the registry below, or add a custom agent.", detail: true));
        if (registry is not null)
        {
            list.Children.Add(Label("AcpRegistryLabel", "Registry"));
            if (combined.Available.Count == 0) list.Children.Add(Text(string.IsNullOrWhiteSpace(search.Text) ? "Every registry agent is installed." : "No agent matches the search.", detail: true));
            foreach (var model in combined.Available) list.Children.Add(RegistryAgentRow(model));
        }
        else if (registryError is null) list.Children.Add(Text("Loading the registry…", detail: true));
    }

    private static Control Pill(string name, string text, IBrush wash, IBrush color, string? tip = null)
    {
        var tag = new Border { Name = name, Background = wash, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = text, FontSize = 11, Foreground = color } };
        if (tip is not null) ToolTip.SetTip(tag, tip);
        return tag;
    }

    private static TextBlock NameText(string text)
    {
        var name = Text(text);
        name.TextWrapping = TextWrapping.NoWrap;
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        return name;
    }

    /// <summary>A pinned row: the same columns as a registry row, with Update, Configure and Remove in the action column.</summary>
    private Control InstalledRow(AcpAgentRowModel row)
    {
        var entry = row.Entry;
        var detail = new AcpAgentDetail(entry, AcpRows.RegistryAgent(entry, registry), row.Problem);
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(NameText(entry.Origin == "registry" && entry.Version is { } version ? $"{entry.Name}  v{version}" : entry.Name));
        if (entry.Origin != "registry") title.Children.Add(Pill("AcpCustom_" + entry.Id, "Custom", Ui.WarningWash, Ui.Muted));
        if (detail.Registry?.NotRecommended is { } reason) title.Children.Add(Pill("AcpNotRecommended_" + entry.Id, "Not recommended", Ui.WarningWash, Ui.Warning, reason));
        var details = new StackPanel { Spacing = 2 };
        if (detail.Description.Length > 0) details.Children.Add(Text(detail.Description, detail: true));
        details.Children.Add(Text(row.Subtitle, detail: true));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        if (detail.CanUpdate) actions.Children.Add(Quiet("AcpUpdate_" + entry.Id, "Update ▷", () => Install(entry.Id)));
        actions.Children.Add(Quiet("AcpConfigure_" + entry.Id, expandedId == entry.Id ? "Hide settings" : "Configure", () =>
        {
            expandedId = expandedId == entry.Id ? null : entry.Id;
            expanded = null;
            RenderAll(false);
        }));
        actions.Children.Add(Quiet("AcpRemoveRow_" + entry.Id, "Remove", () => _ = RemoveAsync(entry)));
        var mark = row.Problem is { } problem ? StatusIcon("alertWarning", Ui.Danger, problem) : detail.CanUpdate ? StatusIcon("refresh", Ui.Muted, "Update available") : StatusIcon("check", Ui.Success, "Installed");
        return RegistryRow.Create("AcpInstalledAgent_" + entry.Id, mark, AcpIconFor(entry.Id), title, null, actions, details);
    }

    private Control RegistryAgentRow(AcpRegistryRowModel model)
    {
        var agent = model.Agent;
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(NameText(model.Title));
        if (agent.NotRecommended is { } reason) title.Children.Add(Pill("AcpNotRecommended_" + agent.Id, "Not recommended", Ui.WarningWash, Ui.Warning, reason));
        var details = new StackPanel { Spacing = 2 };
        details.Children.Add(Text(agent.Description, detail: true));
        if (agent.NotRecommended is { } why) details.Children.Add(Text(why, detail: true));
        details.Children.Add(Text(string.Join(", ", agent.Kinds), detail: true));
        Control action = model.CanInstall
            ? Quiet("AcpInstall_" + agent.Id, model.ActionLabel, () => Install(agent.Id))
            : new TextBlock { Name = "AcpInstalled_" + agent.Id, Text = model.ActionLabel, FontSize = 12, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
        var mark = model.Status == "unavailable" ? StatusIcon("alertWarning", Ui.Muted, model.StatusText) : null;
        return RegistryRow.Create("AcpRegistryAgent_" + agent.Id, mark, AcpIconFor(agent.Id), title, null, action, details);
    }

    /// <summary>Asks in the overlay over this window; the install is requested for exactly the version and distribution the dialog showed.</summary>
    private async void ShowPlan(AcpInstallPlan plan)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await AcpInstallConfirm.ShowAsync(owner, plan)) return;
        // The host refuses the request if the registry moved on since the plan was made.
        await RunAsync(() => context.RequestAsync(AcpContract.Install, new AcpInstallRequest(plan.AgentId, plan.Kind, plan.Version, true)).AsTask(), agents => Apply(agents, true), "Couldn't install the agent");
    }
}