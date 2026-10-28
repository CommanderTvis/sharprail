using System.Diagnostics;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Chat;
using SharpRail.Plugins.Chat.UI;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.Host;
using SharpRail.Plugins.ClaudeCode.UI.Chat;

namespace SharpRail.Checks;

/// <summary>
/// The real chat view in a real workbench window against the real Claude Code host, reached over the plugin call path the
/// app uses, with the scripted CLI of <see cref="ClaudeChatChecks"/> as the child process.
/// </summary>
internal static class ClaudeChatE2E
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Await(Task task) => E2eWorkspace.Until(() => task.IsCompleted);

    public static void Run(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(root, "claude-chat-e2e");
        var previous = new Dictionary<string, string?>();
        foreach (var (name, path) in new Dictionary<string, string>
        {
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(directory, "claude-home"),
            ["SHARPRAIL_STATE_DIR"] = Path.Combine(directory, "state"),
            [ClaudeCodeContract.PluginDirectoryVariable] = Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets", "marketplace", "claude-plugin")
        })
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(name, path);
        }
        try { RunIn(directory); }
        finally { foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value); }
    }

    /// <summary>Two chat tabs in one workspace: a real press and release on a tab button switches to it, also while the shown chat streams (a document id reads as a plugin tool id).</summary>
    public static void TabClicks(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(root, "claude-chat-tab-clicks");
        var previous = new Dictionary<string, string?>();
        foreach (var (name, path) in new Dictionary<string, string>
        {
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(directory, "claude-home"),
            ["SHARPRAIL_STATE_DIR"] = Path.Combine(directory, "state"),
            [ClaudeCodeContract.PluginDirectoryVariable] = Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets", "marketplace", "claude-plugin")
        })
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
            Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(name, path);
        }
        try { TabClicksIn(directory); }
        finally { foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value); }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void TabClicksIn(string directory)
    {
        var project = Path.Combine(directory, "project");
        var fake = Path.Combine(directory, "fake", "claude");
        Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
        File.WriteAllText(fake, ClaudeChatChecks.FakeClaude.Replace("\r\n", "\n"));
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var app = new E2eWorkspace(project, openFiles: false, pluginTerminals: true);
        E2eWorkspace.Until(() => app.Workbench.PluginLoader!.Registry.Entry("claude-code") is not null);
        app.State!.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(new { enabled = true, command = fake, mode = "Chat" }))]).AsTask().GetAwaiter().GetResult();
        E2eWorkspace.Until(() => app.Window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "NewClaude" && button.IsEffectivelyVisible));
        E2eWorkspace.Settle(550);
        app.UntilDialogsGone();
        for (var made = 1; made <= 2; made++)
        {
            app.Click(app.Window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "NewClaude" && button.IsEffectivelyVisible), freshGesture: false);
            E2eWorkspace.Until(() => app.Window.Layout.State.Workspaces.Values.SelectMany(workspace => workspace.Documents.Values.SelectMany(tabs => tabs)).Count(item => item.Kind == "plugin") == made);
            E2eWorkspace.Until(() => View(app)?.Transcript.Header is { Status: ChatStatus.Idle });
        }
        var buttons = app.Window.GetVisualDescendants().OfType<SharpRail.UI.Docking.DockTabButton>().Where(button => button.IsEffectivelyVisible).ToArray();
        Require(buttons.Length >= 2, $"Both chat tabs show in the strip ({buttons.Length}).");
        SharpRail.UI.Docking.DockTabButton[] Buttons() => [.. app.Window.GetVisualDescendants().OfType<SharpRail.UI.Docking.DockTabButton>().Where(button => button.IsEffectivelyVisible)];
        void ClickOther(string what)
        {
            var target = Buttons().Single(button => !button.IsSelected);
            app.Click(target, freshGesture: false);
            Require(target.IsSelected && Buttons().Count(button => button.IsSelected) == 1, $"{what}: one click on the other chat tab selects it.");
        }
        for (var round = 0; round < 3; round++)
        {
            ClickOther($"Round {round}");
            // The shown chat streams a turn while the pointer goes to the other tab's button.
            var view = View(app)!;
            view.Input.Text = "hello";
            Await(view.Submit());
            ClickOther($"Round {round} while the shown chat streams");
            E2eWorkspace.Until(() => View(app)?.Transcript.Header is { Status: ChatStatus.Idle });
        }
    }

    /// <summary>The "Start work" flow with Claude Code in Chat mode: the dialog creates the workspace and opens exactly one chat tab, the prompt is its first message; in Terminal mode it opens a terminal.</summary>
    public static void StartWork(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(root, "claude-chat-start-work");
        var previous = new Dictionary<string, string?>();
        // A stand-in `central` on PATH under a throwaway HOME: Air Governance, set to Central, contributes a wrapper to Claude launches, and the
        // stand-in records the arguments and environment of the launch it was asked to run.
        var governedBin = Path.Combine(directory, "gov-bin");
        Directory.CreateDirectory(governedBin);
        var central = Path.Combine(governedBin, "central");
        File.WriteAllText(central, CentralStandIn.Replace("RUN_LOG", Path.Combine(directory, "central-run.log")));
        File.SetUnixFileMode(central, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var (name, path) in new Dictionary<string, string>
        {
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(directory, "claude-home"),
            ["SHARPRAIL_STATE_DIR"] = Path.Combine(directory, "state"),
            ["HOME"] = Path.Combine(directory, "gov-home"),
            ["PATH"] = governedBin + Path.PathSeparator + (Environment.GetEnvironmentVariable("PATH") ?? ""),
            [ClaudeCodeContract.PluginDirectoryVariable] = Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets", "marketplace", "claude-plugin")
        })
        {
            previous[name] = Environment.GetEnvironmentVariable(name);
            if (name != "PATH") Directory.CreateDirectory(path);
            Environment.SetEnvironmentVariable(name, path);
        }
        try { StartWorkIn(directory); }
        finally { foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value); }
    }

    private const string CentralStandIn = """
        #!/bin/sh
        case "${1:-}" in
        --version) echo 'central 1.13.0 (commit: synthetic)' ;;
        status) echo 'Auth      E2E'; echo 'Proxy     running on port 19516' ;;
        run) shift; { for a in "$@"; do printf 'arg:%s\n' "$a"; done; echo "env:SHARPRAIL_UI_LAUNCH=${SHARPRAIL_UI_LAUNCH-}"; echo "end"; } >> "RUN_LOG" ;;
        *) exit 8 ;;
        esac
        """;

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void StartWorkIn(string directory)
    {
        using var git = new IsolatedGit(Path.Combine(directory, "git"));
        var fake = Path.Combine(directory, "fake", "claude");
        Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
        File.WriteAllText(fake, ClaudeChatChecks.FakeClaude.Replace("\r\n", "\n"));
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var project = Path.Combine(directory, "sample-project");
        IsolatedGit.Repository(project);
        using var app = new E2eWorkspace(project, openFiles: false, pluginTerminals: true);
        IsolatedGit.Run(project, "add", "-A");
        IsolatedGit.Run(project, "commit", "-m", "fixture");
        E2eWorkspace.Until(() => app.Find<TextBlock>("BranchLabel").Text == "main");
        WorkspaceFixture.GoProjectHome(app);
        E2eWorkspace.Until(() => app.Workbench.PluginLoader!.Registry.Entry("claude-code") is not null);
        void Settings(object settings) => app.State!.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(settings))]).AsTask().GetAwaiter().GetResult();
        string[] Spawns() => File.Exists(Path.Combine(directory, "fake", "args.log")) ? [.. File.ReadAllLines(Path.Combine(directory, "fake", "args.log")).Where(line => line.Contains("--output-format stream-json", StringComparison.Ordinal))] : [];
        int ChatTabs() => app.Window.Layout.State.Workspaces.Values.SelectMany(workspace => workspace.Documents.Values.SelectMany(tabs => tabs)).Count(tab => tab.Kind == "plugin");

        Settings(new { enabled = true, command = fake, mode = "Chat", model = "haiku", effort = "high", permissionMode = "acceptEdits" });
        E2eWorkspace.Until(() => app.Workbench.PluginRegistry.LauncherList.Any(launcher => launcher.Id == "claude"));
        E2eWorkspace.Settle(300);

        // One chat start through a dialog: choose Claude, type the prompt, and create with Enter or the button.
        void Begin(Window dialog, string text, bool enter)
        {
            app.Click(dialog.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(button => button.Name == "WsLauncher_claude"), freshGesture: false);
            var box = dialog.GetLogicalDescendants().OfType<TextBox>().Single(item => item.Name == "WsPrompt");
            box.Text = text;
            box.Focus();
            if (enter) app.Window.OwnedWindows.Single().KeyPress(Avalonia.Input.Key.Enter, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Enter, null);
            else app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "WsCreate"), freshGesture: false);
        }
        void Started(string text, int spawns)
        {
            E2eWorkspace.Until(() => View(app) is { } started && started.Transcript.Rows.Select(row => row.Item).OfType<ChatUserItem>().Any(user => user.Text.StartsWith(text, StringComparison.Ordinal)));
            E2eWorkspace.Until(() => Spawns().Length == spawns);
            var line = Spawns()[^1];
            Require(line.Contains("--model haiku", StringComparison.Ordinal) && line.Contains("--effort high", StringComparison.Ordinal) && line.Contains("--permission-mode acceptEdits", StringComparison.Ordinal),
                $"The chosen model, effort and permission mode reach the child's command line ({line}).");
            Require(ChatTabs() == spawns && !app.Terminals.Views.Any() && app.Window.WorkspaceRoot != app.Window.ProjectRoot, $"Each start opens one chat tab in a new workspace and no terminal (tabs {ChatTabs()}).");
        }
        var projectRoot = app.Window.ProjectRoot;

        // The rail's add button, with Enter in the prompt.
        var dialog = WorkspaceFixture.OpenNewWorkspaceDialog(app);
        Begin(dialog, "hello from start work", enter: true);
        Started("hello from start work", 1);
        Require(View(app)!.Transcript.Rows.Select(row => row.Item).OfType<ChatUserItem>().First().Text.StartsWith("hello from start work", StringComparison.Ordinal), "The opening prompt is the chat's first message.");

        // The interface at 150%: the footer still holds the model chip, history and Send inside the chat.
        for (var step = 0; step < 3; step++) app.Window.KeyPress(Avalonia.Input.Key.OemPlus, E2eWorkspace.Command, Avalonia.Input.PhysicalKey.Equal, null);
        E2eWorkspace.Until(() => Math.Abs(InterfaceZoom.Current - 1.5) < 1e-9);
        E2eWorkspace.Settle(300);
        var zoomed = View(app)!;
        var shown = zoomed.GetVisualDescendants().OfType<Control>().Where(control => control.IsEffectivelyVisible).Select(control => control.Name).ToHashSet();
        Require(shown.Contains("ChatHistory") && shown.Contains("ChatSend") && shown.Contains("TerminalAgentFact_model"),
            $"At 150% the footer keeps the model chip, history and Send (density {zoomed.DensityLevel}).");
        app.Window.KeyPress(Avalonia.Input.Key.D0, E2eWorkspace.Command, Avalonia.Input.PhysicalKey.Digit0, null);
        E2eWorkspace.Until(() => Math.Abs(InterfaceZoom.Current - 1) < 1e-9);

        // With the chat focused the same chords are the chat's own: its zoom moves, the page's does not.
        var chatView = View(app)!;
        chatView.Input.Focus();
        E2eWorkspace.Settle(150);
        app.Window.KeyPress(Avalonia.Input.Key.OemPlus, E2eWorkspace.Command, Avalonia.Input.PhysicalKey.Equal, null);
        E2eWorkspace.Until(() => Math.Abs(chatView.Zoom - 1.1) < 1e-9);
        Require(Math.Abs(InterfaceZoom.Current - 1) < 1e-9, "Mod+= with the chat focused zooms the chat and leaves the page zoom alone.");
        app.Window.KeyPress(Avalonia.Input.Key.D0, E2eWorkspace.Command, Avalonia.Input.PhysicalKey.Digit0, null);
        E2eWorkspace.Until(() => chatView.Zoom == 1);

        // Project Home's Create workspace card.
        WorkspaceFixture.GoProjectHome(app);
        E2eWorkspace.Until(() => app.Window.GetVisualDescendants().OfType<Button>().Any(button => button.IsEffectivelyVisible && button.Name is "WelcomeAction" or "WelcomeCta" && button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Create workspace")));
        app.Click(app.Window.GetVisualDescendants().OfType<Button>().First(button => button.IsEffectivelyVisible && button.Name is "WelcomeAction" or "WelcomeCta" && button.GetVisualDescendants().OfType<TextBlock>().Any(text => text.Text == "Create workspace")), freshGesture: false);
        Begin(WorkspaceFixture.Dialog(app, "NewWorkspaceDialog"), "from the home card", enter: false);
        Started("from the home card", 2);

        // The project's own menu in the rail: Start work.
        var row = app.Window.GetVisualDescendants().OfType<Grid>().First(item => item.Name == "ProjectRow" && Equals(item.Tag, projectRoot));
        app.ContextAction(row, "Start work");
        Begin(WorkspaceFixture.Dialog(app, "NewWorkspaceDialog"), "from the project menu", enter: false);
        Started("from the project menu", 3);

        // Launch contributions: with Air Governance set to Central, the child is run by the wrapper with the chat's own arguments after it.
        app.State!.ChangeAsync([HostStateChange.PluginSettings("air-governance", JsonSerializer.Serialize(new { enabled = true, source = "central", claudeCode = true }))]).AsTask().GetAwaiter().GetResult();
        E2eWorkspace.Until(() => app.Workbench.PluginRegistry.LaunchContributions("claude").Count == 1);
        var centralLog = Path.Combine(directory, "central-run.log");
        WorkspaceFixture.GoProjectHome(app);
        var governed = WorkspaceFixture.OpenNewWorkspaceDialog(app);
        Begin(governed, "governed start", enter: false);
        E2eWorkspace.Until(() => File.Exists(centralLog) && File.ReadAllText(centralLog).Contains("end", StringComparison.Ordinal));
        var launched = File.ReadAllLines(centralLog);
        Require(launched.Take(2).SequenceEqual(["arg:claude-code", "arg:--"]) && new[] { "arg:-p", "arg:--output-format", "arg:stream-json", "arg:--effort", "arg:high", "arg:--permission-mode", "arg:acceptEdits", "arg:--model", "arg:haiku" }.All(launched.Contains) &&
            launched.Contains("env:SHARPRAIL_UI_LAUNCH=claude"), "A governed chat start runs through the contributed wrapper with the chat's arguments and environment: " + string.Join(" ", launched));

        // Terminal mode starts a terminal as before.
        Settings(new { enabled = true, command = fake, mode = "Terminal" });
        E2eWorkspace.Settle(600);
        WorkspaceFixture.GoProjectHome(app);
        var second = WorkspaceFixture.OpenNewWorkspaceDialog(app);
        app.Click(second.GetLogicalDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>().Single(button => button.Name == "WsLauncher_claude"), freshGesture: false);
        second.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Name == "WsPrompt").Text = "terminal please";
        app.Click(second.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "WsCreate"), freshGesture: false);
        E2eWorkspace.Until(() => app.Terminals.Views.Any());
        Require(ChatTabs() == 4, "Terminal mode opens a terminal and no further chat tab.");
    }

    private static ChatView? View(E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<ChatView>().LastOrDefault() ?? app.Window.GetVisualDescendants().OfType<ChatView>().LastOrDefault();

    private static string Chip(ChatView view, string name) =>
        string.Join(" ", view.GetVisualDescendants().OfType<Control>().First(control => control.Name == name).GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text)).Trim();

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void RunIn(string directory)
    {
        var project = Path.Combine(directory, "project");
        var fake = Path.Combine(directory, "fake", "claude");
        Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
        File.WriteAllText(fake, ClaudeChatChecks.FakeClaude.Replace("\r\n", "\n"));
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var spawns = Path.Combine(directory, "fake", "args.log");
        // The plugin also runs the command for its own queries (--help and others); only the chat's own starts count.
        string[] Spawns() => File.Exists(spawns) ? [.. File.ReadAllLines(spawns).Where(line => line.Contains("--output-format stream-json", StringComparison.Ordinal))] : [];
        string chat, session;

        using (var app = new E2eWorkspace(project, openFiles: false, pluginTerminals: true))
        {
            E2eWorkspace.Until(() => app.Workbench.PluginLoader!.Registry.Entry("claude-code") is not null);
            app.State!.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(new { enabled = true, command = fake, mode = "Chat" }))]).AsTask().GetAwaiter().GetResult();
            E2eWorkspace.Until(() => app.Window.GetVisualDescendants().OfType<Button>().Any(button => button.Name == "NewClaude" && button.IsEffectivelyVisible));
            E2eWorkspace.Settle(550);
            app.UntilDialogsGone();
            app.Click(app.Window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "NewClaude" && button.IsEffectivelyVisible), freshGesture: false);
            E2eWorkspace.Until(() => View(app) is not null);
            var view = View(app)!;
            var tab = app.Window.Layout.State.Workspaces.Values.SelectMany(workspace => workspace.Documents.Values.SelectMany(tabs => tabs)).Single(item => item.Kind == "plugin");
            chat = tab.Path["claude-chat:".Length..];
            var group = app.Window.Layout.State.Groups.First(item => app.Window.Layout.Tabs(item.Id).Any(candidate => candidate.Id == tab.Id) ||
                app.Window.Layout.State.Workspaces.Values.Any(workspace => workspace.Documents.GetValueOrDefault(item.Id)?.Any(candidate => candidate.Id == tab.Id) == true)).Id;

            // Opening ends in an idle chat with a full header, so no chip is left on its placeholder.
            E2eWorkspace.Until(() => view.Transcript.Header is { Status: ChatStatus.Idle, Model: "fake-model" } header && header.Commands.Count >= 2 && header.Models.Count == 2);
            Require(Chip(view, "TerminalAgentFact_model") == "Fake" &&
                Chip(view, "TerminalAgentFact_permission") is not ("permissions" or ""), "An idle chat shows its model, effort and permission chips filled in.");
            session = view.Transcript.Header!.SessionId!;
            Require(Spawns().Length == 1 && Spawns()[0].Contains("--session-id " + session, StringComparison.Ordinal), "The CLI starts under the session the header reports.");

            // A prompt streams into one assistant item.
            view.Input.Text = "hello";
            Await(view.Submit());
            E2eWorkspace.Until(() => view.Transcript.Rows.Select(row => row.Item).OfType<ChatAssistantItem>().SingleOrDefault() is { Text: "Hi there", Streaming: false } &&
                view.Transcript.Header is { Status: ChatStatus.Idle });
            Require(view.Transcript.Rows.Select(row => row.Item).OfType<ChatUserItem>().Single().Text == "hello", "The prompt is one user item.");

            // A tool call with a nested subagent shows nested.
            view.Input.Text = "run an agent";
            Await(view.Submit());
            E2eWorkspace.Until(() => view.Transcript.Row("toolu_task") is { Item: ChatToolItem { State: ChatToolState.Done } } &&
                view.Transcript.Children("toolu_task").Count >= 2 && view.Transcript.Header is { Status: ChatStatus.Idle });
            Require(view.Transcript.Rows.All(row => row.Item.ParentId is null) && view.Transcript.Row("toolu_task")!.Depth == 0 &&
                view.Transcript.Header!.Tasks is [{ Kind: ChatTaskKind.Subagent, Status: ChatTaskStatus.Done }], "The subagent's items stay out of the main list and the task strip tracks it.");

            // The footer holds the chips and the session stats under the conversation; history search and the subagent dialog read the live transcript.
            var chips = view.GetVisualDescendants().OfType<Control>().First(control => control.Name == "ChatChips");
            Require(chips.TranslatePoint(new Point(0, 0), view)!.Value.Y >= view.List.TranslatePoint(new Point(0, view.List.Bounds.Height), view)!.Value.Y, "The pickers sit below the conversation.");
            Require(view.StatsText.Contains("$0.", StringComparison.Ordinal) && view.StatsText.Contains('%'), "The usage popover holds cost and context from the header: " + view.StatsText);
            Require(ChatView.Search(view.Transcript.AllItems().OrderBy(item => item.Seq).ToArray(), "Hi there", CancellationToken.None).Count >= 1, "History search finds the streamed reply.");
            view.OpenHistory();
            view.HistoryQuery!.Text = "hello";
            E2eWorkspace.Until(() => view.HistoryResults!.ItemCount == 1);
            view.HistoryQuery.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Tab });
            view.HistoryQuery.Text = "Hi there";
            E2eWorkspace.Until(() => view.HistoryResults!.ItemCount >= 1);
            view.HistoryQuery.RaiseEvent(new Avalonia.Input.KeyEventArgs { RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.Escape });
            Require(!view.HistoryOpen, "The history overlay finds the streamed reply and closes on Escape.");
            view.ShowThread("toolu_task");
            E2eWorkspace.Until(() => view.ThreadTranscript is { } thread && thread.Rows.Any(row => row.Item is ChatAssistantItem { Text: "Inside" }));
            foreach (var owned in app.Window.OwnedWindows.ToArray()) owned.Close();
            app.UntilDialogsGone();

            // A permission card answered through the view reaches the child.
            view.Input.Text = "needs perm";
            Await(view.Submit());
            E2eWorkspace.Until(() => view.Transcript.Rows.Select(row => row.Item).OfType<ChatPermissionItem>().Any(item => item.Decision == ChatPermissionDecision.Pending));
            var permission = view.Transcript.Rows.Select(row => row.Item).OfType<ChatPermissionItem>().Single(item => item.Decision == ChatPermissionDecision.Pending);
            Await(view.Answer(permission, ChatHitKind.Allow));
            var answer = Path.Combine(directory, "fake", "answer.txt");
            E2eWorkspace.Until(() => File.Exists(answer) && File.ReadAllText(answer).Contains("\"behavior\":\"allow\"", StringComparison.Ordinal));
            E2eWorkspace.Until(() => view.Transcript.Item(permission.Id) is ChatPermissionItem { Decision: ChatPermissionDecision.Allowed } && view.Transcript.Header is { Status: ChatStatus.Idle });

            // A question the CLI asks becomes a card; picks and Submit send the answers back as the tool's input.
            view.Input.Text = "ask me";
            Await(view.Submit());
            E2eWorkspace.Until(() => view.Transcript.FirstPending() is ChatQuestionItem);
            var question = (ChatQuestionItem)view.Transcript.FirstPending()!;
            view.QuestionPick(question, 0, "A");
            view.QuestionPick(question, 1, "X");
            view.QuestionPick(question, 1, "Y");
            view.SubmitQuestion(question);
            var asked = Path.Combine(directory, "fake", "ask.txt");
            E2eWorkspace.Until(() => File.Exists(asked) && File.ReadAllText(asked).Contains("\"Which more?\":\"X, Y\"", StringComparison.Ordinal));
            E2eWorkspace.Until(() => view.Transcript.Item(question.Id) is ChatQuestionItem { Decision: ChatPermissionDecision.Allowed } && view.Transcript.Header is { Status: ChatStatus.Idle });

            // A failed turn is a card with Try again, which sends the last prompt once more; a compaction is a quiet line.
            view.Input.Text = "fail now";
            Await(view.Submit());
            E2eWorkspace.Until(() => view.Transcript.Rows.Select(row => row.Item).OfType<ChatNoticeItem>().Count(notice => notice.Category == ChatNoticeKinds.Failure) == 1 && view.Transcript.Header is { Status: ChatStatus.Idle });
            Require(view.Transcript.Rows.Select(row => row.Item).OfType<ChatNoticeItem>().Single(notice => notice.Category == ChatNoticeKinds.Failure) is { Retryable: true }, "A retryable failure reaches the view.");
            var failure = view.Transcript.Rows.Select(row => row.Item).OfType<ChatNoticeItem>().Single(notice => notice.Category == ChatNoticeKinds.Failure);
            view.Activate(view.Transcript.Row(failure.Id)!, ChatHitKind.Retry);
            E2eWorkspace.Until(() => view.Transcript.Rows.Select(row => row.Item).OfType<ChatUserItem>().Count(user => user.Text == "fail now") == 2);
            view.Input.Text = "compact it";
            Await(view.Submit());
            E2eWorkspace.Until(() => view.Transcript.Rows.Select(row => row.Item).OfType<ChatNoticeItem>().Any(notice => notice.Category == ChatNoticeKinds.Compaction) && view.Transcript.Header is { Status: ChatStatus.Idle });

            // The model chip changes only after the CLI confirms (the scripted CLI delays its answer).
            var switching = view.SetModel("new-model");
            E2eWorkspace.Until(() => Chip(view, "TerminalAgentFact_model").Contains('…'));
            Require(Chip(view, "TerminalAgentFact_model").StartsWith("Fake", StringComparison.Ordinal) && view.Transcript.Header!.Model == "fake-model", "The chip holds the old model, marked pending, until the CLI confirms.");
            Await(switching);
            E2eWorkspace.Until(() => Chip(view, "TerminalAgentFact_model").StartsWith("New", StringComparison.Ordinal) && view.Transcript.Header!.Model == "new-model");

            // Closing the tab ends the child.
            var grandchild = int.Parse(File.ReadAllText(Path.Combine(directory, "fake", "grandchild.pid")).Trim());
            app.Window.Layout.Close(group, tab.Id);
            E2eWorkspace.Until(() =>
            {
                try { using var survivor = Process.GetProcessById(grandchild); return survivor.HasExited; }
                catch (ArgumentException) { return true; }
            });
            Require(Spawns().Length == 1, "Closing the tab spawned nothing new.");

            // The CLI left a transcript; reopening the same chat resumes it and replays the conversation.
            var transcript = AgentResume.ExpectedTranscriptPath(app.Root, session)!;
            Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
            File.WriteAllLines(transcript,
            [
                """{"type":"user","uuid":"u-1","parentUuid":null,"isSidechain":false,"message":{"role":"user","content":"first question"}}""",
                """{"type":"assistant","uuid":"a-1","parentUuid":"u-1","message":{"id":"r1","content":[{"type":"text","text":"first answer"}]}}"""
            ]);
            Await(app.Window.OpenPluginDocumentAsync(app.Root, "claude-chat", chat, "Claude Code"));
            E2eWorkspace.Until(() => View(app) is { } reopened && !ReferenceEquals(reopened, view) && reopened.Transcript.Header is { Status: ChatStatus.Idle });
            var again = View(app)!;
            E2eWorkspace.Until(() => again.Transcript.Rows.Select(row => row.Item).OfType<ChatAssistantItem>().Any(item => item.Text == "first answer"));
            Require(Spawns().Length == 2 && Spawns()[^1].Contains("--resume " + session, StringComparison.Ordinal) &&
                again.Transcript.Rows.Select(row => row.Item).OfType<ChatUserItem>().Single().Text == "first question", "Reopening resumes the session and replays the transcript.");
        }

        // A restart of the workbench: the restored layout brings the chat tab back, and the host revives it from its transcript.
        using (var app = new E2eWorkspace(project, openFiles: false, pluginTerminals: true))
        {
            E2eWorkspace.Until(() => View(app) is { } restored && restored.Transcript.Header is { Status: ChatStatus.Idle } &&
                restored.Transcript.Rows.Select(row => row.Item).OfType<ChatAssistantItem>().Any(item => item.Text == "first answer"));
            Require(Spawns().Length == 3 && Spawns()[^1].Contains("--resume " + session, StringComparison.Ordinal), "A restarted workbench revives the chat from its transcript.");
        }
    }
}