using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Chat;
using SharpRail.Plugins.Chat.UI;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.UI.Chat;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Checks;

/// <summary>The Claude Code chat view against a scripted client: bounded realization, streaming, nesting, permissions, chips, menus and the mode switch.</summary>
internal static class ClaudeChatUiChecks
{
    private static readonly ChatAgent TestAgent = SharpRail.Plugins.ClaudeCode.UI.Chat.ClaudeChat.Agent(() => Task.FromResult(SharpRail.Plugins.ClaudeCode.UI.PermissionModeSource.Current));

    private const string Workspace = "ws", Chat = "chat-1";

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakeChat : IChatClient
    {
        public List<ChatPush> Snapshot { get; } = [];
        public Action<ChatPush>? Handler { get; private set; }
        public List<ChatAnswerParams> Answers { get; } = [];
        public List<ChatRewindParams> Rewinds { get; } = [];
        public List<ChatAnswerQuestionParams> QuestionAnswers { get; } = [];
        public bool FailQueue { get; set; }
        public List<string> Calls { get; } = [];
        public List<string> Sends { get; } = [];
        public List<(string Method, string Value)> Sets { get; } = [];
        public TaskCompletionSource<ChatHeader>? ModelGate { get; set; }
        public ChatHeader Header { get; set; } = new(ChatStatus.Idle);
        public int Subscriptions { get; private set; }
        public Exception? OpenFailure { get; set; }
        public ChatRewindPreview RewindPreview { get; set; } = new(true) { FilesChanged = ["/w/a.cs", "/w/b.cs"], Insertions = 3, Deletions = 1 };

        public IDisposable Subscribe(ChatKey key, Action<ChatPush> handler)
        {
            Handler = handler;
            Subscriptions++;
            foreach (var push in Snapshot) handler(push);
            return new Release(() => Handler = null);
        }

        private sealed class Release(Action action) : IDisposable { public void Dispose() => action(); }

        public async ValueTask<TResult> RequestAsync<TParams, TResult>(PluginMethod<TParams, TResult> method, TParams parameters, CancellationToken cancellationToken = default)
        {
            Calls.Add(method.Name);
            if (parameters is ChatSendParams sending) Sends.Add(sending.Text);
            await Task.Yield();
            if (OpenFailure is not null && method.Name == ChatContract.Open.Name) throw OpenFailure;
            object? result = parameters switch
            {
                ChatOpenParams => Header,
                ChatSetParams { Value: var value } when method.Name == ChatContract.SetModel.Name =>
                    ModelGate is { } gate ? await gate.Task : Header with { Model = value },
                ChatSetParams plain => Plain(method.Name, plain),
                ChatFilesParams { Query: var query } => new ChatFileList([.. new[] { "src/Alpha.cs", "src/Beta.cs", "README.md" }.Where(path => path.Contains(query, StringComparison.OrdinalIgnoreCase))]),
                ChatAnswerParams answer => Answered(answer),
                ChatAnswerQuestionParams asked => Asked(asked),
                ChatQueuedParams queued => Queued(queued, null),
                ChatEditQueuedParams edited => Queued(new ChatQueuedParams(edited.WorkspaceId, edited.ChatId, edited.ItemId), edited.Text),
                ChatRewindParams rewind when method.Name == ChatContract.Rewind.Name => Rewound(rewind),
                ChatRewindParams when method.Name == ChatContract.RewindPreview.Name => RewindPreview,
                ChatSessionsParams => new ChatSessionList([new ChatSessionInfo("s1", "Fix the parser", "today"), new ChatSessionInfo("s2", "Write docs", "yesterday")]),
                ChatSendParams => new Ack(),
                ChatKey => new Ack(),
                ChatDetailParams => new ChatDetail("full output"),
                _ => new Ack()
            };
            return (TResult)result!;
        }

        private Ack Asked(ChatAnswerQuestionParams asked)
        {
            QuestionAnswers.Add(asked);
            var item = Snapshot.OfType<ChatItemPush>().Select(push => push.Item).OfType<ChatQuestionItem>().Last(question => question.RequestId == asked.RequestId);
            Dispatcher.UIThread.Post(() => Handler?.Invoke(new ChatItemPush(Workspace, Chat, item with
            {
                Decision = asked.Cancelled == true ? ChatPermissionDecision.Denied : ChatPermissionDecision.Allowed,
                Answers = asked.Answers
            })));
            return new Ack();
        }

        private Ack Queued(ChatQueuedParams queued, string? replacement)
        {
            if (FailQueue) throw new PluginCallException(PluginCallError.Failed, "cancelled:false");
            Dispatcher.UIThread.Post(() =>
            {
                Handler?.Invoke(new ChatRemovePush(Workspace, Chat, queued.ItemId));
                if (replacement is not null) Handler?.Invoke(Item(new ChatUserItem(queued.ItemId + "-edited", 99, replacement) { Queued = true }));
            });
            return new Ack();
        }

        private ChatHeader Plain(string method, ChatSetParams set) { Sets.Add((method, set.Value)); return Header; }

        private Ack Rewound(ChatRewindParams rewind) { Rewinds.Add(rewind); return new Ack(); }

        private Ack Answered(ChatAnswerParams answer)
        {
            Answers.Add(answer);
            var item = Snapshot.OfType<ChatItemPush>().Select(push => push.Item).OfType<ChatPermissionItem>().Last(permission => permission.RequestId == answer.RequestId);
            Dispatcher.UIThread.Post(() => Handler?.Invoke(new ChatItemPush(Workspace, Chat, item with { Decision = answer.Allow ? ChatPermissionDecision.Allowed : ChatPermissionDecision.Denied })));
            return new Ack();
        }

        public void Push(ChatPush push) { Snapshot.Add(push); Handler?.Invoke(push); }
    }

    private static ChatItemPush Item(ChatItem item) => new(Workspace, Chat, item);

    private static ChatItem Sample(int index) => (index % 5) switch
    {
        0 => new ChatUserItem("i" + index, index, $"Question {index}: please explain how item {index} behaves when the window is resized"),
        1 => new ChatAssistantItem("i" + index, index, $"Answer {index}\n\nSome **bold** text and `code` for item {index}.\n\n```cs\nvar value = {index};\n```"),
        2 => new ChatThinkingItem("i" + index, index, "Thinking about it for a while. " + new string('x', 200)),
        3 => new ChatToolItem("i" + index, index, "Bash", JsonSerializer.SerializeToElement(new { command = "echo " + index }), ChatToolState.Done) { Summary = "echo " + index, Output = "line\nline\nline" },
        _ => new ChatNoticeItem("i" + index, index, ChatNoticeLevel.Info, "Interrupted") { Category = ChatNoticeKinds.Interrupt }
    };

    private static void Frame(Window window)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame();
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
    }

    private static void Press(ChatView view, string name) =>
        view.RequestPanel.GetVisualDescendants().OfType<Button>().First(button => button.Name == name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

    private static ChatItem Sample2() => new ChatPermissionItem("perm3", 9, "req-3", "Bash", JsonSerializer.SerializeToElement(new { command = "ls" }));

    private static void Await(Task task) => E2eWorkspace.Until(() => task.IsCompleted);

    private static string ChipText(Control root, string name) =>
        string.Join(" ", root.GetVisualDescendants().OfType<Control>().First(control => control.Name == name).GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));

    internal static void Run()
    {
        Placeholders();
        GlyphAlignment();
        ItemKinds();
        Requests();
        Pickers();
        PopupFamily();
        PopupLook();
        ComposerLook();
        SelectionChip();
        Grouping();
        Bounded();
        StreamingStable();
        Composer();
        PageZoomSteps();
        ResizeDrag();
        ChatZoom();
        QuestionStable();
        PastConversations();
        CommandMenu();
        HoverAffordance();
        PasteImages();
        WrappedHistory();
        DraftSurvivesHistory();
        RunningFooter();
        FooterChipEdges();
        PrimaryContrast();
        QuestionPreview();
        ToolCardsText();
        Selection();
        SelectionGeometry();
        MarkdownBounded();
        Behaviour();
    }

    private static ChatToolItem Tool(string name, object input, ChatToolState state = ChatToolState.Done, string? output = null, string? summary = null) =>
        new("t-" + name, 1, name, JsonSerializer.SerializeToElement(input), state) { Output = output, Summary = summary };

    // Tool cards say what the extension's tool table says: headers, the line under them, dots, thinking labels, the clip rule.
    private static void ToolCardsText()
    {
        static void Is(ChatToolItem tool, string name, string secondary, string? line = null, string? because = null)
        {
            var head = ToolCards.Header(tool);
            Require(head.Name == name && head.Secondary == secondary, $"{because ?? tool.Name} header: expected '{name}' / '{secondary}', got '{head.Name}' / '{head.Secondary}'.");
            Require(ToolCards.Secondary(tool) == line, $"{because ?? tool.Name} line: expected '{line}', got '{ToolCards.Secondary(tool)}'.");
        }
        Is(Tool("Bash", new { command = "ls -la", description = "List files" }), "Bash", "List files");
        Is(Tool("Bash", new { command = "ls -la" }), "Bash", "ls -la", because: "Bash without description");
        Is(Tool("Read", new { file_path = "/a/b/c.cs" }), "Read", "c.cs");
        Is(Tool("Read", new { file_path = "/a/b/c.cs", offset = 10, limit = 11 }), "Read", "c.cs (lines 10-20)");
        Is(Tool("Read", new { file_path = "/a/b/c.cs", offset = 7 }), "Read", "c.cs (from line 7)");
        Is(Tool("Edit", new { file_path = "/a/x.cs", old_string = "", new_string = "a\nb\nc" }), "Edit", "x.cs", "Added 3 lines");
        Is(Tool("Edit", new { file_path = "/a/x.cs", old_string = "a\nb", new_string = "" }), "Edit", "x.cs", "Removed 2 lines");
        Is(Tool("Edit", new { file_path = "/a/x.cs", old_string = "a", new_string = "b\nc" }), "Edit", "x.cs", "Added 2 lines, removed 1 line");
        Is(Tool("Edit", new { file_path = "/a/x.cs", old_string = "a", new_string = "b" }), "Edit", "x.cs", "Modified");
        Is(Tool("Edit", new { file_path = "/a/x.cs", old_string = "a", new_string = "b" }, ChatToolState.Failed), "Edit", "x.cs", "Edit failed");
        Is(Tool("Write", new { file_path = "/a/y.cs", content = "1\n2\n" }, ChatToolState.Running), "Write", "y.cs", "2 lines");
        Is(Tool("Write", new { file_path = "/a/y.cs", content = "1" }), "Write", "y.cs", "Write succeeded");
        Is(Tool("Write", new { file_path = "/a/y.cs", content = "1" }, ChatToolState.Failed), "Write", "y.cs", "Write failed");
        Is(Tool("Glob", new { pattern = "**/*.cs" }, output: "a.cs\nb.cs"), "Glob", "pattern: \"**/*.cs\"", "2 lines of output");
        Is(Tool("Grep", new { pattern = "foo", path = "src", glob = "*.cs", type = "cs" }, output: ""), "Grep", "\"foo\" (in src, glob: *.cs, type: cs)", "No matches found");
        Is(Tool("Grep", new { pattern = "foo" }, output: "one hit"), "Grep", "\"foo\"", "1 line of output");
        Is(Tool("Search", new { pattern = "foo" }, ChatToolState.Running), "Search", "pattern: \"foo\"");
        Is(Tool("Skill", new { skill = "/review" }), "review skill", "");
        Is(Tool("TodoWrite", new { todos = Array.Empty<object>() }), "Update Todos", "");
        Is(Tool("WebFetch", new { url = "https://example.com/a" }), "Web Fetch", "https://example.com/a", "Fetched from https://example.com/a");
        Is(Tool("WebSearch", new { query = "avalonia", allowed_domains = new[] { "a.com", "b.com" }, blocked_domains = new[] { "c.com" } }), "Web Search", "avalonia", "Allowed: a.com, b.com \u00B7 Blocked: c.com");
        Is(Tool("NotebookEdit", new { notebook_path = "/n/nb.ipynb", cell_id = "c1", new_source = "x" }), "Edit Notebook Cell", "nb.ipynb:c1", "Success");
        Is(Tool("Artifact", new { file_path = "/p/page.html" }), "Artifact", "/p/page.html");
        Is(Tool("SendUserFile", new { files = new[] { "/a/one.txt", "/a/two.txt" } }), "Sent", "one.txt, two.txt");
        Is(Tool("Agent", new { description = "Find usages", prompt = "Look" }), "Agent:", "Find usages");
        Is(Tool("mcp__slack__post", new { channel = "general", text = "x" }), "Slack [post]", "#general");
        Is(Tool("mcp__web_tools__fetch", new { url = "https://docs.example.com/x/y" }), "Web Tools [fetch]", "docs.example.com");
        Is(Tool("mcp__srv__t", new { text = new string('q', 50) }), "Srv [t]", new string('q', 30) + "\u2026");
        Is(Tool("mcp__srv__t", new { query = new string('q', 50), text = "ignored" }), "Srv [t]", new string('q', 40) + "\u2026");
        Is(Tool("mcp__claude-in-chrome__navigate", new { url = "https://a.example/b" }), "Claude in Chrome [Navigate]", "a.example");
        Is(Tool("Mystery", new { a = 1 }, summary: "sum"), "Mystery", "sum");
        Require(ToolCards.InText(Tool("Mystery", new { a = 1 })).Contains("\n") && ToolCards.InText(Tool("Agent", new { prompt = "P" })) == "P" && ToolCards.InText(Tool("Bash", new { command = "c" })) == "c", "IN is pretty JSON, the agent's prompt, or the command.");
        Require(ToolCards.Rows(Tool("Agent", new { })) == (true, false) && ToolCards.Rows(Tool("Mystery", new { })) == (true, true), "An agent card has IN only; a generic card has IN and OUT.");
        Require(ToolCards.StatusOf(Tool("Bash", new { }, ChatToolState.Running)) == ToolCards.Dot.Progress && ToolCards.StatusOf(Tool("Bash", new { }, ChatToolState.Failed)) == ToolCards.Dot.Failure &&
            ToolCards.StatusOf(Tool("Bash", new { })) == ToolCards.Dot.Success, "The dot is progress while pending, failure for an error, success otherwise.");
        Require(ToolCards.ThinkingLabel(new ChatThinkingItem("t", 1, "x") { Streaming = true, Tokens = 12 }) == "Thinking... \u00B7 12 tokens" &&
            ToolCards.ThinkingLabel(new ChatThinkingItem("t", 1, "x") { Streaming = true }) == "Thinking..." && ToolCards.ThinkingLabel(new ChatThinkingItem("t", 1, "x")) == "Thinking" && ToolCards.ThinkingLabel(new ChatThinkingItem("t", 1, "x") { DurationMs = 4600 }) == "Thought for 5s" && ToolCards.ThinkingLabel(new ChatThinkingItem("t", 1, "x") { DurationMs = 200 }) == "Thought for 1s", "A thinking summary counts tokens while streaming.");
        Require(!ToolCards.Openable(new string('a', 250)) && ToolCards.Openable(new string('a', 251)) && !ToolCards.Openable("1\n2\n3") && ToolCards.Openable("1\n2\n3\n4"), "A text over 250 characters or three lines is openable.");

        // Layout: ToolSearch takes no room; a long IN is clipped to 60 pixels and opens; a short one does not.
        Require(Height(Tool("ToolSearch", new { query = "x" }), true, out _) == 0, "ToolSearch is never drawn.");
        var longIn = Tool("Mystery", new { text = string.Join("\n", Enumerable.Range(0, 30).Select(i => "line " + i)) });
        var shortIn = Tool("Mystery", new { a = 1 });
        var longHeight = Height(longIn, true, out var longHits);
        var shortHeight = Height(shortIn, true, out var shortHits);
        Require(longHits.Any(hit => hit.Kind == ChatHitKind.OpenText && hit.Arg == "in") && !shortHits.Any(hit => hit.Kind == ChatHitKind.OpenText), "Only a long IN opens in full.");
        Require(longHeight < shortHeight + 60, $"A long IN is clipped to about 60 pixels ({longHeight} against {shortHeight}).");
        var outputCard = Tool("Bash", new { command = "x" }, output: new string('o', 400));
        Require(Height(outputCard, true, out var outHits) > Height(outputCard, false, out _) && outHits.Any(hit => hit.Kind == ChatHitKind.OpenText && hit.Arg == "out"), "A long OUT opens in full.");
        Require(Height(Tool("Agent", new { description = "d", prompt = "p" }, output: "result"), true, out var agentHits) < Height(Tool("Mystery", new { description = "d", prompt = "p" }, output: "result"), true, out _) &&
            agentHits.Any(hit => hit.Kind == ChatHitKind.OpenThread), "An agent card has no OUT and offers its thread.");
    }

    // Composer and chrome: Shift+Tab, the pie, the working word, banners, Esc, mention chips and the rewind dialog.
    private static void Composer()
    {
        ChatMode[] modes = [new("default", "Manual", "Claude will ask for approval before making each edit"), new("acceptEdits", "Edit automatically", "Claude will edit your selected text or the whole file"), new("plan", "Plan", "Claude will explore the code and present a plan before editing")];
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) { PermissionMode = "default", PermissionModes = modes, Model = "opus" } };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        chat.Snapshot.Add(Item(new ChatUserItem("u", 1, "first") { Uuid = "u-1" }));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var chip = view.GetVisualDescendants().OfType<Button>().First(button => button.Name == "TerminalAgentFact_permission");
            Require(ToolTip.GetTip(chip) as string == "Claude will ask for approval before making each edit. Click to change, or press Shift+Tab to cycle.", $"The mode button tooltip is the extension's (got '{ToolTip.GetTip(chip)}').");
            view.Input.Focus();
            view.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.Shift });
            E2eWorkspace.Until(() => chat.Sets.Contains((ChatContract.SetPermissionMode.Name, "acceptEdits")));
            Require(chat.Sets.Count(set => set.Method == ChatContract.SetPermissionMode.Name) == 1, "Shift+Tab moves to the next mode.");
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { PermissionMode = "plan" })); Frame(window);
            view.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab, KeyModifiers = KeyModifiers.Shift });
            E2eWorkspace.Until(() => chat.Sets.Contains((ChatContract.SetPermissionMode.Name, "default")));
            Require(true, "Shift+Tab wraps from the last mode to the first.");

            // The pie shows only under half the context left, says how much, and compacts on a click.
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Usage = new ChatUsage { ContextTokens = 50_000, ContextWindow = 200_000, MaxOutputTokens = 64_000 } })); Frame(window);
            Require(view.ContextPie.IsVisible && !(ToolTip.GetTip(view.ContextPie) as string)!.Contains("until auto-compact", StringComparison.Ordinal), "With most of the context left the usage chip does not warn.");
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Usage = new ChatUsage { ContextTokens = 120_000, ContextWindow = 200_000, MaxOutputTokens = 64_000 } })); Frame(window);
            Require(view.ContextPie.IsVisible && (ToolTip.GetTip(view.ContextPie) as string)!.StartsWith("28% of context remaining until auto-compact.", StringComparison.Ordinal), $"Under half the context left the pie says so (tip '{ToolTip.GetTip(view.ContextPie)}').");
            var sends = chat.Calls.Count(call => call == ChatContract.Send.Name);
            // The chip opens a popover with the stats, the meter and Compact now; that button compacts.
            var usagePanel = view.BuildUsagePanel();
            Require(usagePanel is Panel { Children.Count: > 2 } stats && stats.Children.OfType<TextBlock>().Any(text => text.Name == "ChatStats" && text.Text!.Length > 0), "The usage popover holds the stats.");
            ((Panel)usagePanel).Children.OfType<Button>().First(button => button.Name == "ChatCompact").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2eWorkspace.Until(() => chat.Calls.Count(call => call == ChatContract.Send.Name) == sends + 1);

            // The working word comes from the CLI's activity.
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Activity = "Compacting" })); Frame(window);
            var working = view.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "ChatWorking");
            Require(working.IsVisible && working.Text == "Compacting...", $"The working indicator shows the activity (got '{working.Text}').");
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Activity = null })); Frame(window);
            Require(!working.IsVisible, "It goes when the activity does.");

            // A rate-limit banner is dismissible, and a new key brings it back.
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Banner = new ChatBanner("rl-1", "You've used 90% of your limit", ChatNoticeLevel.Warning) })); Frame(window);
            var notice = view.GetVisualDescendants().OfType<Control>().First(control => control.Name == "ChatNoticeBanner");
            Require(notice.IsVisible && notice.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "You've used 90% of your limit"), "The host's banner text is shown.");
            view.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ChatNoticeDismiss").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Frame(window);
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Banner = new ChatBanner("rl-1", "You've used 91% of your limit", ChatNoticeLevel.Warning) })); Frame(window);
            Require(!notice.IsVisible, "A dismissed banner stays dismissed for its key.");
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Banner = new ChatBanner("rl-2", "Limit reached", ChatNoticeLevel.Error) })); Frame(window);
            Require(notice.IsVisible, "A banner with a new key shows.");
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Banner = null })); Frame(window);

            // Esc interrupts a running turn from anywhere, but not while a request waits.
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Status = ChatStatus.Running })); Frame(window);
            var interrupts = chat.Calls.Count(call => call == ChatContract.Interrupt.Name);
            view.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            E2eWorkspace.Until(() => chat.Calls.Count(call => call == ChatContract.Interrupt.Name) == interrupts + 1);
            chat.Push(Item(new ChatPermissionItem("p", 5, "req-p", "Bash", JsonSerializer.SerializeToElement(new { command = "ls" })))); Frame(window);
            view.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape }); Frame(window);
            Require(chat.Calls.Count(call => call == ChatContract.Interrupt.Name) == interrupts + 1, "Esc does not interrupt while a permission request is pending.");

            // Mentions: a quoted path and a range are chips; the text keeps the extension's form.
            var found = ChatView.Mentions("see @\"my dir/a b.cs\"#L10-20 and @src/x.cs#L3 and @plain.cs ");
            Require(found.Count == 3 && found[0].Path == "my dir/a b.cs" && found[0].Range == "#L10-20" && found[1].Path == "src/x.cs" && found[1].Range == "#L3" && found[2].Range == "", "Mentions read quoted paths and line ranges.");
            view.Input.Text = "see @\"my dir/a b.cs\"#L10-20 now";
            Frame(window);
            Require(view.GetVisualDescendants().OfType<Button>().Count(button => button.Name == "ChatMention") == 1, "A finished mention is a chip.");

            // Rewind: the dialog checks the code, then summarises, with Never mind.
            view.Activate(view.Transcript.Row("u")!, ChatHitKind.Rewind);
            E2eWorkspace.Until(() => window.OwnedWindows.Any(owned => Equals(owned.Tag, "ChatRewindDialog")));
            var rewind = window.OwnedWindows.First(owned => Equals(owned.Tag, "ChatRewindDialog"));
            string Summary() => rewind.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "DialogExplanation").Text ?? "";
            E2eWorkspace.Until(() => Summary().Contains("1 line will be removed and 3 lines will be added across 2 files:", StringComparison.Ordinal));
            Require(rewind.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "ChatRewindCancel" && Equals((button.Content as TextBlock)?.Text ?? button.Content, "Never mind")) &&
                rewind.GetVisualDescendants().OfType<RadioButton>().First(box => box.Name == "ChatRewindFiles").IsEnabled, "The dialog lists the changed files and offers Never mind.");
            rewind.Close();
            Require(ChatView.RewindSummary(new ChatRewindPreview(true)).StartsWith("The code has not changed, so no code will be restored.", StringComparison.Ordinal), "Nothing to restore reads 'has not changed'.");
            Require(ChatView.RewindSummary(new ChatRewindPreview(false) { Error = "no checkpoint" }) == "no checkpoint", "A refusal shows its reason.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    // A single-select question with previews shows a pane that follows the hovered option after a pause; a multi-select question shows none.
    private static void QuestionPreview()
    {
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.AwaitingPermission) };
        chat.Snapshot.Add(Item(new ChatQuestionItem("q", 1, "req-q", [new("Which layout?", [new("Grid") { Preview = "# Grid layout\n\nRows and columns" }, new("Stack")])])));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            string Pane() => string.Join(" ", view.RequestPanel.GetVisualDescendants().OfType<Control>().First(control => control.Name == "ChatQuestionPreview").GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text ?? string.Concat(text.Inlines?.OfType<Avalonia.Controls.Documents.Run>().Select(run => run.Text) ?? [])));
            E2eWorkspace.Until(() => Pane().Contains("Grid layout", StringComparison.Ordinal));
            Require(view.Placeholder() == "Waiting for your answer\u2026", "A waiting question names itself in the composer's placeholder.");
            var stack = view.RequestPanel.GetVisualDescendants().OfType<Button>().Where(button => button.Name == "ChatQuestionOption").Last();
            var spot = stack.TranslatePoint(new Point(stack.Bounds.Width / 2, stack.Bounds.Height / 2), window)!.Value;
            window.MouseMove(new Point(spot.X + 1, spot.Y)); window.MouseMove(spot);
            E2eWorkspace.Until(() => Pane().Contains("No preview for this option", StringComparison.Ordinal));
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }

        var multi = new FakeChat { Header = new ChatHeader(ChatStatus.AwaitingPermission) };
        multi.Snapshot.Add(Item(new ChatQuestionItem("q", 1, "req-q", [new("Which checks?", [new("Unit") { Preview = "# Unit" }, new("E2E")]) { MultiSelect = true }])));
        var multiView = new ChatView(new ChatEnvironment(multi, TestAgent), Workspace, Chat);
        var multiWindow = new Window { Width = 900, Height = 700, Content = multiView, Background = Ui.Surface };
        multiWindow.Show();
        try
        {
            Frame(multiWindow); Frame(multiWindow);
            Require(multiView.RequestPanel.IsVisible && !multiView.RequestPanel.GetVisualDescendants().OfType<Control>().Any(control => control.Name == "ChatQuestionPreview"), "A multi-select question shows no preview pane.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { multiWindow.Close(); }
    }

    // While the turn runs the footer holds Stop, Queue, the send menu, history and the working text: at every width, and at 150% zoom,
    // none is clipped by the edge or covers another, and Stop, Send and the model chip always stay.
    private static void RunningFooter()
    {
        foreach (var zoom in new[] { 1.0, 1.5 })
        {
            var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Running) { Model = "opus", Effort = "high", PermissionMode = "default", Cwd = "/work/project", Usage = new ChatUsage { Input = 1200, Output = 300, CostUsd = 0.5, ContextTokens = 40_000, ContextWindow = 200_000 } } };
            chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
            var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
            var host = new LayoutTransformControl { LayoutTransform = new ScaleTransform(zoom, zoom), Child = view };
            var window = new Window { Width = 900, Height = 700, Content = host, Background = Ui.Surface };
            window.Show();
            try
            {
                Frame(window); Frame(window);
                view.Input.Text = "queue this";
                var levels = new List<int>();
                foreach (var width in new[] { 900, 700, 520, 400, 320, 260, 200, 140 })
                {
                    window.Width = width; Frame(window); Frame(window); Frame(window);
                    levels.Add(view.DensityLevel);
                    if (width < 260) continue;
                    var shown = new[] { "ChatStop", "ChatSend", "ChatSendMenu", "ChatHistory", "ChatWorking", "TerminalAgentFact_model" }
                        .Select(name => view.GetVisualDescendants().OfType<Control>().FirstOrDefault(control => control.Name == name)).Where(control => control is { IsEffectivelyVisible: true }).Cast<Control>().ToList();
                    foreach (var control in shown)
                    {
                        var left = control.TranslatePoint(new Point(0, 0), view)!.Value.X;
                        Require(left >= -0.5 && left + control.Bounds.Width <= view.Bounds.Width + 0.5, $"{control.Name} stays inside the chat at {width}px, zoom {zoom} (x {left:0}..{left + control.Bounds.Width:0} of {view.Bounds.Width:0}).");
                    }
                    // The round Send sits the same distance from the shell's top, right and bottom edges.
                    var shellBox = view.GetVisualDescendants().OfType<Control>().First(control => control.Name == "ChatComposerShell");
                    var sendBox = view.SendButton;
                    var shellOrigin = shellBox.TranslatePoint(new Point(0, 0), view)!.Value;
                    var sendOrigin = sendBox.TranslatePoint(new Point(0, 0), view)!.Value;
                    var edge = new[] { "ChatSend", "ChatSendMenu" }.Select(name => view.GetVisualDescendants().OfType<Control>().First(control => control.Name == name)).Where(control => control.IsEffectivelyVisible).MaxBy(control => control.TranslatePoint(new Point(control.Bounds.Width, 0), view)!.Value.X)!;
                    var rightInset = shellOrigin.X + shellBox.Bounds.Width - (edge.TranslatePoint(new Point(edge.Bounds.Width, 0), view)!.Value.X);
                    var bottomInset = shellOrigin.Y + shellBox.Bounds.Height - (sendOrigin.Y + sendBox.Bounds.Height);
                    var topInset = sendOrigin.Y - shellOrigin.Y;
                    var chipsHeight = view.GetVisualDescendants().OfType<WrapPanel>().First(panel => panel.Name == "ChatChips").Bounds.Height;
                    var oneLine = chipsHeight <= sendBox.Bounds.Height + 1;
                    if (width / zoom >= 520) Require(oneLine, $"The footer is one line at {width}px, zoom {zoom}.");
                    if (oneLine) Require(Math.Abs(rightInset - bottomInset) <= 1 && Math.Abs(topInset - bottomInset) <= 1.5 && Math.Abs(topInset - rightInset) <= 1.5,
                        $"Send is inset equally at {width}px, zoom {zoom} (input {view.Input.Bounds.Height:0.0}, top {topInset:0.0}, right {rightInset:0.0}, bottom {bottomInset:0.0}; send height {sendBox.Bounds.Height:0}, chips {chipsHeight:0}).");
                    var names = shown.Select(control => control.Name).ToHashSet();
                    Require(names.Contains("ChatStop") && names.Contains("ChatSend") && names.Contains("TerminalAgentFact_model"), $"Stop, Send and the model chip stay at {width}px, zoom {zoom}.");
                    var row = shown.Where(control => control.Name is "ChatStop" or "ChatSend" or "ChatSendMenu").OrderBy(control => control.TranslatePoint(new Point(0, 0), view)!.Value.X).ToList();
                    for (var i = 1; i < row.Count; i++)
                        Require(row[i - 1].TranslatePoint(new Point(row[i - 1].Bounds.Width, 0), view)!.Value.X <= row[i].TranslatePoint(new Point(0, 0), view)!.Value.X + 0.5, $"Stop, Queue and the menu do not overlap at {width}px, zoom {zoom}.");
                }
                Require(levels.Zip(levels.Skip(1), (x, y) => y >= x).All(ok => ok) && levels[^1] >= 1, $"The running footer collapses monotonically ({string.Join(",", levels)}, zoom {zoom}).");
            }
            catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
            finally { window.Close(); }
        }
    }

    private static double Channel(byte value) { var c = value / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }

    private static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);

    private static Color Over(Color top, Color below)
    {
        var a = top.A / 255.0;
        return Color.FromRgb((byte)Math.Round(top.R * a + below.R * (1 - a)), (byte)Math.Round(top.G * a + below.G * (1 - a)), (byte)Math.Round(top.B * a + below.B * (1 - a)));
    }

    /// <summary>The contrast ratio between a button's resolved label colour and its resolved fill, both laid over the surface.</summary>
    private static double ContrastOf(Button button)
    {
        var surface = ((ISolidColorBrush)Ui.Surface).Color;
        var label = button.GetVisualDescendants().OfType<TextBlock>().First();
        var fill = button.GetVisualDescendants().OfType<ContentPresenter>().First(presenter => presenter.Name == "PART_ContentPresenter").Background as ISolidColorBrush;
        var back = Over(fill?.Color ?? Colors.Transparent, surface);
        var front = Over(((ISolidColorBrush)label.Foreground!).Color, back);
        var (hi, lo) = (Math.Max(Luminance(front), Luminance(back)), Math.Min(Luminance(front), Luminance(back)));
        return (hi + 0.05) / (lo + 0.05);
    }

    // The footer chips wrap on a narrow chat: each wrapped row starts at the same left edge, and a chip without an icon (advisor, model, mode)
    // keeps the same inset between its box and its content as the others, so no chip sits further in than its neighbours.
    private static void FooterChipEdges()
    {
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) { Model = "opus", Effort = "high", PermissionMode = "default", Cwd = "/work/some/long/project/directory-name", Advisor = "opus" } };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        // The environment plugin mounts its row first; each plugin's row is hidden until it has something to show, as in the running app; three of them stand in for the plugins that offer one.
        var view = new ChatView(new ChatEnvironment(chat, TestAgent) { WorkspaceChips = () => [.. Enumerable.Range(0, 3).Select(_ => (Control)new ContentControl { Name = "PluginMount_env", Content = new WrapPanel { Name = "TerminalEnvironmentChips", Margin = new Thickness(8, 0), IsVisible = false } })] }, Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var wrapped = false;
            foreach (var width in new[] { 560, 500, 460, 420, 380, 340, 300 })
            {
                window.Width = width; Frame(window); Frame(window); Frame(window);
                var panel = view.GetVisualDescendants().OfType<WrapPanel>().First(candidate => candidate.Name == "ChatChips");
                var chips = panel.Children.OfType<Control>().Where(chip => chip.IsVisible && chip.Bounds.Width > 0).ToList();
                var rows = chips.GroupBy(chip => Math.Round(chip.TranslatePoint(new Point(0, 0), panel)!.Value.Y / 4)).OrderBy(group => group.Key).Select(group => group.OrderBy(chip => chip.TranslatePoint(new Point(0, 0), panel)!.Value.X).ToList()).ToList();
                // A chip's content starts at its first icon or text; the inset is the distance from the chip's box.
                static double Content(Control chip, Control panel) => chip.GetVisualDescendants().OfType<Visual>().Where(visual => visual is TextBlock or Border { OpacityMask: not null }).Min(visual => visual.TranslatePoint(new Point(0, 0), panel)!.Value.X);
                static double Box(Control chip, Control panel) => chip.TranslatePoint(new Point(0, 0), panel)!.Value.X;
                if (rows.Count < 2) continue;
                wrapped = true;
                var edges = rows.Select(row => Box(row[0], panel) + 0d).ToList();
                var contents = rows.Select(row => Content(row[0], panel)).ToList();
                Require(edges.All(edge => Math.Abs(edge - edges[0]) <= 0.5) && contents.All(edge => Math.Abs(edge - contents[0]) <= 0.5),
                    $"Every wrapped footer row starts at one left edge at {width}px (boxes {string.Join(",", edges.Select(edge => edge.ToString("0.#")))}, content {string.Join(",", contents.Select(edge => edge.ToString("0.#")))}).");
                var insets = new[] { "TerminalAgentFact_advisor", "TerminalAgentFact_model", "TerminalAgentFact_permission" }
                    .Select(name => (name, chip: chips.FirstOrDefault(chip => chip.Name == name))).Where(pair => pair.chip is not null)
                    .Select(pair => (pair.name, inset: Content(pair.chip!, panel) - Box(pair.chip!, panel))).ToList();
                Require(insets.Count >= 2 && insets.All(pair => Math.Abs(pair.inset - insets[0].inset) <= 0.5),
                    $"The advisor, model and mode chips share one left inset at {width}px ({string.Join(", ", insets.Select(pair => pair.name + " " + pair.inset.ToString("0.#")))}).");
                break;
            }
            Require(wrapped, "The footer chips wrap at some narrow width, so the row edges can be compared.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    // A primary button reads when enabled and when disabled: its label takes the button's colour, never its own.
    private static void PrimaryContrast()
    {
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.AwaitingPermission) };
        chat.Snapshot.Add(Item(new ChatQuestionItem("q", 1, "req-q", [new("Which?", [new("A"), new("B")])])));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var submit = view.RequestPanel.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ChatQuestionSubmit");
            var disabled = ContrastOf(submit);
            view.Input.Text = "";
            Frame(window);
            var sendDisabled = ContrastOf(view.SendButton);
            view.Input.Text = "hello";
            Frame(window);
            var sendEnabled = ContrastOf(view.SendButton);
            Console.WriteLine($"contrast: send enabled {sendEnabled:0.0}, send disabled {sendDisabled:0.0}, submit disabled {disabled:0.0}");
            Require(sendEnabled >= 4.5, $"An enabled primary button reads (contrast {sendEnabled:0.0}).");
            Require(sendDisabled >= MinimumDisabledContrast && disabled >= MinimumDisabledContrast, $"A disabled primary button still reads (send {sendDisabled:0.0}, submit {disabled:0.0}).");
            view.QuestionPick((ChatQuestionItem)view.Transcript.Item("q")!, 0, "A");
            Frame(window);
            Require(ContrastOf(view.RequestPanel.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ChatQuestionSubmit")) >= 4.5, "Submit answers reads once enabled.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    private const double MinimumDisabledContrast = 3.0;

    // Up/Down recall history only from the first/last visual line: a soft-wrapped draft moves the caret between its wrapped lines first.
    // Up/Down history never destroys a draft: Down outside history is plain caret movement, and leaving history restores what was typed.
    private static void DraftSurvivesHistory()
    {
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        ChatView.Histories[(Workspace, Chat)] = ["first", "second"];
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 600, Height = 500, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            void Press(Key key) => view.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            view.Input.Focus();
            view.Input.Text = "my draft"; view.Input.CaretIndex = 8; Frame(window);
            Press(Key.Down);
            Require(view.Input.Text == "my draft", $"Down outside history keeps the draft (got '{view.Input.Text}').");
            Press(Key.Up);
            Require(view.Input.Text == "second", "Up recalls the newest prompt.");
            Press(Key.Down);
            Require(view.Input.Text == "my draft", $"Down past the newest entry restores the draft (got '{view.Input.Text}').");
            Press(Key.Up); Press(Key.Up);
            Require(view.Input.Text == "first", "Up twice reaches the older prompt.");
            Press(Key.Down); Press(Key.Down);
            Require(view.Input.Text == "my draft", $"Up Up Down Down returns to the draft (got '{view.Input.Text}').");
            Press(Key.Up);
            view.Input.Text = "edited recall"; Frame(window);
            Press(Key.Down);
            Require(view.Input.Text == "edited recall", $"Editing a recalled entry leaves history; Down keeps the text (got '{view.Input.Text}').");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); ChatView.Histories.Remove((Workspace, Chat)); }
    }

    private static void WrappedHistory()
    {
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        ChatView.Histories[(Workspace, Chat)] = ["previous prompt"];
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 260, Height = 500, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            void Press(Key key) => view.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });
            view.Input.Focus();
            var wrapped = string.Join(" ", Enumerable.Repeat("wrapping", 20));
            view.Input.Text = wrapped; Frame(window); Frame(window);
            view.Input.CaretIndex = wrapped.Length; Frame(window);
            Press(Key.Up);
            Require(view.Input.Text == wrapped && view.Input.CaretIndex < wrapped.Length, $"Up on a wrapped second visual line moves the caret and keeps the draft (caret {view.Input.CaretIndex}).");
            view.Input.CaretIndex = 0; Frame(window);
            Press(Key.Down);
            Require(view.Input.Text == wrapped && view.Input.CaretIndex > 0, $"Down on the first visual line of a wrapped draft moves the caret and keeps the draft (caret {view.Input.CaretIndex}).");
            view.Input.CaretIndex = 0; Frame(window);
            Press(Key.Up);
            Require(view.Input.Text == "previous prompt", "Up on the first visual line recalls the previous prompt.");
            view.Input.Text = "short"; view.Input.CaretIndex = 5; Frame(window);
            Press(Key.Up);
            Require(view.Input.Text == "previous prompt", "A single-line draft still recalls history.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); ChatView.Histories.Remove((Workspace, Chat)); }
    }

    // Paste is the composer's own: an image (or files) on the clipboard becomes an attachment chip with its bytes, by the key, Shift+Insert and
    // the box's Paste; a clipboard of text only still goes into the box. Exercised on the headless in-memory clipboard, not the real macOS pasteboard.
    private static void PasteImages()
    {
        var notes = new List<string>();
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent) { Notify = (_, title, detail) => notes.Add(title + ": " + detail) }, Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII=");
        var file = Path.Combine(Path.GetTempPath(), "sharprail-paste-" + Guid.NewGuid().ToString("N")[..8] + ".png");
        File.WriteAllBytes(file, png);
        try
        {
            Frame(window); Frame(window);
            view.Input.Text = "";
            view.Input.Focus();
            var clipboard = window.Clipboard!;
            void Paste(KeyModifiers modifiers, Key key) => view.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
            int Chips() => view.GetVisualDescendants().OfType<Button>().Count(button => button.Name?.StartsWith("ChatAttachment_", StringComparison.Ordinal) == true);

            // The in-memory clipboard hands back the very bitmap it was given, which a paste disposes: each paste gets a fresh one.
            void SetImage() => Await(Avalonia.Input.Platform.ClipboardExtensions.SetBitmapAsync(clipboard, new Avalonia.Media.Imaging.Bitmap(new MemoryStream(png))));
            SetImage();
            Paste(KeyModifiers.Meta, Key.V);
            E2eWorkspace.Until(() => view.Attachments.Count == 1);
            Require(view.Attachments[0] is { Kind: "image", MediaType: "image/png", Data.Length: > 0 } && Chips() == 1 && view.Input.Text is null or "", $"Cmd+V with an image on the clipboard attaches it with its bytes and a chip ({view.Attachments[0]}, chips {Chips()}, text '{view.Input.Text}').");
            E2eWorkspace.Until(() => view.GetVisualDescendants().OfType<Image>().Any(image => image.Name == "ChatAttachmentPreview" && image.Source is not null));
            SetImage();
            Paste(KeyModifiers.Shift, Key.Insert);
            E2eWorkspace.Until(() => view.Attachments.Count == 2);
            SetImage();
            Paste(KeyModifiers.Control, Key.V);
            E2eWorkspace.Until(() => view.Attachments.Count == 3);
            view.GetVisualDescendants().OfType<Button>().First(button => button.Name?.StartsWith("ChatAttachment_", StringComparison.Ordinal) == true).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            Require(view.Attachments.Count == 2 && Chips() == 2, "A chip removes its attachment, and the others stay.");

            // Files on the clipboard: an image file is read and sent inline too.
            var item = window.StorageProvider.TryGetFileFromPathAsync(new Uri(file));
            Await(item);
            Await(Avalonia.Input.Platform.ClipboardExtensions.SetFileAsync(clipboard, item.Result!));
            Paste(KeyModifiers.Meta, Key.V);
            E2eWorkspace.Until(() => view.Attachments.Count == 3 && view.Attachments[2].Data is { Length: > 0 });
            Require(view.Attachments[2] is { Path: var path, MediaType: "image/png" } && path == file, "A file on the clipboard attaches by path, with its bytes read for an image.");

            // Text only: the box takes it as before, and nothing is attached.
            Await(Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, "plain words"));
            Paste(KeyModifiers.Meta, Key.V);
            E2eWorkspace.Until(() => view.Input.Text == "plain words");
            Require(view.Attachments.Count == 3, "A text-only clipboard is pasted into the box.");

            // Nothing usable: the note says what the clipboard had, so the next failure can be told apart.
            Await(clipboard.ClearAsync());
            Await(Avalonia.Input.Platform.ClipboardExtensions.SetValueAsync(clipboard, Avalonia.Input.DataFormat.CreateStringApplicationFormat("sharprail-test"), "x"));
            view.Input.Text = "";
            Frame(window);
            Await(view.PasteAttachments());
            var note = view.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "ChatPasteNote");
            try { E2eWorkspace.Until(() => note.IsVisible); } catch (InvalidOperationException) { Console.WriteLine("paste notes: note '" + note.Text + "' visible " + note.IsVisible + " " + string.Join("|", notes) + " formats: " + string.Join(",", Avalonia.Input.Platform.ClipboardExtensions.GetDataFormatsAsync(clipboard).GetAwaiter().GetResult().Select(format => format.Identifier)) + " attachments " + view.Attachments.Count + " text '" + view.Input.Text + "'"); throw; }
            Require(note.Text!.StartsWith("Nothing to paste. The clipboard has: ", StringComparison.Ordinal) && note.Text.Contains("sharprail-test", StringComparison.Ordinal), "A paste of nothing usable says so and what the clipboard held: " + note.Text);
            var command = view.BuildPlusMenu(new Flyout()).GetVisualDescendants().OfType<Button>().First(button => button.Name == "ChatCommandMenu");
            Require(ToolTip.GetTip(command) as string == "Show command menu (/)" && command.GetVisualDescendants().OfType<TextBlock>().All(text => text.Text != "/"), "The command menu button is an icon with its tooltip, not a bare slash.");

            // Over the cap: refused with a message.
            view.AddPasted(new byte[ChatView.MaxAttachmentBytes + 1], "image/png");
            Require(view.Attachments.Count == 3 && notes.Any(note => note.Contains("too large", StringComparison.Ordinal)), "An image over the cap is refused with a message.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); File.Delete(file); }
    }

    // Every clickable text in the drawn list shows it: a hand, a wash and an underline under the pointer, and a ring on the target keyboard focus is on.
    private static void HoverAffordance()
    {
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) };
        chat.Snapshot.Add(Item(new ChatUserItem("u", 1, "a short prompt") { Uuid = "u-1" }));
        chat.Snapshot.Add(Item(new ChatUserItem("long", 2, string.Join(" ", Enumerable.Repeat("a long prompt that folds with Show more", 12)))));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var list = view.List;
            var origin = list.TranslatePoint(new Point(0, 0), window)!.Value;
            Point In(string id, double x, double y) => new(origin.X + x, origin.Y + list.RowTop(id) + y);
            // Sweep the long prompt for its Show more: the pointer becomes a hand over it and the painted state changes.
            var paints = list.HoverPaints;
            var found = false;
            for (double y = 10; y < list.VisibleRows().First(row => row.Row.Id == "long").Height && !found; y += 4)
                for (double x = 20; x < 400 && !found; x += 10)
                {
                    window.MouseMove(In("long", x, y));
                    found = list.HoveredHit is { Kind: ChatHitKind.Toggle };
                }
            Frame(window);
            Require(found && list.SurfaceCursor is { } && list.HoverPaints > paints, "Show more has a hover affordance: a hand and a painted state.");
            window.MouseMove(new Point(origin.X + 5, origin.Y - 4)); Frame(window);
            window.MouseMove(In("u", 300, -1)); window.MouseMove(new Point(origin.X + 1, origin.Y + list.Bounds.Height + 40)); Frame(window);
            Require(list.HoveredHit is null && list.SurfaceCursor is null, "Moving away clears the hover and the cursor.");

            // Rewind and Fork appear on hover, are links like any other, and are reached from the keyboard.
            var prompt = In("u", 200, 10);
            window.MouseMove(prompt); window.MouseMove(new Point(prompt.X + 1, prompt.Y)); Frame(window);
            window.MouseDown(prompt, MouseButton.Left); window.MouseUp(prompt, MouseButton.Left); Frame(window);
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Frame(window);
            Require(list.FocusedStop is { Kind: ChatHitKind.Rewind }, "Tab reaches Rewind first, as it reads.");
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Frame(window);
            Require(list.FocusedStop is { Kind: ChatHitKind.Fork }, "Tab reaches Fork next.");
            window.KeyPress(Key.Tab, RawInputModifiers.None, PhysicalKey.Tab, null); Frame(window);
            Require(list.FocusedStop is null, "Tab past the last target leaves the row.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    // The command menu is a compact popup: bounded in width and height, one line per row, and only the rows in view exist, however many commands.
    private static void CommandMenu()
    {
        var commands = Enumerable.Range(0, 500).Select(index => new ChatCommand("skill-" + index, "A long description of what this skill does.\nIt runs over several lines and says far more than a menu row can hold, so it must be cut to one line. " + new string('x', 200)) { ArgumentHint = "[argument]" }).ToList();
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) { Commands = commands, Model = "opus" } };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 1400, Height = 800, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            view.Input.Text = "";
            view.Input.Focus();
            view.Input.Text = "/"; view.Input.CaretIndex = 1;
            E2eWorkspace.Until(() => view.MenuOpen);
            Frame(window); Frame(window);
            var frame = view.MenuFrame;
            var rows = view.Menu.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Require(rows.Count is > 0 and < 40, $"Only the rows in view are realized with 500 commands ({rows.Count}).");
            Require(frame.Bounds.Width <= ChatView.MenuMaxWidth + 0.5 && frame.Bounds.Width <= view.Input.Bounds.Width + 0.5, $"The menu is no wider than the composer or {ChatView.MenuMaxWidth}px ({frame.Bounds.Width:0}).");
            Require(frame.Bounds.Height <= view.Bounds.Height * 0.4 + 2, $"The menu is no taller than two fifths of the chat ({frame.Bounds.Height:0} of {view.Bounds.Height:0}).");
            var entryRows = rows.Where(row => row.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.StartsWith("/skill-", StringComparison.Ordinal) == true)).ToList();
            Require(entryRows.Count > 3 && entryRows.Select(row => Math.Round(row.Bounds.Height)).Distinct().Count() == 1 && entryRows[0].Bounds.Height <= 30, "Every entry row has the same one-line height.");
            Require(entryRows.All(row => row.GetVisualDescendants().OfType<TextBlock>().All(text => text.Text is null || !text.Text.Contains('\n') && text.Bounds.Height <= 20)), "Every name and description is one line.");
            Require(rows.Any(row => row.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "SLASH COMMANDS")), "A section title is a small caps label.");
            Require(ToolTip.GetTip(entryRows[0].GetVisualDescendants().OfType<Grid>().First()) is string tip && tip.Contains("It runs over several lines", StringComparison.Ordinal), "The whole description is in the tooltip.");
            view.Input.Text = "/skill-49"; view.Input.CaretIndex = 9;
            E2eWorkspace.Until(() => view.Menu.ItemCount is > 0 and < 20);
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    // Past conversations: dated buckets, one-line rows with a quiet relative time, markers for this chat's and other tabs' sessions, bounded and
    // virtualized, and a session another tab runs is focused rather than started a second time.
    private static void PastConversations()
    {
        var now = new DateTime(2026, 10, 25, 15, 0, 0, DateTimeKind.Local);
        static string Iso(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().ToString("o");
        var sessions = new List<ChatSessionInfo>
        {
            new("s-own", "This chat's own session", Iso(now.AddMinutes(-5))),
            new("s-other", "Another tab runs this one", Iso(now.AddHours(-3))) { ChatId = "other-chat", StartedIn = "chat" },
            new("s-yesterday", "Yesterday's work", Iso(now.AddDays(-1).AddHours(-2))),
            new("s-week", "Three days back", Iso(now.AddDays(-3))),
            new("s-old", "Last month", Iso(now.AddDays(-30)))
        };
        sessions.AddRange(Enumerable.Range(0, 300).Select(index => new ChatSessionInfo("s-bulk-" + index, "An old conversation with a long title that must be cut to one line " + index, Iso(now.AddDays(-40 - index)))));
        var opened = new List<(string Chat, string Title, ChatOpenParams? Open)>();
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) { SessionId = "s-own" } };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent) { OpenChat = (id, title, open) => opened.Add((id, title, open)) }, Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        Window? flyoutHost = null;
        try
        {
            Frame(window); Frame(window);
            var items = ChatView.SessionItems(sessions, "", "s-own", Chat, now);
            Require(items.OfType<ChatView.SessionBucketHeader>().Select(header => header.Title).SequenceEqual(["Today", "Yesterday", "Previous 7 days", "Older"]), "Sessions fall into Today, Yesterday, Previous 7 days and Older.");
            var rows = items.OfType<ChatView.SessionRow>().ToList();
            Require(rows[0].Relative == "5m ago" && rows[1].Relative == "3h ago" && rows[2].Relative == "Yesterday" && rows[3].Relative == "3d ago" && rows[4].Relative == "Sep 25", $"Times read as relative, then a short date ({string.Join(", ", rows.Take(5).Select(row => row.Relative))}).");
            Require(rows[0].Current && !rows[0].Elsewhere && rows[1].Elsewhere && !rows[1].Current && rows.Skip(2).All(row => !row.Current && !row.Elsewhere), "This chat's session and another tab's are marked, no other.");

            var (panel, list, search) = view.BuildSessions(sessions, session => view.ChooseSession(session), now);
            flyoutHost = new Window { Width = 700, Height = 700, Content = panel, Background = Ui.Surface };
            flyoutHost.Show();
            Frame(flyoutHost); Frame(flyoutHost);
            var realized = list.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Require(realized.Count is > 0 and < 40, $"Only the rows in view exist with 300 sessions ({realized.Count}).");
            Require(panel.Bounds.Width <= ChatView.MenuMaxWidth + 0.5 && list.Bounds.Height <= view.Bounds.Height * 0.4 + 2, $"The flyout is bounded ({panel.Bounds.Width:0} x {list.Bounds.Height:0}).");
            var texts = realized.SelectMany(row => row.GetVisualDescendants().OfType<TextBlock>()).Select(text => text.Text ?? "").ToList();
            Require(texts.Contains("TODAY") && texts.Contains("5m ago") && texts.Contains("this chat") && texts.Contains("open in another tab") && !texts.Any(text => System.Text.RegularExpressions.Regex.IsMatch(text, @"\d{4}-\d\d-\d\dT")), "Headers, relative times and markers show, and no raw timestamp.");
            var sessionRows = realized.Where(row => row.DataContext is ChatView.SessionRow).ToList();
            Require(sessionRows.Count > 3 && sessionRows.All(row => row.GetVisualDescendants().OfType<TextBlock>().All(text => text.Bounds.Height <= 20)), "Session rows are compact.");
            Require(sessionRows.All(row => row.GetVisualDescendants().OfType<Grid>().First().Height == ChatPopup.RowHeight), "Each session is one row of the popup grid.");
            Require(ToolTip.GetTip(sessionRows[0].GetVisualDescendants().OfType<Border>().First(border => border.Name == "ChatSessionRow")) is string tip && tip.Contains("2026", StringComparison.Ordinal), "The tooltip holds the full date and time.");

            // At several flyout widths the time, the count and the markers stay whole inside their row; only the title gives way.
            var twins = new List<ChatSessionInfo>
            {
                new("t1", "meow", Iso(now.AddMinutes(-5))) { Messages = 12 },
                new("t2", "meow", Iso(now.AddHours(-3))) { Messages = 1, ChatId = "other-chat" },
                new("t3", "A very long title that cannot fit a narrow flyout and so is cut with an ellipsis instead of the time", Iso(now.AddDays(-1))) { Messages = 240, StartedIn = "chat" }
            };
            foreach (var width in new[] { 280.0, 340, 460, 700 })
            {
                var (narrowPanel, narrowList, _) = view.BuildSessions(twins, session => { }, now);
                narrowPanel.Width = width;
                var host = new Window { Width = width + 40, Height = 500, Content = narrowPanel, Background = Ui.Surface };
                host.Show();
                try
                {
                    Frame(host); Frame(host);
                    var shown = narrowList.GetVisualDescendants().OfType<ListBoxItem>().Where(row => row.DataContext is ChatView.SessionRow).ToList();
                    Require(shown.Count == 3, $"All rows exist at width {width}.");
                    foreach (var row in shown)
                        foreach (var text in row.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Name == "ChatSessionSubtitle").Children.OfType<TextBlock>())
                        {
                            var at = text.TranslatePoint(new Point(0, 0), row)!.Value;
                            Require(at.X >= 0 && at.X + text.Bounds.Width <= row.Bounds.Width + 0.5 && text.Bounds.Height > 0, $"The subtitle part '{text.Text}' lies inside its row at width {width} ({at.X + text.Bounds.Width:0} of {row.Bounds.Width:0}).");
                        }
                    var subtitles = shown.Take(2).Select(row => string.Join(" ", row.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Name == "ChatSessionSubtitle").Children.OfType<TextBlock>().Select(text => text.Text))).ToList();
                    Require(subtitles[0] != subtitles[1] && subtitles[0].Contains("12 messages") && subtitles[1].Contains("1 message") && subtitles[1].Contains("open in another tab"), $"Two chats with one title read differently ({string.Join(" | ", subtitles)}).");
                }
                finally { host.Close(); }
            }
            search.Text = "Another tab";
            E2eWorkspace.Until(() => list.ItemCount == 2);

            // Choosing: this chat's own does nothing, another tab's is focused, any other resumes in a new tab.
            view.ChooseSession(sessions[0]);
            Require(opened.Count == 0, "Choosing this chat's own session opens nothing.");
            view.ChooseSession(sessions[1]);
            Require(opened.Count == 1 && opened[0].Chat == "other-chat" && opened[0].Open is null, "A session another tab runs is focused, not started again.");
            view.ChooseSession(sessions[2]);
            Require(opened.Count == 2 && opened[1].Chat != "other-chat" && opened[1].Open is { ResumeSessionId: "s-yesterday" }, "Any other session resumes in a new tab.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { flyoutHost?.Close(); window.Close(); }
    }

    // Choosing an answer repaints in place: the same panel, no row replaced or laid out again, no option moves or changes size.
    private static void QuestionStable()
    {
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.AwaitingPermission) };
        chat.Snapshot.Add(Item(new ChatUserItem("u", 1, "do the thing")));
        chat.Snapshot.Add(Item(new ChatAssistantItem("a", 2, "First paragraph.\n\nSecond paragraph with **bold** text.")));
        chat.Snapshot.Add(Item(new ChatQuestionItem("q", 3, "req-q", [new("Which approach?", [new("Fast", "less careful"), new("Careful", "slower"), new("Other way")])])));
        chat.Snapshot.Add(Item(new ChatQuestionItem("q2", 4, "req-q2", [new("Which checks?", [new("Unit"), new("E2E")]) { MultiSelect = true }])));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var item = (ChatQuestionItem)view.Transcript.Item("q")!;
            List<(double X, double Y, double W, double H)> Boxes() => [.. view.RequestPanel.GetVisualDescendants().OfType<Button>().Where(button => button.Name == "ChatQuestionOption")
                .Select(button => (button.TranslatePoint(new Point(0, 0), view.RequestPanel)!.Value is var at ? at.X : 0, button.TranslatePoint(new Point(0, 0), view.RequestPanel)!.Value.Y, button.Bounds.Width, button.Bounds.Height))];
            void Same(string when, object panel, ChatRow row, List<(double, double, double, double)> boxes, int built, double extent, Vector offset)
            {
                Frame(window); Frame(window);
                Require(ReferenceEquals(view.QuestionPanelInstance, panel), $"{when}: the panel is the same object.");
                Require(ReferenceEquals(view.Transcript.Row("q"), row), $"{when}: no transcript row was replaced.");
                Require(ChatRowLayout.Built == built, $"{when}: no layout was built ({ChatRowLayout.Built - built}).");
                Require(Math.Abs(view.List.Scroller!.Extent.Height - extent) < 0.5 && view.List.Scroller.Offset == offset, $"{when}: the list did not move or change size.");
                var after = Boxes();
                Require(after.Count == boxes.Count && after.Zip(boxes).All(pair => Math.Abs(pair.First.Item2 - pair.Second.Item2) < 0.5 && Math.Abs(pair.First.Item4 - pair.Second.Item4) < 0.5 && Math.Abs(pair.First.Item3 - pair.Second.Item3) < 0.5),
                    $"{when}: no option changed size or moved.");
            }
            var boxes = Boxes();
            var panel = view.QuestionPanelInstance!;
            var row = view.Transcript.Row("q")!;
            var built = ChatRowLayout.Built;
            var extent = view.List.Scroller!.Extent.Height;
            var offset = view.List.Scroller.Offset;
            view.QuestionPick(item, 0, "Fast");
            Same("Picking an option", panel, row, boxes, built, extent, offset);
            view.QuestionPick(item, 0, "Careful");
            Same("Changing the pick", panel, row, boxes, built, extent, offset);
            view.QuestionPick(item, 0, "Careful");
            Same("Unpicking", panel, row, boxes, built, extent, offset);
            Require(!view.RequestPanel.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ChatQuestionSubmit").IsEnabled, "Submit stays disabled while nothing is chosen.");
            view.QuestionPick(item, 0, "Fast");
            Frame(window);
            Require(view.RequestPanel.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ChatQuestionSubmit").IsEnabled, "Submit enables as soon as an answer is chosen, without a rebuild.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    private sealed class MemoryZoomStore : SharpRail.Plugins.Api.UI.IPluginPreference
    {
        private string? value;
        public string? Get() => value;
        public void Set(string? next) => value = next;
        public void Remove() => value = null;
    }

    // A chat zooms by itself: keys, wheel and pinch, clamped, remembered per chat, bounded at every scale, and the page zoom stays put.
    private static void ChatZoom()
    {
        var store = new MemoryZoomStore();
        var chat = new FakeChat();
        for (var index = 0; index < 20_000; index++) chat.Snapshot.Add(Item(Sample(index)));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent) { ZoomPreference = store }, Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var page = InterfaceZoom.Current;
            Require(view.Transcript.Rows.Count == 20_000, $"The 20,000 items are rows ({view.Transcript.Rows.Count}).");
            void Chord(KeyModifiers modifiers, Key key) => view.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
            view.Input.Focus();
            Chord(KeyModifiers.Meta, Key.OemPlus); Require(view.Zoom == 1.1 && view.ZoomPlateVisible && view.ZoomLabelText == "110%", $"Mod+= steps the chat's zoom and shows the level ({view.Zoom}, '{view.ZoomLabelText}').");
            Chord(KeyModifiers.Control, Key.OemPlus); Require(view.Zoom == 1.25, "Ctrl+= does the same.");
            Chord(KeyModifiers.Meta, Key.OemMinus); Chord(KeyModifiers.Meta, Key.OemMinus); Require(view.Zoom == 1, "Mod+- steps back down.");
            Chord(KeyModifiers.Meta, Key.OemPlus); Chord(KeyModifiers.Meta, Key.D0); Require(view.Zoom == 1, "Mod+0 resets the chat.");
            Require(InterfaceZoom.Current == page, "The page zoom did not change.");

            window.MouseWheel(new Point(300, 300), new Vector(0, 3), RawInputModifiers.Meta); Frame(window);
            Require(view.Zoom > 1, $"Cmd with the wheel zooms the chat in ({view.Zoom}).");
            view.StepZoom(0);
            var offset = view.List.Scroller!.Offset;
            window.MouseWheel(new Point(300, 300), new Vector(0, -1), RawInputModifiers.None); Frame(window);
            Require(view.Zoom == 1, "A plain wheel only scrolls.");
            view.MagnifyZoom(0.3); Require(view.Zoom == 1.3, $"A pinch magnifies by its delta ({view.Zoom}).");
            view.MagnifyZoom(50); Require(view.Zoom == ChatView.MaxZoom, "The zoom stops at its largest.");
            view.MagnifyZoom(-0.99); Require(view.Zoom == ChatView.MinZoom, "And at its smallest.");
            view.SetZoom(0.1); Require(view.Zoom == ChatView.MinZoom, "A value outside the range is clamped.");
            Require(InterfaceZoom.Current == page, "Gestures did not touch the page zoom.");

            // Bounded realization at every scale, and the list's width follows the scale.
            foreach (var scale in new[] { 0.7, 1.0, 2.0 })
            {
                view.SetZoom(scale);
                Frame(window); Frame(window); Frame(window);
                Require(view.List.RealizedRows is > 0 and < 60 && ChatRowLayout.Live < 110 && ChatRowLayout.LiveTexts < 900, $"At {scale} only the viewport's rows exist ({view.List.RealizedRows} rows, {ChatRowLayout.Live} layouts; rows {view.Transcript.Rows.Count}; list {view.List.Bounds}, viewport {view.List.Scroller!.Viewport}, extent {view.List.Scroller.Extent}, host {view.Bounds}).");
                Require(Math.Abs(view.List.Bounds.Width * scale - view.Bounds.Width) < 2, $"The list reflows at {scale}: {view.List.Bounds.Width:0} wide in a {view.Bounds.Width:0} view.");
            }

            // Remembered per chat id.
            view.SetZoom(1.5);
            var again = new ChatView(new ChatEnvironment(new FakeChat(), TestAgent) { ZoomPreference = store }, Workspace, Chat);
            var other = new ChatView(new ChatEnvironment(new FakeChat(), TestAgent) { ZoomPreference = store }, Workspace, "chat-zoom-b");
            Require(again.Zoom == 1.5 && other.Zoom == 1, $"A reopened chat keeps its zoom and another chat has its own ({again.Zoom}, {other.Zoom}).");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    // Zooming the whole page changes the chat's width in steps: the surface and the scroll position survive each one, only the rows in view are
    // laid out again (once, not once per pass), and the reading position does not move.
    private static void PageZoomSteps()
    {
        var chat = new FakeChat();
        for (var index = 0; index < 20_000; index++) chat.Snapshot.Add(Item(Sample(index)));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var transform = new LayoutTransformControl { LayoutTransform = new ScaleTransform(1, 1), Child = view };
        var window = new Window { Width = 1000, Height = 700, Content = transform, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var scroller = view.List.Scroller!;
            scroller.Offset = new Vector(0, scroller.Extent.Height * 0.5);
            Frame(window); Frame(window); Frame(window);
            var content = view.List.Content;
            var reading = view.List.ReadingPosition();
            Require(reading is not null, "A row is being read.");
            var total = 0;
            foreach (var factor in new[] { 1.1, 1.25, 1.5, 1.25, 1.1, 1.0 })
            {
                var before = ChatRowLayout.Built;
                var passes = view.List.Passes;
                transform.LayoutTransform = new ScaleTransform(factor, factor);
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using (window.CaptureRenderedFrame()) { }
                var first = ChatRowLayout.Built - before;
                Frame(window); Frame(window);
                var settled = ChatRowLayout.Built - before;
                total += settled;
                Require(view.List.Passes - passes <= 2, $"A zoom step costs at most two settling passes ({view.List.Passes - passes}, zoom {factor}).");
                Require(ReferenceEquals(view.List.Content, content), $"The surface survives a zoom to {factor}.");
                Require(settled - first <= 4, $"A zoom step settles in one pass: {first} layouts in the first frame, {settled} in all (zoom {factor}).");
                Require(settled <= 70, $"A zoom step lays out only the rows in view ({settled} layouts, zoom {factor}).");
                var now = view.List.ReadingPosition();
                Require(now is not null && now.Value.Id == reading!.Value.Id, $"The row being read stays the row being read at {factor} ({reading!.Value.Id} then {now?.Id}).");
                Require(view.List.RealizedRows is > 0 and < 60 && ChatRowLayout.Live < 110, $"Realization stays bounded at {factor}.");
            }
            Require(total <= 360, $"Six zoom steps cost {total} layouts in all.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    // Dragging the chat's edge fires many widths in a row: the surface and the reading row survive, each step lays out only what is in view, no frame is
    // drawn for an old width, and the footer's density follows the width monotonically and does not flip at a boundary.
    private static void ResizeDrag()
    {
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) { Model = "opus", Effort = "high", PermissionMode = "default", Models = [new("opus", "Opus 5.1")], Usage = new ChatUsage { Input = 12345, Output = 6789, CostUsd = 0.12 } } };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        for (var index = 0; index < 400; index++) chat.Snapshot.Add(Item(Sample(index)));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var scroller = view.List.Scroller!;
            scroller.Offset = new Vector(0, scroller.Extent.Height * 0.4);
            Frame(window); Frame(window); Frame(window);
            var content = view.List.Content;
            var reading = view.List.ReadingPosition();
            Require(reading is not null, "A row is being read.");
            var worst = 0;
            var staleBefore = view.List.StaleFrames;
            var framesBefore = view.List.Frames;
            int Step(double width)
            {
                var before = ChatRowLayout.Built;
                window.Width = width;
                // One frame per width, as a drag delivers them: no settling in between.
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                using (window.CaptureRenderedFrame()) { }
                Dispatcher.UIThread.RunJobs();
                var built = ChatRowLayout.Built - before;
                worst = Math.Max(worst, built);
                Require(built <= 60, $"A drag step lays out only the rows in view ({built} layouts at {width}).");
                Require(ReferenceEquals(view.List.Content, content), $"The surface survives a drag to {width}.");
                return view.DensityLevel;
            }
            void Settled(string phase)
            {
                Frame(window); Frame(window);
                var now = view.List.ReadingPosition();
                Require(now is not null && now.Value.Id == reading!.Value.Id, $"The row being read stays the row being read after {phase} ({reading!.Value.Id} then {now?.Id}).");
                Require(view.List.RealizedRows is > 0 and < 60 && ChatRowLayout.Live < 110, $"Realization stays bounded after {phase}.");
            }
            var shrinking = Enumerable.Range(0, 20).Select(index => Step(900 - index * 42)).ToList();
            Settled("shrinking");
            Require(shrinking.Zip(shrinking.Skip(1)).All(pair => pair.Second >= pair.First), $"Shrinking never relaxes the footer ({string.Join(",", shrinking)}).");
            var growing = Enumerable.Range(0, 20).Select(index => Step(102 + index * 42)).ToList();
            Settled("growing");
            Require(growing.Zip(growing.Skip(1)).All(pair => pair.Second <= pair.First), $"Growing never tightens the footer ({string.Join(",", growing)}).");
            Require(shrinking[0] == growing[^1] || growing[^1] <= shrinking[0] + 0, "The footer relaxes again at the width it started from.");

            // Around a boundary the level changes at most once in a wobble of six pixels either side.
            var boundaries = new List<double>();
            for (var index = 1; index < shrinking.Count; index++)
                if (shrinking[index] != shrinking[index - 1]) boundaries.Add(900 - index * 24);
            Require(boundaries.Count > 0, $"The sweep crosses at least one density boundary ({string.Join(",", shrinking)}).");
            foreach (var coarse in boundaries.Take(3))
            {
                double high = coarse + 24, low = coarse;
                Step(high);
                var upper = view.DensityLevel;
                for (var round = 0; round < 6; round++)
                {
                    var middle = (high + low) / 2;
                    if (Step(middle) == upper) high = middle; else low = middle;
                    Step(high + 24);
                }
                var edge = (high + low) / 2;
                Step(edge + 30);
                var levels = new List<int>();
                for (var index = 0; index < 12; index++) levels.Add(Step(index % 2 == 0 ? edge - 6 : edge + 6));
                var changes = levels.Zip(levels.Skip(1)).Count(pair => pair.First != pair.Second);
                Require(changes <= 1, $"A wobble of +-6px around {edge:0} changes the footer at most once ({string.Join(",", levels)}).");
            }
            Step(900);
            Settled("the wobble");
            Require(view.List.StaleFrames == staleBefore, $"No frame was drawn for an old width ({view.List.StaleFrames - staleBefore} of {view.List.Frames - framesBefore}).");
            Console.WriteLine($"resize drag: at most {worst} layouts built in a step, {view.List.Frames - framesBefore} frames drawn, none stale");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    // Twenty thousand variable-height items: scrolling, resizing and streaming keep the realized rows and text layouts small.
    // A reply streams in as repeated updates of one row: the extent and the offset a viewer sees between frames never step back, and one
    // update costs one settling pass, so the bottom does not jump to a guessed height and back.
    private static void StreamingStable()
    {
        var chat = new FakeChat();
        for (var index = 0; index < 40; index++) chat.Snapshot.Add(Item(Sample(index)));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var scroller = view.List.Scroller!;
            var seen = new List<(double Extent, double Offset)>();
            scroller.ScrollChanged += (_, _) => seen.Add((scroller.Extent.Height, scroller.Offset.Y));
            var text = "";
            string[] pieces = ["Here is the plan.\n\n", "- first item\n", "- second item with more words to wrap around the line a bit\n\n", "```cs\n", "var a = 1;\n", "var b = 2;\n", "```\n\n", "| a | b |\n|---|---|\n| 1 | 2 |\n\n", "And a closing paragraph that is long enough to wrap onto a second line of the view, repeated. And a closing paragraph that is long enough to wrap onto a second line."];
            var passes = view.List.Passes;
            for (var step = 0; step < pieces.Length; step++)
            {
                text += pieces[step];
                chat.Push(Item(new ChatAssistantItem("stream", 900, text) { Streaming = true }));
                Frame(window);
                Require(view.List.Passes - passes <= 2 * (step + 1), $"Step {step}: an update costs at most two settling passes ({view.List.Passes - passes} so far).");
            }
            Frame(window); Frame(window);
            for (var at = 1; at < seen.Count; at++)
            {
                Require(seen[at].Extent >= seen[at - 1].Extent - 0.5, $"The extent never shrinks while text only grows (step {at}: {seen[at - 1].Extent} then {seen[at].Extent}).");
                Require(seen[at].Offset >= seen[at - 1].Offset - 0.5, $"The pinned offset never steps back while text only grows (step {at}: {seen[at - 1].Offset} then {seen[at].Offset}).");
            }
            Require(view.List.Stuck && scroller.Offset.Y >= scroller.Extent.Height - scroller.Viewport.Height - 4, "The stream keeps following the end.");
        }
        finally { window.Close(); }
    }

    private static void Bounded()
    {
        var chat = new FakeChat();
        for (var index = 0; index < 20_000; index++) chat.Snapshot.Add(Item(Sample(index)));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            Require(view.Transcript.Rows.Count == 20_000, "All items are rows of the transcript.");
            void Bound(string when)
            {
                Require(view.List.RealizedRows is > 0 and < 60, $"{when}: only viewport rows hold controls (had {view.List.RealizedRows}).");
                Require(ChatRowLayout.Live < 110 && ChatRowLayout.LiveTexts < 900, $"{when}: only viewport rows hold text layouts ({ChatRowLayout.Live} layouts, {ChatRowLayout.LiveTexts} texts).");
            }
            Bound("on mount");
            Require(view.List.Stuck && view.List.Scroller!.Offset.Y >= view.List.Scroller.Extent.Height - view.List.Scroller.Viewport.Height - 8, "A new chat follows its newest item.");
            foreach (var fraction in new[] { 0.5, 0.1, 0.9 })
            {
                var scroller = view.List.Scroller!;
                scroller.Offset = new Vector(0, scroller.Extent.Height * fraction);
                Frame(window); Frame(window);
                Bound("scrolled to " + fraction);
            }
            window.Width = 500; Frame(window); Frame(window);
            Bound("narrowed");
            window.Width = 1100; Frame(window); Frame(window);
            Bound("widened");
            view.List.Scroller!.Offset = new Vector(0, view.List.Scroller.Extent.Height);
            Frame(window); Frame(window);
            Require(view.List.Stuck, "Scrolling to the end follows again.");

            var before = ChatRowLayout.Built;
            for (var step = 0; step < 100; step++)
                chat.Push(Item(new ChatAssistantItem("stream", 30_000, "Streaming " + new string('w', step * 4)) { Streaming = true }));
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Frame(window); Frame(window);
            Require(view.Transcript.Rows.Count == 20_001, "Streaming updates one row in place.");
            Require(ChatRowLayout.Built - before < 12, $"A hundred streaming pushes in one frame cost a few layouts, not one per push (cost {ChatRowLayout.Built - before}).");
            Require(view.List.Scroller!.Offset.Y >= view.List.Scroller.Extent.Height - view.List.Scroller.Viewport.Height - 8, "Streaming keeps following the end.");
            Bound("streaming");

            // The user scrolls up: following lets go and "jump to latest" appears.
            view.List.Scroller!.Offset = new Vector(0, view.List.Scroller.Extent.Height * 0.3);
            Frame(window); Frame(window);
            chat.Push(Item(new ChatAssistantItem("late", 30_001, "Arrived while reading")));
            Frame(window);
            Require(!view.List.Stuck && view.List.Scroller.Offset.Y < view.List.Scroller.Extent.Height * 0.5, $"Scrolling up stops following and keeps the position (stuck {view.List.Stuck}, offset {view.List.Scroller.Offset.Y:0}, extent {view.List.Scroller.Extent.Height:0}, viewport {view.List.Scroller.Viewport.Height:0}).");
            var jump = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ChatJumpToLatest");
            Require(jump.IsVisible, "A jump-to-latest control appears when not following.");
            view.List.JumpToLatest(); Frame(window); Frame(window);
            Require(view.List.Stuck && view.List.Scroller.Offset.Y >= view.List.Scroller.Extent.Height - view.List.Scroller.Viewport.Height - 8, "Jump to latest returns to the end.");

            // Scroll anchoring: a row far above the viewport growing taller moves nothing the reader is looking at.
            var anchored = view.List.Scroller!;
            anchored.Offset = new Vector(0, anchored.Extent.Height * 0.5);
            Frame(window); Frame(window);
            double TopOf(string id) => view.List.RowTop(id);
            var reading = view.List.VisibleRows().First(pair => pair.Top >= 0).Row;
            var readingTop = TopOf(reading.Id);
            chat.Push(Item(new ChatUserItem("i100", 100, string.Join(" ", Enumerable.Repeat("a much longer question that wraps over many lines", 80)))));
            Frame(window); Frame(window);
            Require(Math.Abs(TopOf(reading.Id) - readingTop) < 3 && !view.List.Stuck, $"The row being read stays put when one above grows ({readingTop} then {TopOf(reading.Id)}).");

            // Measuring rows above the one being read (as scrolling up through them would) or removing one moves nothing the reader sees.
            var readingRow = view.List.VisibleRows().First(pair => pair.Top >= 0).Row;
            var held = TopOf(readingRow.Id);
            var readingIndex = view.Transcript.Rows.ToList().FindIndex(row => row.Id == readingRow.Id);
            view.List.MeasureRows(readingIndex - 400, 300);
            Frame(window); Frame(window);
            Require(Math.Abs(TopOf(readingRow.Id) - held) < 1, $"Measuring rows above the reading row moves it {TopOf(readingRow.Id) - held:0.0} px.");
            chat.Push(new ChatRemovePush(Workspace, Chat, "i50"));
            Frame(window); Frame(window);
            Require(view.Transcript.Item("i50") is null && Math.Abs(TopOf(readingRow.Id) - held) < 1, "Removing a row far above the reading row moves nothing in view.");

            // The scrollbar thumb only moves forward as the list is scrolled through, though rows are first measured on the way.
            var thumbScroller = view.List.Scroller;
            thumbScroller.Offset = new Vector(0, 0); Frame(window); Frame(window);
            var thumbStep = thumbScroller.Extent.Height / 60;
            var lastThumb = -1.0;
            for (var at = 0; at < 60; at++)
            {
                thumbScroller.Offset = new Vector(0, thumbStep * at); Frame(window);
                var thumb = thumbScroller.Offset.Y / Math.Max(1, thumbScroller.Extent.Height - thumbScroller.Viewport.Height);
                Require(thumb >= lastThumb - 0.002, $"The thumb stepped back at {at}: {lastThumb:0.0000} then {thumb:0.0000}.");
                lastThumb = thumb;
            }
            Bound("after the long scroll");

            // Estimates stay near what layout measures, so a scroll to a row far away lands close; the reading position survives a tab switch.
            var drift = view.List.VisibleRows().Where(pair => pair.Height > 0)
                .Select(pair => (Estimate: ChatRowEstimates.Estimate(pair.Row, view.List.Bounds.Width), Measured: pair.Height)).ToList();
            Require(drift.Count > 3 && drift.Count(pair => pair.Estimate >= pair.Measured * 0.5 && pair.Estimate <= pair.Measured * 2) * 4 >= drift.Count * 3,
                "Row height estimates stay within half to double of the measured heights: " + string.Join(", ", drift.Select(pair => $"{pair.Estimate:0}/{pair.Measured:0}")));
            var readingBefore = view.List.ReadingPosition();
            window.Content = null; Frame(window);
            window.Content = view; Frame(window); Frame(window); Frame(window);
            var readingAfter = view.List.ReadingPosition();
            Require(readingBefore is { } was && readingAfter is { } now && was.Id == now.Id && Math.Abs(was.Delta - now.Delta) < 60, $"The reading position survives a tab switch ({readingBefore} then {readingAfter}).");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
        Require(ChatRowLayout.Live < 40, "Closing releases the realized layouts.");
    }

    // Text selection in the drawn list: drags, words, lines, copy text, streaming, and a whole 20,000-row transcript without realizing rows.
    private static void Selection()
    {
        var chat = new FakeChat();
        chat.Snapshot.Add(Item(new ChatUserItem("u1", 1, "alpha beta gamma")));
        chat.Snapshot.Add(Item(new ChatAssistantItem("a1", 2, "First paragraph here\n\nSecond paragraph there")));
        chat.Snapshot.Add(Item(new ChatAssistantItem("s1", 3, "Streaming so far") { Streaming = true }));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var list = view.List;
            Point Spot(string id, double x, double dy)
            {
                var origin = list.TranslatePoint(new Point(0, 0), window)!.Value;
                return new Point(origin.X + x, origin.Y + list.RowTop(id) + dy);
            }
            string Selected() { var task = list.SelectedTextAsync(); Await(task); return task.Result; }

            window.MouseDown(Spot("u1", 24, 16), MouseButton.Left); window.MouseMove(Spot("u1", 600, 16)); window.MouseUp(Spot("u1", 600, 16), MouseButton.Left);
            Frame(window);
            Require(Selected() == "alpha beta gamma", $"Dragging within a row selects its text (got '{Selected()}').");

            window.MouseDown(Spot("u1", 24, 16), MouseButton.Left); window.MouseMove(Spot("a1", 600, 50)); window.MouseUp(Spot("a1", 600, 50), MouseButton.Left);
            Frame(window);
            var across = Selected();
            Require(across.StartsWith("alpha beta gamma\n\nFirst paragraph here") && across.Length > 40, $"Dragging across rows selects the text between (got '{across}').");

            list.SelectAll();
            Require(Selected() == "alpha beta gamma\n\nFirst paragraph here\nSecond paragraph there\n\nStreaming so far", $"Select all reads the whole transcript (got '{Selected()}').");

            var (wordStart, wordEnd) = ChatRowLayout.WordAt("alpha beta gamma", 7);
            Require("alpha beta gamma"[wordStart..wordEnd] == "beta", "A word is the run of letters around the offset.");
            var (lineStart, lineEnd) = ChatRowLayout.ParagraphAt("one\ntwo words\nthree", 6);
            Require("one\ntwo words\nthree"[lineStart..lineEnd] == "two words", "A line is the run between line breaks.");

            // A selection is anchored to ids and offsets: a streaming append and a taller row above leave it where it was.
            list.Select(("s1", 0), ("s1", 9));
            Require(Selected() == "Streaming", "A selection inside the streaming reply reads its first word.");
            chat.Push(Item(new ChatAssistantItem("s1", 3, "Streaming so far and more words") { Streaming = true }));
            chat.Push(Item(new ChatUserItem("u1", 1, string.Join(" ", Enumerable.Repeat("alpha beta gamma", 40)))));
            Frame(window); Frame(window);
            Require(Selected() == "Streaming", $"The selection survives a streaming append and a height change above (got '{Selected()}').");

            window.MouseDown(Spot("s1", 800, 600), MouseButton.Left); window.MouseUp(Spot("s1", 800, 600), MouseButton.Left);
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }

        var big = new FakeChat();
        for (var index = 0; index < 20_000; index++) big.Snapshot.Add(Item(Sample(index)));
        var bigView = new ChatView(new ChatEnvironment(big, TestAgent), Workspace, Chat);
        var bigWindow = new Window { Width = 900, Height = 700, Content = bigView, Background = Ui.Surface };
        bigWindow.Show();
        try
        {
            Frame(bigWindow); Frame(bigWindow);
            bigView.List.SelectAll();
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var task = bigView.List.SelectedTextAsync();
            Await(task);
            Require(task.Result.Length > 500_000 && task.Result.StartsWith("Question 0"), $"Select all over 20,000 rows reads them all ({task.Result.Length} characters).");
            Require(bigView.List.RealizedRows is > 0 and < 60 && ChatRowLayout.Live < 110, $"Copying the transcript realizes no rows (realized {bigView.List.RealizedRows}, layouts {ChatRowLayout.Live}, {watch.ElapsedMilliseconds} ms).");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { bigWindow.Close(); }
    }

    // Highlights cover glyphs only: inside a text line's box, inside the clip the text draws in, never in a gap, and clipped text is not copied.
    private static void SelectionGeometry()
    {
        var tool = Tool("Mystery", new { text = string.Join("\n", Enumerable.Range(0, 30).Select(i => "input line " + i)) }, output: string.Join("\n", Enumerable.Range(0, 30).Select(i => "output line " + i)));
        var reply = new ChatAssistantItem("g1", 2, "A wrapped paragraph " + string.Join(" ", Enumerable.Repeat("words that wrap over several lines", 12)) + "\n\n```csharp\nvar a = 1;\n\nvar b = 2;\n```\n\nTail text.");
        foreach (var (item, expanded) in new (ChatItem, bool)[] { (tool, true), (reply, false) })
        {
            using var layout = ChatRowLayout.Build(new ChatRow(item, 0, 1, expanded, null), 420, true);
            layout.Realize(0, layout.Height);
            var parts = layout.Parts();
            var text = layout.SelectableText();
            Require(text.Length > 20, "The row has selectable text.");
            var rects = layout.RangeRects(0, text.Length);
            Require(rects.Count > 0, "A selection over the whole row highlights something.");
            foreach (var rect in rects)
            {
                var owner = parts.Where(part => part.Layout is not null).FirstOrDefault(part =>
                {
                    var local = rect.Translate(new Vector(-part.Origin.X, -part.Origin.Y));
                    var line = ChatRowLayout.LineBox(part.Layout!, local.Y + local.Height / 2);
                    var inLine = local.X >= line.X - 0.5 && local.Right <= line.Right + ChatRowLayout.NewlineCue + 0.5 && local.Y >= line.Y - 0.5 && local.Bottom <= line.Bottom + 0.5;
                    return inLine && (part.Clip is not { } clip || clip.Inflate(0.5).Contains(rect));
                });
                Require(owner.Layout is not null, $"Every highlight lies inside a text line's box and its clip ({rect}).");
            }
            Require(parts.All(part => part.Layout is not null), "Every part is realized.");
            if (item == tool)
            {
                var clipped = parts.Where(part => part.Clip is not null).ToList();
                Require(clipped.Count > 0 && clipped.All(part => part.Text.Length < 30 * 14), "Clipped boxes expose only the visible text.");
                Require(clipped.All(part => !part.Text.Contains("line 29", StringComparison.Ordinal)), "Text clipped away is not selectable or copied.");
                Require(rects.All(rect => clipped.Any(part => part.Clip!.Value.Inflate(0.5).Contains(rect)) || parts.Any(part => part.Clip is null)), "No highlight runs past a clipped box.");
            }
            else
            {
                var blocks = parts.Select(part => new Rect(part.Origin, new Size(part.Layout!.Width, part.Layout.Height))).OrderBy(box => box.Y).ToList();
                foreach (var rect in rects)
                    Require(blocks.Any(box => box.Inflate(ChatRowLayout.NewlineCue + 0.5).Contains(rect)), "No highlight is painted in a gap between blocks.");
            }
        }
        using var empty = ChatRowLayout.Build(new ChatRow(new ChatUserItem("e", 1, ""), 0, 1, false, null), 420, true);
        Require(empty.RangeRects(0, 10).Count == 0, "An empty row draws no highlight.");
    }

    // Markdown-heavy replies: blocks come and go with the viewport inside a row, and rows with the viewport in the list.
    private static void MarkdownBounded()
    {
        const string reply = "# Heading\n\nA paragraph with [a link](https://example.com), **bold**, *emphasis* and `code`.\n\n- first\n  - nested one\n  - nested two\n1. ordered\n\n> a quote that goes on\n\n| name | value |\n|---|---|\n| a | 1 |\n| b | 2 |\n\n---\n\n```cs\nvar x = 1;\n```\n\nClosing paragraph.";
        var chat = new FakeChat();
        for (var index = 0; index < 20_000; index++) chat.Snapshot.Add(Item(new ChatAssistantItem("m" + index, index, reply + " " + index)));
        chat.Snapshot.Add(Item(new ChatAssistantItem("big", 30_000, string.Join("\n\n", Enumerable.Range(0, 400).Select(block => $"Paragraph {block} of a very long answer with **weight**.")))));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var scroller = view.List.Scroller!;
            Require(view.Transcript.Rows.Count == 20_001, "Every reply is a row.");
            void Bound(string when)
            {
                Require(view.List.RealizedRows is > 0 and < 60, $"{when}: only viewport rows hold controls (had {view.List.RealizedRows}).");
                Require(ChatRowLayout.Live < 700 && ChatRowLayout.LiveTexts < 3000, $"{when}: only viewport blocks hold text layouts ({ChatRowLayout.Live} layouts, {ChatRowLayout.LiveTexts} texts).");
            }
            Bound("markdown on mount");
            // The last reply has 400 blocks: scrolling inside it realizes only the blocks in range.
            Require(ChatRowLayout.LiveTexts < 300, $"A 400-block reply holds only the blocks near the viewport (held {ChatRowLayout.LiveTexts}).");
            foreach (var fraction in new[] { 0.5, 0.02, 0.97 })
            {
                scroller.Offset = new Vector(0, scroller.Extent.Height * fraction);
                Frame(window); Frame(window);
                Bound("markdown scrolled to " + fraction);
            }
            window.Width = 480; Frame(window); Frame(window);
            Bound("markdown narrowed");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    // The model, effort and permission pickers are one popup family: one container, a muted header, equal rows with a check column, one click chooses and closes.
    private static void PopupFamily()
    {
        ChatMode[] modes = [new("default", "Manual", "Claude will ask for approval before making each edit"), new("acceptEdits", "Edit automatically", "Claude will edit your selected text or the whole file without asking, and a long description is cut to one line"), new("plan", "Plan", "Claude will explore the code and present a plan before editing")];
        ChatModel[] models = [new("default", "Default (recommended)"), new("opus", "Opus 5.1") { Efforts = ["low", "medium", "high"] }, new("sonnet", "Sonnet 5.5")];
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) { PermissionMode = "acceptEdits", PermissionModes = modes, Model = "opus", Models = models, Effort = "medium" } };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        var closed = 0;
        view.PopupClosed += () => closed++;
        var hosts = new List<Window>();
        try
        {
            Frame(window); Frame(window);
            var header = chat.Header;
            var built = new List<(string Name, string Title, string Method, string Pick, Control Panel)>();
            foreach (var (name, title, method, pick, make) in new (string, string, string, string, Func<Flyout, Control>)[]
            {
                ("ChatModelPicker", "Model", ChatContract.SetModel.Name, "sonnet", flyout => view.ModelPanel(header, flyout)),
                ("ChatEffortPicker", "Effort", ChatContract.SetEffort.Name, "high", flyout => view.EffortPanel(header, flyout)),
                ("ChatModePicker", "Permission mode", ChatContract.SetPermissionMode.Name, "plan", flyout => view.ModePanel(header, flyout))
            })
                built.Add((name, title, method, pick, make(new Flyout())));
            var facts = new List<(CornerRadius Radius, Thickness Padding, IBrush? Background, IBrush? Border, double Height, double CheckX, double CheckWidth)>();
            foreach (var (name, title, method, pick, panel) in built)
            {
                var host = new Window { Width = 520, Height = 520, Content = new StackPanel { Children = { panel } }, Background = Ui.Surface };
                hosts.Add(host);
                host.Show();
                Frame(host); Frame(host);
                var container = (Border)panel;
                Require(container.Name == name, name + " is the container.");
                var label = container.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == name + "Header");
                Require(label.Text == title && label.Foreground == Ui.Muted, $"{name} has the muted header '{title}'.");
                var list = container.GetVisualDescendants().OfType<ListBox>().First(box => box.Name != null && box.Name.EndsWith("List", StringComparison.Ordinal));
                var items = list.GetVisualDescendants().OfType<ListBoxItem>().Where(item => item.DataContext is not PickRow { Kind: PickRowKind.Header }).ToList();
                Require(items.Count >= 3 && items.Select(item => Math.Round(item.Bounds.Height)).Distinct().Count() == 1, $"{name} rows are all one height ({string.Join(",", items.Select(item => item.Bounds.Height))}).");
                var checks = items.Select(item => item.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "ChatPopupCheck")).ToList();
                Require(checks.Count(mark => mark.Text == "\u2713") == 1, $"{name} marks exactly the current value.");
                var shown = checks.First(mark => mark.Text == "\u2713");
                var current = items[checks.IndexOf(shown)];
                var currentLabel = current.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name != "ChatPopupCheck" && !string.IsNullOrEmpty(text.Text) && text.Text != "\u2605" && text.Text != "\u2606").Text;
                Require(currentLabel == (name == "ChatModelPicker" ? "Opus 5.1" : name == "ChatEffortPicker" ? "medium" : "Edit automatically"), $"{name} marks the right row ('{currentLabel}').");
                Require(checks.All(mark => Math.Abs(mark.TranslatePoint(new Point(0, 0), container)!.Value.X - shown.TranslatePoint(new Point(0, 0), container)!.Value.X) < 0.5 && mark.Bounds.Width == ChatPopup.CheckWidth), $"{name} keeps the check in one column.");
                facts.Add((container.CornerRadius, container.Padding, container.Background, container.BorderBrush, items[0].Bounds.Height, shown.TranslatePoint(new Point(0, 0), container)!.Value.X, shown.Bounds.Width));
                if (name == "ChatModePicker")
                {
                    var rows = list.GetVisualDescendants().OfType<Grid>().Where(grid => grid.Height == ChatPopup.RowHeight).ToList();
                    Require(!list.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "ChatPopupDetail") && rows.Count == modes.Length &&
                        rows.Zip(modes).All(pair => ToolTip.GetTip(pair.First) as string == pair.Second.Description), "Each mode row shows only its name; the description is its tooltip.");
                    Require(items.All(item => ToolTip.GetTip(item.GetVisualDescendants().OfType<Grid>().First()) is string tip && tip.Length > 10), "The full description is the tooltip.");
                }
                if (name == "ChatEffortPicker")
                    Require(items.Select(item => item.GetVisualDescendants().OfType<TextBlock>().First(text => text.Name != "ChatPopupCheck").Text).SequenceEqual(["low", "medium", "high"]), "The effort levels are lowercase labels.");
                // One click on another row chooses it and closes the popup.
                var pickLabel = name == "ChatModelPicker" ? "Sonnet 5.5" : name == "ChatEffortPicker" ? "high" : "Plan";
                var target = items.First(item => item.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == pickLabel));
                var middle = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), host)!.Value;
                var before = closed;
                host.MouseDown(middle, MouseButton.Left); host.MouseUp(middle, MouseButton.Left);
                try { E2eWorkspace.Until(() => method == ChatContract.SetModel.Name ? chat.Calls.Contains(method) : chat.Sets.Contains((method, pick))); } catch (InvalidOperationException) { Console.WriteLine($"{name}: sets {string.Join(",", chat.Sets)} closed {closed - before} selected {list.SelectedItem}"); throw; }
                Require(closed == before + 1, $"One click on {pick} chose it and closed the {title} popup.");
            }
            Require(facts.Select(fact => fact.Radius).Distinct().Count() == 1 && facts.Select(fact => fact.Padding).Distinct().Count() == 1 && facts.Select(fact => fact.Background).Distinct().Count() == 1 && facts.Select(fact => fact.Border).Distinct().Count() == 1, "The three popups share one container: radius, padding, surface and border.");
            Require(facts.Select(fact => Math.Round(fact.Height)).Distinct().Count() == 1 && facts.Select(fact => Math.Round(fact.CheckX)).Distinct().Count() == 1 && facts.Select(fact => fact.CheckWidth).Distinct().Count() == 1, $"The three popups share the row height and the check column ({string.Join("; ", facts.Select(fact => $"h {fact.Height} x {fact.CheckX} w {fact.CheckWidth}"))}).");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { foreach (var host in hosts) host.Close(); window.Close(); }
    }

    private static bool IsWash(IBrush? brush) =>
        brush is null || ReferenceEquals(brush, Ui.Hover) || ReferenceEquals(brush, Ui.Selected) || brush is ISolidColorBrush { Color.A: 0 };

    // Every popup of the chat is one visual system in both themes: one surface, transparent lists and rows with only the two washes, equal insets, nothing past
    // the container, text that reads. Set SHARPRAIL_POPUP_SHOTS to a folder to also write each popup as a PNG.
    private static void PopupLook()
    {
        ChatMode[] modes = [new("default", "Manual", "Claude will ask for approval before making each edit"), new("acceptEdits", "Edit automatically", "Claude will edit your selected text or the whole file without asking, and a long description is cut to one line"), new("plan", "Plan", "Claude will explore the code and present a plan before editing")];
        ChatModel[] models = [new("default", "Default (recommended)"), new("opus", "Opus 5.1"), new("sonnet", "Sonnet 5.5")];
        ChatCommand[] commands = [.. Enumerable.Range(0, 12).Select(index => new ChatCommand("skill-" + index, "Does something quite useful with a description long enough that it has to be cut to one line " + index))];
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) { PermissionMode = "acceptEdits", PermissionModes = modes, Model = "opus", Models = models, Effort = "medium", Commands = commands } };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        var now = new DateTime(2026, 10, 25, 15, 0, 0, DateTimeKind.Local);
        static string Iso(DateTime local) => DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime().ToString("o");
        var sessions = new List<ChatSessionInfo>
        {
            new("a", "meow", Iso(now.AddMinutes(-5))) { Messages = 12 }, new("b", "meow", Iso(now.AddHours(-3))) { Messages = 1 },
            new("c", "A rather long conversation title that must be cut with an ellipsis inside its row", Iso(now.AddDays(-2))) { StartedIn = "chat" },
            new("d", "Last month", Iso(now.AddDays(-30))), new("e", "Older still", Iso(now.AddDays(-60)))
        };
        var shots = Environment.GetEnvironmentVariable("SHARPRAIL_POPUP_SHOTS");
        var original = Ui.Theme;
        var hosts = new List<Window>();
        try
        {
            Frame(window); Frame(window);
            foreach (var theme in new[] { "dark", "light" })
            {
                Ui.Apply(SharpRail.UI.Rendering.Themes.Resolve(theme));
                window.Background = Ui.Surface;
                if (shots is not null) Console.WriteLine($"tokens {theme}: " + string.Join(" ", new (string, IBrush)[] { ("Workspace", Ui.Workspace), ("Surface", Ui.Surface), ("Sidebar", Ui.Sidebar), ("Header", Ui.Header), ("Elevated", Ui.Elevated), ("ControlFill", Ui.ControlFill), ("Hover", Ui.Hover), ("Selected", Ui.Selected), ("Border", Ui.BorderBrush), ("ControlBorder", Ui.ControlBorder) }.Select(pair => pair.Item1 + "=" + ((ISolidColorBrush)pair.Item2).Color)));
                var flyout = new Flyout();
                var usage = ChatPopup.Frame("ChatUsagePopup", view.BuildUsagePanel());
                var popups = new (string Name, Control Panel)[]
                {
                    ("model", view.ModelPanel(chat.Header, flyout)), ("effort", view.EffortPanel(chat.Header, flyout)), ("mode", view.ModePanel(chat.Header, flyout)),
                    ("sessions", view.BuildSessions(sessions, _ => { }, now).Panel), ("skills", view.BuildSkills(flyout)), ("send", view.BuildSendOptions(flyout)), ("usage", usage), ("plus", view.BuildPlusMenu(flyout))
                };
                foreach (var (name, panel) in popups)
                {
                    var host = new Window { Width = 700, Height = 520, Content = new Border { Padding = new Thickness(20), Child = panel }, Background = Ui.Surface };
                    hosts.Add(host);
                    host.Show();
                    Frame(host); Frame(host);
                    var label = $"{name} ({theme})";
                    var frame = (Border)panel;
                    if (shots is not null) { Directory.CreateDirectory(shots); using var capture = host.CaptureRenderedFrame(); capture!.Save(Path.Combine(shots, $"{name}-{theme}.png"), PngBitmapEncoderOptions.Default); }
                    Require(frame.Padding.Left == frame.Padding.Top && frame.Padding.Top == frame.Padding.Right && frame.Padding.Right == frame.Padding.Bottom && frame.Padding.Left > 0, $"{label}: the container pads all four sides alike.");
                    Require(ReferenceEquals(frame.Background, ChatPopup.Surface) && frame.CornerRadius.TopLeft > 0 && frame.BorderThickness.Left > 0, $"{label}: one surface, border and radius.");
                    var inner = new Rect(frame.BorderThickness.Left + frame.Padding.Left, frame.BorderThickness.Top + frame.Padding.Top, frame.Bounds.Width - 2 * (frame.BorderThickness.Left + frame.Padding.Left), frame.Bounds.Height - 2 * (frame.BorderThickness.Top + frame.Padding.Top));
                    foreach (var control in frame.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible && control.Bounds.Width > 0 && control.GetVisualAncestors().OfType<ScrollViewer>().Count(scroller => frame.IsVisualAncestorOf(scroller)) == 0))
                    {
                        var at = control.TranslatePoint(new Point(0, 0), frame)!.Value;
                        var box = new Rect(at, control.Bounds.Size);
                        if (control is Avalonia.Controls.Primitives.Popup or ToolTip || control.GetType().Name.Contains("Scroll", StringComparison.Ordinal) || control.GetType().Name.Contains("Adorner", StringComparison.Ordinal)) continue;
                        Require(box.Left >= inner.Left - 0.5 && box.Right <= inner.Right + 0.5 && box.Top >= inner.Top - 0.5 && box.Bottom <= inner.Bottom + 0.5, $"{label}: {control.GetType().Name} '{control.Name}' lies inside the container's inner rect ({box} vs {inner}).");
                    }
                    foreach (var list in frame.GetVisualDescendants().OfType<ListBox>())
                    {
                        Require(IsWash(list.Background) && list.BorderThickness == default, $"{label}: the list has no fill or border of its own.");
                        Require(list.GetVisualDescendants().OfType<ScrollViewer>().All(scroller => IsWash(scroller.Background)), $"{label}: the list's scroll viewer has no fill.");
                        foreach (var item in list.GetVisualDescendants().OfType<ListBoxItem>())
                            Require(IsWash(item.Background) && item.GetVisualDescendants().OfType<ContentPresenter>().All(presenter => IsWash(presenter.Background)), $"{label}: a row is transparent or a wash.");
                        var rowsOf = list.GetVisualDescendants().OfType<ListBoxItem>().Where(item => item.DataContext is not PickRow { Kind: PickRowKind.Header } && item.DataContext is not ChatView.SessionBucketHeader).ToList();
                        if (rowsOf.Count > 0)
                        {
                            var listLeft = list.TranslatePoint(new Point(0, 0), frame)!.Value.X;
                            Require(rowsOf.All(item => Math.Abs(item.TranslatePoint(new Point(0, 0), frame)!.Value.X - listLeft) < 0.5 && item.Bounds.Right <= list.Bounds.Width + 0.5), $"{label}: rows start at the list's left edge and end inside it.");
                            var first = rowsOf[0].GetVisualDescendants().OfType<TextBlock>().First();
                            var text = first.TranslatePoint(new Point(0, 0), frame)!.Value.X;
                            foreach (var heading in frame.GetVisualDescendants().OfType<TextBlock>().Where(block => block.Name is { } n && n.EndsWith("Header", StringComparison.Ordinal)))
                                Require(Math.Abs(heading.TranslatePoint(new Point(0, 0), frame)!.Value.X - text) < 0.5, $"{label}: the header label lines up with the rows' text ({heading.TranslatePoint(new Point(0, 0), frame)!.Value.X} vs {text}).");
                            foreach (var box in frame.GetVisualDescendants().OfType<TextBox>().Where(box => box.IsEffectivelyVisible))
                                Require(Math.Abs(box.TranslatePoint(new Point(0, 0), frame)!.Value.X - listLeft) < 0.5 && box.Bounds.Right <= inner.Right + 0.5, $"{label}: the search box lines up with the list and stays inside.");
                        }
                    }
                    // Text reads on the surface it sits on (rows also selected).
                    var surface = ((ISolidColorBrush)ChatPopup.Surface).Color;
                    var worst = double.MaxValue;
                    foreach (var text in frame.GetVisualDescendants().OfType<TextBlock>().Where(block => block.Foreground is ISolidColorBrush && !string.IsNullOrWhiteSpace(block.Text) && block.Text is not ("\u00B7" or "\u2713")))
                        foreach (var back in new[] { surface, Over(((ISolidColorBrush)Ui.Selected).Color, surface), Over(((ISolidColorBrush)Ui.Hover).Color, surface) })
                        {
                            var front = Over(((ISolidColorBrush)text.Foreground!).Color, back);
                            var ratio = (Math.Max(Luminance(front), Luminance(back)) + 0.05) / (Math.Min(Luminance(front), Luminance(back)) + 0.05);
                            if (ratio < 3.0) Console.WriteLine($"low contrast {ratio:0.0}: '{text.Text}' {text.Name} fg {((ISolidColorBrush)text.Foreground!).Color} on {back} in {text.GetVisualAncestors().OfType<Control>().Take(3).Select(a => a.GetType().Name + ':' + a.Name).Aggregate((x, y) => x + '/' + y)}");
                            worst = Math.Min(worst, ratio);
                        }
                    Console.WriteLine($"popup look: {label} worst text contrast {worst:0.0}");
                    Require(worst >= 3.0, $"{label}: popup text reads on the surface and its washes (contrast {worst:0.0}).");
                }
                foreach (var host in hosts) host.Close();
                hosts.Clear();
            }
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { foreach (var host in hosts) host.Close(); Ui.Apply(original); window.Close(); }
    }

    // The composer is one calm bubble: a raised surface with the only border and radius, a borderless input in every state, plain footer controls.
    // Set SHARPRAIL_POPUP_SHOTS to a folder to also write the composer in each state as a PNG.
    private static void ComposerLook()
    {
        var shots = Environment.GetEnvironmentVariable("SHARPRAIL_POPUP_SHOTS");
        var original = Ui.Theme;
        ChatSelection? selected = new("README.md", 10, 20, true);
        foreach (var theme in new[] { "dark", "light" })
        {
            Ui.Apply(SharpRail.UI.Rendering.Themes.Resolve(theme));
            foreach (var state in new[] { "idle", "typing", "running", "chips" })
            {
                var chat = new FakeChat { Header = new ChatHeader(state == "running" ? ChatStatus.Running : ChatStatus.Idle) { Model = "sonnet", Models = [new("sonnet", "Sonnet 5.5")], Effort = "medium", PermissionMode = "default", PermissionModes = [new("default", "Manual", "Ask before each edit")], Cwd = "/work/project", Usage = new ChatUsage { Input = 1200, Output = 300, CostUsd = 0.5 } } };
                chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
                chat.Snapshot.Add(Item(new ChatAssistantItem("a", 2, "An answer above the composer.")));
                var view = new ChatView(new ChatEnvironment(chat, TestAgent) { ActiveSelection = () => state == "chips" ? selected : null }, Workspace, Chat);
                var window = new Window { Width = 760, Height = 300, Content = view, Background = Ui.Surface };
                window.Show();
                try
                {
                    Frame(window); Frame(window);
                    if (state == "typing" || state == "running") view.Input.Text = "Explain what this function does and why";
                    if (state == "chips") { view.Input.Text = "Look at this"; view.AddPasted(new byte[] { 1, 2, 3 }, "image/png"); view.RefreshSelectionChip(); }
                    Frame(window); Frame(window);
                    var label = $"composer {state} ({theme})";
                    var root = view.GetVisualDescendants().OfType<Control>().First(control => control.Name == "ChatComposer");
                    var shell = (Border)root.GetVisualDescendants().OfType<Control>().First(control => control.Name == "ChatComposerShell");
                    if (shots is not null) { Directory.CreateDirectory(shots); using var capture = window.CaptureRenderedFrame(); capture!.Save(Path.Combine(shots, $"composer-{state}-{theme}.png"), PngBitmapEncoderOptions.Default); }
                    Require(Luminance(((ISolidColorBrush)shell.Background!).Color) > Luminance(((ISolidColorBrush)Ui.Surface).Color), $"{label}: the box is a step lighter than the page.");
                    Require(shell.CornerRadius.TopLeft >= 16 && shell.BorderThickness.Left > 0 && shell.Padding.Top == shell.Padding.Bottom, $"{label}: one large rounded bubble.");
                    var ringed = root.GetVisualDescendants().OfType<Border>().Where(border => border.BorderThickness.Left > 0 && border.CornerRadius.TopLeft > 0).ToList();
                    Require(ringed.Count == 1 && ReferenceEquals(ringed[0], shell), $"{label}: exactly one element, the shell, has a border and a radius ({ringed.Count}).");
                    Require(view.Input.BorderThickness == default && IsWash(view.Input.Background) && view.Input.CornerRadius == default, $"{label}: the input has no border, fill or radius of its own.");
                    var pseudo = (IPseudoClasses)view.Input.Classes;
                    foreach (var pseudoState in new[] { "", ":pointerover", ":focus", ":disabled" })
                    {
                        if (pseudoState.Length > 0) pseudo.Set(pseudoState, true);
                        Frame(window);
                        var inner = view.Input.GetVisualDescendants().OfType<Border>().First(border => border.Name == "PART_BorderElement");
                        Require(inner.BorderThickness == default && inner.CornerRadius == default && IsWash(inner.Background) && !ReferenceEquals(inner.Background, Ui.Hover),
                            $"{label}: the input's own border element stays bare in state '{pseudoState}' (thickness {inner.BorderThickness}, radius {inner.CornerRadius}, fill {inner.Background}, brush {inner.BorderBrush}).");
                        if (pseudoState.Length > 0) pseudo.Set(pseudoState, false);
                    }
                    foreach (var button in root.GetVisualDescendants().OfType<Button>().Where(button => button.Name is "ChatPlus" or "TerminalAgentFact_model" or "TerminalAgentFact_effort" or "TerminalAgentFact_permission"))
                        Require(IsWash(button.Background) && button.BorderThickness == default, $"{label}: {button.Name} is a plain control.");
                    var model = root.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text == "Sonnet 5.5");
                    var effort = root.GetVisualDescendants().OfType<TextBlock>().First(text => text.Text is { } t && t.StartsWith("medium", StringComparison.Ordinal));
                    Require(ReferenceEquals(model.Foreground, Ui.TextBrush) && ReferenceEquals(effort.Foreground, Ui.Muted), $"{label}: the model reads in the normal tone, the effort muted.");
                }
                catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
                finally { window.Close(); }
            }
        }
        Ui.Apply(original);
    }

    // The composer shows what the next prompt carries from the editor: the selection with its lines, a preview's quote, or the open file; removable, live, cleared by sending.
    private static void SelectionChip()
    {
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        ChatSelection? selected = new("README.md", 10, 20, true);
        Action? changed = null;
        var view = new ChatView(new ChatEnvironment(chat, TestAgent) { ActiveSelection = () => selected, WatchSelection = handler => { changed = handler; return new Subscription(() => changed = null); } }, Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            Control Row() => view.GetVisualDescendants().OfType<Control>().First(control => control.Name == "ChatSelectionRow");
            string Label() => Row().GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "ChatSelectionChipLabel").Text!;
            string All() => string.Join(" ", Row().GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
            void Fire() { changed!(); Frame(window); Frame(window); }
            Require(changed is not null, "The view watches the editor's selection.");
            Fire();
            Require(Row().IsVisible && Label() == "README.md, 11 lines" && All().Contains("#L10-20", StringComparison.Ordinal), $"A line selection shows 'name, N lines' with its range ({All()}).");
            selected = new("README.md", 3, 3, true); Fire();
            Require(Label() == "README.md, 1 line" && All().Contains("#L3-3", StringComparison.Ordinal), "The chip follows the selection as it changes.");
            var quote = "Some selected text\nfrom the rendered preview that is far too long for a chip label";
            selected = new("README.md", 1, 1, false) { Quote = quote }; Fire();
            Require(Row().IsVisible && Label().StartsWith("README.md: \u201CSome selected text from the ren", StringComparison.Ordinal) && Label().Length < quote.Length && Label().Contains('\u2026'), $"A selection without source lines shows its quote, cut ({Label()}).");
            Require(ToolTip.GetTip(Row().GetVisualDescendants().OfType<Button>().First()) is string tip && tip.Contains("far too long for a chip label", StringComparison.Ordinal), "The tooltip holds the whole quote.");
            selected = new("README.md", 1, 1, false); Fire();
            Require(Row().IsVisible && Label() == "README.md" && Row().GetVisualDescendants().OfType<TextBlock>().First(text => text.Name == "ChatSelectionChipLabel").Foreground == Ui.Hint, "With no selection the open file shows alone, lighter.");

            // Removing turns the attachment off for the prompt; a new selection brings it back.
            selected = new("README.md", 4, 6, true); Fire();
            Row().GetVisualDescendants().OfType<Button>().First().RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)); Frame(window);
            Require(!Row().IsVisible && view.WithEditorContext("plain") == "plain", "Removing the chip leaves the selection out of the prompt.");
            selected = new("README.md", 7, 9, true); Fire();
            Require(Row().IsVisible && Label() == "README.md, 3 lines", "A new selection brings the chip back.");

            // The prompt carries exactly what the chip shows, and sending clears the chip.
            view.Input.Text = "explain";
            Await(view.Submit()); Frame(window);
            Require(chat.Sends.Last() == "@README.md#L7-9 explain" && !Row().IsVisible, $"The sent text matches the chip and the chip clears ({chat.Sends.Last()}).");
            selected = new("README.md", 1, 1, false) { Quote = "a quoted line" }; Fire();
            view.Input.Text = "what is this";
            Await(view.Submit()); Frame(window);
            Require(chat.Sends.Last() == "@README.md\n> a quoted line\n\nwhat is this" && !Row().IsVisible, $"A quote goes out as a block quote ({chat.Sends.Last().Replace("\n", "|")}).");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    private sealed class Subscription(Action dispose) : IDisposable { public void Dispose() => dispose(); }

    // The model picker is one flat list: Favorites and Recent only when they hold something, then the CLI's models with Default first; no headers
    // unless the host names a second provider. A chip shows the list entry's label for an id or alias the entry answers to.
    private static void Pickers()
    {
        ChatModel[] models = [new("opus", "Opus 5.1"), new("default", "Default (recommended)"), new("sonnet", "Sonnet 5.5"), new("haiku", "Haiku 5.5")];
        var none = new HashSet<string>();
        var plain = ModelPicks.Rows(models, ModelPreferences.Empty, "", null, false, none);
        Require(plain.All(row => row.Kind == PickRowKind.Model) && plain.Select(row => row.Model!.Id).SequenceEqual(["default", "opus", "sonnet", "haiku"]),
            "With no favourites or recents the picker is a flat list, the Default entry first and the CLI's order after it.");
        var prefs = ModelPreferences.Empty.ToggleFavorite("haiku").Picked("opus").ToggleHidden("sonnet");
        var ordered = ModelPicks.Rows(models, prefs, "", null, false, none);
        Require(ordered[0] is { Title: "Favorites" } && ordered[1].Model!.Id == "haiku" && ordered[2] is { Title: "Recent" } && ordered[3].Model!.Id == "opus" &&
            ordered.Skip(4).Select(row => row.Model?.Id).SequenceEqual(["default", "opus"]) && !ordered.Any(row => row.Model?.Id == "sonnet"),
            "Favorites and Recent lead when non-empty, then every other model; a hidden model stays out of the list.");
        Require(ModelPicks.Rows(models, prefs, "", null, true, none).Any(row => row.Model?.Id == "sonnet"), "Hidden models come back when asked for.");
        var found = ModelPicks.Rows(models, ModelPreferences.Empty, "son", null, false, none).Where(row => row.Kind == PickRowKind.Model).Select(row => row.Model!.Id).ToList();
        Require(found.Contains("sonnet") && !found.Contains("opus") && ModelPicks.Score("Sonnet 5.5", "s55") > 0 && ModelPicks.Score("Sonnet 5.5", "zz") == 0, "The filter is fuzzy: it ranks word starts and drops non-matches.");
        ChatModel[] two = [new("a", "Alpha") { Provider = "One" }, new("b", "Beta") { Provider = "Two" }];
        Require(ModelPicks.Rows(two, ModelPreferences.Empty, "", "a", false, none).Count(row => row.Kind == PickRowKind.Header) == 2 &&
            !ModelPicks.Rows([new("a", "Alpha") { Provider = "One" }, new("b", "Beta") { Provider = "One" }], ModelPreferences.Empty, "", "a", false, none).Any(row => row.Kind == PickRowKind.Header),
            "Only a second provider adds headers.");
        ChatModel[] catalog = [new("default", "Default (recommended)"), new("sonnet-5-5", "Sonnet 5.5"), new("haiku-5-5", "Haiku 5.5")];
        Require(ModelPicks.Label("haiku-5-5", catalog) == "Haiku 5.5" && ModelPicks.Label("claude-sonnet-5-5", catalog) == "Sonnet 5.5" && ModelPicks.Label("claude-haiku-5-5-20260101", catalog) == "Haiku 5.5" &&
            ModelPicks.Label("default", catalog) == "Default (recommended)" && ModelPicks.Label("mystery-9", catalog) == "mystery-9", "A chip names a model by its list entry, leniently, and by the raw id when none matches.");
        Require(ModelPreferences.Parse(prefs.Serialize()) is { } round && round.Favorites.SequenceEqual(prefs.Favorites) && round.Hidden.SequenceEqual(prefs.Hidden) && ModelPreferences.Parse("not json") == ModelPreferences.Empty,
            "Preferences round-trip through the stored text and survive garbage.");
        // Flyout lists are as tall as their text, and a selected row reads: its text has contrast against its fill.
        var plainList = ChatView.Tight(new ListBox { ItemsSource = new[] { "one", "two", "three" }, SelectedIndex = 1, Width = 200 });
        var tightWindow = new Window { Width = 300, Height = 300, Content = plainList, Background = Ui.Surface };
        tightWindow.Show();
        try
        {
            Frame(tightWindow); Frame(tightWindow);
            var rowsShown = plainList.GetVisualDescendants().OfType<ListBoxItem>().ToList();
            Require(rowsShown.Count == 3 && rowsShown.All(item => item.Bounds.Height <= 26), $"List rows are compact (heights {string.Join(", ", rowsShown.Select(item => item.Bounds.Height))}).");
            var chosen = rowsShown[1].GetVisualDescendants().OfType<ContentPresenter>().First();
            static double Luminance(Color color) { static double Channel(byte value) { var c = value / 255.0; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); } return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B); }
            var fill = ((ISolidColorBrush)Ui.Selected).Color;
            var ink = chosen.Foreground is ISolidColorBrush foreground ? foreground.Color : Colors.Black;
            var (light, dark) = (Math.Max(Luminance(fill), Luminance(ink)), Math.Min(Luminance(fill), Luminance(ink)));
            Require(chosen.Background is ISolidColorBrush shown && shown.Color == fill && (light + 0.05) / (dark + 0.05) >= 4.5, "A selected row is drawn with the selected fill and text that contrasts with it.");
        }
        finally { tightWindow.Close(); }

        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.Idle) { Model = "claude-opus-4-5", Models = models } };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 500, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            Require(view.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "TerminalAgentFact_model") && !view.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "TerminalAgentFact_effort"),
                "The footer offers one model selector with the effort beside it, and no separate effort chip.");
            // The host's provider groups the picker, a model's own efforts are what the thinking selector offers, and a change under way shows on its chip.
            ChatModel[] hosted = [new("a", "Alpha") { Efforts = ["low", "high"] }];
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Pending = "effort low" }));
            Frame(window);
            Require(ChipText(view, "TerminalAgentFact_model").Contains("\u2026", StringComparison.Ordinal) && !ChipText(view, "TerminalAgentFact_model").Contains('\u2192'), "A change the host reports under way shows an ellipsis on its chip, not an arrow.");
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Pending = null, Effort = "high" }));
            Frame(window);
            Require(!ChipText(view, "TerminalAgentFact_model").Contains('\u2026'), "The pending marker clears once the host does.");
        }
        finally { window.Close(); }
    }

    // The chevron and the status dot share one vertical centre, the first text line's, for every tool state and for the thinking and activity rows.
    private static void GlyphAlignment()
    {
        var input = JsonSerializer.SerializeToElement(new { command = "ls" });
        var rows = new List<(string Name, ChatItem Item, bool Open, bool Dot)>();
        foreach (var state in new[] { ChatToolState.Done, ChatToolState.Running, ChatToolState.Failed })
            foreach (var open in new[] { false, true })
                rows.Add(($"Bash {state} open={open}", new ChatToolItem("g" + state, 1, "Bash", input, state), open, true));
        rows.Add(("Thought", new ChatThinkingItem("gt", 1, "hmm"), false, false));
        rows.Add(("Thought open", new ChatThinkingItem("gt", 1, "hmm"), true, false));
        foreach (var (name, item, open, dot) in rows)
        {
            using var layout = ChatRowLayout.Build(new ChatRow(item, 0, 1, open, null), 700, true);
            var (chevron, dotY, text) = layout.GlyphCentres();
            Require(chevron is { } c && text is { } t && Math.Abs(c - t) <= 1 && (!dot || dotY is { } d && Math.Abs(d - c) < 0.01),
                $"{name}: chevron {chevron}, dot {dotY} and first line {text} share one vertical centre.");
        }
    }

    private static double Height(ChatItem item, bool expanded, out IReadOnlyList<ChatHit> hits)
    {
        using var layout = ChatRowLayout.Build(new ChatRow(item, 0, 1, expanded, null), 700, true);
        hits = [.. layout.OwnHits];
        return layout.Height;
    }

    // Each kind of item is drawn as the extension draws it: folded rows open, meta lines are plain, empty rows take no room, a long prompt is clipped.
    private static void ItemKinds()
    {
        var peer = new ChatNoticeItem("peer", 1, ChatNoticeLevel.Info, "ignored") { Category = ChatNoticeKinds.Peer, Label = "Message from @ana", Detail = "hello there\nsecond line\nthird line" };
        Require(Height(peer, false, out var peerHits) < Height(peer, true, out _) && peerHits.Any(hit => hit.Kind == ChatHitKind.Toggle), "A peer message is a folded row that opens to its body.");
        var task = new ChatNoticeItem("tn", 2, ChatNoticeLevel.Info, "Background command done") { Category = ChatNoticeKinds.TaskNotification };
        Height(task, false, out var taskHits);
        Require(taskHits.Count == 0, "A task notification without details is a plain line, nothing to open.");
        var compaction = new ChatNoticeItem("c", 3, ChatNoticeLevel.Info, "Compacted chat \u00B7 auto \u00B7 120k tokens freed") { Category = ChatNoticeKinds.Compaction, Detail = "summary\nof\nthe chat" };
        Require(Height(compaction, false, out _) < Height(compaction, true, out _), "A compaction is a folded row that opens to its summary.");
        Require(Height(new ChatAssistantItem("e", 4, "   "), false, out _) == 0 && Height(new ChatUserItem("e2", 5, ""), false, out _) == 0, "Empty rows draw nothing.");
        var longPrompt = new ChatUserItem("long", 6, string.Join("\n", Enumerable.Repeat("a line of a long prompt", 12)));
        Require(Height(longPrompt, false, out var clipHits) < Height(longPrompt, true, out _) && clipHits.Any(hit => hit.Kind == ChatHitKind.Toggle), "A long prompt is clipped, with Show more.");
        var fromPlugin = new ChatUserItem("pl", 7, "automated") { Origin = new ChatOrigin("plugin") { Name = "ci" }, Uuid = "u" };
        Height(fromPlugin, false, out var pluginHits);
        Require(!pluginHits.Any(hit => hit.Kind is ChatHitKind.Fork or ChatHitKind.Rewind), "A prompt that no human typed is a folded row without fork or rewind.");
        // Actions never overlap text: a reply has none (Copy is in the right-click menu), a prompt's Fork and Rewind sit below its bubble, for short, long and wrapped messages.
        foreach (var text in new[] { "ok", string.Join(" ", Enumerable.Repeat("a long wrapped sentence that goes on", 30)), "line one\nline two\nline three\nline four\nline five" })
        {
            using var reply = ChatRowLayout.Build(new ChatRow(new ChatAssistantItem("r", 1, text), 0, 1, false, null), 500, true);
            Require(reply.OwnHits.Count == 0 && reply.Height - reply.BlocksBottom < 24, $"A reply has no hover action and no strip below it (hits {reply.OwnHits.Count}, height {reply.Height}, blocks end {reply.BlocksBottom}).");
            Require(ChatList.CopyText(new ChatAssistantItem("r", 1, text)) == text && ChatList.CopyText(new ChatUserItem("p", 2, text)) == text && ChatList.CopyText(new ChatAssistantItem("e", 3, "")) is null, "Right-click Copy takes a reply's or a prompt's text.");
            using var prompt = ChatRowLayout.Build(new ChatRow(new ChatUserItem("p", 2, text) { Uuid = "u" }, 0, 1, false, null), 500, true);
            var actions = prompt.OwnHits.Where(hit => hit.Kind is ChatHitKind.Fork or ChatHitKind.Rewind).Select(hit => hit.Bounds).ToList();
            Require(actions.Count == 2 && actions.All(action => !prompt.TextBounds().Any(bounds => bounds.Intersects(action))) && actions.All(action => action.Bottom <= prompt.Height), $"Fork and Rewind clear the prompt's text ({text.Length} chars).");
        }
        var held = new ChatUserItem("held", 8, "later") { Queued = true, Uuid = "u2" };
        Height(held, false, out var heldHits);
        Require(!heldHits.Any(hit => hit.Kind is ChatHitKind.Fork or ChatHitKind.Rewind), "A held prompt has no actions.");
    }

    private static void PressKey(Control target, Key key) => target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key });

    // A request waits above the composer, first in first out: the transcript dims, numbers and Esc answer, Left and Right choose where "don't ask again" is saved.
    private static void Requests()
    {
        var chat = new FakeChat { Header = new ChatHeader(ChatStatus.AwaitingPermission) };
        ChatDestination[] places = [new("localSettings", "this project (just you)", "Saves to .claude/settings.local.json"), new("userSettings", "all projects", "Saves to ~/.claude/settings.json")];
        ChatPermissionItem Ask(string id, long seq) => new(id, seq, "req-" + id, "Bash", JsonSerializer.SerializeToElement(new { command = "ls" }))
        { Prompt = "Allow this bash command?", AlwaysLabel = "Yes, allow Bash(ls)", AlwaysScoped = true, DefaultDestination = "localSettings", Destinations = places };
        chat.Snapshot.Add(Item(Ask("one", 1)));
        chat.Snapshot.Add(Item(Ask("two", 2)));
        var preference = new MemoryPreference();
        var view = new ChatView(new ChatEnvironment(chat, TestAgent) { ComposerPreference = preference }, Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            Require(view.RequestPanel.IsVisible && view.RequestPrompt == "Allow this bash command?" && view.List.Opacity < 1 && view.Placeholder() == "Waiting for permission\u2026" && view.Input.PlaceholderText == "Waiting for permission\u2026",
                "The first request shows above the composer, the transcript dims and the composer says it is waiting.");
            var panel = view.RequestPanel.GetVisualDescendants().OfType<StackPanel>().First(item => item.Name == "ChatPermission");
            var always = () => view.RequestPanel.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ChatPermAlways");
            Require(string.Join(" ", always().GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text)).Contains("for this project (just you)", StringComparison.Ordinal), "The second answer names where it is saved.");
            PressKey(panel, Key.Right); Frame(window);
            Require(string.Join(" ", always().GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text)).Contains("for all projects", StringComparison.Ordinal) && preference.Get()!.Contains("userSettings", StringComparison.Ordinal), "Right changes the destination and it is remembered.");
            PressKey(view.RequestPanel.GetVisualDescendants().OfType<StackPanel>().First(item => item.Name == "ChatPermission"), Key.D2);
            E2eWorkspace.Until(() => chat.Answers.Count == 1);
            Require(chat.Answers[0] is { Allow: true, Remember: true, Destination: "userSettings" }, "Key 2 allows and remembers the rule for the chosen destination.");
            E2eWorkspace.Until(() => view.RequestPanel.IsVisible && view.Transcript.FirstPending()?.Id == "two");
            var reason = view.RequestPanel.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "ChatPermReason");
            Require(reason.PlaceholderText == "Tell Claude what to do instead", "Rejecting takes a reason.");
            reason.Text = "use rg instead";
            Press(view, "ChatPermNo");
            E2eWorkspace.Until(() => chat.Answers.Count == 2);
            Require(chat.Answers[1] is { Allow: false, Reason: "use rg instead" }, "No carries the reason typed beside it.");
            E2eWorkspace.Until(() => !view.RequestPanel.IsVisible && view.List.Opacity == 1);
            chat.Push(Item(Ask("three", 3)));
            Frame(window);
            PressKey(view.RequestPanel.GetVisualDescendants().OfType<StackPanel>().First(item => item.Name == "ChatPermission"), Key.Escape);
            E2eWorkspace.Until(() => chat.Answers.Count == 3);
            Require(chat.Answers[2] is { Allow: false, Reason: null }, "Esc rejects with the plain refusal.");
        }
        finally { window.Close(); }
    }

    private static ChatToolItem Read(string id, long seq, ChatToolState state = ChatToolState.Done) =>
        new(id, seq, "Read", JsonSerializer.SerializeToElement(new { file_path = id + ".cs" }), state) { Summary = id + ".cs" };

    // Consecutive routine steps fold into one activity row that stays one row as it grows; thousands of them stay bounded.
    private static void Grouping()
    {
        var chat = new FakeChat();
        chat.Snapshot.Add(Item(new ChatUserItem("u", 1, "look around")));
        chat.Snapshot.Add(Item(Read("r1", 2)));
        chat.Snapshot.Add(Item(Read("r2", 3)));
        chat.Snapshot.Add(Item(new ChatThinkingItem("t", 4, "hmm")));
        chat.Snapshot.Add(Item(new ChatToolItem("g", 5, "Grep", JsonSerializer.SerializeToElement(new { pattern = "x" }), ChatToolState.Running) { Summary = "x" }));
        chat.Snapshot.Add(Item(new ChatAssistantItem("a", 6, "Found it")));
        var view = new ChatView(new ChatEnvironment(chat, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 600, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var transcript = view.Transcript;
            Require(transcript.Rows.Select(row => row.Id).SequenceEqual(["u", "act:r1", "a"]), "Routine steps fold into one activity row between the messages.");
            var group = (ChatActivityItem)transcript.Row("act:r1")!.Item;
            Require(group.Steps.Count == 4 && ChatRowLayout.Breadcrumb(group.Steps) == "Read 2 files \u00B7 Searched 1 time \u00B7 Thought", "The breadcrumb says what the steps amounted to.");
            var revision = transcript.Row("act:r1")!.Revision;
            chat.Push(Item(Read("r1", 2, ChatToolState.Failed)));
            Frame(window);
            Require(transcript.Rows.Count == 3 && transcript.Row("act:r1")!.Revision > revision && ((ChatActivityItem)transcript.Row("act:r1")!.Item).Steps.Count == 4,
                "An update to a step replaces the group's row in place.");
            chat.Push(Item(Read("r3", 7)));
            chat.Push(Item(Read("r4", 8)));
            Frame(window);
            Require(transcript.Rows.Select(row => row.Id).SequenceEqual(["u", "act:r1", "a", "act:r3"]) && transcript.RowFor("r4") is { Id: "act:r3" }, "After a message a new run of steps starts a new group, and a step is found through its group.");
            transcript.Toggle("act:r1");
            Require(transcript.Row("act:r1")!.Expanded, "A group opens to its steps.");
        }
        finally { window.Close(); }

        var many = new FakeChat();
        for (var index = 0; index < 18_000; index++)
            many.Snapshot.Add(Item(index % 6 == 0 ? new ChatUserItem("u" + index, index, "ask " + index) : Read("r" + index, index)));
        var big = new ChatView(new ChatEnvironment(many, TestAgent), Workspace, Chat);
        var host = new Window { Width = 900, Height = 700, Content = big, Background = Ui.Surface };
        host.Show();
        try
        {
            Frame(host); Frame(host);
            Require(big.Transcript.Rows.Count == 6_000, $"Eighteen thousand items fold into six thousand rows (had {big.Transcript.Rows.Count}).");
            var scroller = big.List.Scroller!;
            foreach (var fraction in new[] { 0.5, 0.1 })
            {
                scroller.Offset = new Vector(0, scroller.Extent.Height * fraction);
                Frame(host); Frame(host);
                Require(big.List.RealizedRows is > 0 and < 60 && ChatRowLayout.Live < 120, $"Grouped rows stay bounded at {fraction} ({big.List.RealizedRows} rows, {ChatRowLayout.Live} layouts).");
            }
        }
        finally { host.Close(); }
    }

    // Before Claude Code reports, the chips read one muted word each; a host without the chat handler says so plainly.
    private static void Placeholders()
    {
        var starting = new FakeChat { Header = new ChatHeader(ChatStatus.Starting) };
        starting.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, starting.Header));
        var view = new ChatView(new ChatEnvironment(starting, TestAgent), Workspace, Chat);
        var window = new Window { Width = 900, Height = 500, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            Require(ChipText(view, "TerminalAgentFact_model").Trim() == "model effort" &&
                ChipText(view, "TerminalAgentFact_permission").Trim() == "permissions", "Before the header arrives each chip holds a single placeholder word.");
            starting.Push(new ChatHeaderPush(Workspace, Chat, new ChatHeader(ChatStatus.Idle)));
            Frame(window); Frame(window);
            Require(ChipText(view, "TerminalAgentFact_model").Trim() == "model",
                "An idle chat with no reported model or effort says the CLI's default applies.");
        }
        finally { window.Close(); }

        var missing = new FakeChat { OpenFailure = new PluginCallException(PluginCallError.Unknown, "Plugin claude-code registered no handler for plugin.claude-code.chatOpen") };
        var broken = new ChatView(new ChatEnvironment(missing, TestAgent), Workspace, Chat);
        var host = new Window { Width = 900, Height = 500, Content = broken, Background = Ui.Surface };
        host.Show();
        try
        {
            Frame(host); Frame(host);
            string Banner() => string.Join(" ", broken.GetVisualDescendants().OfType<Control>().First(control => control.Name == "ChatBanner").GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text));
            E2eWorkspace.Until(() => Banner().Contains("chat mode", StringComparison.Ordinal));
            Require(Banner().Contains("does not provide Claude Code chat mode", StringComparison.Ordinal) && !Banner().Contains("registered no handler", StringComparison.Ordinal),
                "A host without the chat handler gets a plain message, not the wire error.");
        }
        finally { host.Close(); }
    }

    private static void Behaviour()
    {
        var chat = new FakeChat
        {
            Header = new ChatHeader(ChatStatus.Idle)
            {
                Model = "opus",
                Effort = "high",
                PermissionMode = "default",
                Cwd = "/work/project",
                Models = [new("opus", "Opus"), new("sonnet", "Sonnet")],
                Commands = [new("compact", "Compact the conversation") { ArgumentHint = "[focus]" }, new("review", "Review a PR"), new("security-review", "Security review")]
            }
        };
        chat.Snapshot.Add(new ChatHeaderPush(Workspace, Chat, chat.Header));
        chat.Snapshot.Add(Item(new ChatUserItem("u1", 1, "Hello") { Uuid = "m1" }));
        chat.Snapshot.Add(Item(new ChatToolItem("task", 2, "Task", JsonSerializer.SerializeToElement(new { description = "Explore" }), ChatToolState.Running) { Summary = "Explore" }));
        chat.Snapshot.Add(Item(new ChatToolItem("c1", 3, "Read", JsonSerializer.SerializeToElement(new { file_path = "a.cs" }), ChatToolState.Done) { Summary = "a.cs", Output = "x", ParentId = "task" }));
        chat.Snapshot.Add(Item(new ChatToolItem("c2", 4, "Grep", JsonSerializer.SerializeToElement(new { pattern = "x" }), ChatToolState.Running) { Summary = "x", ParentId = "task" }));
        chat.Snapshot.Add(Item(new ChatPermissionItem("perm", 5, "req-1", "Bash", JsonSerializer.SerializeToElement(new { command = "rm -rf build" })) { Suggestions = JsonSerializer.SerializeToElement(new[] { "Bash(rm:*)" }) }));
        var diffs = new List<ChatDiffRequest>();
        var composerPreference = new MemoryPreference();
        var opened = new List<(string Path, int Line)>();
        ChatSelection? selected = new("src/Alpha.cs", 3, 5, true);
        var view = new ChatView(new ChatEnvironment(chat, TestAgent)
        {
            OpenDiff = request => { diffs.Add(request); return Task.CompletedTask; },
            ActiveSelection = () => selected,
            ComposerPreference = composerPreference,
            OpenFileAt = (path, line) => { opened.Add((path, line)); return Task.CompletedTask; }
        }, Workspace, Chat);
        var window = new Window { Width = 900, Height = 700, Content = view, Background = Ui.Surface };
        window.Show();
        try
        {
            Frame(window); Frame(window);
            var transcript = view.Transcript;
            Require(chat.Calls.Contains(ChatContract.Open.Name), "Mounting opens the chat, which is also how a restored tab revives its session.");
            Require(transcript.Rows.Select(row => row.Id).SequenceEqual(["u1", "task", "perm"]) && transcript.Item("c1")!.ParentId == "task",
                "Subagent traffic is kept but not drawn in the main list: the Agent card is its only trace there.");
            chat.Push(Item(new ChatToolItem("c3", 6, "Edit", JsonSerializer.SerializeToElement(new { file_path = "b.cs" }), ChatToolState.Running) { ParentId = "task" }));
            Frame(window);
            Require(transcript.Rows.Select(row => row.Id).SequenceEqual(["u1", "task", "perm"]) && transcript.Children("task").Count == 3, "A new child joins the thread, not the list.");
            chat.Push(Item(new ChatResultItem("res", 70, false) { CostUsd = 0.5 }));
            Frame(window);
            Require(transcript.Item("res") is null && transcript.Rows.Count == 3, "A result is never a row.");

            // A permission round trip: the answer comes back as the pushed item, which folds the card to its decision.
            _ = view.Answer((ChatPermissionItem)transcript.Item("perm")!, ChatHitKind.Always);
            E2eWorkspace.Until(() => transcript.Item("perm") is ChatPermissionItem { Decision: ChatPermissionDecision.Allowed });
            Require(chat.Calls.Contains(ChatContract.Answer.Name), "Answering calls the host.");

            // Diffs open in SharpRail's own view from the tool input, with paths under the chat's directory made workspace-relative.
            view.Activate(transcript.Row("perm")!, ChatHitKind.OpenDiff);
            chat.Push(Item(new ChatToolItem("edit", 7, "Edit", JsonSerializer.SerializeToElement(new { file_path = "/work/project/src/B.cs", old_string = "a", new_string = "b" }), ChatToolState.Done) { FilePath = "/work/project/src/B.cs" }));
            Frame(window);
            view.Activate(transcript.Row("edit")!, ChatHitKind.OpenDiff);
            Require(diffs.Select(request => request.Path).SequenceEqual(["", "src/B.cs"]) && diffs[1].Propose is null,
                "A finished edit card opens SharpRail's diff of the file's changes, relative to the workspace.");
            chat.Push(Item(new ChatPermissionItem("perm2", 8, "req-2", "Edit", JsonSerializer.SerializeToElement(new { file_path = "/work/project/src/C.cs", old_string = "a", new_string = "b" }))));
            Frame(window);
            view.Activate(transcript.Row("perm2")!, ChatHitKind.OpenDiff);
            Require(diffs[2].Path == "src/C.cs" && diffs[2].Propose?.Invoke("x a y") == "x b y",
                "A pending edit's card opens a diff of the file against the proposed text, which works for an unmodified file.");
            var write = ChatView.Proposal("Write", JsonSerializer.SerializeToElement(new { file_path = "w.cs", content = "new" }));
            var multi = ChatView.Proposal("MultiEdit", JsonSerializer.SerializeToElement(new { file_path = "m.cs", edits = new[] { new { old_string = "a", new_string = "b" }, new { old_string = "b", new_string = "c" } } }));
            Require(write?.Invoke("old") == "new" && multi?.Invoke("a") == "c", "Write and MultiEdit propose the whole file from their input.");

            // The pending edit is answered; the next request is a Bash command whose text can be changed in the panel before it is allowed.
            Await(view.Answer((ChatPermissionItem)transcript.Item("perm2")!, ChatHitKind.Deny));
            E2eWorkspace.Until(() => transcript.Item("perm2") is ChatPermissionItem { Decision: ChatPermissionDecision.Denied });
            chat.Push(Item(Sample2()));
            Frame(window);
            var bashBox = view.RequestPanel.GetVisualDescendants().OfType<TextBox>().First(box => box.Name == "ChatPermCommand");
            Require(view.RequestPanel.IsVisible && bashBox.Text == "ls" && view.RequestPrompt == "Do you want to proceed with Bash?", "A Bash request shows its command in an editable box above the composer.");
            bashBox.Text = "ls -la";
            Press(view, "ChatPermYes");
            E2eWorkspace.Until(() => chat.Answers.Count > 0 && chat.Answers.Last() is { Allow: true, UpdatedInput: { } updated } && updated.GetProperty("command").GetString() == "ls -la");
            E2eWorkspace.Until(() => transcript.Item("perm3") is ChatPermissionItem { Decision: ChatPermissionDecision.Allowed });

            // The IDE-context chip is an explicit toggle: the active file and selection lead the next prompt once.
            Require(view.WithEditorContext("fix this") == "@src/Alpha.cs#L3-5 fix this" && view.WithEditorContext("again") == "again",
                "With the toggle on the prompt carries the selection as an @path#Lstart-end mention, once.");
            selected = new("src/Alpha.cs", 1, 1, false);
            Require(view.WithEditorContext("next") == "@src/Alpha.cs next", "Without a selection the open file is mentioned.");

            // A proposed plan is a panel of its own: the plan is read there and approved with the mode to continue in, or sent back with feedback.
            chat.Push(Item(new ChatPermissionItem("plan", 20, "req-plan", "ExitPlanMode", JsonSerializer.SerializeToElement(new { plan = "## Plan\n\n- first step\n- second step" }))));
            Frame(window);
            Require(view.RequestPrompt == "Accept this plan?" && view.RequestPanel.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("first step", StringComparison.Ordinal) == true) &&
                view.RequestPanel.GetVisualDescendants().OfType<Button>().Select(button => button.Name).Contains("ChatPermManual"), "A plan request shows the plan and its three answers.");
            Press(view, "ChatPermManual");
            E2eWorkspace.Until(() => chat.Answers.Last() is { Allow: true, PlanMode: "default" });
            E2eWorkspace.Until(() => transcript.Item("plan") is ChatPermissionItem { Decision: ChatPermissionDecision.Allowed });

            // Selecting text of a plan and commenting sends the quote with the comment.
            chat.Push(Item(new ChatPermissionItem("plan2", 22, "req-plan2", "ExitPlanMode", JsonSerializer.SerializeToElement(new { plan = "## Plan\n\n- first step\n- second step" }))));
            Frame(window);
            var planBody = view.RequestPanel.GetVisualDescendants().OfType<SelectableTextBlock>().Single(block => block.Name == "ChatPlanBody");
            planBody.SelectionStart = planBody.Text!.IndexOf("second", StringComparison.Ordinal);
            planBody.SelectionEnd = planBody.SelectionStart + "second step".Length;
            view.RequestPanel.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "ChatPermReason").Text = "Split this step";
            Press(view, "ChatPlanComment");
            Press(view, "ChatPermNo");
            E2eWorkspace.Until(() => chat.Answers.Last() is { Allow: false, PlanComments: [{ Quote: "second step", Comment: "Split this step" }] });

            // A subagent's thread opens in a dialog and follows the live transcript.
            view.ShowThread("task");
            Frame(window);
            Require(view.ThreadTranscript is { } thread && thread.Rows.Select(row => row.Id).SequenceEqual(["act:c1", "c3"]) && window.OwnedWindows.Any(owned => Equals(owned.Tag, "ChatSubagentThread")),
                "The subagent dialog lists the thread's own items at the top level, its reads and searches grouped.");
            chat.Push(Item(new ChatToolItem("c4", 21, "Glob", JsonSerializer.SerializeToElement(new { pattern = "*" }), ChatToolState.Running) { ParentId = "task" }));
            Frame(window);
            Require(view.ThreadTranscript!.Rows.Count == 3, "The dialog follows the thread while it is open.");
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();

            // A clipped output opens in full in a dialog; one the wire cut is fetched first.
            chat.Push(Item(new ChatToolItem("long", 23, "Bash", JsonSerializer.SerializeToElement(new { command = "big" }), ChatToolState.Done) { Output = new string('o', 400), OutputTruncated = true }));
            Frame(window);
            view.Activate(transcript.Row("long")!, ChatHitKind.OpenText, "out");
            E2eWorkspace.Until(() => window.OwnedWindows.Any(owned => Equals(owned.Tag, "ChatToolText")));
            Require(window.OwnedWindows.First(owned => Equals(owned.Tag, "ChatToolText")).GetVisualDescendants().OfType<TextBox>().Any(box => box.Name == "ChatToolFullText" && box.Text == "full output"), "The full output opens in a dialog after it is fetched.");
            foreach (var owned in window.OwnedWindows.ToArray()) owned.Close();

            // The model chip changes only once the header confirms it.
            chat.ModelGate = new TaskCompletionSource<ChatHeader>();
            _ = view.SetModel("sonnet");
            E2eWorkspace.Until(() => ChipText(view, "TerminalAgentFact_model").Contains('…'));
            Require(!ChipText(view, "ChatChips").Contains('\u2192'), "A change under way shows no arrow on any chip: " + ChipText(view, "ChatChips"));
            Require(ChipText(view, "TerminalAgentFact_model").Contains("Opus") && !ChipText(view, "TerminalAgentFact_model").StartsWith("Sonnet", StringComparison.Ordinal),
                "A pick is shown as pending while the old model stays.");
            chat.ModelGate.SetResult(chat.Header with { Model = "sonnet" });
            E2eWorkspace.Until(() => ChipText(view, "TerminalAgentFact_model").StartsWith("Sonnet", StringComparison.Ordinal));

            // The model, effort and mode sit in the footer under the box; the folder, IDE switch and attach are chips beside them.
            var chipsTop = view.GetVisualDescendants().OfType<Control>().First(control => control.Name == "ChatChips").TranslatePoint(new Point(0, 0), view)!.Value.Y;
            var listBottom = view.List.TranslatePoint(new Point(0, view.List.Bounds.Height), view)!.Value.Y;
            var shell = view.GetVisualDescendants().OfType<Control>().First(control => control.Name == "ChatComposerShell");
            var shellBottom = shell.TranslatePoint(new Point(0, shell.Bounds.Height), view)!.Value.Y;
            Require(chipsTop >= listBottom && shell.Bounds.Height > 0 && chipsTop >= shellBottom - 0.5 && !view.GetVisualDescendants().OfType<Control>().Any(control => control.Name == "ChatFacts"),
                "The pickers sit in a footer row below the box and the list; there is no separate chip line.");
            var plusMenu = view.BuildPlusMenu(new Flyout()).GetVisualDescendants().OfType<Control>().Select(control => control.Name).ToHashSet();
            Require(plusMenu.IsSupersetOf(["ChatSkills", "ChatCommandMenu"]) && !plusMenu.Contains("TerminalAttachFile") && ChipText(view, "ChatChips").Contains("IDE context") && view.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "TerminalAttachFile") && view.GetVisualDescendants().OfType<Control>().Any(control => control.Name == "ChatPlus") &&
                view.GetVisualDescendants().OfType<Control>().Any(control => control.Name == "ChatHistory"),
                "The \"+\" popup carries Skills, the command menu and the MCP servers; attach and the IDE switch are footer chips; history stays by Send.");

            // The model button's popup lists a flagship per family, then Effort and More models.
            var menuModels = new ChatModel[] { new("o1", "Opus 5.5") { Family = "opus" }, new("o0", "Opus 4.8") { Family = "opus" }, new("s1", "Sonnet 5.5") { Family = "sonnet" } };
            var menuList = view.ModelMenu(chat.Header with { Models = menuModels }, new Flyout()).GetVisualDescendants().OfType<ListBox>().Single(list => list.Name == "ChatModelMenuList");
            Require(menuList.ItemsSource is System.Collections.ICollection { Count: 4 }, "The model popup lists one flagship per family plus Effort and More models.");

            // History search finds text across the transcript and scrolls to it.
            var found = ChatView.Search(transcript.AllItems().OrderBy(item => item.Seq).ToArray(), "hello", CancellationToken.None);
            Require(found.Count == 1 && found[0].Id == "u1", "History search matches the transcript's text, newest first.");

            // The history search is an overlay of its own: prompts first, messages and a preview once zoomed, Esc closes.
            ChatItem[] talk = [new ChatUserItem("p1", 1, "find me"), new ChatAssistantItem("m1", 2, "I will find you")];
            Require(ChatView.Search(talk, "find", CancellationToken.None, false).Select(hit => hit.Id).SequenceEqual(["p1"]) &&
                ChatView.Search(talk, "find", CancellationToken.None, true).Select(hit => hit.Id).SequenceEqual(["p1", "m1"]), "The compact stage lists prompts; zoomed adds messages, prompts first.");
            view.OpenHistory();
            Frame(window);
            var overlay = view.GetVisualDescendants().OfType<Border>().First(border => border.Name == "ChatHistoryOverlay");
            Require(view.HistoryOpen && overlay.IsVisible && overlay.Bounds.Width >= view.Bounds.Width - 1 && overlay.Bounds.Height >= view.Bounds.Height - 1, "History opens as a full overlay over the conversation.");
            view.HistoryQuery!.Text = "hello";
            E2eWorkspace.Until(() => view.HistoryResults!.ItemCount == 1);
            view.HistoryQuery.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
            Require(view.HistoryZoomed, "Tab zooms the history to messages and a preview.");
            view.HistoryQuery.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            Require(!view.HistoryOpen, "Escape closes the history overlay.");

            // Slash and @ menus.
            view.Input.Focus();
            view.Input.Text = "/"; view.Input.CaretIndex = 1;
            E2eWorkspace.Until(() => view.MenuOpen);
            var slash = view.Menu.Items.Cast<object>().Select(item => item.ToString()!).ToList();
            var sectionsShown = slash.Where(line => ChatView.SlashSections.Contains(line)).ToList();
            Require(sectionsShown.SequenceEqual(["Context", "Model", "Slash Commands", "Support"]) && slash.Count(line => line.StartsWith('/')) == 3 && slash.Any(line => line.StartsWith("Attach file\u2026", StringComparison.Ordinal)),
                "A slash lists every command, skill and plugin command under the extension's sections, in its order, after the built-in actions SharpRail can do: " + string.Join(" | ", slash));
            view.Input.Text = "/rev"; view.Input.CaretIndex = 4;
            E2eWorkspace.Until(() => view.Menu.ItemCount == 3);
            Require(view.Menu.SelectedIndex == 1, "The selection starts on the first command, not on a section title.");
            view.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
            E2eWorkspace.Until(() => view.Input.Text!.StartsWith("/review ", StringComparison.Ordinal));
            view.Input.Text = "see @alp"; view.Input.CaretIndex = 8;
            E2eWorkspace.Until(() => view.MenuOpen && view.Menu.ItemCount == 1);
            view.Input.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Tab });
            E2eWorkspace.Until(() => view.Input.Text == "see @src/Alpha.cs ");

            // Sending clears the draft; with the turn running the same button interrupts.
            view.Input.Text = "do it";
            Await(view.Submit());
            Require(chat.Calls.Contains(ChatContract.Send.Name) && view.Input.Text == "", "Sending clears the composer.");
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Status = ChatStatus.Running }));
            Frame(window);
            Await(view.Submit());
            Require(chat.Calls.Contains(ChatContract.Interrupt.Name), "An empty composer while running interrupts.");
            // File references in replies open the editor at their line; only paths inside the chat's directory are followed.
            Require(Markdown.FileTarget("src/A.cs:12:3") == ("src/A.cs", 12, 3) && Markdown.FileTarget("a/b.cs#L7") == ("a/b.cs", 7, 0) && Markdown.FileTarget("../x.cs") is null &&
                Markdown.FileTarget("https://x.y/a.cs") is null && Markdown.FileTarget("//h/a.cs") is null && Markdown.FileTarget("%2e%2e/x.cs") is null && Markdown.FileTarget("plain words") is null,
                "A file reference reads path, line and column and refuses anything that leaves the worktree.");
            var (_, _, inlineLinks) = Markdown.Inline("see `src/A.cs:9` and [there](b/c.cs:2) or [web](https://x.y)", Ui.TextBrush, Ui.Accent, 14);
            Require(inlineLinks.Select(link => link.Url).SequenceEqual(["file:src/A.cs:9", "file:b/c.cs:2", "https://x.y"]), "Code spans and links naming files become file chips next to web links.");
            view.OpenFileLink("src/Alpha.cs:12"); view.OpenFileLink("/work/project/src/B.cs:3"); view.OpenFileLink("/etc/passwd.txt"); view.OpenFileLink("../up.cs");
            Require(opened.SequenceEqual([("src/Alpha.cs", 12), ("src/B.cs", 3)]), "A reference opens relative to the chat's directory, and an outside path does not open.");

            // A long user message folds to a preview with Show more; a Skill call keeps its name in the header; a finished background command leaves a notice.
            var longUser = new ChatUserItem("long", 1, string.Join(" ", Enumerable.Repeat("a very long pasted paragraph of words", 40)));
            using (var folded = ChatRowLayout.Build(new ChatRow(longUser, 0, 1, false, null), 600, true))
            using (var unfolded = ChatRowLayout.Build(new ChatRow(longUser, 0, 2, true, null), 600, true))
                Require(folded.Height < unfolded.Height && folded.OwnHits.Any(hit => hit.Kind == ChatHitKind.Toggle), "A message over 500 characters folds to a preview with Show more.");
            using (var skill = ChatRowLayout.Build(new ChatRow(new ChatToolItem("sk", 1, "Skill", JsonSerializer.SerializeToElement(new { skill = "review", args = "the diff" }), ChatToolState.Done), 0, 1, true, null), 600, true))
                Require(skill.TextCount >= 3, "A Skill card shows its name and the request after it.");
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Tasks = [new ChatTask("bg1", ChatTaskKind.Shell, "npm test", ChatTaskStatus.Running)] }));
            Frame(window);
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Tasks = [new ChatTask("bg1", ChatTaskKind.Shell, "npm test", ChatTaskStatus.Done)] }));
            Frame(window);
            Require(transcript.Item("bg:bg1") is ChatNoticeItem { Text: var finished } && finished.Contains("finished: npm test", StringComparison.Ordinal), "A background command that finishes leaves a notice.");
            // A shell task's description is its whole command: the chip stays one trimmed line however long and multi-line it is.
            var heredoc = "cd /work && python3 - <<'EOF'\n" + string.Join("\n", Enumerable.Repeat("s=s.replace('a very long line of a script that goes on and on', 'b')", 40)) + "\nEOF";
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Tasks = [new ChatTask("bg2", ChatTaskKind.Shell, heredoc, ChatTaskStatus.Running)] }));
            Frame(window);
            var taskChip = view.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ChatTask_bg2");
            Require(taskChip.Bounds.Height < 40 && taskChip.Bounds.Width < 480, $"A long multi-line background command must keep a one-line chip, got {taskChip.Bounds.Width:0}x{taskChip.Bounds.Height:0}.");

            // Composer extras: Enter sends by default and Ctrl or Cmd+Enter when that is switched off; recent prompts are remembered; the send menu
            // appears with a running turn, "stop and send" interrupts first; an image attachment shows a preview chip; a narrow footer is compact.
            Require(view.SendsOnKeys(false, false) && !view.SendsOnKeys(false, true), "Enter sends, Shift+Enter breaks the line.");
            view.SetEnterSends(false);
            Require(!view.SendsOnKeys(false, false) && view.SendsOnKeys(true, false), "With the option off, Ctrl or Cmd+Enter sends.");
            view.SetEnterSends(true);
            Require(composerPreference.Get() is { } stored && stored.Contains("do it", StringComparison.Ordinal), "Sent prompts are remembered as recents.");
            var png = Path.Combine(Path.GetTempPath(), "chat-preview-" + Guid.NewGuid().ToString("N")[..6] + ".png");
            File.WriteAllBytes(png, Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII="));
            try
            {
                view.AddAttachment(png);
                Frame(window);
                E2eWorkspace.Until(() => view.GetVisualDescendants().OfType<Image>().Any(image => image.Name == "ChatAttachmentPreview" && image.Source is not null));
            }
            finally { File.Delete(png); }
            // The footer collapses by priority at measured widths; the model chip, history, and Send always stay and nothing is clipped.
            var levels = new List<int>();
            // The widths a footer collapses at depend on the text it measures, which differs with fonts and themes loaded earlier in a process: the sweep
            // goes on below the usual widths until the footer has collapsed, and only the usual widths must still show History and Send.
            foreach (var width in new[] { 900, 700, 520, 400, 320, 260, 200, 140 })
            {
                window.Width = width; Frame(window); Frame(window); Frame(window);
                levels.Add(view.DensityLevel);
                if (width < 320) continue;
                var names = view.GetVisualDescendants().OfType<Control>().Where(control => control.IsVisible).Select(control => control.Name).ToHashSet();
                Require(names.Contains("ChatHistory") && names.Contains("ChatSend") && view.Bounds.Width <= width, $"History and Send stay at {width}px.");
            }
            Require(levels[0] <= 2 && levels.Zip(levels.Skip(1), (x, y) => y >= x).All(ok => ok) && levels[^1] >= 2, $"The footer collapses monotonically as width shrinks ({string.Join(",", levels)}).");
            window.Width = 900; Frame(window); Frame(window);
            // While the turn runs the footer offers Stop beside Queue, shows the working time, the session stats and the queued messages.
            chat.Push(new ChatHeaderPush(Workspace, Chat, chat.Header with { Status = ChatStatus.Running, Usage = new ChatUsage { Input = 1200, Output = 300, CostUsd = 0.5, ContextTokens = 40_000, ContextWindow = 200_000 } }));
            chat.Push(Item(new ChatUserItem("queued", 40, "do this next") { Queued = true }));
            Frame(window);
            var statsText = view.StatsText;
            Require(view.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ChatSendMenu").IsVisible, "The send options appear while a turn runs.");
            var callsBefore = chat.Calls.Count;
            view.Input.Text = "now please";
            Await(view.InterruptThenSend());
            Require(chat.Calls.Skip(callsBefore).Take(2).SequenceEqual([ChatContract.Interrupt.Name, ChatContract.Send.Name]), "Stop and send interrupts the turn, then sends.");
            Require(view.StopButton.IsVisible && statsText is not null && statsText.Contains("$0.500", StringComparison.Ordinal) && statsText.Contains("20.0%/200k", StringComparison.Ordinal) && statsText.Contains('\u25B0'),
                "Stop appears while the chat runs, and the footer's stats show cost and a context bar: " + statsText);
            var queueStrip = view.GetVisualDescendants().OfType<StackPanel>().First(panel => panel.Name == "ChatQueue");
            Require(queueStrip.IsVisible && queueStrip.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("do this next", StringComparison.Ordinal) == true),
                "Messages queued behind the turn are listed above the composer.");

            // Each queued message can be edited or removed; a message the CLI already took up stays, with a plain note.
            chat.FailQueue = true;
            Await(view.Dequeue((ChatUserItem)transcript.Item("queued")!));
            Frame(window);
            Require(transcript.Item("queued") is not null && view.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Name == "ChatQueueNote" && text.Text!.Contains("already took this message up", StringComparison.Ordinal)),
                "A message the CLI already took up cannot be removed: it stays, and the strip says why.");
            chat.FailQueue = false;
            Await(view.EditQueued((ChatUserItem)transcript.Item("queued")!, "do that next"));
            E2eWorkspace.Until(() => transcript.Item("queued") is null && transcript.Item("queued-edited") is ChatUserItem { Text: "do that next", Queued: true });
            Frame(window);
            Await(view.Dequeue((ChatUserItem)transcript.Item("queued-edited")!));
            E2eWorkspace.Until(() => transcript.Item("queued-edited") is null);
            Require(transcript.QueuedMessages().Count == 0, "Editing sends the new text as a queued message in place of the old; a removal push drops the item.");

            // AskUserQuestion: single and multi select, Other with typed text, submit and cancel; answered, the card folds to the answers.
            ChatQuestion[] questions =
            [
                new("Which approach?", [new("Fast", "less careful"), new("Careful")]) { Header = "Approach" },
                new("Which checks?", [new("Unit"), new("E2E")]) { MultiSelect = true }
            ];
            chat.Push(Item(new ChatQuestionItem("ask", 50, "req-ask", questions)));
            Frame(window);
            var ask = (ChatQuestionItem)transcript.Item("ask")!;
            Require(view.RequestPanel.IsVisible && view.RequestPanel.GetVisualDescendants().OfType<Button>().First(button => button.Name == "ChatQuestionSubmit").IsEnabled == false, "An incomplete card cannot submit.");
            view.QuestionPick(ask, 0, "Careful");
            view.QuestionPick(ask, 0, "Fast");
            view.QuestionPick(ask, 1, "Unit");
            view.QuestionPick(ask, 1, "E2E");
            view.QuestionOther(ask, 1, "Lint");
            var draft = transcript.DraftOf(ask);
            Require(draft.Chosen[0].SequenceEqual(["Fast"]) && draft.Chosen[1].SequenceEqual(["Unit", "E2E"]) && draft.Other[1] == "Lint" && draft.Complete, "A single-select question keeps one choice, a multi-select several, and Other holds typed text.");
            view.SubmitQuestion(ask);
            E2eWorkspace.Until(() => transcript.Item("ask") is ChatQuestionItem { Decision: ChatPermissionDecision.Allowed });
            Require(chat.QuestionAnswers.Single() is { Cancelled: null, Answers: { } sent } && sent["Which approach?"] == "Fast" && sent["Which checks?"] == "Unit, E2E, Lint", "The answers go back keyed by question, a multi-select as its labels joined.");
            chat.Push(Item(new ChatQuestionItem("ask2", 51, "req-ask2", [questions[0]])));
            Frame(window);
            view.CancelQuestion((ChatQuestionItem)transcript.Item("ask2")!);
            E2eWorkspace.Until(() => transcript.Item("ask2") is ChatQuestionItem { Decision: ChatPermissionDecision.Denied });
            Require(chat.QuestionAnswers.Last().Cancelled == true, "Cancel answers the question as cancelled.");

            // Notices by category: a failure with Retry sends the last prompt again; compaction and restart are quiet lines; a review verdict is a card.
            chat.Push(Item(new ChatNoticeItem("fail", 60, ChatNoticeLevel.Error, "The turn failed: overloaded") { Category = ChatNoticeKinds.Failure, Subtype = "error_during_execution", Retryable = true }));
            chat.Push(Item(new ChatNoticeItem("compact", 61, ChatNoticeLevel.Info, "auto, 120k tokens") { Category = ChatNoticeKinds.Compaction }));
            Frame(window);
            using (var failure = ChatRowLayout.Build(transcript.Row("fail")!, 700, true))
                Require(failure.OwnHits.Any(hit => hit.Kind == ChatHitKind.Retry), "A retryable failure card offers Try again.");
            using (var plainFailure = ChatRowLayout.Build(new ChatRow(new ChatNoticeItem("f2", 1, ChatNoticeLevel.Error, "no") { Category = ChatNoticeKinds.Failure, Retryable = false }, 0, 1, false, null), 700, true))
                Require(!plainFailure.OwnHits.Any(hit => hit.Kind == ChatHitKind.Retry), "A failure that is not retryable offers no retry.");
            var sentBefore = chat.Calls.Count(call => call == ChatContract.Send.Name);
            Await(view.RetryLast());
            Require(chat.Calls.Count(call => call == ChatContract.Send.Name) == sentBefore + 1, "Try again sends the last prompt once more.");
            Require(ChatRowLayout.ReviewVerdict("mcp__x__request_review", "{\"verdict\":\"changes\",\"itemTitle\":\"Plan A\",\"findings\":[{\"comment\":\"fix the loop\"}]}") is { Summary: var verdictSummary } && verdictSummary.Contains("Changes requested on", StringComparison.Ordinal) &&
                ChatRowLayout.ReviewVerdict("mcp__x__request_review", "{\"verdict\":\"approve\",\"itemTitle\":\"Plan A\"}")!.Value.Summary == "Approved \u201CPlan A\u201D" && ChatRowLayout.ReviewVerdict("Bash", "{}") is null,
                "A review tool's verdict reads as a card summary.");

            // A removal above the reading position moves nothing the reader is looking at is checked with the long list; here the item and its group state go.
            chat.Push(new ChatRemovePush(Workspace, Chat, "compact"));
            Frame(window);
            Require(transcript.Item("compact") is null && transcript.Row("compact") is null, "A remove push drops the item and its row.");

            // A rebuilt view (a restored tab) opens the same chat id and repaints from the replay.
            var revived = new FakeChat { Header = chat.Header };
            revived.Snapshot.Add(Item(new ChatUserItem("u1", 1, "Hello")));
            var again = new ChatView(new ChatEnvironment(revived, TestAgent), Workspace, Chat);
            window.Content = again; Frame(window); Frame(window);
            Require(again.Transcript.Rows.Count == 1 && revived.Calls.Contains(ChatContract.Open.Name), "A restored chat tab opens its chat id and shows the replayed items.");
            revived.Push(new ChatResetPush(Workspace, Chat));
            revived.Push(Item(new ChatUserItem("u1", 1, "Hello")));
            revived.Push(Item(new ChatAssistantItem("a1", 2, "Hi")));
            Frame(window);
            Require(again.Transcript.Rows.Count == 2, "A reset replaces what was shown.");
        }
        catch (Exception error) { Console.WriteLine("FAILED " + error); throw; }
        finally { window.Close(); }
    }

    /// <summary>With Mode set to Chat the launcher opens a chat document tab and no terminal; in Terminal mode it opens a terminal.</summary>
    internal static void Launcher(string root)
    {
        var directory = Path.Combine(root, "claude-chat-launcher");
        Directory.CreateDirectory(directory);
        var fake = Path.Combine(directory, "fake-claude");
        File.WriteAllText(fake, "#!/bin/sh\nsleep 60\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var app = new E2eWorkspace(Path.Combine(directory, "project"), openFiles: false);
        E2eWorkspace.Until(() => app.Workbench.PluginLoader!.Registry.Entry("claude-code") is not null);
        void Settings(object settings) => app.State!.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(settings))]).AsTask().GetAwaiter().GetResult();
        Settings(new { enabled = true, command = fake, mode = "Chat" });
        E2eWorkspace.Until(() => app.Window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "NewClaude" && button.IsEffectivelyVisible));
        int Count(string kind) => app.Window.Layout.State.Groups.Sum(group => app.Window.Layout.Tabs(group.Id).Count(tab => tab.Kind == kind)) +
            app.Window.Layout.State.Workspaces.Values.SelectMany(view => view.Documents.Values.SelectMany(tabs => tabs)).Count(tab => tab.Kind == kind);
        var terminals = Count("terminal");
        E2eWorkspace.Settle(550);
        app.Click(app.Window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "NewClaude" && button.IsEffectivelyVisible), freshGesture: false);
        E2eWorkspace.Until(() => app.Window.GetVisualDescendants().OfType<ChatView>().Any() || app.Window.GetLogicalDescendants().OfType<ChatView>().Any());
        Require(Count("terminal") == terminals, "Chat mode starts a chat, not a terminal.");
        // A plugin document's owner hears of its close exactly once: a closed tab, then the window's remaining tabs.
        var closes = new List<string>();
        app.Workbench.PluginRegistry.AddDocument("claude-code", new("fixture-doc", _ => new TextBlock { Text = "fixture" }) { Closed = props => closes.Add(props.Id) });
        Await(app.Window.OpenPluginDocumentAsync(app.Root, "fixture-doc", "a", "A"));
        Await(app.Window.OpenPluginDocumentAsync(app.Root, "fixture-doc", "b", "B"));
        var group = app.Window.Layout.State.Groups.First(item => app.Window.Layout.Tabs(item.Id).Any(tab => tab.Kind == "plugin")).Id;
        app.Window.Layout.Close(group, "plugin:fixture-doc:a");
        E2eWorkspace.Until(() => closes.SequenceEqual(["a"]));
        app.Window.Layout.Close(group, "plugin:fixture-doc:a");
        Require(closes.SequenceEqual(["a"]), "Closing a tab tells its document's owner once.");
        Require(ChatLauncher.Handles("") && ChatLauncher.Handles("--continue") && ChatLauncher.Handles("--resume --fork-session") && ChatLauncher.Handles("--model opus") &&
            !ChatLauncher.Handles("--teleport") && !ChatLauncher.Handles("--add-dir ../x"), "Chat mode takes the presets a chat can honour and leaves the rest to the terminal.");
        var open = ChatLauncher.Params(app.Root, "c1", new ClaudeCodeSettings { Effort = "high" }, "--model opus", "plan");
        Require(open is { Model: "opus", Effort: "high", PermissionMode: "plan" }, "A preset's model and the run's mode reach the chat's open params.");
        app.Window.Close();
        E2eWorkspace.Until(() => closes.SequenceEqual(["a", "b"]));
        E2eWorkspace.Settle(300);
        Require(closes.SequenceEqual(["a", "b"]), "Closing the window tells the owner of each remaining document once.");
    }
}