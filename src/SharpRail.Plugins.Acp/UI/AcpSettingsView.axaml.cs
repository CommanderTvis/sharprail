using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Acp.UI;

/// <summary>
/// Settings › ACP, laid out like Settings › Code Intelligence: a narrow list of every ACP agent (registry installs and custom ones together,
/// with a Browse registry entry on top) and, for the selected one, a pane that configures it on its own.
/// </summary>
internal sealed partial class AcpSettingsView : StackPanel
{
    /// <summary>The list entry that shows the registry browser instead of an agent.</summary>
    internal const string BrowseId = "\u0001browse";

    private readonly IPluginUIContext context;
    private readonly MasterDetail master;
    private readonly TextBlock defaultNote;
    private readonly StackPanel registryList;
    private readonly TextBlock status;
    private readonly TextBox search;
    private readonly Control browser;
    private AcpRegistryView? registry;
    private IReadOnlyList<AcpAgentEntry> configured = [];
    private IReadOnlyDictionary<string, string> problems = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> icons = new Dictionary<string, string>();
    private IDisposable? settingsWatch;
    // The agent to select when the next list arrives (one that was just added).
    private string? selectNext;
    private string? shownId;

    internal AcpSettingsView(IPluginUIContext context)
    {
        AvaloniaXamlLoader.Load(this);
        this.context = context;
        defaultNote = this.FindControl<TextBlock>("AcpDefaultNote")!;
        master = new MasterDetail("AcpMaster");
        this.FindControl<Border>("AcpMasterHost")!.Child = master;
        master.Selected += id => ShowDetail(id);
        registryList = new StackPanel { Spacing = 0 };
        status = new TextBlock { Name = "AcpRegistryStatus", FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
        search = new TextBox { Name = "AcpSearch", PlaceholderText = "Search the registry", Width = 260, FontSize = 12 };
        Avalonia.Automation.AutomationProperties.SetName(search, "search registry");
        search.TextChanged += (_, _) => RenderRegistry();
        browser = BuildBrowser();
        var add = new Button { Name = "AcpAddCustom", Content = "Add custom agent…", HorizontalAlignment = HorizontalAlignment.Stretch };
        Avalonia.Automation.AutomationProperties.SetName(add, "add custom agent");
        add.Click += (_, _) => AddCustom();
        master.Footer.Children.Add(add);
        DetachedFromVisualTree += (_, _) => { settingsWatch?.Dispose(); settingsWatch = null; };
        AttachedToVisualTree += (_, _) =>
        {
            settingsWatch ??= context.OnSettings<AcpSettings>(_ => RenderAll(false));
            _ = RunAsync(() => context.RequestAsync(AcpContract.Agents, new AcpNoParams()).AsTask(), list => Apply(list, true), "Couldn't list the agents");
            if (registry is null) LoadRegistry(false);
        };
        RenderAll(true);
    }

    private async Task RunAsync<T>(Func<Task<T>> request, Action<T> apply, string failure)
    {
        try
        {
            var result = await Task.Run(request);
            await Dispatcher.UIThread.InvokeAsync(() => apply(result));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, failure, error.Message);
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
    private void Apply(AcpAgentList list, bool rebuildDetail)
    {
        configured = list.Agents;
        problems = list.Problems;
        RenderAll(rebuildDetail);
    }

    private void RenderAll(bool rebuildDetail)
    {
        var current = selectNext ?? master.SelectedId;
        selectNext = null;
        var defaultId = context.Settings<AcpSettings>().DefaultAgent;
        var rows = new List<Control> { MasterDetail.Row(BrowseId, Ui.Icon("box", Ui.TextBrush), "Browse registry", "Find and install agents", null) };
        rows.AddRange(AcpRows.Agents(configured, registry, problems, defaultId).Select(row => MasterDetail.Row(row.Entry.Id, AcpIconFor(row.Entry.Id), row.Entry.Name, row.Subtitle, row.Problem)));
        master.SetRows(rows, current);
        defaultNote.IsVisible = AcpLauncher.DefaultIsMissing(defaultId, configured);
        defaultNote.Text = defaultNote.IsVisible ? "The agent chosen as default is no longer in the list. The New ACP chat button asks until you choose another." : "";
        if (rebuildDetail || master.SelectedId != shownId) ShowDetail(master.SelectedId);
        RenderRegistry();
    }

    private void ShowDetail(string? id)
    {
        shownId = id;
        if (id is null) { master.Detail.Content = null; return; }
        if (id == BrowseId) { master.Detail.Content = browser; return; }
        master.Detail.Content = configured.FirstOrDefault(agent => agent.Id == id) is { } entry ? BuildDetail(entry) : null;
    }

    /// <summary>Saves an edited agent; the list refreshes and the pane stays as typed.</summary>
    private void Save(AcpAgentEntry entry) =>
        _ = RunAsync(() => context.RequestAsync(AcpContract.AddAgent, entry).AsTask(), list => Apply(list, false), "Couldn't save the agent");

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
        if (detail.IsRegistry) panel.Children.Add(Text("Location: " + detail.Location, detail: true));

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
        await RunAsync(() => context.RequestAsync(AcpContract.RemoveAgent, new AcpChooseParams("", "", entry.Id)).AsTask(), list => Apply(list, true), "Couldn't remove the agent");
    }

    private void Install(string agentId) =>
        _ = RunAsync(() => context.RequestAsync(AcpContract.InstallPlan, new AcpInstallPlanParams(agentId)).AsTask(), ShowPlan, "Couldn't prepare the install");

    /// <summary>The Add custom agent popup, in the overlay. The new agent is selected in the list once the host has saved it.</summary>
    private async void AddCustom()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (await AcpCustomForm.ShowAsync(owner, configured) is not { } entry) return;
        selectNext = entry.Id;
        await RunAsync(() => context.RequestAsync(AcpContract.AddAgent, entry).AsTask(), list => Apply(list, true), "Couldn't add the agent");
    }

    private Control BuildBrowser()
    {
        var panel = new StackPanel { Name = "AcpBrowser", Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "ACP registry", FontSize = 16, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBrush });
        panel.Children.Add(Text("Agents that speak the Agent Client Protocol, from the public registry.", detail: true));
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        controls.Children.Add(search);
        var refresh = new Button { Name = "AcpRefresh", Content = "Refresh" };
        Avalonia.Automation.AutomationProperties.SetName(refresh, "refresh registry");
        refresh.Click += (_, _) => LoadRegistry(true);
        controls.Children.Add(refresh);
        panel.Children.Add(controls);
        panel.Children.Add(status);
        panel.Children.Add(registryList);
        return panel;
    }

    private void LoadRegistry(bool refresh)
    {
        status.Text = refresh ? "Refreshing…" : "Loading the registry…";
        _ = RunAsync(() => context.RequestAsync(AcpContract.Registry, new AcpRegistryParams(refresh)).AsTask(), view => { registry = view; RenderAll(false); LoadIcons(refresh); }, "Couldn't read the registry");
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

    private void RenderRegistry()
    {
        registryList.Children.Clear();
        if (registry is null) return;
        status.Text = registry.Source switch
        {
            "network" => "Fetched " + registry.FetchedAt,
            "cache" => "Saved copy from " + registry.FetchedAt,
            _ => "Built-in list"
        } + (registry.Note is { } note ? ". " + note : "");
        var rows = AcpRows.Registry(registry, configured, search.Text);
        if (rows.Count == 0) registryList.Children.Add(Text("No agent matches the search.", detail: true));
        foreach (var model in rows)
        {
            var agent = model.Agent;
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            var name = Text(model.Title);
            name.TextWrapping = TextWrapping.NoWrap;
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            title.Children.Add(name);
            if (agent.NotRecommended is { } reason)
            {
                var tag = new Border { Name = "AcpNotRecommended_" + agent.Id, Background = Ui.WarningWash, CornerRadius = new CornerRadius(8), Padding = new Thickness(8, 0), VerticalAlignment = VerticalAlignment.Center, Child = new TextBlock { Text = "Not recommended", FontSize = 11, Foreground = Ui.Warning } };
                ToolTip.SetTip(tag, reason);
                title.Children.Add(tag);
            }
            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(Text(agent.Description, detail: true));
            if (agent.NotRecommended is { } why) details.Children.Add(Text(why, detail: true));
            details.Children.Add(Text(string.Join(", ", agent.Kinds), detail: true));
            Control action = model.CanInstall
                ? Quiet("AcpInstall_" + agent.Id, model.ActionLabel, () => Install(agent.Id))
                : new TextBlock { Name = "AcpInstalled_" + agent.Id, Text = model.ActionLabel, FontSize = 12, Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center };
            var mark = model.Status switch { "installed" => StatusIcon("check", Ui.Success, model.StatusText), "update" => StatusIcon("refresh", Ui.Muted, model.StatusText), "unavailable" => StatusIcon("alertWarning", Ui.Muted, model.StatusText), _ => null };
            registryList.Children.Add(RegistryRow.Create("AcpRegistryAgent_" + agent.Id, mark, AcpIconFor(agent.Id), title, null, action, details));
        }
    }

    /// <summary>Asks in the overlay over this window; the install is requested for exactly the version and distribution the dialog showed.</summary>
    private async void ShowPlan(AcpInstallPlan plan)
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        if (!await AcpInstallConfirm.ShowAsync(owner, plan)) return;
        // The host refuses the request if the registry moved on since the plan was made.
        await RunAsync(() => context.RequestAsync(AcpContract.Install, new AcpInstallRequest(plan.AgentId, plan.Kind, plan.Version, true)).AsTask(), list => Apply(list, true), "Couldn't install the agent");
    }
}