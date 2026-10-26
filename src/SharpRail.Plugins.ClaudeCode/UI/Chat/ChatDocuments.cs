using Avalonia.Controls;
using Avalonia.Controls.Primitives;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.ClaudeCode.UI.Chat;

/// <summary>Registers the chat document and opens chats into it: the launcher, resume and fork all end at <see cref="Open"/>.</summary>
internal static class ChatDocuments
{
    public static IDisposable Register(IPluginUIContext context, IChatClient client)
    {
        var selection = new SelectionSource();
        var watch = context.Editors.OnEvent(selection.Observe);
        context.Document(new PluginDocumentRegistration(ChatContract.DocumentScheme,
            props => new ChatView(Environment(context, client, props.WorkspaceId, selection), props.WorkspaceId, props.Id))
        {
            Icon = "asset:claude.svg",
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
            context.Notify(PluginNotificationKind.Error, "Couldn't end the chat's session", ClaudeParts.ErrorText(error));
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

    public static ChatEnvironment Environment(IPluginUIContext context, IChatClient client, string workspaceId, SelectionSource selection) => new(client)
    {
        PermissionModes = async () => await PermissionModeSource.RefreshAsync(context),
        // A proposal opens SharpRail's own diff of the file against the text the call would write; a call that already ran shows the file's changes.
        OpenDiff = request => OpenDiffAsync(context, workspaceId, request),
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
        OpenChat = (chatId, title, open) => Open(context, workspaceId, chatId, title, open)
    };

    private static async Task OpenDiffAsync(IPluginUIContext context, string workspaceId, ChatDiffRequest request)
    {
        if (request.Path.Length == 0) return;
        if (request.Propose is null)
        {
            await context.Editors.OpenAsync(workspaceId, request.Path);
            context.SetDiffScope(workspaceId, new UncommittedDiffScope());
            return;
        }
        var current = "";
        try { current = (await context.RequestAsync(ClaudeCodeContract.ReadFile, new ReadFileParams(workspaceId, request.Path))).Content; }
        catch (Exception error) when (error is PluginCallException or IOException) { }
        await context.OpenTextDiffAsync(workspaceId, request.Path, request.Propose(current));
    }

    private static void Launch(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    /// <summary>Opens a chat tab; the open params wait for the view that mounts it.</summary>
    public static void Open(IPluginUIContext context, string workspaceId, string chatId, string title, ChatOpenParams? open = null,
        string? prompt = null, IReadOnlyList<string>? images = null)
    {
        // Two Claude Code processes writing one transcript would mix two conversations: a session a terminal of this workspace runs is not resumed here.
        if (open?.ResumeSessionId is { } session && open.Fork != true &&
            context.Host().Terminals.GetValueOrDefault(workspaceId)?.Any(tab => tab.Agent is { Kind: "claude", SessionId: { } running } && running == session) == true)
        {
            context.Notify(PluginNotificationKind.Error, "That conversation is running in a terminal", "Close that terminal first, or fork it from the sessions list.");
            return;
        }
        if (open is not null) ChatLaunches.Put(open with { WorkspaceId = workspaceId, ChatId = chatId }, prompt, images);
        _ = OpenAsync(context, workspaceId, chatId, title);
    }

    private static async Task OpenAsync(IPluginUIContext context, string workspaceId, string chatId, string title)
    {
        try { await context.OpenDocumentAsync(workspaceId, ChatContract.DocumentScheme, chatId, title); }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't open the chat", ClaudeParts.ErrorText(error));
        }
    }

    public static string NewId() => "chat-" + Guid.NewGuid().ToString("N")[..12];
}

/// <summary>Starts Claude Code as a chat tab when the setting says so, from the same presets the terminal launcher offers.</summary>
internal static class ChatLauncher
{
    /// <summary>Presets a chat can honour; anything else (teleport, free-form flags) still starts the terminal.</summary>
    public static bool Handles(string args)
    {
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 || words is ["--continue"] or ["--resume"] or ["--resume", "--fork-session"] or ["--model", _];
    }

    public static ChatOpenParams Params(string workspaceId, string chatId, ClaudeCodeSettings settings, string args, string? permissionMode)
    {
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var model = words is ["--model", var named] ? named : settings.Model;
        return new ChatOpenParams(workspaceId, chatId)
        {
            Model = model.Length > 0 ? model : null,
            Effort = settings.Effort.Length > 0 ? settings.Effort : null,
            PermissionMode = permissionMode is { Length: > 0 } ? permissionMode : null
        };
    }

    /// <summary>The launcher's <c>Open</c>: Create-workspace and Project Home starts, in Chat mode, open a chat tab with the opening prompt.</summary>
    public static ValueTask<bool> OpenFromLauncher(IPluginUIContext context, string workspaceId, LauncherCommandOptions options)
    {
        var settings = context.Settings<ClaudeCodeSettings>();
        if (settings.Mode != ClaudeCodeMode.Chat) return ValueTask.FromResult(false);
        var open = Params(workspaceId, ChatDocuments.NewId(), settings, options.Model is { } model ? "--model " + model : "", settings.PermissionMode) with { ResumeSessionId = options.ResumeSessionId };
        ChatDocuments.Open(context, workspaceId, open.ChatId, "Claude Code", open, options.InitialPrompt, options.Images);
        return ValueTask.FromResult(true);
    }

    public static async Task StartAsync(IPluginUIContext context, IChatClient client, string workspaceId, string args, string? permissionMode, Control anchor)
    {
        var settings = context.Settings<ClaudeCodeSettings>();
        var open = Params(workspaceId, ChatDocuments.NewId(), settings, args, permissionMode);
        if (args.StartsWith("--continue", StringComparison.Ordinal) || args.StartsWith("--resume", StringComparison.Ordinal))
        {
            ChatSessionList sessions;
            try { sessions = await client.RequestAsync(ChatContract.Sessions, new ChatSessionsParams(workspaceId)); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                context.Notify(PluginNotificationKind.Error, "Couldn't list sessions", ClaudeParts.ErrorText(error));
                return;
            }
            var fork = args.Contains("--fork-session", StringComparison.Ordinal);
            if (args.StartsWith("--continue", StringComparison.Ordinal))
            {
                // A session another tab runs is focused, never started a second time.
                if (sessions.Sessions.FirstOrDefault() is { ChatId: { } running } busy) ChatDocuments.Open(context, workspaceId, running, busy.Title);
                else if (sessions.Sessions.FirstOrDefault() is { } latest) ChatDocuments.Open(context, workspaceId, open.ChatId, latest.Title, open with { ResumeSessionId = latest.SessionId });
                else ChatDocuments.Open(context, workspaceId, open.ChatId, "Claude Code", open);
                return;
            }
            var menu = new ContextMenu { Name = "ChatResumeMenu", Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var session in sessions.Sessions.Take(30))
            {
                var when = DateTime.TryParse(session.LastModified, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? ChatView.RelativeTime(parsed.ToLocalTime(), DateTime.Now) : session.LastModified;
                var item = new MenuItem { Header = $"{session.Title}  ·  {when}" + (session.ChatId is null ? "" : "  ·  open in another tab") };
                item.Click += (_, _) =>
                {
                    if (session.ChatId is { } running && !fork) ChatDocuments.Open(context, workspaceId, running, session.Title);
                    else ChatDocuments.Open(context, workspaceId, open.ChatId, session.Title, open with { ResumeSessionId = session.SessionId, Fork = fork ? true : null });
                };
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No earlier sessions", IsEnabled = false });
            menu.Open(anchor);
            return;
        }
        ChatDocuments.Open(context, workspaceId, open.ChatId, "Claude Code", open);
    }
}