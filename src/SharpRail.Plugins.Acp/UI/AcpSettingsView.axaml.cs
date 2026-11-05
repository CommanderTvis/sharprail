using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Acp.UI;

/// <summary>Settings › ACP: the agents you can chat with, a custom-agent form and the registry browser with its install confirmation.</summary>
internal sealed partial class AcpSettingsView : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly StackPanel agents, registryList;
    private readonly TextBlock status;
    private readonly TextBox search;
    private AcpRegistryView? registry;

    internal AcpSettingsView(IPluginUIContext context)
    {
        AvaloniaXamlLoader.Load(this);
        this.context = context;
        agents = this.FindControl<StackPanel>("AcpAgents")!;
        registryList = this.FindControl<StackPanel>("AcpRegistryList")!;
        status = this.FindControl<TextBlock>("AcpRegistryStatus")!;
        search = this.FindControl<TextBox>("AcpSearch")!;
        var name = this.FindControl<TextBox>("AcpName")!;
        var command = this.FindControl<TextBox>("AcpCommand")!;
        var arguments = this.FindControl<TextBox>("AcpArguments")!;
        var environment = this.FindControl<TextBox>("AcpEnvironment")!;
        var directory = this.FindControl<TextBox>("AcpDirectory")!;
        this.FindControl<Button>("AcpAdd")!.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(name.Text) || string.IsNullOrWhiteSpace(command.Text))
            {
                context.Notify(PluginNotificationKind.Info, "A name and a command are needed");
                return;
            }
            var entry = new AcpAgentEntry(AcpEntryText.IdFor(name.Text), name.Text.Trim(), command.Text.Trim())
            {
                Args = AcpEntryText.SplitArguments(arguments.Text ?? ""),
                Env = AcpEntryText.ParseEnvironment(environment.Text ?? ""),
                Cwd = directory.Text?.Trim() ?? ""
            };
            _ = RunAsync(() => context.RequestAsync(AcpContract.AddAgent, entry).AsTask(), list =>
            {
                RenderAgents(list);
                name.Text = command.Text = arguments.Text = environment.Text = directory.Text = "";
            }, "Couldn't add the agent");
        };
        this.FindControl<Button>("AcpRefresh")!.Click += (_, _) => LoadRegistry(true);
        search.TextChanged += (_, _) => RenderRegistry();
        AttachedToVisualTree += (_, _) =>
        {
            _ = RunAsync(() => context.RequestAsync(AcpContract.Agents, new AcpNoParams()).AsTask(), RenderAgents, "Couldn't list the agents");
            if (registry is null) LoadRegistry(false);
        };
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

    private void LoadRegistry(bool refresh)
    {
        status.Text = refresh ? "Refreshing…" : "Loading the registry…";
        _ = RunAsync(() => context.RequestAsync(AcpContract.Registry, new AcpRegistryParams(refresh)).AsTask(), view => { registry = view; RenderRegistry(); LoadIcons(refresh); }, "Couldn't read the registry");
    }

    /// <summary>Asks the host for the icons after the list is on screen. The rows show the generic glyph meanwhile, and keep it when no icon comes.</summary>
    private void LoadIcons(bool refresh) =>
        _ = Task.Run(async () =>
        {
            AcpIconSet set;
            try { set = await context.RequestAsync(AcpContract.Icons, new AcpIconParams(refresh, false)); }
            catch (Exception error) when (error is not OperationCanceledException) { return; }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                icons = set.Svgs;
                RenderAgents(new AcpAgentList(configured));
            });
        });

    private static TextBlock Text(string text, bool detail = false, bool code = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = detail || code ? 12 : 13, Foreground = detail ? Ui.Muted : Ui.TextBrush };
        if (code) block.FontFamily = Ui.CodeFont;
        return block;
    }

    private readonly HashSet<string> expanded = [];
    private IReadOnlyDictionary<string, string> icons = new Dictionary<string, string>();
    private IReadOnlyList<AcpAgentEntry> configured = [];

    private static Control Quiet(string name, string label, Action click)
    {
        var button = new Button { Name = name, Content = label, Classes = { "link" }, Padding = new Avalonia.Thickness(0, 2) };
        button.Click += (_, _) => click();
        return button;
    }

    private static Control StatusIcon(string icon, Avalonia.Media.IBrush color, string description)
    {
        var mark = Ui.Icon(icon, color);
        ToolTip.SetTip(mark, description);
        Avalonia.Automation.AutomationProperties.SetName(mark, description);
        return mark;
    }

    private void RenderAgents(AcpAgentList list)
    {
        configured = list.Agents;
        agents.Children.Clear();
        if (list.Agents.Count == 0) agents.Children.Add(Text("No agent yet. Install one from the registry or add your own below.", detail: true));
        foreach (var agent in list.Agents)
        {
            var open = expanded.Contains(agent.Id);
            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(Text(AcpEntryText.CommandLine(agent), code: true));
            if (open)
            {
                var environment = new TextBox { Text = AcpEntryText.FormatEnvironment(agent.Env), AcceptsReturn = true, PlaceholderText = "Environment, one NAME=value per line", FontFamily = Ui.CodeFont, FontSize = 12, MinHeight = 40 };
                var directory = new TextBox { Text = agent.Cwd, PlaceholderText = "Working directory (blank: the workspace)", FontFamily = Ui.CodeFont, FontSize = 12 };
                void Save() => _ = RunAsync(() => context.RequestAsync(AcpContract.AddAgent, agent with { Env = AcpEntryText.ParseEnvironment(environment.Text ?? ""), Cwd = directory.Text?.Trim() ?? "" }).AsTask(), _ => { }, "Couldn't save the agent");
                environment.LostFocus += (_, _) => Save();
                directory.LostFocus += (_, _) => Save();
                details.Children.Add(environment);
                details.Children.Add(directory);
            }
            var title = Text(agent.Name + (agent.Version is { } version ? "  v" + version : "") + (agent.Origin == "registry" ? "  (registry)" : "  (custom)"));
            title.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
            title.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
            var disclosure = Quiet("AcpAgentDetails_" + agent.Id, open ? "▾" : "▸", () => { if (!expanded.Remove(agent.Id)) expanded.Add(agent.Id); RenderAgents(list); });
            var remove = Quiet("AcpAgentRemove_" + agent.Id, "Remove", () => _ = RunAsync(() => context.RequestAsync(AcpContract.RemoveAgent, new AcpChooseParams("", "", agent.Id)).AsTask(), RenderAgents, "Couldn't remove the agent"));
            agents.Children.Add(RegistryRow.Create("AcpAgent_" + agent.Id, StatusIcon("check", Ui.Success, "Ready to chat"), AcpIconFor(agent.Id), title, disclosure, remove, details));
        }
        // What is configured decides each registry row's Install, Installed or Update.
        RenderRegistry();
    }

    /// <summary>The agent's mark: its registry icon once the host has one, the generic ACP glyph until then and for any agent the registry has no icon for (custom ones).</summary>
    private Control AcpIconFor(string id) => AcpGlyph.For(icons.GetValueOrDefault(id), 16);

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
            var title = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8 };
            var name = Text(model.Title);
            name.TextWrapping = Avalonia.Media.TextWrapping.NoWrap;
            name.TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis;
            title.Children.Add(name);
            if (agent.NotRecommended is { } reason)
            {
                var tag = new Border { Name = "AcpNotRecommended_" + agent.Id, Background = Ui.WarningWash, CornerRadius = new Avalonia.CornerRadius(8), Padding = new Avalonia.Thickness(8, 0), VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center, Child = new TextBlock { Text = "Not recommended", FontSize = 11, Foreground = Ui.Warning } };
                ToolTip.SetTip(tag, reason);
                title.Children.Add(tag);
            }
            var details = new StackPanel { Spacing = 2 };
            details.Children.Add(Text(agent.Description, detail: true));
            if (agent.NotRecommended is { } why) details.Children.Add(Text(why, detail: true));
            details.Children.Add(Text(string.Join(", ", agent.Kinds), detail: true));
            Control action;
            if (model.CanInstall) action = Quiet("AcpInstall_" + agent.Id, model.ActionLabel, () => _ = RunAsync(() => context.RequestAsync(AcpContract.InstallPlan, new AcpInstallPlanParams(agent.Id)).AsTask(), ShowPlan, "Couldn't prepare the install"));
            else action = new TextBlock { Name = "AcpInstalled_" + agent.Id, Text = model.ActionLabel, FontSize = 12, Foreground = Ui.Muted, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center };
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
        await RunAsync(() => context.RequestAsync(AcpContract.Install, new AcpInstallRequest(plan.AgentId, plan.Kind, plan.Version, true)).AsTask(), RenderAgents, "Couldn't install the agent");
    }
}
