using Avalonia.Controls;
using Avalonia.Controls.Primitives;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Chat.UI;

/// <summary>Registers an agent's chat document and opens chats into it: the launcher, resume and fork all end at <see cref="Open"/>.</summary>
internal static class ChatDocuments
{
    /// <summary>Registers the agent's chat document; <paramref name="readFile"/> reads a workspace file for the diff of a proposed edit.</summary>
    public static IDisposable Register(IPluginUIContext context, IChatClient client, ChatAgent agent, Func<string, string, Task<string>> readFile)
    {
        var selection = new SelectionSource();
        var watch = context.Editors.OnEvent(selection.Observe);
        context.Document(new PluginDocumentRegistration(agent.Scheme,
            props => new ChatView(Environment(context, client, agent, readFile, props.WorkspaceId, selection), props.WorkspaceId, props.Id))
        {
            Icon = agent.Icon,
            // Closing the tab ends the child process; the transcript stays on disk and reopening the chat resumes it.
            Closed = props => _ = CloseAsync(context, client, props)
        });
        return watch;
    }

    private static async Task CloseAsync(IPluginUIContext context, IChatClient client, PluginDocumentProps props)
    {
        try { await client.RequestAsync(ChatContract.Close, new ChatKey(props.WorkspaceId, props.Id)); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't end the chat's session", ChatErrors.Text(error));
        }
    }

    /// <summary>The last selection each editor reported: the source the terminal's IDE context shares.</summary>
    internal sealed class SelectionSource
    {
        private readonly Dictionary<string, EditorSelection?> selections = [];

        public void Observe(EditorEvent editorEvent)
        {
            if (editorEvent is EditorSelectionEvent selected) selections[selected.Editor.Id] = selected.Selection;
        }

        public ChatSelection? For(EditorRef? editor, string workspaceId)
        {
            if (editor is not { Kind: EditorKind.File } || editor.WorkspaceId != workspaceId) return null;
            return selections.GetValueOrDefault(editor.Id) switch
            {
                { Text.Length: > 0, Mapped: false } quoted => new ChatSelection(editor.Path, 1, 1, false) { Quote = quoted.Text },
                { Text.Length: > 0 } chosen => new ChatSelection(editor.Path, chosen.StartLine, chosen.EndLine, true),
                _ => new ChatSelection(editor.Path, 1, 1, false)
            };
        }
    }

    public static ChatEnvironment Environment(IPluginUIContext context, IChatClient client, ChatAgent agent, Func<string, string, Task<string>> readFile, string workspaceId, SelectionSource selection) => new(client, agent)
    {
        // A proposal opens SharpRail's own diff of the file against the text the call would write; a call that already ran shows the file's changes.
        OpenDiff = request => OpenDiffAsync(context, readFile, workspaceId, request),
        OpenFile = async path => { if (path.Length > 0) await context.Editors.OpenAsync(workspaceId, path); },
        OpenFileAt = async (path, line) => { if (path.Length > 0) await context.Editors.OpenAsync(workspaceId, path, new() { Line = line > 0 ? line : null }); },
        OpenLink = url => Launch(url),
        ModelPreference = context.Preference("chat-models"),
        ComposerPreference = context.Preference("chat-composer"),
        ZoomPreference = context.Preference("chat-zoom"),
        WorkspaceChips = () => context.WorkspaceChips(workspaceId),
        ActiveSelection = () => selection.For(context.Editors.Active, workspaceId),
        WatchSelection = changed => context.Editors.OnEvent(editorEvent => { if (editorEvent is EditorSelectionEvent or EditorLifecycleEvent) changed(); }),
        PickFile = async () => await context.PickFileAsync(new FilePickOptions(workspaceId)),
        PickFolder = async () => await context.PickFileAsync(new FilePickOptions(workspaceId, true)),
        Notify = context.Notify,
        OpenChat = (chatId, title, open) => Open(context, agent, workspaceId, chatId, title, open)
    };

    private static async Task OpenDiffAsync(IPluginUIContext context, Func<string, string, Task<string>> readFile, string workspaceId, ChatDiffRequest request)
    {
        if (request.Path.Length == 0) return;
        if (request.Propose is null)
        {
            await context.Editors.OpenAsync(workspaceId, request.Path);
            context.SetDiffScope(workspaceId, new UncommittedDiffScope());
            return;
        }
        var current = "";
        try { current = await readFile(workspaceId, request.Path); }
        catch (Exception error) when (error is PluginCallException or IOException) { }
        await context.OpenTextDiffAsync(workspaceId, request.Path, request.Propose(current));
    }

    private static void Launch(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    /// <summary>Opens a chat tab; the open params wait for the view that mounts it.</summary>
    public static void Open(IPluginUIContext context, ChatAgent agent, string workspaceId, string chatId, string title, ChatOpenParams? open = null,
        string? prompt = null, IReadOnlyList<string>? images = null)
    {
        // Two processes writing one transcript would mix two conversations: a session a terminal of this workspace runs is not resumed here.
        if (agent.TerminalKind.Length > 0 && open?.ResumeSessionId is { } session && open.Fork != true &&
            context.Host().Terminals.GetValueOrDefault(workspaceId)?.Any(tab => tab.Agent is { SessionId: { } running } agentTab && agentTab.Kind == agent.TerminalKind && running == session) == true)
        {
            context.Notify(PluginNotificationKind.Error, "That conversation is running in a terminal", "Close that terminal first, or fork it from the sessions list.");
            return;
        }
        if (open is not null) ChatLaunches.Put(open with { WorkspaceId = workspaceId, ChatId = chatId }, prompt, images);
        _ = OpenAsync(context, agent, workspaceId, chatId, title);
    }

    private static async Task OpenAsync(IPluginUIContext context, ChatAgent agent, string workspaceId, string chatId, string title)
    {
        try { await context.OpenDocumentAsync(workspaceId, agent.Scheme, chatId, title); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't open the chat", ChatErrors.Text(error));
        }
    }

    public static string NewId() => "chat-" + Guid.NewGuid().ToString("N")[..12];
}