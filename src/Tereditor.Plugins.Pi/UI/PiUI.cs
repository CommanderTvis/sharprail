using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Chat;
using SharpRail.Plugins.Chat.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Pi.UI;

/// <summary>The Pi plugin's UI half: Settings › Pi, the side pane, the chat document and the launcher that opens a chat.</summary>
public sealed class PiUI : PluginUIModule
{
    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        PiStatus? status = null;
        var client = new PluginChatClient(context);
        // Pi edits files itself and proposes nothing to diff, so there is no file to read for a proposal.
        var selections = ChatDocuments.Register(context, client, PiChat.Agent, (_, _) => Task.FromResult(""));
        _ = Task.Run(async () =>
        {
            try { status = await context.RequestAsync(PiContract.Status, new PiNoParams()); }
            catch (Exception error) when (error is not OperationCanceledException) { }
        });
        context.SideTool(new("pane", _ => PiPanel.Create(context)));
        context.SettingsSection(new("Pi", PiManifest.Icon, () => new PiSettingsView(context, () => status)));
        context.Launcher(new AgentLauncher(PiManifest.Id, "Pi", PiManifest.Icon, _ => context.Settings<PiSettings>().Command,
            () => status is { Installed: false } ? new(false, "Pi is not installed; see https://github.com/badlogic/pi-mono") : new(true))
        {
            Open = (workspace, options) =>
            {
                var open = new ChatOpenParams(workspace, ChatDocuments.NewId()) { Model = options.Model, ResumeSessionId = options.ResumeSessionId };
                ChatDocuments.Open(context, PiChat.Agent, workspace, open.ChatId, "Pi", open, options.InitialPrompt, options.Images);
                return ValueTask.FromResult(true);
            }
        });
        // Like Claude's start button, a chat starts from the strip of a workspace's tabs.
        context.WorkspaceAction(new WorkspaceScopedActionRegistration("pi-chat", (workspace, _) =>
        {
            var button = new Button
            {
                Name = "NewPi",
                Content = PiGlyph.Create(16),
                Width = 32,
                Height = 32,
                Padding = new Avalonia.Thickness(8),
                Background = Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                CornerRadius = new Avalonia.CornerRadius(0)
            };
            AutomationProperties.SetName(button, "New Pi chat");
            ToolTip.SetTip(button, "New Pi chat");
            button.Click += (_, _) =>
            {
                var id = ChatDocuments.NewId();
                ChatDocuments.Open(context, PiChat.Agent, workspace, id, "Pi", new ChatOpenParams(workspace, id), null, []);
            };
            return button;
        }));
        return () => selections.Dispose();
    }
}