using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Chat;
using SharpRail.Plugins.Chat.UI;

namespace SharpRail.Plugins.Acp.UI;

/// <summary>The ACP plugin's UI half (Early support): the chat document and the button that starts a chat with one of the configured agents.</summary>
public sealed class AcpUI : PluginUIModule
{
    public const string Label = "New ACP chat";

    public override PluginDisposer? Activate(IPluginUIContext context)
    {
        var client = new PluginChatClient(context);
        // The agent edits files through the client methods the host serves, so the diff of a proposal has no file to read here.
        var selections = ChatDocuments.Register(context, client, AcpChat.Agent, (_, _) => Task.FromResult(""));
        context.SettingsSection(new("ACP", AcpManifest.Manifest.Icon, () => new AcpSettingsView(context)));
        context.WorkspaceAction(new WorkspaceScopedActionRegistration("acp-chat", (workspace, group) =>
        {
            var button = new Button
            {
                Name = "NewAcp",
                Content = AcpGlyph.Create(16),
                Width = 32,
                Height = 32,
                Padding = new Avalonia.Thickness(8),
                Background = Brushes.Transparent,
                BorderThickness = new Avalonia.Thickness(0),
                CornerRadius = new Avalonia.CornerRadius(0)
            };
            AutomationProperties.SetName(button, Label);
            ToolTip.SetTip(button, Label);
            button.Click += (_, _) => _ = ChooseAsync(context, button, workspace);
            return button;
        }));
        return () => selections.Dispose();
    }

    private static async Task ChooseAsync(IPluginUIContext context, Button anchor, string workspace)
    {
        AcpAgentList list;
        try { list = await Task.Run(() => context.RequestAsync(AcpContract.Agents, new AcpNoParams()).AsTask()); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't list ACP agents", error.Message);
            return;
        }
        var icons = await CachedIconsAsync(context);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (list.Agents.Count == 0)
            {
                context.Notify(PluginNotificationKind.Info, "No ACP agent yet", "Add an agent in Settings under ACP.");
                return;
            }
            var menu = new ContextMenu { Name = "AcpAgentMenu", Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = anchor };
            foreach (var agent in list.Agents)
            {
                var item = new MenuItem { Header = agent.Name, Icon = AcpGlyph.For(icons.GetValueOrDefault(agent.Id), 14) };
                item.Click += (_, _) => _ = StartAsync(context, workspace, agent);
                menu.Items.Add(item);
            }
            menu.Open(anchor);
        });
    }

    /// <summary>The icons already on disk. The menu never waits for the network: an agent without one shows the generic glyph.</summary>
    private static async Task<IReadOnlyDictionary<string, string>> CachedIconsAsync(IPluginUIContext context)
    {
        try { return (await Task.Run(() => context.RequestAsync(AcpContract.Icons, new AcpIconParams(false, true)).AsTask())).Svgs; }
        catch (Exception error) when (error is not OperationCanceledException) { return new Dictionary<string, string>(); }
    }

    private static async Task StartAsync(IPluginUIContext context, string workspace, AcpAgentEntry agent)
    {
        var id = ChatDocuments.NewId();
        try { await Task.Run(() => context.RequestAsync(AcpContract.Choose, new AcpChooseParams(workspace, id, agent.Id)).AsTask()); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't start the chat", error.Message);
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() => ChatDocuments.Open(context, AcpChat.Agent, workspace, id, agent.Name, new ChatOpenParams(workspace, id), null, []));
    }
}