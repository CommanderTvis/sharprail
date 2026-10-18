using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Context;
using SharpRail.Plugins.Api;
using SharpRail.UI.Panels;

namespace SharpRail.Checks;

internal static partial class ContextChecks
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

    private static async Task WorkflowTransportAsync(IProjectServices host, HostStateStore state, string workspace, string stateDirectory, Uri? endpoint)
    {
        var created = new ConcurrentDictionary<string, WorkflowRuntime>();
        state.ContextRuntimeOverride = (root, log, _) => Task.FromResult<IContextRuntime>(created.GetOrAdd(root, _ => new(root, log)));
        var tools = ContextMcpTools.Create(state, workspace);
        Require(tools.Select(tool => tool.Name).Order().SequenceEqual(new[] { "context_index", "context_search", "context_status" }), "Context exposed consent/login/enable or arbitrary native tools.");
        var search = tools.Single(tool => tool.Name == "context_search");
        var index = tools.Single(tool => tool.Name == "context_index");
        var disabled = await search.Call(new() { ["query"] = "locate behavior" }, default);
        Require(JsonNode.Parse(disabled.Text)!["state"]!.ToString() == "disabled" && created.IsEmpty, "Searching enabled or started a disabled Context workspace.");
        var terminal = new TerminalRef(workspace, "context-agent");
        state.SetTerminalAgent(terminal, new("codex", "fixture-agent") { LaunchedByUi = true });
        Require(created.IsEmpty, "A disabled workspace indexed on launch.");
        state.SetTerminalAgent(terminal, null);
        Require((await host.ContextAsync(new(workspace, "enable"))).Enabled, "Explicit user enablement did not persist.");
        Require(new HostStateStore(stateDirectory).Current.ContextWorkspaces.Contains(workspace), "Context enablement disappeared on host reopen.");
        if (endpoint is not null)
        {
            using var remoteState = new RemoteStateAdapter(endpoint, "context-fixture");
            Require((await remoteState.GetStateAsync()).ContextWorkspaces.Contains(workspace), "Remote state lost Context workspace enablement.");
        }
        state.SetTerminalAgent(terminal, new("codex", "manual-inside-terminal") { LaunchedByUi = false });
        Require(created.IsEmpty, "A manual/outside agent triggered Context indexing.");
        state.SetTerminalAgent(terminal, new("codex", "ui-launch") { LaunchedByUi = true });
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!created.TryGetValue(workspace, out var runtime) || runtime.Indexes == 0) await Task.Delay(10, deadline.Token);
        var active = created[workspace];
        var reopened = new HostStateStore(stateDirectory) { ContextConsentOverride = state.ContextConsentOverride };
        WorkflowRuntime? resumedRuntime = null;
        reopened.ContextRuntimeOverride = (path, log, _) => Task.FromResult<IContextRuntime>(resumedRuntime = new(path, log));
        var savedAgent = reopened.Current.TerminalAgents.Single(agent => agent.Terminal == terminal).Record;
        reopened.SetTerminalAgent(terminal, savedAgent);
        while (resumedRuntime?.Indexes != 1) await Task.Delay(10, deadline.Token);
        await reopened.StopLanguagesAsync();
        Require(resumedRuntime.Disposed, "A restored agent launch did not get a fresh owned index lifecycle.");
        await index.Call(new(), deadline.Token);
        state.SetTerminalAgent(terminal, new("codex", "ui-launch") { LaunchedByUi = true, Model = "changed" });
        Require(active.Indexes == 1 && (await host.ContextAsync(new(workspace))).State == "indexing", "Indexing was blocking, duplicated or restarted by a status update.");
        Require(JsonNode.Parse((await search.Call(new() { ["query"] = "locate behavior" }, deadline.Token)).Text)!["state"]!.ToString() == "indexing", "Pending indexing claimed usable search results.");
        active.SignedIn = true; active.Indexed.TrySetResult();
        while ((await host.ContextAsync(new(workspace))).State == "indexing") await Task.Delay(10, deadline.Token);
        var reply = JsonNode.Parse((await search.Call(new() { ["query"] = "locate behavior", ["path"] = ".", ["limit"] = 7 }, deadline.Token)).Text)!;
        var result = reply["results"]!;
        Require(result["workspace"]!.ToString() == workspace && result["limit"]!.GetValue<int>() == 7, "Semantic search lost workspace routing or its prewritten arguments.");
        active.SignedIn = false;
        Require(JsonNode.Parse((await search.Call(new() { ["query"] = "retry while signed out" }, deadline.Token)).Text)!["state"]!.ToString() == "requiresLogin", "An authentication failure was not actionable.");
        Require((await host.ContextAsync(new(workspace, "login"))).Command == "fixture-context-login", "User-only sign-in did not provide its terminal command.");
        active.SignedIn = true;
        Require(JsonNode.Parse((await search.Call(new() { ["query"] = "retry after sign-in" }, deadline.Token)).Text)!["state"]!.ToString() == "ready", "Search did not recover after sign-in.");
        var other = Path.Combine(workspace, "context-other"); Directory.CreateDirectory(other);
        Require(!(await host.ContextAsync(new(other))).Enabled && !state.Current.ContextWorkspaces.Contains(other), "Cloud enablement was inherited into another workspace.");
        try { await search.Call(new() { ["query"] = "x", ["workspace"] = other }, default); throw new InvalidOperationException("Context search crossed workspaces."); }
        catch (UnauthorizedAccessException) { }
        try { await search.Call(new() { ["query"] = "x", ["path"] = "../outside" }, default); throw new InvalidOperationException("Context path escaped the workspace."); }
        catch (UnauthorizedAccessException) { }
        try { await search.Call(new() { ["query"] = "x", ["operation"] = "enable" }, default); throw new InvalidOperationException("An agent supplied a user-only operation."); }
        catch (ArgumentException) { }
        var logs = await host.ContextAsync(new(workspace, "logs"));
        Require(logs.Data.Contains("fixture indexing", StringComparison.Ordinal) && !logs.Data.Contains("fixture-private-secret", StringComparison.Ordinal), "Context logs lost progress or exposed credentials.");
        if (endpoint is not null)
        {
            using var http = new HttpClient { BaseAddress = endpoint }; http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "context-fixture");
            var call = JsonSerializer.Serialize(new { jsonrpc = "2.0", id = 1, method = "tools/call", @params = new { name = "context_search", arguments = new { query = "locate behavior" } } });
            using var response = await http.PostAsync("/mcp", new StringContent(call, Encoding.UTF8, "application/json"));
            var frame = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            Require(response.IsSuccessStatusCode && JsonNode.Parse(frame["result"]!["content"]![0]!["text"]!.GetValue<string>())!["state"]!.ToString() == "ready", "Production headless MCP did not forward the Context search.");
        }
        var waiting = host.ContextAsync(new(workspace, "search", Query: "wait")).AsTask();
        await active.SearchEntered.Task.WaitAsync(deadline.Token);
        await host.ContextAsync(new(workspace, "disable")).AsTask().WaitAsync(deadline.Token);
        Require((await waiting.WaitAsync(deadline.Token)).State == "stopped", "Disabling did not cancel an in-flight semantic search.");
        Require(active.Disposed && !(await host.ContextAsync(new(workspace))).Enabled && created.Count == 1, "Disabling did not dispose only the owned runtime or leaked into other workspaces.");
        state.SetTerminalAgent(terminal, null);
        await Task.WhenAll(state.StopLanguagesAsync().AsTask(), state.StopLanguagesAsync().AsTask());
        Console.WriteLine("PASS Context workflow: persisted user enablement, launch indexing, deduplication, namespace search, scope validation, remote state, logs redaction and teardown");
    }

    private static void WorkflowUi(string root)
    {
        var storage = Path.Combine(root, "context-workflow-ui"); Directory.CreateDirectory(storage);
        using var handler = new Handler(_ => Task.FromResult("fixture"u8.ToArray())); using var http = new HttpClient(handler);
        var bundle = new ContextBundle("workflow", "fixture", new("https://download.jetbrains.com/fixture"), Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("fixture"u8)));
        var state = new HostStateStore(Path.Combine(storage, "state"));
        state.ContextConsentOverride = new(Path.Combine(storage, "context"), http, bundle);
        var host = new LocalProjectAdapter(new ProjectServices(storage, state));
        void Await(Task task) { E2E.E2eWorkspace.Until(() => task.IsCompleted); task.GetAwaiter().GetResult(); }
        WorkflowRuntime? runtime = null; string? login = null;
        state.ContextRuntimeOverride = (workspace, log, _) => Task.FromResult<IContextRuntime>(runtime = new(workspace, log));
        var panel = new LanguagePanel(host, storage, _ => Task.CompletedTask, contextLogin: command => { login = command; return Task.CompletedTask; });
        var window = new Window { Width = 520, Height = 600, Content = panel, Background = SharpRail.Plugins.UI.Kit.Ui.Sidebar, Foreground = SharpRail.Plugins.UI.Kit.Ui.TextBrush, FontFamily = SharpRail.Plugins.UI.Kit.Ui.InterfaceFont, FontSize = 14, RequestedThemeVariant = SharpRail.Plugins.UI.Kit.Ui.Theme.IsLight ? Avalonia.Styling.ThemeVariant.Light : Avalonia.Styling.ThemeVariant.Dark }; window.Show();
        T Find<T>(Window owner, string name) where T : Control => owner.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        void Button(Window owner, string name) => Find<Button>(owner, name).RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        void Toggle()
        {
            var checkbox = Find<CheckBox>(window, "ContextEnabled"); checkbox.BringIntoView(); window.UpdateLayout();
            var point = checkbox.TranslatePoint(new Point(10, checkbox.Bounds.Height / 2), window)!.Value;
            window.MouseDown(point, Avalonia.Input.MouseButton.Left); window.MouseUp(point, Avalonia.Input.MouseButton.Left);
        }
        try
        {
            E2E.E2eWorkspace.Until(() => Find<TextBlock>(window, "ContextState").Text?.StartsWith("Not enabled", StringComparison.Ordinal) == true);
            Toggle(); E2E.E2eWorkspace.Until(() => window.OwnedWindows.Any());
            var confirmation = window.OwnedWindows.Single(); confirmation.Close(false);
            E2E.E2eWorkspace.Until(() => Find<CheckBox>(window, "ContextEnabled").IsChecked == false);
            Require(!state.Current.ContextWorkspaces.Contains(storage) && runtime is null, "Canceling cloud enablement changed host state or started Context.");
            Toggle(); E2E.E2eWorkspace.Until(() => window.OwnedWindows.Any());
            confirmation = window.OwnedWindows.Single(); Button(confirmation, "EnableContextConfirm");
            E2E.E2eWorkspace.Until(() => window.OwnedWindows.OfType<ContextConsentWindow>().Any());
            var terms = window.OwnedWindows.OfType<ContextConsentWindow>().Single();
            E2E.E2eWorkspace.Until(() => Find<CheckBox>(terms, "ConsentCheck").IsEnabled);
            Require(!state.Current.ContextWorkspaces.Contains(storage) && runtime is null && Find<CheckBox>(terms, "ConsentCheck").IsChecked == false,
                "Missing vendor consent enabled or executed Context instead of opening review.");
            terms.Close();
            Await(Task.Run(async () => { var review = await host.ContextConsentAsync(new()); await host.ContextConsentAsync(new("acceptAndDownload", review.ArtifactId, review.TermsSha256)); }));
            E2E.E2eWorkspace.Until(() => Find<CheckBox>(window, "ContextEnabled").IsEnabled);
            Toggle(); E2E.E2eWorkspace.Until(() => window.OwnedWindows.Any());
            confirmation = window.OwnedWindows.Single(); Button(confirmation, "EnableContextConfirm");
            E2E.E2eWorkspace.Until(() => state.Current.ContextWorkspaces.Contains(storage) && Find<Button>(window, "ContextIndex").IsEnabled);
            Require(runtime is null, "Enabling started an index before a launch or explicit Index now.");
            Button(window, "ContextLogin"); E2E.E2eWorkspace.Until(() => login is not null);
            Require(login == "fixture-context-login", "Sign in did not use the dedicated terminal callback.");
            E2E.E2eWorkspace.Until(() => Find<Button>(window, "ContextIndex").IsEnabled);
            Button(window, "ContextIndex"); E2E.E2eWorkspace.Until(() => runtime?.Indexes == 1);
            if (Environment.GetEnvironmentVariable("SHARPRAIL_CONTEXT_CAPTURE") == "1")
            {
                E2E.E2eWorkspace.Until(() => Find<CheckBox>(window, "ContextEnabled").IsEnabled && Equals(Find<Button>(window, "ContextSetup").Content, "View installation…"));
                Directory.CreateDirectory(".bench/context-consent");
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                using var image = window.CaptureRenderedFrame();
                image!.Save(".bench/context-consent/workspace.png", Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
            Button(window, "ContextLogs"); E2E.E2eWorkspace.Until(() => window.OwnedWindows.OfType<LanguageLogsWindow>().Any());
            var logs = window.OwnedWindows.OfType<LanguageLogsWindow>().Single();
            E2E.E2eWorkspace.Until(() => Find<TextBox>(logs, "ServerOutput").Text?.Contains("fixture indexing", StringComparison.Ordinal) == true);
            Require(!Find<TextBox>(logs, "ServerOutput").Text!.Contains("fixture-private-secret", StringComparison.Ordinal), "The logs window exposed a secret."); logs.Close();
            E2E.E2eWorkspace.Until(() => Find<Button>(window, "ContextStop").IsEnabled);
            Button(window, "ContextStop"); E2E.E2eWorkspace.Until(() => runtime!.Disposed);
            E2E.E2eWorkspace.Until(() => Find<CheckBox>(window, "ContextEnabled").IsEnabled);
            Toggle(); E2E.E2eWorkspace.Until(() => !state.Current.ContextWorkspaces.Contains(storage));
            Require(Find<CheckBox>(window, "ContextEnabled").IsChecked == false, "Disabling was not immediately visible.");
        }
        finally { window.Close(); Await(state.StopLanguagesAsync().AsTask()); }
        Console.WriteLine("PASS Context workspace UI: cloud confirmation/cancel, host enablement, sign-in terminal, explicit indexing, redacted logs and owned stop");
    }
}