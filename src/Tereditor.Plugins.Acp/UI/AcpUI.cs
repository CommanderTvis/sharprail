using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.UI;
using Tereditor.Plugins.Chat;
using Tereditor.Plugins.Chat.UI;

namespace Tereditor.Plugins.Acp.UI;

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
            var menu = new ContextMenu { Name = "AcpLaunchMenu", Placement = PlacementMode.BottomEdgeAlignedLeft };
            // A touch screen has no right click: a tap opens the menu, which lists every agent.
            if (OperatingSystem.IsAndroid()) button.Click += (_, _) => menu.Open(button);
            else
            {
                ToolTip.SetTip(button, Label + " (right-click to choose an agent)");
                button.Click += (_, _) => _ = LaunchAsync(context, button, workspace);
            }
            menu.Opening += (_, _) => _ = FillMenuAsync(context, menu, workspace);
            _ = FillMenuAsync(context, menu, workspace);
            button.ContextMenu = menu;
            return button;
        }));
        return () => selections.Dispose();
    }

    /// <summary>Fills the right-click menu with every configured agent, the default marked, and a way to the settings. It shows the last list at once and refreshes while it opens.</summary>
    private static async Task FillMenuAsync(IPluginUIContext context, ContextMenu menu, string workspace)
    {
        AcpAgentList list;
        try { list = await Task.Run(() => context.RequestAsync(AcpContract.Agents, new AcpNoParams()).AsTask()); }
        catch (Exception error) when (error is not OperationCanceledException) { return; }
        var icons = await CachedIconsAsync(context);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var defaultId = context.Settings<AcpSettings>().DefaultAgent;
            menu.Items.Clear();
            foreach (var (agent, isDefault) in AcpLauncher.MenuEntries(defaultId, list.Agents))
            {
                var item = new MenuItem { Name = "AcpLaunch_" + agent.Id, Header = isDefault ? agent.Name + " (default)" : agent.Name, Icon = AcpGlyph.For(icons.GetValueOrDefault(agent.Id), 14) };
                item.Click += (_, _) => _ = StartAsync(context, workspace, agent);
                menu.Items.Add(item);
            }
            if (list.Agents.Count > 0) menu.Items.Add(new Separator());
            var settings = new MenuItem { Name = "AcpLaunch_settings", Header = "ACP settings" };
            settings.Click += (_, _) => context.OpenSettings(UiPages.PluginSection(AcpManifest.Id));
            menu.Items.Add(settings);
        });
    }

    /// <summary>A left click: the default agent starts at once. Without a usable default it is the picker, or the only agent when there is just one.</summary>
    private static async Task LaunchAsync(IPluginUIContext context, Button anchor, string workspace)
    {
        AcpAgentList list;
        try { list = await Task.Run(() => context.RequestAsync(AcpContract.Agents, new AcpNoParams()).AsTask()); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't list ACP agents", error.Message);
            return;
        }
        var choice = AcpLauncher.Resolve(context.Settings<AcpSettings>().DefaultAgent, list.Agents);
        if (choice is { Kind: "start", Agent: { } agent }) { await StartAsync(context, workspace, agent); return; }
        var icons = await CachedIconsAsync(context);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (choice.Kind == "none")
            {
                context.Notify(PluginNotificationKind.Info, "No ACP agent yet", "Add an agent in Settings under ACP.");
                return;
            }
            var menu = new ContextMenu { Name = "AcpAgentMenu", Placement = PlacementMode.BottomEdgeAlignedLeft, PlacementTarget = anchor };
            foreach (var agent in AcpLauncher.Enabled(list.Agents))
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