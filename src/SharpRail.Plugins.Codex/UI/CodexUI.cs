using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>
/// The Codex plugin's UI half: the config side tool, Settings › Codex, the launcher and its start action, the tab
/// decorations with the hook-reported status, the terminal accessory and the IDE-context replies.
/// </summary>
public sealed class CodexUI : PluginUIModule
{
    /// <summary>The launch line with this host's settings: its command, permissions mode and MCP hand-off.</summary>
    public static string LaunchLine(IPluginUIContext context, CodexLaunchOptions options)
    {
        var settings = context.Settings<CodexSettings>();
        return CodexLaunch.Line(settings.Command, options with
        {
            PermissionMode = settings.PermissionMode,
            AppendSystemPrompt = settings.AppendSystemPrompt,
            Mcp = settings.Mcp,
            Windows = context.Host().HostPlatform == HostPlatform.Windows
        });
    }

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        CodexGlyph.Reset();
        var store = new CodexStore();
        var launcherIdeContext = new HashSet<(string, string)>();
        CodexEditorContext.Register(context);
        context.Subscribe(CodexContract.Status, store.Apply);
        _ = LoadModels();

        var hydrated = new HashSet<string>();
        void EnsureHydrated(string workspaceId)
        {
            if (!hydrated.Add(workspaceId)) return;
            _ = Hydrate();

            async Task Hydrate()
            {
                try
                {
                    var rows = await context.RequestAsync(CodexContract.StatusSnapshot, new CodexWorkspaceParams(workspaceId));
                    Dispatcher.UIThread.Post(() => { foreach (var row in rows) store.Apply(row); });
                }
                catch (Exception) { }
            }
        }
        context.OnWorkspaceRemoved(workspaceId =>
        {
            hydrated.Remove(workspaceId);
            store.EvictWorkspace(workspaceId);
        });

        context.SettingsSection(new("Codex", CodexManifest.Icon, () => CodexSettingsSection.Create(context)));
        context.SideTool(new("config", workspace => CodexPanel.Create(context, workspace)));

        context.Launcher(new AgentLauncher(CodexManifest.Id, "Codex", CodexManifest.Icon,
            options => LaunchLine(context, new(true, false)
            {
                Model = options.Model,
                SystemPrompt = options.SystemPrompt,
                InitialPrompt = options.InitialPrompt,
                Resume = options.ResumeSessionId is not null,
                ResumeSessionId = options.ResumeSessionId
            }),
            () => new(true))
        {
            CreateIcon = (size, color) => new CodexGlyph(context, size, color as ISolidColorBrush),
            Models = () => [.. store.Models.Select(model => new LauncherModel(model.Id, model.Label, "openai-line"))]
        });

        context.WorkspaceAction(new WorkspaceScopedActionRegistration("codex-launch", (workspace, group) =>
            CodexLauncher.Create(context, store, preset => _ = Start(workspace, group, preset))));

        // Core re-reads decorations on plugin invalidation, not on agent records, so a new or departing Codex invalidates.
        context.WatchHost(host => string.Join("\n", host.Terminals.SelectMany(pair =>
            pair.Value.Where(tab => tab.Agent?.Kind == CodexManifest.Id).Select(tab => pair.Key + "\t" + tab.TabKey))), (_, _) => context.Invalidate());
        context.TabDecoration(tab =>
        {
            switch (tab)
            {
                case TerminalTabRef terminal:
                    var agent = context.Host().Terminals.GetValueOrDefault(terminal.WorkspaceId)?.FirstOrDefault(item => item.TabKey == terminal.TabKey)?.Agent;
                    if (agent?.Kind != CodexManifest.Id) return null;
                    EnsureHydrated(terminal.WorkspaceId);
                    return new TabDecoration { Icon = CodexManifest.Icon, Adornment = () => new CodexStatusBadge(store, terminal.WorkspaceId, terminal.TabKey) };
                case ToolTabRef { Tool: var tool } when tool == CodexManifest.ConfigTool:
                    return new TabDecoration { Icon = CodexManifest.Icon };
                default:
                    return null;
            }
        });

        context.TerminalAccessory(new(terminal => new CodexTerminalFacts(context, store, terminal,
            accessory => launcherIdeContext.Contains((accessory.WorkspaceId, accessory.TabKey)))));
        return null;

        async Task LoadModels()
        {
            try
            {
                var models = await context.RequestAsync(CodexContract.Models, new CodexNoParams());
                Dispatcher.UIThread.Post(() => store.SetModels(models));
            }
            catch (Exception) { }
        }

        async Task Start(string workspace, string group, CodexLaunchPreset? preset)
        {
            var tabKey = await context.OpenTerminalAsync(workspace, new() { Command = LaunchLine(context, new(true, false) { Preset = preset }), GroupId = group });
            if (context.Settings<CodexSettings>().IdeContext) launcherIdeContext.Add((workspace, tabKey));
        }
    }
}

/// <summary>The status a Codex terminal's tab wears: a spinner while running, the warning glyph when blocked, a dot when done.</summary>
internal sealed class CodexStatusBadge : ContentControl
{
    private readonly CodexStore store;
    private readonly string workspaceId;
    private readonly string tabKey;
    private CodexStatus? shown;

    public CodexStatusBadge(CodexStore store, string workspaceId, string tabKey)
    {
        this.store = store; this.workspaceId = workspaceId; this.tabKey = tabKey;
        Name = "TerminalCodexStatus";
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(4, 0, 0, 0);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        store.Changed += Update;
        shown = null;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        store.Changed -= Update;
    }

    private void Update()
    {
        var status = store.Session(workspaceId, tabKey)?.Status;
        if (status == shown && Content is not null) return;
        shown = status;
        Tag = status?.ToString().ToLowerInvariant();
        var label = status switch
        {
            CodexStatus.Running => "Codex is working",
            CodexStatus.Blocked => "Codex: Action required",
            CodexStatus.Done => "Codex finished",
            _ => null
        };
        ToolTip.SetTip(this, label);
        AutomationProperties.SetName(this, label);
        IsVisible = label is not null;
        Content = status switch
        {
            CodexStatus.Running => new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), BorderBrush = Ui.Muted, BorderThickness = new Thickness(2) },
            CodexStatus.Blocked => Ui.Icon("alertWarning", Ui.Warning, 14),
            CodexStatus.Done => new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Background = Ui.Success },
            _ => null
        };
    }
}