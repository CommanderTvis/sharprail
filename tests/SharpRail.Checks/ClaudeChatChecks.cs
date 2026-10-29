using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Chat;
using SharpRail.Plugins.Chat.UI;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.Host;

namespace SharpRail.Checks;

/// <summary>Chat mode's host half, against a scripted stand-in for the claude CLI that speaks the stream-json protocol.</summary>
internal static class ClaudeChatChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class Terminals(TerminalRef terminal) : IPluginTerminalSeams
    {
        public Func<TerminalRef, IReadOnlyDictionary<string, string>>? EnvironmentContributor { get; set; }
        public Func<TerminalRef, CancellationToken, ValueTask>? EnvironmentPreparer { get; set; }
        public Action<TerminalEvent>? Lifecycle { get; set; }
        public Func<TerminalRef, TerminalPrefill?>? RevivePrefill { get; set; }
        public Func<TerminalRef, TerminalPrefill, bool>? ReviveGate { get; set; }
        public Action<string, TerminalRef?>? SessionClosed { get; set; }
        public string Token(TerminalRef owner) => "chat-fixture-token";
        public TerminalRef? ForToken(string token) => token == "chat-fixture-token" ? terminal : null;
        public void Write(TerminalRef owner, string data) { }
        public IReadOnlyList<TerminalProcess> List() => [];
        public string? WorkspaceForProcess(int pid) => null;
    }

    // Answers the protocol from a shell loop: every user prompt gets a scripted turn chosen by a word in its text, and the
    // control requests get their success responses (set_model after a delay, so the check can see the header hold still).
    internal const string FakeClaude = """
        #!/bin/bash
        D=$(cd "$(dirname "$0")" && pwd)
        echo "$*" >> "$D/args.log"
        sleep 300 >/dev/null 2>&1 &
        # The plugin also runs the command for its own queries (--help); only a chat's start names the grandchild to check.
        case "$*" in *stream-json*) echo $! > "$D/grandchild.pid" ;; esac
        SID=$(echo "$*" | sed -n 's/.*--session-id \([^ ]*\).*/\1/p')
        [ -z "$SID" ] && SID=$(echo "$*" | sed -n 's/.*--resume \([^ ]*\).*/\1/p')
        MODEL=$(echo "$*" | sed -n 's/.*--model \([^ ]*\).*/\1/p'); [ -z "$MODEL" ] && MODEL=fake-model
        MODE=$(echo "$*" | sed -n 's/.*--permission-mode \([^ ]*\).*/\1/p'); [ -z "$MODE" ] && MODE=default
        EFFORT=$(echo "$*" | sed -n 's/.*--effort \([^ ]*\).*/\1/p')
        env | grep -E '^(SHARPRAIL_UI_LAUNCH|THINKRAIL_MCP_URL|CLAUDE_CODE_SSE_PORT)=' | sort >> "$D/env.log"
        emit() { printf '%s\n' "$1"; }
        rid() { echo "$1" | sed 's/.*"request_id":"\([^"]*\)".*/\1/'; }
        ok() { local body="${2:-"{}"}"; emit "{\"type\":\"control_response\",\"response\":{\"subtype\":\"success\",\"request_id\":\"$1\",\"response\":$body}}"; }
        result() { emit "{\"type\":\"result\",\"subtype\":\"success\",\"is_error\":false,\"duration_ms\":12,\"num_turns\":1,\"result\":\"done\",\"total_cost_usd\":0.25,\"uuid\":\"res-$RANDOM\",\"usage\":{\"input_tokens\":3,\"output_tokens\":4},\"modelUsage\":{\"fake-model\":{\"contextWindow\":200000}}}"; }
        emit "{\"type\":\"system\",\"subtype\":\"init\",\"session_id\":\"$SID\",\"model\":\"$MODEL\",\"permissionMode\":\"$MODE\",\"cwd\":\"$PWD\",\"claude_code_version\":\"9.9\",\"slash_commands\":[\"compact\",\"fix\"],\"skills\":[\"fix\"],\"agents\":[\"Explore\"],\"mcp_servers\":[{\"name\":\"m\",\"status\":\"connected\"}]}"
        while IFS= read -r line; do
          case "$line" in
            *'"subtype":"initialize"'*) ok "$(rid "$line")" '{"models":[{"value":"fake-model","displayName":"Fake","description":"d","supportedEffortLevels":["low","high"]},{"value":"new-model","displayName":"New"}],"commands":[{"name":"compact","description":"Compact"},{"name":"fix","description":"Fix","argumentHint":"<x>"}]}' ;;
            *'"subtype":"set_model"'*) sleep 0.5; ok "$(rid "$line")" ;;
            *'"subtype":"set_permission_mode"'*) ok "$(rid "$line")" ;;
            *'"subtype":"apply_flag_settings"'*)
              E=$(echo "$line" | sed 's/.*"effortLevel":"\([^"]*\)".*/\1/')
              if [ "$E" = "legacy" ]; then emit "{\"type\":\"control_response\",\"response\":{\"subtype\":\"error\",\"request_id\":\"$(rid "$line")\",\"error\":\"Unsupported control request subtype\"}}"
              else EFFORT=$E; ok "$(rid "$line")"; fi ;;
            *'"subtype":"get_settings"'*) ok "$(rid "$line")" "{\"applied\":{\"effort\":\"$EFFORT\"}}" ;;
            *'"subtype":"stop_task"'*) ok "$(rid "$line")" ;;
            *'"subtype":"cancel_async_message"'*)
              CU=$(echo "$line" | sed 's/.*"message_uuid":"\([^"]*\)".*/\1/')
              if grep -q "$CU" "$D/taken.txt" 2>/dev/null; then ok "$(rid "$line")" '{"cancelled":false}'; else ok "$(rid "$line")" '{"cancelled":true}'; fi ;;
            *'"subtype":"interrupt"'*) ok "$(rid "$line")"; result ;;
            *'"type":"user"'*)
              case "$line" in *'"type":"image"'*) echo "$line" > "$D/image.txt";; esac
              case "$line" in *'"type":"document"'*) echo "$line" > "$D/document.txt";; esac
              UUID=$(echo "$line" | sed 's/.*"uuid":"\([^"]*\)".*/\1/')
              TEXT=$(echo "$line" | sed 's/.*"text":"\([^"]*\)".*/\1/')
              # A prompt sent mid-turn is held by the CLI: no echo and no turn until it is taken up.
              case "$TEXT" in *queued*|*taken*) case "$TEXT" in *taken*) echo "$UUID" >> "$D/taken.txt";; esac; continue;; esac
              emit "{\"type\":\"user\",\"uuid\":\"$UUID\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"echo\"}]},\"parent_tool_use_id\":null}"
              case "$TEXT" in
                *hello*)
                  emit '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m1"}},"parent_tool_use_id":null}'
                  emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}'
                  emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"Hi "}},"parent_tool_use_id":null}'
                  emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"there"}},"parent_tool_use_id":null}'
                  sleep 0.1
                  emit '{"type":"assistant","message":{"id":"m1","content":[{"type":"text","text":"Hi there"}],"usage":{"input_tokens":2,"cache_read_input_tokens":10}},"parent_tool_use_id":null,"uuid":"as-1"}'
                  result ;;
                *tool*)
                  emit '{"type":"assistant","message":{"id":"m2","content":[{"type":"tool_use","id":"toolu_bash","name":"Bash","input":{"command":"ls -la\nmore"}}]},"parent_tool_use_id":null,"uuid":"as-2"}'
                  emit '{"type":"assistant","message":{"id":"m3","content":[{"type":"tool_use","id":"toolu_edit","name":"Edit","input":{"file_path":"/tmp/x.txt","old_string":"a","new_string":"b"}}]},"parent_tool_use_id":null,"uuid":"as-3"}'
                  BIG=$(head -c 20000 /dev/zero | tr '\0' x)
                  emit "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"tool_use_id\":\"toolu_bash\",\"content\":\"$BIG\"}]},\"parent_tool_use_id\":null}"
                  emit '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_edit","content":[{"type":"text","text":"boom"}],"is_error":true}]},"parent_tool_use_id":null}'
                  result ;;
                *agent*)
                  emit '{"type":"assistant","message":{"id":"m4","content":[{"type":"tool_use","id":"toolu_task","name":"Task","input":{"description":"Look around","prompt":"p"}}]},"parent_tool_use_id":null,"uuid":"as-4"}'
                  emit '{"type":"system","subtype":"task_started","task_id":"task-1","task_type":"local_agent","tool_use_id":"toolu_task","description":"Look around"}'
                  emit '{"type":"assistant","message":{"id":"m5","content":[{"type":"text","text":"Inside"}]},"parent_tool_use_id":"toolu_task","uuid":"as-5"}'
                  emit '{"type":"assistant","message":{"id":"m6","content":[{"type":"tool_use","id":"toolu_read","name":"Read","input":{"file_path":"/tmp/y"}}]},"parent_tool_use_id":"toolu_task","uuid":"as-6"}'
                  emit '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_read","content":"file body"}]},"parent_tool_use_id":"toolu_task"}'
                  emit '{"type":"system","subtype":"task_progress","task_id":"task-1","description":"Look around","last_tool_name":"Read","usage":{"total_tokens":99,"tool_uses":1,"duration_ms":5}}'
                  emit '{"type":"system","subtype":"task_notification","task_id":"task-1","status":"completed","summary":"found it"}'
                  emit '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_task","content":"all done"}]},"parent_tool_use_id":null}'
                  result ;;
                *perm*)
                  emit '{"type":"assistant","message":{"id":"m7","content":[{"type":"tool_use","id":"toolu_w","name":"Write","input":{"file_path":"/tmp/z","content":"c"}}]},"parent_tool_use_id":null,"uuid":"as-7"}'
                  emit '{"type":"control_request","request_id":"perm-1","request":{"subtype":"can_use_tool","tool_name":"Write","input":{"file_path":"/tmp/z","content":"c"},"tool_use_id":"toolu_w","permission_suggestions":[{"type":"addRules","rules":[{"toolName":"Write"}]}],"title":"Write z"}}'
                  IFS= read -r answer; echo "$answer" > "$D/answer.txt"
                  emit '{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_w","content":"ok"}]},"parent_tool_use_id":null}'
                  result ;;
                *ask*)
                  emit '{"type":"control_request","request_id":"ask-1","request":{"subtype":"can_use_tool","tool_name":"AskUserQuestion","input":{"questions":[{"question":"Pick one?","header":"Pick","multiSelect":false,"options":[{"label":"A","description":"first"},{"label":"B"}]},{"question":"Which more?","header":"More","multiSelect":true,"options":[{"label":"X"},{"label":"Y"}]}]},"tool_use_id":"toolu_ask"}}'
                  IFS= read -r answer; echo "$answer" > "$D/ask.txt"
                  result ;;
                *compact*)
                  emit '{"type":"system","subtype":"compact_boundary","compact_metadata":{"trigger":"auto","pre_tokens":1234}}'
                  result ;;
                *fail*)
                  emit '{"type":"result","subtype":"error_during_execution","is_error":true,"duration_ms":1,"num_turns":1,"result":"API Error: overloaded","total_cost_usd":0.25,"uuid":"res-fail"}' ;;
                *slow*)
                  emit '{"type":"stream_event","event":{"type":"message_start","message":{"id":"m8"}},"parent_tool_use_id":null}'
                  emit '{"type":"stream_event","event":{"type":"content_block_start","index":0,"content_block":{"type":"text","text":""}},"parent_tool_use_id":null}'
                  emit '{"type":"stream_event","event":{"type":"content_block_delta","index":0,"delta":{"type":"text_delta","text":"working"}},"parent_tool_use_id":null}' ;;
                *)
                  result ;;
              esac ;;
          esac
        done
        """;

    private sealed class Fixture : IAsyncDisposable
    {
        public required PluginRuntime Runtime { get; init; }
        public required string Project { get; init; }
        public required string Directory { get; init; }
        public ConcurrentQueue<ChatPush> Pushes { get; } = new();
        public CancellationTokenSource Stop { get; } = new();

        public async Task<T> Call<P, T>(PluginMethod<P, T> method, P parameters) =>
            PluginJson.Convert<T>(await Runtime.CallAsync(new("claude-code", method.Name, parameters, "chat-client")));

        public void Watch(string chatId)
        {
            var updates = Runtime.SubscribeAsync(new("claude-code", "chatUpdates", new { workspaceId = Project, chatId }, "chat-client"), Stop.Token);
            _ = Task.Run(async () =>
            {
                try { await foreach (var payload in updates) Pushes.Enqueue(PluginJson.Convert<ChatPush>(payload!)); }
                catch (OperationCanceledException) { }
            });
        }

        public async Task<ChatSnapshot> View(string chatId)
        {
            var pushes = await Call(ChatContract.Snapshot, new ChatKey(Project, chatId));
            var header = pushes.OfType<ChatHeaderPush>().Last().Header;
            return new([.. pushes.OfType<ChatItemPush>().Select(push => push.Item)], header);
        }

        public async Task<ChatSnapshot> Until(string chatId, Func<ChatSnapshot, bool> condition, string what)
        {
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(20))
            {
                var view = await View(chatId);
                if (condition(view)) return view;
                await Task.Delay(25);
            }
            throw new InvalidOperationException("Timed out waiting for: " + what);
        }

        public async ValueTask DisposeAsync()
        {
            Stop.Cancel();
            await Runtime.DisposeAsync();
        }
    }

    public static async Task Run(string root)
    {
        ClaudeChatSemanticsChecks.Run();
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(root, "claude-chat");
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
        try { await RunIn(directory); await SharedDraft(Path.Combine(directory, "shared-draft")); }
        finally { foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value); }
    }

    private static async Task<Fixture> Start(string directory, StateHolder holder)
    {
        var project = Path.Combine(directory, "project");
        Directory.CreateDirectory(project);
        var fake = Path.Combine(directory, "fake", "claude");
        Directory.CreateDirectory(Path.GetDirectoryName(fake)!);
        File.WriteAllText(fake, FakeClaude.Replace("\r\n", "\n"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var state = holder.State ??= new HostStateStore(Path.Combine(directory, "state"));
        await state.ChangeAsync([HostStateChange.OpenProject(project)]);
        var runtime = new PluginRuntime(new()
        {
            StateDirectory = Path.Combine(directory, "state"),
            State = state,
            Terminals = new Terminals(new TerminalRef(project, "chat-tab")),
            PublicBaseUrl = () => "http://127.0.0.1:1",
            Builtins = [(ClaudeCodeManifest.Manifest, new ClaudeCodeHost())],
            Worktrees = (_, _) => Task.FromResult<IReadOnlyList<WorktreeInfo>>([])
        });
        await runtime.Start();
        await state.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(new { enabled = true, command = fake, mode = "Chat" }))]);
        await runtime.Schedule();
        Require((await runtime.ListAsync()).Single().Status == PluginStatus.Active, "The Claude Code host activates with the scripted CLI as its command.");
        return new Fixture { Runtime = runtime, Project = project, Directory = directory };
    }

    private static async Task<ChatDraft> DraftOf(Fixture fixture, string chat) =>
        (await fixture.Call(ChatContract.Snapshot, new ChatKey(fixture.Project, chat))).OfType<ChatDraftPush>().Single().Draft;

    // The shared composer draft on the host: revisions, last-writer-wins, the send claim, persistence across a host restart, and bounds.
    private static async Task SharedDraft(string directory)
    {
        var chat = Guid.NewGuid().ToString();
        var other = Guid.NewGuid().ToString();
        var gone = Guid.NewGuid().ToString();
        long consumed;
        await using (var fixture = await Start(directory, new StateHolder()))
        {
            var project = fixture.Project;
            fixture.Watch(chat);
            Require((await DraftOf(fixture, chat)) is { Text: "", Revision: 0 }, "A chat with no stored draft reports an empty draft at revision 0, also before it was opened.");
            var one = await fixture.Call(ChatContract.SetDraft, new ChatSetDraftParams(project, chat, 0, "one", "desk"));
            Require(one is { Text: "one", Revision: 1, Origin: "desk" }, "A write gets the next revision and names its client.");
            var same = await fixture.Call(ChatContract.SetDraft, new ChatSetDraftParams(project, chat, 1, "one", "phone"));
            Require(same.Revision == 1, "Writing the text that is already there makes no new revision.");
            var two = await fixture.Call(ChatContract.SetDraft, new ChatSetDraftParams(project, chat, 0, "two", "phone"));
            Require(two is { Text: "two", Revision: 2, Origin: "phone" }, "Last writer wins whatever base revision it names.");
            for (var wait = 0; wait < 100 && fixture.Pushes.OfType<ChatDraftPush>().Count(push => push.Draft.Revision > 0) < 2; wait++) await Task.Delay(20);
            Require(fixture.Pushes.OfType<ChatDraftPush>().Select(push => (push.Draft.Revision, push.Draft.Origin)).Where(pair => pair.Revision > 0).SequenceEqual([(1L, "desk"), (2L, "phone")]),
                "Every change is pushed on the channel with its revision and origin.");
            var tooLong = false;
            try { await fixture.Call(ChatContract.SetDraft, new ChatSetDraftParams(project, chat, 2, new string('x', ChatContract.DraftLimit + 1), "desk")); }
            catch (Exception error) when (error is PluginCallException or InvalidOperationException) { tooLong = true; }
            Require(tooLong && (await DraftOf(fixture, chat)).Text == "two", "A draft over the limit is refused and the stored one stays.");
            Require((await fixture.Call(ChatContract.SetDraft, new ChatSetDraftParams(project, chat, 2, new string('x', ChatContract.DraftLimit), "desk"))).Revision == 3, "A draft at the limit is stored.");

            // A send claims its revision: the same revision, or an older one, is refused; a send that is not the draft leaves it alone.
            await fixture.Call(ChatContract.Open, new ChatOpenParams(project, chat));
            await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle, "the draft chat to be ready");
            var typed = await fixture.Call(ChatContract.SetDraft, new ChatSetDraftParams(project, chat, 3, "hello draft", "desk"));
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "hello draft") { DraftRevision = typed.Revision, ClientId = "desk" });
            var cleared = await DraftOf(fixture, chat);
            Require(cleared is { Text: "" } && cleared.Revision > typed.Revision && cleared.Origin == "desk", "A send clears the draft with a new revision, naming the sender.");
            foreach (var stale in new[] { typed.Revision, typed.Revision - 1 })
            {
                var refused = "";
                try { await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "hello draft") { DraftRevision = stale, ClientId = "phone" }); }
                catch (Exception error) when (error is PluginCallException or InvalidOperationException) { refused = error.Message; }
                Require(refused.Contains(ChatContract.AlreadySent, StringComparison.Ordinal), $"A send from revision {stale}, already consumed, is refused as already sent: {refused}");
            }
            await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle, "the first send to finish");
            Require((await fixture.View(chat)).Items.OfType<ChatUserItem>().Count(item => item.Text == "hello draft") == 1, "Only one send reached the chat.");
            var newer = await fixture.Call(ChatContract.SetDraft, new ChatSetDraftParams(project, chat, cleared.Revision, "keep this", "desk"));
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "hello plain"));
            Require((await DraftOf(fixture, chat)) is { Text: "keep this" } kept && kept.Revision == newer.Revision, "A send without a draft revision neither checks nor clears the draft.");
            consumed = typed.Revision;

            // A send that fails (no such running chat) releases its claim and keeps the draft.
            var failed = await fixture.Call(ChatContract.SetDraft, new ChatSetDraftParams(project, other, 0, "unsent", "desk"));
            try { await fixture.Call(ChatContract.Send, new ChatSendParams(project, other, "unsent") { DraftRevision = failed.Revision, ClientId = "desk" }); }
            catch (Exception error) when (error is PluginCallException or InvalidOperationException) { }
            Require((await DraftOf(fixture, other)).Text == "unsent", "A failed send keeps the draft.");

            await fixture.Call(ChatContract.SetDraft, new ChatSetDraftParams(project, gone, 0, "goes with its workspace", "desk"));
            await fixture.Call(ChatContract.Close, new ChatKey(project, chat));
            // The save is debounced: it happens shortly after the last change.
            await Task.Delay(SharpRail.Plugins.ClaudeCode.Host.Chat.ChatDrafts.SaveDelay + TimeSpan.FromMilliseconds(700));
        }

        // A host restart: a new state store over the same directory still has the drafts, the revisions and the consumed marker.
        await using (var fixture = await Start(directory, new StateHolder()))
        {
            var project = fixture.Project;
            var restored = await DraftOf(fixture, chat);
            Require(restored.Text == "keep this" && restored.Revision > consumed, "A draft survives a host restart with its revision.");
            Require((await DraftOf(fixture, other)).Text == "unsent", "Every stored draft survives a host restart.");
            var next = await fixture.Call(ChatContract.SetDraft, new ChatSetDraftParams(project, chat, restored.Revision, "after restart", "desk"));
            Require(next.Revision == restored.Revision + 1, "Revisions go on from the stored one after a restart.");
            await fixture.Call(ChatContract.Open, new ChatOpenParams(project, chat));
            await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle, "the restarted chat to be ready");
            var refused = false;
            try { await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "again") { DraftRevision = consumed, ClientId = "phone" }); }
            catch (Exception error) when (error is PluginCallException or InvalidOperationException) { refused = true; }
            Require(refused, "A consumed revision stays consumed after a host restart.");

        }
        StoreBounds();
    }

    // The draft store on its own: persistence round trip, the stored-chat bound, the length bound and a removed workspace's drafts.
    private static void StoreBounds()
    {
        var saved = new Dictionary<string, SharpRail.Plugins.ClaudeCode.Host.Chat.StoredDraft>();
        var pushes = new List<ChatPush>();
        using (var store = new SharpRail.Plugins.ClaudeCode.Host.Chat.ChatDrafts(() => [], all => { saved = all; }, pushes.Add, _ => { }))
        {
            for (var index = 0; index < SharpRail.Plugins.ClaudeCode.Host.Chat.ChatDrafts.MaxChats + 20; index++)
                store.Set("w", "c" + index, "text " + index, "x");
            store.Set("w", "empty", "temp", "x");
            store.Set("w", "empty", "", "x");
            store.Set("other", "kept", "elsewhere", "x");
            Require(store.Get("w", "c0").Text == "text 0" || store.Get("w", "c0").Revision == 0, "The oldest drafts may be dropped, never altered.");
            store.Save();
            Require(saved.Count <= SharpRail.Plugins.ClaudeCode.Host.Chat.ChatDrafts.MaxChats, $"At most {SharpRail.Plugins.ClaudeCode.Host.Chat.ChatDrafts.MaxChats} chats are stored ({saved.Count}).");
            Require(store.Get("w", "c" + (SharpRail.Plugins.ClaudeCode.Host.Chat.ChatDrafts.MaxChats + 19)).Text.StartsWith("text ", StringComparison.Ordinal) && store.Get("other", "kept").Text == "elsewhere",
                "The most recent drafts and non-empty ones are kept.");
            Require(store.Get("w", "empty") is { Text: "" }, "A cleared draft is empty.");
            var before = pushes.Count;
            store.Set("w", "c150", store.Get("w", "c150").Text, "x");
            Require(pushes.Count == before, "Writing the stored text again pushes nothing.");
            store.ForgetWorkspace("w");
            store.Save();
            Require(saved.Keys.All(key => key.StartsWith("other\0", StringComparison.Ordinal)) && saved.Count == 1, "A removed workspace's drafts are dropped and the drop is saved.");
        }
        var json = JsonSerializer.Serialize(saved, PluginJson.Options);
        var back = JsonSerializer.Deserialize<Dictionary<string, SharpRail.Plugins.ClaudeCode.Host.Chat.StoredDraft>>(json, PluginJson.Options)!;
        using var reloaded = new SharpRail.Plugins.ClaudeCode.Host.Chat.ChatDrafts(() => back, _ => { }, _ => { }, _ => { });
        Require(reloaded.Get("other", "kept") is { Text: "elsewhere", Revision: 1 }, "A stored draft round-trips through the state file's JSON.");
    }


    private sealed class StateHolder { public HostStateStore? State { get; set; } }

    private static async Task RunIn(string directory)
    {
        var holder = new StateHolder();
        string chat = Guid.NewGuid().ToString();
        var argsLog = Path.Combine(directory, "fake", "args.log");
        string[] Spawns() => File.Exists(argsLog) ? File.ReadAllLines(argsLog) : [];
        await using (var fixture = await Start(directory, holder))
        {
            var project = fixture.Project;
            fixture.Watch(chat);
            var clock = Stopwatch.StartNew();
            var opened = await fixture.Call(ChatContract.Open, new ChatOpenParams(project, chat) { PermissionMode = "acceptEdits" });
            Require(clock.Elapsed < TimeSpan.FromSeconds(2) && opened.Status is ChatStatus.Starting or ChatStatus.Idle, "Open returns at once with a header; the child starts in the background.");
            var ready = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle && view.Header.Models.Count == 2, "the chat to be ready");
            Require(ready.Header is { Model: "fake-model", Version: "9.9", SessionId: { } } &&
                ready.Header.Commands.Any(command => command is { Name: "fix", Source: "skill", ArgumentHint: "<x>" }) &&
                ready.Header.Models.Any(model => model.Id == "new-model") && ready.Header.McpServers is [{ Name: "m", Status: "connected" }] && ready.Header.Agents.SequenceEqual(["Explore"]),
                "Init and the initialize answer fill the header: model, commands from skills, models, agents, MCP servers.");
            Require(Spawns().Last().Contains("--output-format stream-json", StringComparison.Ordinal) && Spawns().Last().Contains("--session-id " + chat, StringComparison.Ordinal) &&
                Spawns().Last().Contains("--permission-mode acceptEdits", StringComparison.Ordinal) && Spawns().Last().Contains("--append-system-prompt-file", StringComparison.Ordinal) &&
                Spawns().Last().Contains("--permission-prompt-tool=stdio", StringComparison.Ordinal),
                "A new chat starts the CLI under its id with the stream-json flags, the chosen permission mode and the appended prompt.");
            var environment = File.ReadAllText(Path.Combine(directory, "fake", "env.log"));
            Require(environment.Contains("SHARPRAIL_UI_LAUNCH=claude", StringComparison.Ordinal) && environment.Contains("THINKRAIL_MCP_URL=http://127.0.0.1:1", StringComparison.Ordinal) &&
                environment.Contains("/mcp/chat-fixture-token", StringComparison.Ordinal) && environment.Contains("CLAUDE_CODE_SSE_PORT=", StringComparison.Ordinal),
                "The child gets SharpRail's launch marker, its MCP address and the IDE bridge port.");
            Require((await fixture.Call(ChatContract.Open, new ChatOpenParams(project, chat))).SessionId == ready.Header.SessionId && Spawns().Length == 1, "Open is idempotent per chat id.");

            // A streamed answer: partials grow one item, which the complete message then finishes.
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "hello"));
            var answered = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle, "the first answer");
            Require(answered.Items.OfType<ChatAssistantItem>().Single() is { Text: "Hi there", Streaming: false } &&
                answered.Items.OfType<ChatUserItem>().Single() is { Text: "hello", Queued: false } &&
                !answered.Items.OfType<ChatResultItem>().Any() &&
                answered.Header.Usage is { CostUsd: 0.25, ContextTokens: 12, ContextWindow: 200000 },
                "A turn folds into the user item and one assistant item, with no per-turn result row; cost and context go to the header.");
            var assistantId = answered.Items.OfType<ChatAssistantItem>().Single().Id;
            // The channel delivers asynchronously: give the pushes a moment to arrive.
            for (var wait = 0; wait < 100 && !fixture.Pushes.OfType<ChatItemPush>().Any(push => push.Item is ChatAssistantItem { Streaming: false } final && final.Id == assistantId); wait++) await Task.Delay(20);
            Require(fixture.Pushes.OfType<ChatItemPush>().Any(push => push.Item is ChatAssistantItem { Streaming: true } streaming && streaming.Id == assistantId) &&
                fixture.Pushes.OfType<ChatItemPush>().Count(push => push.Item.Id == assistantId) <= 4,
                "Partials reach the view as in-place updates of one item, coalesced.");
            Require(fixture.Pushes.OfType<ChatItemPush>().Count(push => push.Item is ChatUserItem) >= 1 &&
                (await fixture.Call(ChatContract.Snapshot, new ChatKey(project, chat))).Last() is ChatHeaderPush,
                "The snapshot is the items followed by the header, and the channel carried the item pushes.");
            Require(PluginJson.Convert<ChatPush>(JsonSerializer.SerializeToElement(fixture.Pushes.First(), PluginJson.Options)).GetType() == fixture.Pushes.First().GetType(),
                "Pushes keep their kind through the plugin wire encoding that remote hosts use.");
            var rows = await fixture.Call(ClaudeCodeContract.StatusSnapshot, new StatusSnapshotParams(project));
            Require(rows.Single(row => row.TabKey == chat).Status == ClaudeCodeStatus.Done, "A result marks the chat's status Done for badges and notifications.");

            // A tool call and its result, cut at the wire limit with the whole text on request.
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "use a tool"));
            var tools = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle && view.Items.OfType<ChatToolItem>().Count() == 2, "the tool turn");
            var bash = tools.Items.OfType<ChatToolItem>().Single(tool => tool.Name == "Bash");
            var edit = tools.Items.OfType<ChatToolItem>().Single(tool => tool.Name == "Edit");
            Require(bash is { State: ChatToolState.Done, Summary: "ls -la", OutputTruncated: true } && bash.Output!.Length == ChatContract.OutputLimit &&
                edit is { State: ChatToolState.Failed, FilePath: "/tmp/x.txt", Output: "boom" },
                "A tool item carries its summary, state, file path and output cut at the limit.");
            Require((await fixture.Call(ChatContract.ItemDetail, new ChatDetailParams(project, chat, bash.Id))).Text.Length == 20000, "The cut output is served whole by ItemDetail.");

            // A subagent: its messages nest under the Task call, and the task reaches the header.
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "run an agent"));
            var nested = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle, "the subagent turn");
            var task = nested.Items.OfType<ChatToolItem>().Single(tool => tool.Name == "Task");
            Require(task is { TaskId: "task-1", State: ChatToolState.Done, Output: "all done" } &&
                nested.Items.OfType<ChatAssistantItem>().Single(item => item.Text == "Inside").ParentId == "toolu_task" &&
                nested.Items.OfType<ChatToolItem>().Single(tool => tool.Name == "Read").ParentId == "toolu_task" &&
                nested.Header.Tasks is [{ TaskId: "task-1", Kind: ChatTaskKind.Subagent, Status: ChatTaskStatus.Done, ToolUseId: "toolu_task", LastTool: "Read", Tokens: 99 }],
                "Subagent output nests by parent_tool_use_id, and the task notification settles the header task.");
            await fixture.Call(ChatContract.StopTask, new ChatTaskParams(project, chat, "task-1"));

            // A permission request blocks the chat until answered; the answer carries the rule suggestions when remembered.
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "needs perm"));
            var asked = await fixture.Until(chat, view => view.Header.Status == ChatStatus.AwaitingPermission && view.Items.OfType<ChatPermissionItem>().Any(), "the permission request");
            var permission = asked.Items.OfType<ChatPermissionItem>().Single();
            Require(permission is { RequestId: "perm-1", ToolName: "Write", Title: "Write z", Decision: ChatPermissionDecision.Pending, Suggestions: not null } &&
                (await fixture.Call(ClaudeCodeContract.StatusSnapshot, new StatusSnapshotParams(project))).Single(row => row.TabKey == chat).Status == ClaudeCodeStatus.Blocked,
                "A can_use_tool request becomes a pending permission item and blocks the status.");
            await fixture.Call(ChatContract.Answer, new ChatAnswerParams(project, chat, "perm-1", true) { Remember = true });
            var granted = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle, "the permitted turn");
            var answer = File.ReadAllText(Path.Combine(directory, "fake", "answer.txt"));
            Require(granted.Items.OfType<ChatPermissionItem>().Single().Decision == ChatPermissionDecision.Allowed &&
                answer.Contains("\"behavior\":\"allow\"", StringComparison.Ordinal) && answer.Contains("\"request_id\":\"perm-1\"", StringComparison.Ordinal) &&
                answer.Contains("\"updatedPermissions\":[{\"type\":\"addRules\"", StringComparison.Ordinal) && answer.Contains("\"updatedInput\"", StringComparison.Ordinal),
                "Allowing with Remember answers the control request with the input and the CLI's own suggestions.");

            // The model changes in the header only once the CLI confirms; permission mode likewise.
            var switching = fixture.Call(ChatContract.SetModel, new ChatSetParams(project, chat, "new-model"));
            await Task.Delay(150);
            Require(!switching.IsCompleted && (await fixture.View(chat)).Header.Model == "fake-model", "The header keeps the old model while the CLI has not confirmed the switch.");
            Require((await switching).Model == "new-model" && (await fixture.View(chat)).Header.Model == "new-model", "The confirmed switch reaches the header.");
            Require((await fixture.Call(ChatContract.SetPermissionMode, new ChatSetParams(project, chat, "plan"))).PermissionMode == "plan", "A confirmed permission mode reaches the header.");

            // Questions, compaction and failures.
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "please ask"));
            var asking = await fixture.Until(chat, view => view.Header.Status == ChatStatus.AwaitingPermission && view.Items.OfType<ChatQuestionItem>().Any(), "the question");
            var question = asking.Items.OfType<ChatQuestionItem>().Single();
            Require(question is { RequestId: "ask-1", Decision: ChatPermissionDecision.Pending } && question.Questions is [{ Question: "Pick one?", Header: "Pick", MultiSelect: false, Options: [{ Label: "A", Description: "first" }, { Label: "B" }] }, { MultiSelect: true }] &&
                !asking.Items.OfType<ChatPermissionItem>().Any(item => item.RequestId == "ask-1"), "An AskUserQuestion request becomes a typed question item, not a permission.");
            await fixture.Call(ChatContract.AnswerQuestion, new ChatAnswerQuestionParams(project, chat, "ask-1") { Answers = new Dictionary<string, string> { ["Pick one?"] = "A", ["Which more?"] = "X, Y" } });
            var asked2 = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle && view.Items.OfType<ChatQuestionItem>().Single().Decision == ChatPermissionDecision.Allowed, "the answered question");
            var reply = File.ReadAllText(Path.Combine(directory, "fake", "ask.txt"));
            Require(asked2.Items.OfType<ChatQuestionItem>().Single().Answers!["Which more?"] == "X, Y" && reply.Contains("\"behavior\":\"allow\"", StringComparison.Ordinal) &&
                reply.Contains("\"answers\":{\"Pick one?\":\"A\",\"Which more?\":\"X, Y\"}", StringComparison.Ordinal) && reply.Contains("\"questions\":[{\"question\":\"Pick one?\"", StringComparison.Ordinal),
                "Answering sends the CLI its own questions back with the answers keyed by question text.");
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "compact now"));
            var compacted = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle && view.Items.OfType<ChatNoticeItem>().Any(notice => notice.Category == ChatNoticeKinds.Compaction), "the compaction notice");
            Require(compacted.Items.OfType<ChatNoticeItem>().Single(notice => notice.Category == ChatNoticeKinds.Compaction).Text == "Compacted chat · auto · 1k tokens freed", "A compact boundary becomes a compaction notice with the trigger and the size freed.");
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "this will fail"));
            var failed = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle && view.Items.OfType<ChatNoticeItem>().Any(notice => notice.Category == ChatNoticeKinds.Failure), "the failure notice");
            Require(failed.Items.OfType<ChatNoticeItem>().Single(notice => notice.Category == ChatNoticeKinds.Failure) is { Level: ChatNoticeLevel.Error, Subtype: "error_during_execution", Retryable: true, Text: "API Error: overloaded" } &&
                (await fixture.Call(ClaudeCodeContract.StatusSnapshot, new StatusSnapshotParams(project))).Single(row => row.TabKey == chat).Status == ClaudeCodeStatus.Failed,
                "A failed result adds a retryable failure notice and marks the status Failed.");

            // An interrupt ends a turn that has not finished.
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "something slow"));
            var running = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Running && view.Items.OfType<ChatAssistantItem>().Any(item => item.Text == "working" && item.Streaming), "the slow turn");
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "queued while running"));
            Require((await fixture.View(chat)).Items.OfType<ChatUserItem>().Single(item => item.Text == "queued while running").Queued, "A message sent during a turn is marked queued.");
            // Queued messages can be cancelled or edited until the CLI takes them up.
            var queuedItem = (await fixture.View(chat)).Items.OfType<ChatUserItem>().Single(item => item.Text == "queued while running");
            await fixture.Call(ChatContract.Dequeue, new ChatQueuedParams(project, chat, queuedItem.Id));
            for (var wait = 0; wait < 100 && !fixture.Pushes.OfType<ChatRemovePush>().Any(push => push.ItemId == queuedItem.Id); wait++) await Task.Delay(20);
            Require((await fixture.View(chat)).Items.All(item => item.Id != queuedItem.Id) && fixture.Pushes.OfType<ChatRemovePush>().Any(push => push.ItemId == queuedItem.Id),
                "Dequeue cancels the prompt in the CLI and removes its item, with a remove push.");
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "queued first"));
            var first = (await fixture.View(chat)).Items.OfType<ChatUserItem>().Single(item => item.Text == "queued first");
            await fixture.Call(ChatContract.EditQueued, new ChatEditQueuedParams(project, chat, first.Id, "queued second"));
            var edited = await fixture.View(chat);
            Require(edited.Items.All(item => item.Id != first.Id) && edited.Items.OfType<ChatUserItem>().Single(item => item.Text == "queued second").Queued, "Editing a queued message cancels it and queues the new text.");
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "taken already"));
            var taken = (await fixture.View(chat)).Items.OfType<ChatUserItem>().Single(item => item.Text == "taken already");
            try
            {
                await fixture.Call(ChatContract.Dequeue, new ChatQueuedParams(project, chat, taken.Id));
                throw new InvalidOperationException("A taken message was dequeued.");
            }
            catch (PluginCallException) { }
            Require((await fixture.View(chat)).Items.Any(item => item.Id == taken.Id), "A message the CLI already took up stays, and the cancel is refused.");
            // Send now: interrupts the running turn, sends the chosen queued message first and queues the others again behind it in order.
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "queued alpha"));
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "queued beta"));
            var beforeNow = await fixture.View(chat);
            var beta = beforeNow.Items.OfType<ChatUserItem>().Single(item => item.Text == "queued beta");
            Require(beforeNow.Header.CanSendQueuedNow && beforeNow.Header.Status == ChatStatus.Running, "The host offers send-now, and a turn runs.");
            await fixture.Call(ChatContract.SendQueuedNow, new ChatQueuedParams(project, chat, beta.Id));
            var afterNow = await fixture.View(chat);
            var texts = afterNow.Items.OfType<ChatUserItem>().Select(item => item.Text).Where(text => text is "queued alpha" or "queued beta").ToList();
            Require(texts.SequenceEqual(["queued beta", "queued alpha"]) && afterNow.Items.All(item => item.Id != beta.Id) &&
                afterNow.Items.OfType<ChatAssistantItem>().All(item => !item.Streaming),
                "Send now interrupts the turn, then sends the chosen message ahead of the other queued ones: " + string.Join(", ", texts));
            try
            {
                await fixture.Call(ChatContract.SendQueuedNow, new ChatQueuedParams(project, chat, taken.Id));
                throw new InvalidOperationException("A taken message was sent now.");
            }
            catch (PluginCallException) { }
            await fixture.Call(ChatContract.Interrupt, new ChatKey(project, chat));
            var interrupted = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle, "the interrupt");
            Require(interrupted.Items.OfType<ChatAssistantItem>().All(item => !item.Streaming), "An interrupt settles the streaming item.");

            // An image the UI sends as bytes reaches the child as a base64 image block; the transcript keeps the name and not the bytes.
            var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII=");
            await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "hello with a picture") { Attachments = [new ChatAttachment("pasted-1.png", "") { Kind = "image", MediaType = "image/png", Data = png }] });
            var imageLog = Path.Combine(directory, "fake", "image.txt");
            for (var wait = 0; wait < 60 && !File.Exists(imageLog); wait++) await Task.Delay(50);
            var sentLine = File.Exists(imageLog) ? File.ReadAllText(imageLog) : "";
            Require(sentLine.Contains("\"type\":\"image\"", StringComparison.Ordinal) && sentLine.Contains("\"media_type\":\"image/png\"", StringComparison.Ordinal) && sentLine.Contains(Convert.ToBase64String(png), StringComparison.Ordinal),
                "The child receives the pasted image as a base64 image block with its media type: " + sentLine);
            await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle, "the picture turn");
            var pictured = (await fixture.View(chat)).Items.OfType<ChatUserItem>().Last(item => item.Attachments is { Count: > 0 });
            Require(pictured.Attachments![0].Name == "pasted-1.png" && pictured.Attachments[0].Data is null, "The transcript keeps the attachment's name, not its bytes.");
            var refused = false;
            try { await fixture.Call(ChatContract.Send, new ChatSendParams(project, chat, "too big") { Attachments = [new ChatAttachment("huge.png", "") { MediaType = "image/png", Data = new byte[ChatAttachment.MaxDataBytes + 1] }] }); }
            catch (PluginCallException) { refused = true; }
            Require(refused, "An attachment over the cap is refused with a message.");

            // The effort changes live: no restart, no notice, and the header follows once the CLI has confirmed it.
            var before = Spawns().Length;
            var effort = await fixture.Call(ChatContract.SetEffort, new ChatSetParams(project, chat, "high"));
            Require(ready.Header.Models.Single(model => model.Id == "fake-model").Efforts.SequenceEqual(["low", "high"]), "Models carry the efforts the CLI reports.");
            Require(effort.Effort == "high" && effort.Status == ChatStatus.Idle && Spawns().Length == before, "A new effort is applied live: the child is not restarted.");
            Require(!(await fixture.View(chat)).Items.OfType<ChatNoticeItem>().Any(notice => notice.Text.Contains("Restarting", StringComparison.Ordinal)), "The live path leaves no restart notice.");

            // A CLI that rejects the request gets the restart on the same session, with the notice.
            var legacy = await fixture.Call(ChatContract.SetEffort, new ChatSetParams(project, chat, "legacy"));
            await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle && view.Header.Effort == "legacy", "the restart");
            Require(Spawns().Length == before + 1 && Spawns().Last().Contains("--effort legacy", StringComparison.Ordinal) && Spawns().Last().Contains("--session-id " + chat, StringComparison.Ordinal), "A rejected live change restarts the child on the same session.");
            Require((await fixture.View(chat)).Items.OfType<ChatNoticeItem>().Any(notice => notice.Text.Contains("effort legacy", StringComparison.Ordinal)), "The fallback restart leaves a notice.");
            effort = await fixture.Call(ChatContract.SetEffort, new ChatSetParams(project, chat, "high"));
            Require(effort.Effort == "high" && Spawns().Length == before + 1, "The restarted child takes the live change again.");

            // One session, one child: a second tab cannot resume a session another tab of the workspace is running.
            var runningSession = (await fixture.View(chat)).Header.SessionId!;
            var duplicate = "";
            try { await fixture.Call(ChatContract.Open, new ChatOpenParams(project, "second-" + Guid.NewGuid().ToString("N")[..6]) { ResumeSessionId = runningSession }); }
            catch (PluginCallException error) { duplicate = error.Message; }
            Require(duplicate.Contains("already open in another tab", StringComparison.Ordinal) && duplicate.Contains(chat, StringComparison.Ordinal), "Resuming a session another tab runs is refused with the chat that has it: " + duplicate);
            var listed = (await fixture.Call(ChatContract.Sessions, new ChatSessionsParams(project))).Sessions.FirstOrDefault(info => info.SessionId == runningSession);
            Require(listed is null || listed.ChatId == chat && listed.StartedIn == "chat", "The session list names the chat that runs a session and says a chat started it.");

            var sessionId = (await fixture.View(chat)).Header.SessionId!;
            await fixture.Call(ChatContract.Close, new ChatKey(project, chat));
            Require((await fixture.View(chat)).Header.Status == ChatStatus.Exited, "Closing ends the chat's child.");
            var grandchild = int.Parse(File.ReadAllText(Path.Combine(directory, "fake", "grandchild.pid")).Trim());
            var gone = false;
            for (var wait = 0; wait < 40 && !gone; wait++)
            {
                try { using var survivor = Process.GetProcessById(grandchild); gone = survivor.HasExited; }
                catch (ArgumentException) { gone = true; }
                if (!gone) await Task.Delay(50);
            }
            Require(gone, "Closing takes the child's whole process group, a grandchild it started included.");
            Require((await fixture.Call(ChatContract.Files, new ChatFilesParams(project, "x"))).Paths is not null && (await fixture.Call(ChatContract.Sessions, new ChatSessionsParams(project))).Sessions is not null,
                "The sessions and file lookups answer.");

            // @ path completion lists one host directory: directories first, prefix-filtered, hidden only on a dot, ~ against the home.
            var listed2 = Path.Combine(directory, "complete");
            Directory.CreateDirectory(Path.Combine(listed2, "Beta"));
            Directory.CreateDirectory(Path.Combine(listed2, "alpha"));
            Directory.CreateDirectory(Path.Combine(listed2, ".hid"));
            File.WriteAllText(Path.Combine(listed2, "aardvark.txt"), "");
            File.WriteAllText(Path.Combine(listed2, "zed.txt"), "");
            async Task<string> Complete(string prefix) => string.Join(",", (await fixture.Call(ChatContract.CompletePath, new ChatPathParams(project, chat, prefix))).Entries.Select(entry => entry.Name + (entry.IsDirectory ? "/" : "")));
            var slash = listed2.Replace('\\', '/') + "/";
            Require(await Complete(slash) == "alpha/,Beta/,aardvark.txt,zed.txt", "Path completion lists directories first, then files, hiding dot entries: " + await Complete(slash));
            Require(await Complete(slash + "A") == "alpha/,aardvark.txt" && await Complete(slash + ".") == ".hid/" && await Complete(slash + "q") == "", "The last segment filters by prefix ignoring case; a dot shows hidden entries.");
            Require(await Complete(slash + "missing/") == "" && await Complete(slash + "zed.txt/") == "", "A missing directory or a file lists nothing, without an error.");
            var realHome = Environment.GetEnvironmentVariable("HOME");
            try
            {
                Environment.SetEnvironmentVariable("HOME", directory);
                Require(await Complete("~/complete/z") == "zed.txt", "~ expands against the host user's home.");
            }
            finally { Environment.SetEnvironmentVariable("HOME", realHome); }

            // Revival without a transcript starts afresh under the recorded id, with the recorded choices.
            await fixture.Call(ChatContract.Open, new ChatOpenParams(project, chat));
            var revivedBare = await fixture.Until(chat, view => view.Header.Status == ChatStatus.Idle, "the revival without a transcript");
            Require(Spawns().Last().Contains("--session-id " + sessionId, StringComparison.Ordinal) && !Spawns().Last().Contains("--resume", StringComparison.Ordinal) &&
                Spawns().Last().Contains("--model new-model", StringComparison.Ordinal) && Spawns().Last().Contains("--effort high", StringComparison.Ordinal) &&
                Spawns().Last().Contains("--permission-mode plan", StringComparison.Ordinal), "A chat whose session left no transcript restarts under its id with its recorded model, effort and mode.");
            await fixture.Call(ChatContract.Close, new ChatKey(project, chat));
        }

        // An app restart: a new runtime has no sessions, only the persisted record and the transcript on disk.
        var home = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")!;
        var resumed = Guid.NewGuid().ToString();
        await using (var fixture = await Start(directory, holder))
        {
            var project = fixture.Project;
            await fixture.Call(ChatContract.Open, new ChatOpenParams(project, resumed) { Model = "fake-model" });
            await fixture.Until(resumed, view => view.Header.Status == ChatStatus.Idle, "a fresh chat");
            await fixture.Call(ChatContract.Close, new ChatKey(project, resumed));
        }
        var transcript = AgentResume.ExpectedTranscriptPath(Path.Combine(directory, "project"), resumed)!;
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        File.WriteAllLines(transcript,
        [
            """{"type":"user","uuid":"u-1","parentUuid":null,"isSidechain":false,"message":{"role":"user","content":"first question"}}""",
            """{"type":"assistant","uuid":"a-1","parentUuid":"u-1","message":{"id":"r1","content":[{"type":"text","text":"first answer"}]}}""",
            """{"type":"assistant","uuid":"a-2","parentUuid":"a-1","message":{"id":"r2","content":[{"type":"tool_use","id":"toolu_agent","name":"Task","input":{"description":"dig"}}]}}""",
            """{"type":"user","uuid":"u-2","parentUuid":"a-2","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"toolu_agent","content":"agent report"}]}}""",
            """{"type":"user","uuid":"u-3","parentUuid":"u-2","isSidechain":false,"message":{"role":"user","content":"second question"}}""",
            """{"type":"assistant","uuid":"a-3","parentUuid":"u-3","message":{"id":"r3","content":[{"type":"text","text":"second answer"}]}}"""
        ]);
        var subagents = Path.Combine(Path.GetDirectoryName(transcript)!, resumed, "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllText(Path.Combine(subagents, "agent-a1.meta.json"), """{"toolUseId":"toolu_agent"}""");
        File.WriteAllLines(Path.Combine(subagents, "agent-a1.jsonl"),
        [
            """{"type":"user","isSidechain":true,"uuid":"s-0","message":{"role":"user","content":"dig into it"}}""",
            """{"type":"assistant","isSidechain":true,"uuid":"s-1","message":{"id":"sa1","content":[{"type":"text","text":"digging"}]}}"""
        ]);
        await using (var fixture = await Start(directory, holder))
        {
            var project = fixture.Project;
            fixture.Watch(resumed);
            await fixture.Call(ChatContract.Open, new ChatOpenParams(project, resumed));
            var revived = await fixture.Until(resumed, view => view.Header.Status == ChatStatus.Idle && view.Items.OfType<ChatAssistantItem>().Count() == 3, "the revival with a transcript");
            Require(Spawns().Last().Contains("--resume " + resumed, StringComparison.Ordinal) && !Spawns().Last().Contains("--session-id", StringComparison.Ordinal) &&
                Spawns().Last().Contains("--model fake-model", StringComparison.Ordinal), "A chat whose transcript exists resumes its session with the recorded model after a restart.");
            Require(revived.Items.OfType<ChatUserItem>().Select(item => item.Text).SequenceEqual(["first question", "second question"]) &&
                revived.Items.OfType<ChatToolItem>().Single() is { Name: "Task", Output: "agent report", State: ChatToolState.Done } &&
                revived.Items.OfType<ChatAssistantItem>().Single(item => item.Text == "digging").ParentId == "toolu_agent",
                "The transcript replays into the conversation, tool results and the subagent's nested output included.");
            Require(fixture.Pushes.OfType<ChatResetPush>().Any(), "A revival emits a reset before the replayed items.");
            var firstItem = fixture.Pushes.ToArray().SkipWhile(push => push is not ChatResetPush).Skip(1).First();
            Require(firstItem is ChatItemPush, "Replayed items follow the reset.");
            var statuses = await fixture.Call(ClaudeCodeContract.StatusSnapshot, new StatusSnapshotParams(project));
            Require(statuses.Any(row => row.TabKey == resumed), "A revived chat reports status like a launch of SharpRail's own.");

            // Rewind keeps what preceded the prompt and continues from there under a new session.
            await fixture.Call(ChatContract.Rewind, new ChatRewindParams(project, resumed, "u-3"));
            var rewound = await fixture.Until(resumed, view => view.Header.Status == ChatStatus.Idle && view.Items.OfType<ChatAssistantItem>().All(item => item.Text != "second answer"), "the rewind");
            Require(Spawns().Last().Contains("--resume " + resumed, StringComparison.Ordinal) && Spawns().Last().Contains("--fork-session", StringComparison.Ordinal) &&
                Spawns().Last().Contains("--resume-session-at u-2", StringComparison.Ordinal) && rewound.Items.OfType<ChatUserItem>().Single().Text == "first question",
                "A rewind replays up to the prompt and resumes the CLI at the message before it, as a fork.");
            var forked = await fixture.Call(ChatContract.Fork, new ChatRewindParams(project, resumed, "u-3"));
            await fixture.Call(ChatContract.Open, new ChatOpenParams(project, forked.ChatId));
            await fixture.Until(forked.ChatId, view => view.Header.Status == ChatStatus.Idle, "the fork");
            Require(forked.ChatId != resumed && Spawns().Last().Contains("--fork-session", StringComparison.Ordinal) && Spawns().Last().Contains("--resume-session-at u-2", StringComparison.Ordinal),
                "A fork is a new chat that branches from the source session before the chosen prompt.");
        }
        Require(Directory.Exists(home), "The checks ran against their own Claude home.");
    }
}