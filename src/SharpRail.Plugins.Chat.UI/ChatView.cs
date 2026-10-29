using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Chat.UI;

/// <summary>What the chat view reaches outside itself for: the wire client and the few workbench actions a chat triggers.</summary>
internal sealed record ChatEnvironment(IChatClient Client, ChatAgent Agent)
{
    /// <summary>Opens SharpRail's own diff view for a file: of the proposed change, or of what the call already changed.</summary>
    public Func<ChatDiffRequest, Task> OpenDiff { get; init; } = _ => Task.CompletedTask;
    public Func<string, Task> OpenFile { get; init; } = _ => Task.CompletedTask;
    /// <summary>Follows a link that passed the safe-scheme check.</summary>
    /// <summary>Opens a workspace file at a one-based line (0 for none).</summary>
    public Func<string, int, Task> OpenFileAt { get; init; } = (_, _) => Task.CompletedTask;
    public Action<string> OpenLink { get; init; } = _ => { };
    public Func<Task<string?>> PickFile { get; init; } = () => Task.FromResult<string?>(null);
    public Func<Task<string?>> PickFolder { get; init; } = () => Task.FromResult<string?>(null);
    public Action<PluginNotificationKind, string, string?> Notify { get; init; } = (_, _, _) => { };
    /// <summary>Opens another chat of this workspace in its own tab; the open params (resume, fork) wait for that tab's view.</summary>
    public Action<string, string, ChatOpenParams?> OpenChat { get; init; } = (_, _, _) => { };
    /// <summary>The active editor's file and selection, for the "include selection" toggle; null when no editor is active.</summary>
    public Func<ChatSelection?> ActiveSelection { get; init; } = () => null;
    /// <summary>Calls back (on any thread) whenever the active editor or its selection may have changed; dispose to stop.</summary>
    public Func<Action, IDisposable> WatchSelection { get; init; } = _ => EmptyWatch.Instance;
    /// <summary>The model picker's favourites, recents and hidden models: client-local, never in a project.</summary>
    public SharpRail.Plugins.Api.UI.IPluginPreference ModelPreference { get; init; } = new MemoryPreference();
    /// <summary>The composer's own choices (Enter sends, recent prompts), client-local like the model preferences.</summary>
    public SharpRail.Plugins.Api.UI.IPluginPreference ComposerPreference { get; init; } = new MemoryPreference();
    /// <summary>Each chat's own zoom, by chat id, client-local.</summary>
    public SharpRail.Plugins.Api.UI.IPluginPreference ZoomPreference { get; init; } = new MemoryPreference();
    public Action<string> SetTitle { get; init; } = _ => { };
    /// <summary>The environment chips (mise, direnv, JVM) of the chat's workspace, the same rows a terminal shows.</summary>
    public Func<IReadOnlyList<Control>> WorkspaceChips { get; init; } = () => [];
}

internal sealed class EmptyWatch : IDisposable
{
    public static readonly EmptyWatch Instance = new();
    public void Dispose() { }
}

/// <summary>A preference held in memory, for a view that is not mounted in a workbench.</summary>
internal sealed class MemoryPreference : SharpRail.Plugins.Api.UI.IPluginPreference
{
    private string? value;
    public string? Get() => value;
    public void Set(string next) => value = next;
    public void Remove() => value = null;
}

/// <summary>A diff to open: <paramref name="Propose"/> turns the file's current text into the proposed text, or is null when the file already holds the change.</summary>
internal sealed record ChatDiffRequest(string Path, Func<string, string>? Propose);

/// <summary>The active editor's file, and the lines selected in it when something is.</summary>
internal sealed record ChatSelection(string Path, int StartLine, int EndLine, bool HasRange)
{
    /// <summary>The selected text when it maps to no source lines (a rendered preview): the prompt quotes it instead of naming lines.</summary>
    public string? Quote { get; init; }

    /// <summary>The <c>@path#Lstart-end</c> mention the extension's at_mentioned carries.</summary>
    public string Mention() => "@" + TerminalFacts.QuoteAttachPath(Path) + (HasRange ? $"#L{StartLine}-{EndLine}" : "") + (Quote is { } quote ? "\n> " + quote.ReplaceLineEndings("\n").Replace("\n", "\n> ") : "");
}

/// <summary>Open params waiting for the tab that will mount a chat: a launcher, resume or fork sets them, the view takes them once.</summary>
internal static class ChatLaunches
{
    private static readonly Dictionary<(string, string), Start> pending = [];

    /// <summary>What a chat starts with: how to open it and, for a launcher start, the first prompt and its image paths.</summary>
    internal sealed record Start(ChatOpenParams Open, string? Prompt = null, IReadOnlyList<string>? Images = null);

    public static void Put(ChatOpenParams open, string? prompt = null, IReadOnlyList<string>? images = null)
    {
        lock (pending) pending[(open.WorkspaceId, open.ChatId)] = new Start(open, prompt, images);
    }

    public static Start Take(string workspaceId, string chatId)
    {
        lock (pending)
            return pending.Remove((workspaceId, chatId), out var start) ? start : new Start(new ChatOpenParams(workspaceId, chatId));
    }
}

/// <summary>
/// An agent conversation: the transcript list, the chips, the task strip and the composer. It
/// subscribes to the chat's channel, asks the host to open (or revive) the session and then only applies pushes by id.
/// </summary>
internal sealed partial class ChatView : Grid, IChatRowActions
{
    private readonly ChatEnvironment env;
    private readonly string workspaceId;
    private readonly string chatId;
    private readonly ChatKey key;
    private readonly ChatTranscript transcript = new();
    private readonly ChatList list;
    // Every chip and icon button lives in the composer's footer, in one flow: pickers, then the facts, then the buttons.
    private readonly WrapPanel chips = new() { Name = "ChatChips", Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, ItemSpacing = Space.Gap, LineSpacing = Space.Gap };
    private readonly WrapPanel tasks = new() { Name = "ChatTasks", Orientation = Orientation.Horizontal };
    private readonly TextBlock title = new() { Name = "ChatTitle", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Border banner = new() { Name = "ChatBanner", IsVisible = false, Padding = new Thickness(12, 6) };
    private readonly TextBlock bannerText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border notice = new() { Name = "ChatNoticeBanner", IsVisible = false, Padding = new Thickness(12, 6) };
    private readonly TextBlock noticeText = new() { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly HashSet<string> dismissedBanners = [];
    private readonly Button jump;
    private readonly Queue<ChatPush> inbox = new();
    private readonly HashSet<string> answering = [];
    private IDisposable? subscription;
    private bool flushing;
    private bool started;
    private string chipSignature = "";
    private bool densityPosted;
    private string? pendingModel, pendingEffort, pendingMode;
    private IReadOnlyList<ChatMode> modes = [];
    private readonly bool ideInitial = true;
    private bool ide;

    internal ChatView(ChatEnvironment env, string workspaceId, string chatId)
    {
        this.env = env; this.workspaceId = workspaceId; this.chatId = chatId;
        key = new ChatKey(workspaceId, chatId);
        ide = ideInitial;
        Name = "ChatView";
        InitZoom();
        list = new ChatList(transcript, this);
        jump = new Button
        {
            Name = "ChatJumpToLatest",
            Content = Ui.Text("Jump to latest", Ui.TextBrush, 12),
            Padding = new Thickness(10, 4),
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
            IsVisible = false
        };
        jump.Click += (_, _) => list.JumpToLatest();
        list.StuckChanged += stuck => jump.IsVisible = !stuck && transcript.Count > 0;
        transcript.HeaderChanged += OnHeader;

        tasks.Margin = new Thickness(12, 0, 12, 4);
        Place(tasks, 1);
        var restart = Ui.Button("Restart", () => _ = Open(restartFrom: transcript.Header?.SessionId));
        restart.Name = "ChatRestart";
        var bannerRow = new DockPanel();
        DockPanel.SetDock(restart, Dock.Right);
        bannerRow.Children.Add(restart);
        bannerRow.Children.Add(bannerText);
        banner.Child = bannerRow;
        // The rate-limit or error banner the host formats: dismissed per key, and back when the key changes.
        var dismiss = Ui.IconButton("close", "Dismiss", () =>
        {
            if (transcript.Header?.Banner is { } shown) dismissedBanners.Add(shown.Key);
            notice.IsVisible = false;
        });
        dismiss.Name = "ChatNoticeDismiss";
        var noticeRow = new DockPanel();
        DockPanel.SetDock(dismiss, Dock.Right);
        noticeRow.Children.Add(dismiss);
        noticeRow.Children.Add(noticeText);
        notice.Child = noticeRow;
        var banners = new StackPanel();
        banners.Children.Add(banner);
        banners.Children.Add(notice);
        Place(banners, 2);
        AddHandler(KeyDownEvent, ViewKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var body = new Grid();
        body.Children.Add(list);
        body.Children.Add(jump);
        Place(body, 3);
        Place(requestPanel, 4);
        Place(BuildComposer(), 5);

        // Size changes arrive in steps while the page zooms: the footer is fitted once per frame, not once per step.
        SizeChanged += (_, _) =>
        {
            if (densityPosted) return;
            densityPosted = true;
            Dispatcher.UIThread.Post(() => { densityPosted = false; ApplyDensity(); }, DispatcherPriority.Render);
        };
        AttachedToVisualTree += (_, _) => { Start(); if (TopLevel.GetTopLevel(this) is Window window) { deactivating = window; window.Deactivated += Dismiss; } };
        DetachedFromVisualTree += (_, _) => { Stop(); if (deactivating is { } window) window.Deactivated -= Dismiss; deactivating = null; };
    }

    private Window? deactivating;

    // Switching to another window leaves no popup hanging over this one.
    private void Dismiss(object? sender, EventArgs e)
    {
        menu.IsOpen = false;
        ChatPopup.CloseAll();
    }

    // The system opener for a link, falling back to the host's.
    private void OpenUrl(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && TopLevel.GetTopLevel(this)?.Launcher is { } launcher) _ = launcher.LaunchUriAsync(uri);
        else env.OpenLink(url);
    }

    private void Place(Control child, int row)
    {
        SetRow(child, row);
        content.Children.Add(child);
    }

    internal ChatTranscript Transcript => transcript;
    internal ChatList List => list;

    // The subscription delivers the snapshot first, so a tab shown again repaints from it in place; opening is idempotent on the host.
    private void Start()
    {
        if (subscription is not null) return;
        subscription = env.Client.Subscribe(key, Enqueue);
        WatchEditorSelection();
        if (!started)
        {
            started = true;
            _ = Open();
        }
        _ = LoadModes();
    }

    private void Stop()
    {
        subscription?.Dispose();
        subscription = null;
        UnwatchEditorSelection();
    }

    private async Task LoadModes()
    {
        try { modes = await env.Agent.Modes(); }
        catch (Exception error) when (error is not OperationCanceledException) { }
    }

    private async Task Open(string? restartFrom = null)
    {
        try
        {
            var start = ChatLaunches.Take(workspaceId, chatId);
            var launch = restartFrom is not null ? start.Open with { ResumeSessionId = restartFrom } : start.Open;
            var header = await env.Client.RequestAsync(ChatContract.Open, launch);
            Enqueue(new ChatHeaderPush(workspaceId, chatId, header));
            // A launcher start brings its first prompt along; it goes once the child is ready, as a prompt sent earlier would be refused.
            if (start.Prompt is { Length: > 0 } || start.Images is { Count: > 0 }) firstPrompt = start;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Enqueue(new ChatHeaderPush(workspaceId, chatId, new ChatHeader(ChatStatus.Failed) { Error = Explain(env.Agent, error) }));
        }
    }

    /// <summary>What a failed open tells the user: a host that has no chat handler says so in plain words instead of the wire's message.</summary>
    internal static string Explain(ChatAgent agent, Exception error) =>
        error is PluginCallException { Error: PluginCallError.Unknown or PluginCallError.Disabled } || error.Message.Contains("registered no handler", StringComparison.Ordinal)
            ? $"This host does not provide {agent.Name} chat mode (its {agent.Name} plugin is missing, disabled or too old). Update or restart the host{agent.UnavailableHint}"
            : ChatErrors.Text(error);

    /// <summary>Queues a push; the queue is applied once per frame, and consecutive updates of one item collapse to the last.</summary>
    internal void Enqueue(ChatPush push)
    {
        if (push.WorkspaceId != workspaceId || push.ChatId != chatId) return;
        inbox.Enqueue(push);
        if (flushing) return;
        flushing = true;
        Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
    }

    internal void Flush()
    {
        flushing = false;
        var batch = inbox.ToArray();
        inbox.Clear();
        var later = new HashSet<string>();
        var skip = new bool[batch.Length];
        for (var index = batch.Length - 1; index >= 0; index--)
        {
            if (batch[index] is ChatResetPush) later.Clear();
            else if (batch[index] is ChatItemPush { Item: var item } && !later.Add(item.Id)) skip[index] = true;
        }
        for (var index = 0; index < batch.Length; index++)
            if (!skip[index]) transcript.Apply(batch[index]);
        ShowQueue();
        UpdateRequests();
        if (list.Stuck) list.FollowEnd();
        jump.IsVisible = !list.Stuck && transcript.Count > 0;
    }

    // Every chip shows the header's facts; a pick is shown as pending until a returned or pushed header agrees.
    private ChatLaunches.Start? firstPrompt;
    private string? pendingFromHeader;
    private readonly Dictionary<string, ChatTaskStatus> taskStatus = [];

    // A background command, monitor or workflow that finishes leaves a notice in the conversation, as upstream's completion row.
    private void NoteTaskCompletions(ChatHeader header)
    {
        foreach (var task in header.Tasks)
        {
            if (taskStatus.TryGetValue(task.TaskId, out var before) && before == ChatTaskStatus.Running && task.Status != ChatTaskStatus.Running && task.Kind != ChatTaskKind.Subagent)
            {
                var what = task.Kind switch { ChatTaskKind.Shell => "Background command", ChatTaskKind.Monitor => "Monitor", ChatTaskKind.Workflow => "Workflow", _ => "Background task" };
                var outcome = task.Status switch { ChatTaskStatus.Done => "finished", ChatTaskStatus.Failed => "failed", _ => "was stopped" };
                transcript.Upsert(new ChatNoticeItem("bg:" + task.TaskId, long.MaxValue, task.Status == ChatTaskStatus.Failed ? ChatNoticeLevel.Error : ChatNoticeLevel.Info, $"{what} {outcome}: {OneLine(task.Description)}"));
            }
            taskStatus[task.TaskId] = task.Status;
        }
    }

    // A shell task's description is its whole command: a notice or tooltip carries its first line, cut at about 120 characters.
    internal static string OneLine(string? description)
    {
        var line = ChatRowLayout.Preview(description);
        return line.Length > 120 ? line[..119].TrimEnd() + "\u2026" : line;
    }

    private void OnHeader(ChatHeader header)
    {
        NoteTaskCompletions(header);
        if (firstPrompt is { } first && header.Status == ChatStatus.Idle)
        {
            firstPrompt = null;
            _ = SendText(first.Prompt ?? "", first.Images is { Count: > 0 } images ? [.. images.Select(path => new ChatAttachment(path.Replace('\\', '/').Split('/')[^1], path))] : null);
        }
        if (pendingModel is not null && header.Model == pendingModel) pendingModel = null;
        if (pendingEffort is not null && header.Effort == pendingEffort) pendingEffort = null;
        if (pendingMode is not null && header.PermissionMode == pendingMode) pendingMode = null;
        if (header.Pending is null && pendingFromHeader is { } owned)
        {
            if (pendingEffort == owned) pendingEffort = null;
            if (pendingModel == owned) pendingModel = null;
            if (pendingMode == owned) pendingMode = null;
            pendingFromHeader = null;
        }
        // A change the host reports under way ("effort high" while the child restarts) shows on its chip as the user's own pick does.
        if (header.Pending is { Length: > 0 } underway && underway.Split(' ', 2) is [var what, var wanted])
        {
            if (what == "effort") pendingEffort ??= wanted;
            else if (what == "model") pendingModel ??= wanted;
            else if (what is "mode" or "permission") pendingMode ??= wanted;
            pendingFromHeader = wanted;
        }
        title.Text = header.Title is { Length: > 0 } text ? text : env.Agent.Name;
        env.SetTitle(title.Text);
        var failed = header.Status is ChatStatus.Failed or ChatStatus.Exited;
        banner.IsVisible = failed || header.Status == ChatStatus.Starting;
        banner.Background = failed ? Ui.DangerWash : Ui.Elevated;
        bannerText.Foreground = failed ? Ui.Danger : Ui.Muted;
        bannerText.Text = header.Status switch
        {
            ChatStatus.Starting => $"Starting {env.Agent.Name}…",
            ChatStatus.Exited => env.Agent.Name + " exited." + (header.Error is { Length: > 0 } exited ? " " + exited : ""),
            ChatStatus.Failed => env.Agent.Name + " failed." + (header.Error is { Length: > 0 } error ? " " + error : ""),
            _ => ""
        };
        if (banner.Child is DockPanel { Children: [Button restart, ..] }) restart.IsVisible = failed;
        var extra = header.Banner is { } host && !dismissedBanners.Contains(host.Key) ? host : null;
        notice.IsVisible = extra is not null;
        if (extra is not null)
        {
            notice.Background = extra.Level == ChatNoticeLevel.Error ? Ui.DangerWash : extra.Level == ChatNoticeLevel.Warning ? Ui.WarningWash : Ui.InfoWash;
            noticeText.Foreground = extra.Level == ChatNoticeLevel.Error ? Ui.Danger : extra.Level == ChatNoticeLevel.Warning ? Ui.Warning : Ui.TextBrush;
            noticeText.Text = extra.Text;
        }
        RebuildChips(header);
        RebuildTasks(header);
        ShowState(header);
    }

    // Shift+Tab cycles the permission mode, and Esc interrupts the turn from anywhere in the view: not while a request is waiting for an answer
    // (Esc declines that), and not while a menu or the history overlay has the key.
    private void ViewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers == KeyModifiers.Shift)
        {
            CycleMode();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && status == ChatStatus.Running && transcript.PendingCount == 0 && !menu.IsOpen && !HistoryOpen)
        {
            _ = Interrupt();
            e.Handled = true;
        }
    }

    /// <summary>The modes the session offers, in menu order: the host's list, or the machine's own when the host has sent none.</summary>
    private IReadOnlyList<ChatMode> ModeList(ChatHeader? header) => header is { PermissionModes.Count: > 0 } ? header.PermissionModes

        // Without the host's list the machine's own is used, but bypass is only offered when the session already runs in it: the CLI refuses it otherwise.
        : [.. modes.Concat(header?.PermissionMode is { Length: > 0 } saved && modes.All(mode => mode.Key != saved) ? [env.Agent.DescribeMode(saved)] : [])
            .Where(mode => !mode.Dangerous || mode.Key == header?.PermissionMode).Select(mode => mode with { Label = env.Agent.ModeLabel(mode) })];

    /// <summary>Moves to the next permission mode, wrapping at the end.</summary>
    internal void CycleMode()
    {
        var offered = ModeList(transcript.Header);
        if (offered.Count == 0) return;
        var current = pendingMode ?? transcript.Header?.PermissionMode;
        var at = offered.ToList().FindIndex(mode => mode.Key == current);
        _ = SetMode(offered[(at + 1) % offered.Count].Key);
    }

    private IReadOnlyList<Control>? environmentChips;

    /// <summary>
    /// Gives a fact chip the pickers' look: no box, the same padding, text size and weight. The chips set these as local values, so a style
    /// cannot; rows that fill in later (the environment chips) are followed as their children arrive.
    /// </summary>
    private static void Flatten(Control control)
    {
        switch (control)
        {
            case Border { OpacityMask: not null }: break; // an icon: its fill is the glyph
            case Border border:
                border.Background = Brushes.Transparent;
                border.Padding = new Thickness(4, 2);
                border.MaxWidth = double.PositiveInfinity;
                border.Margin = new Thickness(0);
                if (border.Child is { } inner) Flatten(inner);
                break;
            case TextBlock text:
                text.FontSize = 12;
                text.FontWeight = Ui.InterfaceWeight;
                break;
            case Panel panel:
                foreach (var child in panel.Children) Flatten(child);
                panel.Children.CollectionChanged += (_, e) => { foreach (var added in e.NewItems?.OfType<Control>() ?? []) Flatten(added); };
                break;
            case Button { Content: Control content } button:
                button.Padding = new Thickness(4, 2); // the chip's own local padding would beat the footer's "plain" style; fact chips and buttons share the buttons' padding so the footer stays one line at 520px
                content.Margin = new Thickness(0);
                Flatten(content);
                break;
            case ContentControl { Content: Control content }:
                content.Margin = new Thickness(0); // a plugin's mounted row carries margins of its own
                Flatten(content);
                break;
        }
    }

    private void RebuildChips(ChatHeader header)
    {
        var signature = string.Join("|", densityLevel, header.Pending, header.Status, header.Model, header.Effort, header.PermissionMode, string.Join(",", header.PermissionModes.Select(mode => mode.Key)), header.Cwd, pendingModel, pendingEffort, pendingMode, ide, header.Advisor, header.Chrome,
            string.Join(",", header.Models.Select(model => model.Id)), string.Join(",", header.McpServers.Select(server => server.Name + server.Status)));
        if (signature == chipSignature) return;
        chipSignature = signature;
        chips.Children.Clear();
        void Add(Control chip) { Flatten(chip); chips.Children.Add(chip); }
        // The pickers sit last, nearest Send; the facts come before them, in the same plain look.
        var pickers = new List<Control>();
        void AddPicker(Control chip) => pickers.Add(chip);
        void AddFactButton(Button button) { button.Classes.Add("plain"); button.Background = Brushes.Transparent; Add(button); }
        // Until the agent has reported, a chip holds a one-word muted placeholder rather than a value it does not know.
        var waiting = header.Status == ChatStatus.Starting;
        var modelLabel = header.Model is null ? "model" : ModelLabel(header.Model, header);
        // A change under way shows the value in force with a quiet ellipsis, never an arrow, and nothing when the target is the value already.
        var modelShown = pendingModel is null || pendingModel == header.Model ? modelLabel : modelLabel + "…";
        // When room runs short the pickers shorten their labels (the full text stays in the tooltip): the effort beside the model goes, "Accept edits" is "edits".
        var shortened = densityLevel >= 2;
        var effortLabel = header.Effort is { } known ? known : waiting ? "effort" : "default";
        var effortShown = pendingEffort is null || pendingEffort == header.Effort ? effortLabel : effortLabel + "…";
        AddPicker(PickerButton("model", modelShown, pendingModel is null ? (header.Model ?? "Model") + " — click to switch the model or the effort (" + (header.Effort ?? "default") + ")" : "Waiting for Claude Code to confirm the model", flyout =>
            header.Models.Count == 0 ? Ui.Text($"Waiting for {env.Agent.Name}…", Ui.Muted, 12) : ModelMenu(header, flyout), shortened || (header.Effort is null && pendingEffort is null && !waiting) ? null : effortShown));
        var offeredModes = ModeList(header);
        var currentMode = offeredModes.FirstOrDefault(mode => mode.Key == header.PermissionMode);
        var modeLabel = header.PermissionMode is null && waiting ? "permissions" : currentMode?.Label ?? ModeLabel(header.PermissionMode);
        var modeShown = shortened ? (header.PermissionMode is null or "default" ? "ask" : modeLabel.Split(' ')[^1].ToLowerInvariant()) : modeLabel;
        AddPicker(PickerButton("permission", pendingMode is null || pendingMode == header.PermissionMode ? modeShown : modeShown + "…",
            (currentMode?.Description ?? env.Agent.DescribeMode(header.PermissionMode ?? "default").Description) + ". Click to change, or press Shift+Tab to cycle.",
            flyout => ModePanel(header, flyout)));
        if (header.PermissionMode == "plan") Add(TerminalFacts.FactChip("plan", "Plan mode", $"{env.Agent.Short} plans and asks before changing anything"));
        foreach (var row in environmentChips ??= env.WorkspaceChips())
        {
            row.Margin = new Thickness(0);
            // A plugin's row hides itself until it has chips; its empty mount would still take a wrap gap, pushing the first row in.
            if (row is ContentControl { Content: Control inner }) row.Bind(IsVisibleProperty, inner.GetObservable(IsVisibleProperty));
            Add(row);
        }
        // The facts the terminal shows beside the pickers: the launch choices, the folder, the IDE switch and attach. A fact chip is read-only.
        // They give way with the width (see the density levels): the read-only facts at level 2, attach and the IDE switch at level 3 (the switch stays in the "+" popup).
        if (header.Advisor is { Length: > 0 } advisor) Add(TerminalFacts.FactChip("advisor", "advisor " + advisor, "Started with --advisor"));
        if (TerminalFacts.CwdLabel(header.Cwd) is { } directory) Add(TerminalFacts.FactChip("cwd", directory, $"{env.Agent.Short} runs in {header.Cwd}"));
        AddFactButton(TerminalFacts.IdeContextChip(ide, () => _ = ToggleIde()));
        // Looks and sits as in the terminal: lowercase "chrome" with the Chrome mark, right after the IDE switch.
        if (header.Chrome) Add(TerminalFacts.FactChip("chrome", "chrome", "Started with Claude in Chrome (--chrome)", Ui.Icon("chrome", Ui.Muted, 12)));
        Button? attach = null;
        attach = TerminalFacts.AttachButton($"Put a file or folder in front of {env.Agent.Short}, as @path", () => PickFileOrFolder(attach!), AddAttachment,
            error => env.Notify(PluginNotificationKind.Error, "Couldn't open the file picker", ChatErrors.Text(error)));
        AddFactButton(attach);
        foreach (var picker in pickers) Add(picker);
        // The "+" wears a warning dot when an MCP server is down.
        var down = header.McpServers.Count(server => server.Status is not ("connected" or "ok"));
        plusBadge.IsVisible = down > 0;
        plusBadge.Background = Ui.Warning;
        Avalonia.Automation.AutomationProperties.SetName(plus!, down > 0 ? $"Add to the chat ({down} MCP servers down)" : "Add to the chat");
        ApplyDensity();
    }

    private static string ModelLabel(string? id, ChatHeader header) => id is null ? "model" : ModelPicks.Label(id, header.Models);

    private string ModeLabel(string? id) => env.Agent.DescribeMode(id ?? "default").Label;

    internal async Task SetModel(string id) => await Set(value => pendingModel = value, id, ChatContract.SetModel, "model");

    private async Task SetEffort(string level) => await Set(value => pendingEffort = value, level, ChatContract.SetEffort, "effort");

    private async Task SetMode(string mode) => await Set(value => pendingMode = value, mode, ChatContract.SetPermissionMode, "permission mode");

    // The pick shows as pending and becomes the chip's value only when the header the call returns (or a push) says so.
    private async Task Set(Action<string?> assign, string value, PluginMethod<ChatSetParams, ChatHeader> method, string what)
    {
        assign(value);
        if (transcript.Header is { } before) { chipSignature = ""; RebuildChips(before); }
        try
        {
            var header = await env.Client.RequestAsync(method, new ChatSetParams(workspaceId, chatId, value));
            Enqueue(new ChatHeaderPush(workspaceId, chatId, header));
            Dispatcher.UIThread.Post(Flush, DispatcherPriority.Render);
            Dispatcher.UIThread.Post(() => ClearPending(value, header), DispatcherPriority.Render);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            env.Notify(PluginNotificationKind.Error, $"Couldn't change the {what}", ChatErrors.Text(error));
            ClearPending(value, null);
        }
    }

    // A header that disagrees with the pick leaves the old value shown and says so, instead of pretending.
    private void ClearPending(string value, ChatHeader? header)
    {
        var changed = pendingModel == value || pendingEffort == value || pendingMode == value;
        if (!changed) return;
        if (pendingModel == value) pendingModel = null;
        if (pendingEffort == value) pendingEffort = null;
        if (pendingMode == value) pendingMode = null;
        if (header is not null && header.Model != value && header.Effort != value && header.PermissionMode != value)
            env.Notify(PluginNotificationKind.Error, $"{env.Agent.Name} did not confirm the change", value);
        if (transcript.Header is { } current) { chipSignature = ""; RebuildChips(current); }
    }

    // A headless child cannot reach the IDE bridge, so the chip is an explicit switch: while on, the next prompt carries the active file and selection.
    private Task ToggleIde()
    {
        ide = !ide;
        lastMention = null;
        removedMention = null;
        RefreshSelectionChip();
        if (transcript.Header is { } header) { chipSignature = ""; RebuildChips(header); }
        return Task.CompletedTask;
    }

    private string? lastMention;

    internal string WithEditorContext(string text)
    {
        if (ContextSelection() is not { } selection) return text;
        var mention = selection.Mention();
        lastMention = mention;
        RefreshSelectionChip();
        return mention + (selection.Quote is null ? " " : "\n\n") + text;
    }

    private const double TaskLabelWidth = 320;

    private void RebuildTasks(ChatHeader header)
    {
        tasks.Children.Clear();
        // Only running tasks keep a chip; a finished one has left its notice in the transcript.
        var running = header.Tasks.Where(task => task.Status == ChatTaskStatus.Running).ToList();
        tasks.IsVisible = running.Count > 0;
        foreach (var task in running)
        {
            var (mark, brush) = task.Status switch
            {
                ChatTaskStatus.Running => ("◐", Ui.Accent),
                ChatTaskStatus.Done => ("✓", Ui.Success),
                ChatTaskStatus.Failed => ("✗", Ui.Danger),
                _ => ("■", Ui.Muted)
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            row.Children.Add(Ui.Text(mark, brush, 11));
            // A shell task's description is its whole command, newlines included: the chip shows its first line, trimmed, and the tooltip the rest.
            var label = Ui.Text($"{task.Kind}: {ChatRowLayout.Preview(task.Description)}", Ui.Muted, 11);
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.MaxWidth = TaskLabelWidth;
            row.Children.Add(label);
            if (task.LastTool is { Length: > 0 } last && task.Status == ChatTaskStatus.Running)
            {
                var tool = Ui.Text(ChatRowLayout.Preview(last), Ui.Hint, 11);
                tool.TextTrimming = TextTrimming.CharacterEllipsis;
                tool.MaxWidth = TaskLabelWidth;
                row.Children.Add(tool);
            }
            var chip = TerminalFacts.ChipButton("ChatTask_" + task.TaskId, row, task.Summary is { Length: > 0 } summary ? summary : task.Description.Length > 600 ? task.Description[..600] + "…" : task.Description, () => RevealTask(task));
            chip.Margin = new Thickness(0, 0, 4, 4);
            tasks.Children.Add(chip);
            if (task.Status == ChatTaskStatus.Running)
            {
                var stop = Ui.IconButton("close", "Stop " + OneLine(task.Description), () => _ = StopTask(task));
                stop.Name = "ChatTaskStop_" + task.TaskId;
                stop.Margin = new Thickness(0, 0, 8, 4);
                tasks.Children.Add(stop);
            }
        }
    }

    private async Task StopTask(ChatTask task)
    {
        try { await env.Client.RequestAsync(ChatContract.StopTask, new ChatTaskParams(workspaceId, chatId, task.TaskId)); }
        catch (Exception error) when (error is not OperationCanceledException) { env.Notify(PluginNotificationKind.Error, "Couldn't stop the task", ChatErrors.Text(error)); }
    }

    internal void RevealTask(ChatTask task)
    {
        if (transcript.Reveal(task.ToolUseId) is { } id && transcript.Row(id) is { } row) list.ScrollToRow(row);
    }

    // Row actions: every one asks the host and lets the pushed items show the result.
    public void Activate(ChatRow row, ChatHitKind kind, string? arg = null)
    {
        switch (kind)
        {
            case ChatHitKind.Toggle: transcript.Toggle(row.Id); break;
            case ChatHitKind.Allow or ChatHitKind.Deny or ChatHitKind.Always when row.Item is ChatPermissionItem permission:
                _ = Answer(permission, kind); break;
            case ChatHitKind.OpenDiff: _ = OpenDiff(row.Item); break;
            case ChatHitKind.OpenThread when arg is not null: OpenThread(arg); break;
            case ChatHitKind.Retry: _ = RetryLast(); break;
            case ChatHitKind.Link when arg is not null && Markdown.SafeLink(arg): OpenUrl(arg); break;
            case ChatHitKind.Link when arg is not null && arg.StartsWith("file:", StringComparison.Ordinal): OpenFileLink(arg[5..]); break;
            case ChatHitKind.LoadFull: _ = LoadFull(row); break;
            case ChatHitKind.OpenText when arg is not null: _ = OpenText(row, arg); break;
            case ChatHitKind.Rewind when row.Item.Uuid is { } uuid: ShowRewind(uuid, row); break;
            case ChatHitKind.Fork when row.Item.Uuid is { } uuid: _ = Fork(uuid); break;
        }
    }

    private Task OpenDiff(ChatItem item)
    {
        var (name, input, applied) = item switch
        {
            ChatToolItem tool => (tool.Name, tool.Input, tool.State == ChatToolState.Done),
            ChatPermissionItem permission => (permission.ToolName, permission.Input, false),
            _ => ("", default, true)
        };
        return env.OpenDiff(new ChatDiffRequest(Relative(DiffPath(item)), applied ? null : Proposal(name, input)));
    }

    private static string Str(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string Replace(string current, string oldText, string newText, bool all) =>
        oldText.Length == 0 ? current : all ? current.Replace(oldText, newText, StringComparison.Ordinal)
            : current.IndexOf(oldText, StringComparison.Ordinal) is var at and >= 0 ? current[..at] + newText + current[(at + oldText.Length)..] : current;

    /// <summary>The whole file as the call would leave it, from the call's input.</summary>
    internal static Func<string, string>? Proposal(string tool, JsonElement input)
    {
        switch (tool)
        {
            case "Write": return input.ValueKind == JsonValueKind.Object && input.TryGetProperty("content", out _) ? _ => Str(input, "content") : null;
            case "Edit":
                var all = input.ValueKind == JsonValueKind.Object && input.TryGetProperty("replace_all", out var flag) && flag.ValueKind == JsonValueKind.True;
                return current => Replace(current, Str(input, "old_string"), Str(input, "new_string"), all);
            case "MultiEdit" when input.ValueKind == JsonValueKind.Object && input.TryGetProperty("edits", out var edits) && edits.ValueKind == JsonValueKind.Array:
                return current => edits.EnumerateArray().Aggregate(current, (text, edit) => Replace(text, Str(edit, "old_string"), Str(edit, "new_string"),
                    edit.TryGetProperty("replace_all", out var each) && each.ValueKind == JsonValueKind.True));
            default: return null;
        }
    }

    // A file reference in a reply opens in the editor at its line, if it stays inside the chat's directory.
    internal void OpenFileLink(string target)
    {
        if (Markdown.FileTarget(target) is not { } file) return;
        var path = Relative(file.Path);
        if (path.StartsWith('/') || path.Length > 1 && path[1] == ':') return;
        _ = env.OpenFileAt(path, file.Line);
    }

    private static string DiffPath(ChatItem item) => item switch
    {
        ChatToolItem tool => tool.FilePath ?? "",
        ChatPermissionItem { Input: { ValueKind: JsonValueKind.Object } input } when input.TryGetProperty("file_path", out var path) => path.GetString() ?? "",
        _ => ""
    };

    private string Relative(string path) =>
        transcript.Header?.Cwd is { Length: > 0 } cwd && path.StartsWith(cwd.TrimEnd('/') + "/", StringComparison.Ordinal) ? path[(cwd.TrimEnd('/').Length + 1)..] : path;

    internal Task Answer(ChatPermissionItem permission, ChatHitKind kind, JsonElement? updatedInput = null) =>
        AnswerPermission(permission, kind != ChatHitKind.Deny, remember: kind == ChatHitKind.Always, updated: updatedInput);

    private async Task LoadFull(ChatRow row)
    {
        try
        {
            var detail = await env.Client.RequestAsync(ChatContract.ItemDetail, new ChatDetailParams(workspaceId, chatId, row.Id));
            transcript.SetFull(row.Id, detail.Text);
        }
        catch (Exception error) when (error is not OperationCanceledException) { env.Notify(PluginNotificationKind.Error, "Couldn't load the full output", ChatErrors.Text(error)); }
    }

    // A clipped IN or OUT row opens its whole text in a dialog; an output the wire cut is fetched first.
    private async Task OpenText(ChatRow row, string which)
    {
        if (row.Item is not ChatToolItem tool) return;
        var text = which == "in" ? ToolCards.InText(tool) : row.Full ?? tool.Output ?? "";
        if (which != "in" && tool.OutputTruncated && row.Full is null)
        {
            await LoadFull(row);
            text = transcript.Row(row.Id)?.Full ?? text;
        }
        var dialog = DialogWindow.Create($"{ToolCards.Header(tool).Name.TrimEnd(':')} tool {(which == "in" ? "input" : "output")}", 720);
        dialog.Tag = "ChatToolText";
        dialog.FindControl<TextBlock>("DialogExplanation")!.IsVisible = false;
        dialog.FindControl<StackPanel>("DialogFields")!.Children.Add(new TextBox { Name = "ChatToolFullText", Text = text, IsReadOnly = true, AcceptsReturn = true, FontFamily = Ui.CodeFont, FontSize = 12, Height = 420, MinWidth = 640 });
        dialog.FindControl<StackPanel>("DialogActions")!.Children.Add(Ui.Button("Close", () => dialog.Close()));
        if (TopLevel.GetTopLevel(this) is Window owner) _ = dialog.ShowDialog(owner);
    }

    // Rewinding drops later turns, and optionally restores the files as they were, so it asks first: the dialog checks what the code
    // would change before it offers the restore.
    private void ShowRewind(string uuid, ChatRow row)
    {
        var dialog = DialogWindow.Create("Rewind", 520);
        dialog.Tag = "ChatRewindDialog";
        var summary = dialog.FindControl<TextBlock>("DialogExplanation")!;
        summary.Text = "Checking code changes\u2026";
        var conversation = new RadioButton { Name = "ChatRewindConversation", Content = "Restore conversation", IsChecked = true, GroupName = "rewind" };
        var files = new RadioButton { Name = "ChatRewindFiles", Content = "Restore code and conversation", IsEnabled = false, GroupName = "rewind" };
        var fields = dialog.FindControl<StackPanel>("DialogFields")!;
        fields.Children.Add(conversation);
        fields.Children.Add(files);
        var go = ChatButtons.Primary(Ui.Button("Rewind", () => { dialog.Close(); _ = Rewind(uuid, files.IsChecked == true); }));
        go.Name = "ChatRewindGo";
        var never = Ui.Button("Never mind", () => dialog.Close());
        never.Name = "ChatRewindCancel";
        var actions = dialog.FindControl<StackPanel>("DialogActions")!;
        actions.Children.Add(never);
        actions.Children.Add(go);
        _ = Preview();
        if (TopLevel.GetTopLevel(this) is Window owner) _ = dialog.ShowDialog(owner);

        async Task Preview()
        {
            try
            {
                var preview = await env.Client.RequestAsync(ChatContract.RewindPreview, new ChatRewindParams(workspaceId, chatId, uuid));
                summary.Text = RewindSummary(preview, Relative);
                files.IsEnabled = preview.CanRewind && preview.FilesChanged.Count > 0;
            }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                summary.Text = "Couldn't check the code: " + ChatErrors.Text(error);
            }
        }
    }

    /// <summary>What the confirm dialog says once the preview is in: the extension's wording, with the changed files listed.</summary>
    internal static string RewindSummary(ChatRewindPreview preview, Func<string, string>? relative = null)
    {
        if (!preview.CanRewind) return preview.Error is { Length: > 0 } error ? error : "The code cannot be restored to this point.";
        const string Warning = "Rewinding does not affect files edited manually or via bash.";
        if (preview.FilesChanged.Count == 0) return "The code has not changed, so no code will be restored.\n" + Warning;
        static string Lines(int count) => $"{count} {(count == 1 ? "line" : "lines")}";
        var files = preview.FilesChanged.Count;
        return $"{Lines(preview.Deletions ?? 0)} will be removed and {Lines(preview.Insertions ?? 0)} will be added across {files} {(files == 1 ? "file" : "files")}:\n" +
            string.Join("\n", preview.FilesChanged.Select(path => relative?.Invoke(path) ?? path)) + "\n" + Warning;
    }

    internal async Task Rewind(string uuid, bool files)
    {
        try { await env.Client.RequestAsync(ChatContract.Rewind, new ChatRewindParams(workspaceId, chatId, uuid) { Files = files ? true : null }); }
        catch (Exception error) when (error is not OperationCanceledException) { env.Notify(PluginNotificationKind.Error, "Couldn't rewind", ChatErrors.Text(error)); }
    }

    private async Task Fork(string uuid)
    {
        try
        {
            var forked = await env.Client.RequestAsync(ChatContract.Fork, new ChatRewindParams(workspaceId, chatId, uuid));
            env.OpenChat(forked.ChatId, (title.Text ?? env.Agent.Name) + " (fork)", null);
        }
        catch (Exception error) when (error is not OperationCanceledException) { env.Notify(PluginNotificationKind.Error, "Couldn't fork", ChatErrors.Text(error)); }
    }
}