using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.AirContext;
using SharpRail.Plugins.AirContext.Host;
using SharpRail.Plugins.Api;

namespace SharpRail.Checks;

internal static partial class AirContextChecks
{
    private sealed class WorkflowRuntime(string workspace, Action<string> log) : IContextRuntime
    {
        internal int Indexes;
        internal bool Disposed;
        internal bool SignedIn;
        internal readonly TaskCompletionSource SearchEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Indexed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> PrepareLoginAsync(CancellationToken token) => Task.FromResult("fixture-context-login");
        public async Task IndexAsync(CancellationToken token)
        {
            Interlocked.Increment(ref Indexes);
            log("fixture indexing token=fixture-private-secret\n");
            await Indexed.Task.WaitAsync(token);
            if (!SignedIn) throw new ContextLoginRequiredException();
        }
        public async Task<string> SearchAsync(string query, string path, int limit, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!SignedIn) throw new ContextLoginRequiredException();
            if (query == "wait") { SearchEntered.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            return JsonSerializer.Serialize(new { workspace, query, path, limit, file = "src/example.cs", line = 4 });
        }
        public Task<string> StatusAsync(CancellationToken token) => Task.FromResult("{\"indexed\":true}");
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private static JsonNode Reply((string Text, bool Error) result) => JsonNode.Parse(result.Text)!;

    private static async Task WorkflowTransportAsync(Rig rig, Fixtures fixtures, string workspace)
    {
        var created = new ConcurrentDictionary<string, WorkflowRuntime>();
        fixtures.Value.Runtime = (root, log, _) => Task.FromResult<IContextRuntime>(created.GetOrAdd(root, _ => new(root, log)));
        var tools = rig.Tools(workspace);
        Require(tools.Select(tool => tool.Name).Order().SequenceEqual(new[] { "context_index", "context_search", "context_status" }), "Context exposed consent/login/enable or arbitrary native tools.");
        var search = tools.Single(tool => tool.Name == "context_search");
        var index = tools.Single(tool => tool.Name == "context_index");
        var disabled = await search.Call(new() { ["query"] = "locate behavior" }, default);
        Require(Reply(disabled)["state"]!.ToString() == "disabled" && created.IsEmpty, "Searching enabled or started a disabled Context workspace.");
        var terminal = new TerminalRef(workspace, "context-agent");
        rig.Runtime.SetAgentRecord(terminal, new("codex", "fixture-agent") { LaunchedByUi = true });
        Require(created.IsEmpty, "A disabled workspace indexed on launch.");
        rig.Runtime.SetAgentRecord(terminal, null);
        Require((await rig.Workspace(workspace, "enable")).Enabled, "Explicit user enablement did not persist.");
        Require(Enabled(rig.StateDirectory).Contains(workspace), "Context enablement disappeared from the plugin's state.");
        if (rig.Endpoint is not null)
        {
            using var other = new RemotePluginAdapter(rig.Endpoint, "context-fixture");
            Require(PluginJson.Convert<ContextOutcome>(await other.CallAsync(new(AirContextContract.Id, "workspace", new ContextCall(workspace), "second-client"))).Enabled, "A second remote client lost Context workspace enablement.");
        }
        rig.Runtime.SetAgentRecord(terminal, new("codex", "manual-inside-terminal") { LaunchedByUi = false });
        Require(created.IsEmpty, "A manual/outside agent triggered Context indexing.");
        rig.Runtime.SetAgentRecord(terminal, new("codex", "ui-launch") { LaunchedByUi = true });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!created.TryGetValue(workspace, out var runtime) || runtime.Indexes == 0) await Task.Delay(10, deadline.Token);
        var active = created[workspace];
        if (rig.Endpoint is null)
        {
            // A restored agent launch gets a fresh owned index lifecycle on a host that reopens the same state.
            WorkflowRuntime? resumedRuntime = null;
            fixtures.Value.Runtime = (path, log, _) => Task.FromResult<IContextRuntime>(resumedRuntime = new(path, log));
            await using var reopened = await Rig.Open(workspace, rig.StateDirectory, remote: false);
            var savedAgent = reopened.State.Current.TerminalAgents.Single(agent => agent.Terminal == terminal).Record;
            reopened.Runtime.SetAgentRecord(terminal, savedAgent);
            while (resumedRuntime?.Indexes != 1) await Task.Delay(10, deadline.Token);
            await reopened.Runtime.DisposeAsync();
            Require(resumedRuntime.Disposed, "A restored agent launch did not get a fresh owned index lifecycle.");
            fixtures.Value.Runtime = (root, log, _) => Task.FromResult<IContextRuntime>(created.GetOrAdd(root, _ => new(root, log)));
        }
        await index.Call(new(), deadline.Token);
        rig.Runtime.SetAgentRecord(terminal, new("codex", "ui-launch") { LaunchedByUi = true, Model = "changed" });
        Require(active.Indexes == 1 && (await rig.Workspace(workspace)).State == "indexing", "Indexing was blocking, duplicated or restarted by a status update.");
        Require(Reply(await search.Call(new() { ["query"] = "locate behavior" }, deadline.Token))["state"]!.ToString() == "indexing", "Pending indexing claimed usable search results.");
        active.SignedIn = true; active.Indexed.TrySetResult();
        while ((await rig.Workspace(workspace)).State == "indexing") await Task.Delay(10, deadline.Token);
        var reply = Reply(await search.Call(new() { ["query"] = "locate behavior", ["path"] = ".", ["limit"] = 7 }, deadline.Token));
        var result = reply["results"]!;
        Require(result["workspace"]!.ToString() == workspace && result["limit"]!.GetValue<int>() == 7, "Semantic search lost workspace routing or its prewritten arguments.");
        active.SignedIn = false;
        Require(Reply(await search.Call(new() { ["query"] = "retry while signed out" }, deadline.Token))["state"]!.ToString() == "requiresLogin", "An authentication failure was not actionable.");
        Require((await rig.Workspace(workspace, "login")).Command == "fixture-context-login", "User-only sign-in did not provide its terminal command.");
        active.SignedIn = true;
        Require(Reply(await search.Call(new() { ["query"] = "retry after sign-in" }, deadline.Token))["state"]!.ToString() == "ready", "Search did not recover after sign-in.");
        var other2 = Path.Combine(workspace, "context-other"); Directory.CreateDirectory(other2);
        Require(!(await rig.Workspace(other2)).Enabled && !Enabled(rig.StateDirectory).Contains(other2), "Cloud enablement was inherited into another workspace.");
        var crossed = await search.Call(new() { ["query"] = "x", ["workspace"] = other2 }, default);
        Require(crossed.Error && crossed.Text.Contains("bound to their calling", StringComparison.Ordinal), "Context search crossed workspaces.");
        var escaped = await search.Call(new() { ["query"] = "x", ["path"] = "../outside" }, default);
        Require(escaped.Error && escaped.Text.Contains("outside this workspace", StringComparison.Ordinal), "Context path escaped the workspace.");
        var userOnly = await search.Call(new() { ["query"] = "x", ["operation"] = "enable" }, default);
        Require(userOnly.Error, "An agent supplied a user-only operation.");
        try { await rig.Workspace(workspace, "search"); throw new InvalidOperationException("A client reached the agent-only search through the user-interface method."); }
        catch (PluginCallException) { }
        var logs = await rig.Workspace(workspace, "logs");
        Require(logs.Data.Contains("fixture indexing", StringComparison.Ordinal) && !logs.Data.Contains("fixture-private-secret", StringComparison.Ordinal), "Context logs lost progress or exposed credentials.");
        if (rig.Endpoint is not null)
        {
            using var http = new HttpClient { BaseAddress = rig.Endpoint }; http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "context-fixture");
            var call = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "context_search", arguments = new { query = "locate behavior" } } });
            using var response = await http.PostAsync("/mcp", new StringContent(call, Encoding.UTF8, "application/json"));
            var frame = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Require(response.IsSuccessStatusCode && JsonNode.Parse(frame["result"]!["content"]![0]!["text"]!.GetValue<string>())!["state"]!.ToString() == "ready", "Production headless MCP did not forward the Context search.");
        }
        var waiting = search.Call(new() { ["query"] = "wait" }, deadline.Token);
        await active.SearchEntered.Task.WaitAsync(deadline.Token);
        await rig.Workspace(workspace, "disable").WaitAsync(deadline.Token);
        Require(Reply(await waiting.WaitAsync(deadline.Token))["state"]!.ToString() == "stopped", "Disabling did not cancel an in-flight semantic search.");
        Require(active.Disposed && !(await rig.Workspace(workspace)).Enabled && created.Count == 1, "Disabling did not dispose only the owned runtime or leaked into other workspaces.");
        rig.Runtime.SetAgentRecord(terminal, null);
        Console.WriteLine("PASS Context workflow: persisted user enablement, launch indexing, deduplication, namespace search, scope validation, remote state, logs redaction and teardown");
    }

    /// <summary>The workspaces the plugin's state file lists.</summary>
    private static string[] Enabled(string stateDirectory)
    {
        var path = Path.Combine(stateDirectory, PluginIdentity.StateFile(AirContextContract.Id, AirContextContract.WorkspacesState));
        return File.Exists(path) ? JsonSerializer.Deserialize<ContextWorkspaces>(File.ReadAllText(path), PluginJson.Options)!.Paths.ToArray() : [];
    }

    private static void WorkflowUi(string root)
    {
        var storage = Path.Combine(root, "context-workflow-ui"); Directory.CreateDirectory(storage);
        using var handler = new Handler(_ => Task.FromResult("fixture"u8.ToArray())); using var http = new HttpClient(handler);
        var bundle = new ContextBundle("workflow", "fixture", new("https://download.jetbrains.com/fixture"), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("fixture"u8)));
        using var fixtures = new Fixtures();
        fixtures.Value.Consent = directory => new(directory, http, bundle);
        WorkflowRuntime? runtime = null;
        fixtures.Value.Runtime = (workspace, log, _) => Task.FromResult<IContextRuntime>(runtime = new(workspace, log));
        using var app = new E2E.E2eWorkspace(storage);
        var window = app.Window;
        void Await(Task task) { E2E.E2eWorkspace.Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        TurnOn(app);
        var plugins = app.PluginRuntime!;
        T Find<T>(Window owner, string name) where T : Control => owner.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        void Button(Window owner, string name) => Find<Button>(owner, name).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        void Toggle()
        {
            var checkbox = Find<CheckBox>(window, "ContextEnabled"); checkbox.BringIntoView(); window.UpdateLayout();
            var point = checkbox.TranslatePoint(new Point(10, checkbox.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left); window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        }
        string[] EnabledNow() => Enabled(app.State!.DirectoryPath!);
        var ownedAtStart = window.OwnedWindows.Count;
        IReadOnlyList<Window> Dialogs() => [.. window.OwnedWindows.Skip(ownedAtStart)];
        Reveal(app);
        var workspace = app.Window.WorkspaceRoot;
        try
        {
            E2E.E2eWorkspace.Until(() => window.GetLogicalDescendants().OfType<TextBlock>().Any(control => control.Name == "ContextState" && control.Text?.StartsWith("Not enabled", StringComparison.Ordinal) == true));
            Toggle(); E2E.E2eWorkspace.Until(() => Dialogs().Count > 0);
            var confirmation = Dialogs().Single(); confirmation.Close(false);
            E2E.E2eWorkspace.Until(() => Find<CheckBox>(window, "ContextEnabled").IsChecked == false);
            Require(!EnabledNow().Contains(workspace) && runtime is null, "Canceling cloud enablement changed host state or started Context.");
            Toggle(); E2E.E2eWorkspace.Until(() => Dialogs().Count > 0);
            confirmation = Dialogs().Single(); Button(confirmation, "EnableContextConfirm");
            E2E.E2eWorkspace.Until(() => window.OwnedWindows.OfType<SharpRail.Plugins.AirContext.UI.ConsentWindow>().Any());
            var terms = window.OwnedWindows.OfType<SharpRail.Plugins.AirContext.UI.ConsentWindow>().Single();
            E2E.E2eWorkspace.Until(() => Find<CheckBox>(terms, "ConsentCheck").IsEnabled);
            Require(!EnabledNow().Contains(workspace) && runtime is null && Find<CheckBox>(terms, "ConsentCheck").IsChecked == false,
                "Missing vendor consent enabled or executed Context instead of opening review.");
            terms.Close();
            Await(Task.Run(async () =>
            {
                var review = PluginJson.Convert<ContextConsentResult>(await plugins.CallAsync(new(AirContextContract.Id, "consent", new ContextConsentRequest(), "ui")));
                await plugins.CallAsync(new(AirContextContract.Id, "consent", new ContextConsentRequest("acceptAndDownload", review.ArtifactId, review.TermsSha256), "ui"));
            }));
            E2E.E2eWorkspace.Until(() => Find<CheckBox>(window, "ContextEnabled").IsEnabled);
            Toggle(); E2E.E2eWorkspace.Until(() => Dialogs().Count > 0);
            confirmation = Dialogs().Single(); Button(confirmation, "EnableContextConfirm");
            E2E.E2eWorkspace.Until(() => EnabledNow().Contains(workspace) && Find<Button>(window, "ContextIndex").IsEnabled);
            Require(runtime is null, "Enabling started an index before a launch or explicit Index now.");
            var terminals = app.Window.Layout.Tabs(app.Center).Count(tab => tab.Kind == "terminal");
            Button(window, "ContextLogin"); E2E.E2eWorkspace.Until(() => app.Window.Layout.Tabs(app.Center).Count(tab => tab.Kind == "terminal") == terminals + 1);
            E2E.E2eWorkspace.Until(() => Find<Button>(window, "ContextIndex").IsEnabled);
            Button(window, "ContextIndex"); E2E.E2eWorkspace.Until(() => runtime?.Indexes == 1);
            Button(window, "ContextLogs"); E2E.E2eWorkspace.Until(() => window.OwnedWindows.OfType<SharpRail.Plugins.AirContext.UI.LogsWindow>().Any());
            var logs = window.OwnedWindows.OfType<SharpRail.Plugins.AirContext.UI.LogsWindow>().Single();
            E2E.E2eWorkspace.Until(() => Find<TextBox>(logs, "ServerOutput").Text?.Contains("fixture indexing", StringComparison.Ordinal) == true);
            Require(!Find<TextBox>(logs, "ServerOutput").Text!.Contains("fixture-private-secret", StringComparison.Ordinal), "The logs window exposed a secret."); logs.Close();
            E2E.E2eWorkspace.Until(() => Find<Button>(window, "ContextStop").IsEnabled);
            Button(window, "ContextStop"); E2E.E2eWorkspace.Until(() => runtime!.Disposed);
            E2E.E2eWorkspace.Until(() => Find<CheckBox>(window, "ContextEnabled").IsEnabled);
            Toggle(); E2E.E2eWorkspace.Until(() => !EnabledNow().Contains(workspace));
            Require(Find<CheckBox>(window, "ContextEnabled").IsChecked == false, "Disabling was not immediately visible.");
        }
        finally { foreach (var dialog in Dialogs()) dialog.Close(); }
        Console.WriteLine("PASS Context workspace UI: cloud confirmation/cancel, host enablement, sign-in terminal, explicit indexing, redacted logs and owned stop");
    }
}