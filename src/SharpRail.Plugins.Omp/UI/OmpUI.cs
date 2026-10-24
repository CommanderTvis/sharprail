using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Omp.UI;

/// <summary>
/// The omp plugin's UI half: Settings › omp, the side tool, the agent launcher and its start action, and the tab
/// icon of terminals running omp.
/// </summary>
public sealed class OmpUI : PluginUIModule
{
    /// <summary>The launch line with this host's settings; every new launch gets its own session folder.</summary>
    public static string LaunchLine(IPluginUIContext context, LauncherCommandOptions options)
    {
        var settings = context.Settings<OmpSettings>();
        var resume = options.ResumeSessionId;
        return OmpLaunch.Line(settings.Command, new()
        {
            Mcp = settings.Mcp,
            Windows = context.Host().HostPlatform == HostPlatform.Windows,
            UiLaunch = true,
            Model = options.Model is { Length: > 0 } model ? model : settings.Model,
            ApprovalMode = settings.ApprovalMode,
            Advisor = settings.Advisor,
            OwnCodeIntelligence = settings.OwnCodeIntelligence,
            SessionId = resume ?? OmpLaunch.NewSessionId(),
            Continue = resume is not null,
            SystemPrompt = options.SystemPrompt,
            InitialPrompt = options.InitialPrompt,
            Images = options.Images,
            Contributions = context.AgentLaunchContributions(OmpManifest.Id)
        });
    }

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        OmpStatus? status = null;
        OmpGlyph.Reset();
        context.SettingsSection(new("omp", OmpManifest.Icon, () => new OmpSettingsView(context)));
        context.SideTool(new("context", workspace => OmpPanel.Create(context, workspace)));
        context.Launcher(new AgentLauncher(OmpManifest.Id, "omp", OmpManifest.Icon, options => LaunchLine(context, options),
            () => status is { Installed: false } ? new(false, "omp is not installed; see " + OmpLaunch.InstallUrl) : new(true))
        {
            CreateIcon = (size, color) => new OmpGlyph(context, size, color as ISolidColorBrush),
            Models = () => [.. OmpLaunch.ModelSuggestions.Select(model => new LauncherModel(model, model))]
        });
        context.WorkspaceAction(new WorkspaceScopedActionRegistration("omp-launch", (workspace, group) =>
        {
            var button = new Button
            {
                Name = "NewOmp",
                Content = new OmpGlyph(context, 16),
                Width = 32,
                Height = 32,
                Padding = new Thickness(8),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };
            AutomationProperties.SetName(button, "Start omp");
            ToolTip.SetTip(button, "Start omp (early support)");
            button.Click += (_, _) => _ = context.OpenTerminalAsync(workspace, new() { Command = LaunchLine(context, new()), GroupId = group }).AsTask();
            return button;
        }));
        context.WatchHost(host => string.Join("\n", host.Terminals.SelectMany(pair =>
            pair.Value.Where(tab => tab.Agent?.Kind == OmpManifest.Id).Select(tab => pair.Key + "\t" + tab.TabKey))), (_, _) => context.Invalidate());
        context.TabDecoration(tab => tab switch
        {
            TerminalTabRef terminal when context.Host().Terminals.GetValueOrDefault(terminal.WorkspaceId)?
                .FirstOrDefault(item => item.TabKey == terminal.TabKey)?.Agent?.Kind == OmpManifest.Id => new TabDecoration { Icon = OmpManifest.Icon },
            ToolTabRef { Tool: var tool } when tool == OmpManifest.ContextTool => new TabDecoration { Icon = OmpManifest.Icon },
            _ => null
        });
        _ = Probe();
        return null;

        async Task Probe()
        {
            try
            {
                var read = await context.RequestAsync(OmpContract.Status, new OmpNoParams());
                Dispatcher.UIThread.Post(() => { status = read; context.Invalidate(); });
            }
            catch (Exception) { }
        }
    }
}