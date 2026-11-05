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
    private AcpInstallPlan? pending;

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
        var confirm = this.FindControl<Border>("AcpConfirm")!;
        this.FindControl<Button>("AcpConfirmCancel")!.Click += (_, _) => { pending = null; confirm.IsVisible = false; };
        this.FindControl<Button>("AcpConfirmInstall")!.Click += (_, _) =>
        {
            if (pending is not { } plan) return;
            confirm.IsVisible = false;
            pending = null;
            // The confirmation names exactly the version and distribution the user saw; the host refuses it if the registry moved on.
            _ = RunAsync(() => context.RequestAsync(AcpContract.Install, new AcpInstallRequest(plan.AgentId, plan.Kind, plan.Version, true)).AsTask(), RenderAgents, "Couldn't install the agent");
        };
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
        _ = RunAsync(() => context.RequestAsync(AcpContract.Registry, new AcpRegistryParams(refresh)).AsTask(), view => { registry = view; RenderRegistry(); }, "Couldn't read the registry");
    }

    private static TextBlock Text(string text, bool detail = false, bool code = false)
    {
        var block = new TextBlock { Text = text, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = detail || code ? 12 : 13, Foreground = detail ? Ui.Muted : Ui.TextBrush };
        if (code) block.FontFamily = Ui.CodeFont;
        return block;
    }

    private void RenderAgents(AcpAgentList list)
    {
        agents.Children.Clear();
        if (list.Agents.Count == 0) agents.Children.Add(Text("No agent yet. Install one from the registry or add your own below.", detail: true));
        foreach (var agent in list.Agents)
        {
            var row = new StackPanel { Spacing = 4, Name = "AcpAgent_" + agent.Id };
            row.Children.Add(Text(agent.Name + (agent.Version is { } version ? "  v" + version : "") + (agent.Origin == "registry" ? "  (registry)" : "  (custom)")));
            row.Children.Add(Text(AcpEntryText.CommandLine(agent), code: true));
            var environment = new TextBox { Text = AcpEntryText.FormatEnvironment(agent.Env), AcceptsReturn = true, PlaceholderText = "Environment, one NAME=value per line", FontFamily = Ui.CodeFont, FontSize = 12, MinHeight = 40 };
            var directory = new TextBox { Text = agent.Cwd, PlaceholderText = "Working directory (blank: the workspace)", FontFamily = Ui.CodeFont, FontSize = 12 };
            void Save() => _ = RunAsync(() => context.RequestAsync(AcpContract.AddAgent, agent with { Env = AcpEntryText.ParseEnvironment(environment.Text ?? ""), Cwd = directory.Text?.Trim() ?? "" }).AsTask(), _ => { }, "Couldn't save the agent");
            environment.LostFocus += (_, _) => Save();
            directory.LostFocus += (_, _) => Save();
            row.Children.Add(environment);
            row.Children.Add(directory);
            var remove = new Button { Content = "Remove", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            remove.Click += (_, _) => _ = RunAsync(() => context.RequestAsync(AcpContract.RemoveAgent, new AcpChooseParams("", "", agent.Id)).AsTask(), RenderAgents, "Couldn't remove the agent");
            row.Children.Add(remove);
            agents.Children.Add(row);
        }
    }

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
        var query = search.Text?.Trim() ?? "";
        foreach (var agent in registry.Agents.Where(agent => query.Length == 0 || agent.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || agent.Description.Contains(query, StringComparison.OrdinalIgnoreCase) || agent.Id.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            var row = new StackPanel { Spacing = 2, Name = "AcpRegistryAgent_" + agent.Id };
            row.Children.Add(Text($"{agent.Name}  v{agent.Version}  ({string.Join(", ", agent.Kinds)})"));
            row.Children.Add(Text(agent.Description, detail: true));
            var install = new Button { Content = agent.Installable ? "Install…" : "Not available on this machine", IsEnabled = agent.Installable, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
            install.Click += (_, _) => _ = RunAsync(() => context.RequestAsync(AcpContract.InstallPlan, new AcpInstallPlanParams(agent.Id)).AsTask(), ShowPlan, "Couldn't prepare the install");
            row.Children.Add(install);
            registryList.Children.Add(row);
        }
    }

    private void ShowPlan(AcpInstallPlan plan)
    {
        pending = plan;
        this.FindControl<TextBlock>("AcpConfirmTitle")!.Text = $"Install {plan.Name} {plan.Version}?";
        this.FindControl<TextBlock>("AcpConfirmBody")!.Text =
            $"Source: {plan.Source}\nRuns: {plan.CommandLine}\n{(plan.Sha256 is { } sha ? "SHA-256: " + sha + "\n" : "")}{(plan.License is { } license ? "License: " + license + "\n" : "")}{(plan.Repository is { } repository ? "Repository: " + repository + "\n" : "")}{plan.Note}";
        this.FindControl<Border>("AcpConfirm")!.IsVisible = true;
    }
}