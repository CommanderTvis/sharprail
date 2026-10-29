using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Chat;
using SharpRail.Plugins.Chat.UI;
using SharpRail.Plugins.ClaudeCode;

namespace SharpRail.Checks;

/// <summary>
/// Two chat views of one host, the way a desktop and a phone attach to it: each with its own client id and subscription over the real Claude
/// Code host and the scripted CLI. The composer draft typed in one shows in the other, and a send clears it everywhere once.
/// </summary>
internal static class ClaudeChatDraftE2E
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Await(Task task) => E2eWorkspace.Until(() => task.IsCompleted);

    /// <summary>A second client of the app's host: the plugin runtime's call and subscribe path with its own caller name.</summary>
    private sealed class RuntimeClient(PluginRuntime runtime, string caller) : IChatClient
    {
        public IDisposable Subscribe(ChatKey key, Action<ChatPush> handler)
        {
            var stop = new CancellationTokenSource();
            var updates = runtime.SubscribeAsync(new("claude-code", ChatContract.Updates.Name, new { workspaceId = key.WorkspaceId, chatId = key.ChatId }, caller), stop.Token);
            _ = Task.Run(async () =>
            {
                try { await foreach (var payload in updates) { var push = PluginJson.Convert<ChatPush>(payload!); Dispatcher.UIThread.Post(() => handler(push)); } }
                catch (OperationCanceledException) { }
            });
            return new Cancel(stop);
        }

        public async ValueTask<TResult> RequestAsync<TParams, TResult>(PluginMethod<TParams, TResult> method, TParams parameters, CancellationToken cancellationToken = default) =>
            PluginJson.Convert<TResult>(await runtime.CallAsync(new("claude-code", method.Name, parameters, caller), cancellationToken));

        private sealed class Cancel(CancellationTokenSource stop) : IDisposable { public void Dispose() => stop.Cancel(); }
    }

    public static void Run(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(root, "claude-chat-draft-e2e");
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

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void RunIn(string directory)
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
        app.Click(app.Window.GetVisualDescendants().OfType<Button>().First(button => button.Name == "NewClaude" && button.IsEffectivelyVisible), freshGesture: false);
        E2eWorkspace.Until(() => Views(app).Count > 0);
        var desk = Views(app)[^1];
        E2eWorkspace.Until(() => desk.Transcript.Header is { Status: ChatStatus.Idle });
        var runtime = app.PluginRuntime!;
        var key = desk.ChatKey;
        var agent = new ChatAgent("Claude Code", "Claude", "claude-chat", "claude");
        var windows = new List<Window>();
        ChatView Attach(string caller)
        {
            var view = new ChatView(new ChatEnvironment(new RuntimeClient(runtime, caller), agent), key.WorkspaceId, key.ChatId);
            var window = new Window { Width = 800, Height = 600, Content = view };
            window.Show();
            windows.Add(window);
            return view;
        }
        var phone = Attach("phone");
        try
        {
            E2eWorkspace.Until(() => phone.Transcript.Header is { Status: ChatStatus.Idle });
            var host = new RuntimeClient(runtime, "other");
            ChatDraft HostDraft() => PluginJson.Convert<IReadOnlyList<ChatPush>>(runtime.CallAsync(new("claude-code", ChatContract.Snapshot.Name, key, "other")).AsTask().GetAwaiter().GetResult()!)
                .OfType<ChatDraftPush>().Single().Draft;

            // The desk types, the phone shows it; the phone edits, the desk follows.
            desk.Input.Text = "from the desk";
            E2eWorkspace.Until(() => phone.Input.Text == "from the desk");
            Require(HostDraft() is { Text: "from the desk", Revision: > 0 }, "The host holds the draft with a revision.");
            phone.Input.Text = "from the desk and the phone";
            E2eWorkspace.Until(() => desk.Input.Text == "from the desk and the phone");
            Require(desk.Input.Text == "from the desk and the phone", "The desk follows the phone.");
            Console.WriteLine("PASS shared draft: typing on one client shows on the other, both ways");

            // Unsent local text wins when a remote revision arrives, and is then pushed as the next revision.
            var before = HostDraft().Revision;
            desk.Input.Text = "local on the desk";
            Await(host.RequestAsync(ChatContract.SetDraft, new ChatSetDraftParams(key.WorkspaceId, key.ChatId, before, "written elsewhere", "other-client")).AsTask());
            E2eWorkspace.Settle(80);
            Require(desk.Input.Text == "local on the desk", "Unsent local text is kept when a remote draft arrives.");
            E2eWorkspace.Until(() => HostDraft().Text == "local on the desk" && phone.Input.Text == "local on the desk");
            Require(HostDraft().Revision >= before + 2, "The kept local text was pushed as a later revision.");
            Console.WriteLine("PASS shared draft: unsent local text wins for the moment and is pushed as the next revision");

            // Reload: a client that attaches later (another process: no local copy) shows the stored draft.
            ChatView.ForgetLocalDrafts();
            var reloaded = Attach("tablet");
            E2eWorkspace.Until(() => reloaded.Input.Text == "local on the desk");
            Console.WriteLine("PASS shared draft: a client that attaches later shows the stored draft");

            // A send from one client clears the draft on the others.
            desk.Input.Text = "hello";
            E2eWorkspace.Until(() => phone.Input.Text == "hello" && reloaded.Input.Text == "hello");
            Await(desk.Submit());
            E2eWorkspace.Until(() => phone.Input.Text == "" && desk.Input.Text == "" && reloaded.Input.Text == "" && HostDraft().Text == "");
            E2eWorkspace.Until(() => desk.Transcript.Header is { Status: ChatStatus.Idle });
            Require(desk.Transcript.Rows.Select(row => row.Item).OfType<ChatUserItem>().Count(item => item.Text == "hello") == 1, "The send reached the chat once.");
            Console.WriteLine("PASS shared draft: a send clears the draft on every client");

            // Two sends of one draft at the same moment: one goes, the other says it was already sent.
            desk.Input.Text = "hello again";
            E2eWorkspace.Until(() => phone.Input.Text == "hello again");
            var first = desk.Submit();
            var second = phone.Submit();
            Await(first);
            Await(second);
            E2eWorkspace.Until(() => desk.Input.Text == "" && phone.Input.Text == "");
            E2eWorkspace.Until(() => desk.Transcript.Header is { Status: ChatStatus.Idle });
            Require(desk.Transcript.Rows.Select(row => row.Item).OfType<ChatUserItem>().Count(item => item.Text == "hello again") == 1, "Two sends of one draft reach the chat once.");
            Require(new[] { desk.DraftNote, phone.DraftNote }.Count(note => note is { Length: > 0 }) == 1 &&
                new[] { desk.DraftNote, phone.DraftNote }.Single(note => note is { Length: > 0 })!.Contains("already sent", StringComparison.OrdinalIgnoreCase),
                $"The second send shows a quiet already-sent note (desk: {desk.DraftNote}; phone: {phone.DraftNote}).");
            Console.WriteLine("PASS shared draft: two simultaneous sends send once and the second says already sent");
        }
        finally { foreach (var window in windows) window.Close(); }
    }

    private static IReadOnlyList<ChatView> Views(E2eWorkspace app) =>
        [.. app.Window.GetVisualDescendants().OfType<ChatView>()];
}
