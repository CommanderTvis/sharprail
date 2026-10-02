using System.Net;
using System.Text.Json;
using System.Threading.Channels;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Blueprint;
using SharpRail.Plugins.Blueprint.Host;
using SharpRail.Plugins.SpecDialect;

using static SharpRail.Checks.E2E.E2eWorkspace;
using static SharpRail.Checks.E2E.WorkspaceFixture;

namespace SharpRail.Checks;

internal static class BlueprintChecks
{
    private const string Document = "Intro.\n\n!control select language\n= Python — most conventional\n- Haskell — strongest types\n\n!control multi deploy-as\n[x] Docker image — most portable\n[ ] Nix flake — most reproducible\n";

    public static void RunUi(string root)
    {
        var project = Path.Combine(root, "blueprint-ui");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "BLUEPRINT.md"), Document);
        using var app = new E2eWorkspace(project);
        Until(() => app.Workbench.PluginRegistry.Active.Contains("blueprint"));
        app.Workbench.PluginRegistry.AddLauncher("fixture", new("claude", "Claude Code", "terminal", _ => ":", () => new(true)));
        var opening = app.Window.OpenDocumentAsync("BLUEPRINT.md", true);
        Until(() => opening.IsCompleted); opening.GetAwaiter().GetResult();
        Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "Blueprint"));
        var pane = app.Find<UserControl>("Blueprint");
        Until(() => pane.GetLogicalDescendants().OfType<Control>().Count(control => control.Name == "BlueprintControl") == 2);
        var passage = pane.GetLogicalDescendants().OfType<UserControl>().First(control => control.Name == "BlueprintProse");
        app.Click(passage.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "Edit"));
        var input = passage.GetLogicalDescendants().OfType<TextBox>().Single();
        input.Text = "Edited through the reader's panel.";
        input.Focus();
        Require(ReferenceEquals(app.Window.FocusManager?.GetFocusedElement(), input), "The Blueprint text field owns keyboard focus while editing.");
        Press(input, Key.Enter, RawInputModifiers.Control);
        Until(() => pane.GetLogicalDescendants().OfType<Border>().Any(control => control.Name == "Edits" && control.IsVisible));
        Require(File.ReadAllText(Path.Combine(project, "BLUEPRINT.md")).StartsWith("Intro.", StringComparison.Ordinal), "A UI passage edit is staged.");
        Require(ReferenceEquals(pane, app.Find<UserControl>("Blueprint")), "A state update keeps the companion pane mounted.");
        app.Click(pane.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Confirm"));
        Until(() => File.ReadAllText(Path.Combine(project, "BLUEPRINT.md")).StartsWith("Edited through the reader's panel.", StringComparison.Ordinal));
        var checkbox = pane.GetLogicalDescendants().OfType<CheckBox>().Single(control => Equals(control.Tag, "nix-flake"));
        app.Click(checkbox);
        Until(() => File.ReadAllText(Path.Combine(project, "BLUEPRINT.md")).Contains("[x] Nix flake", StringComparison.Ordinal));
        // blueprint-watch.spec.ts: a terminal author writes the file itself; only the host's watcher can bring it to the pane.
        string PaneText() => string.Concat(pane.GetLogicalDescendants().OfType<SelectableTextBlock>()
            .SelectMany(block => block.Inlines?.OfType<Avalonia.Controls.Documents.Run>() ?? []).Select(run => run.Text));
        File.WriteAllText(Path.Combine(project, "BLUEPRINT.md"), "An author that reports to nobody.\n\n" + Document["Intro.\n\n".Length..]);
        Until(() => PaneText().Contains("An author that reports to nobody.", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(project, "BLUEPRINT.md"), "An author that reports to nobody, twice.\n\n" + Document["Intro.\n\n".Length..]);
        Until(() => PaneText().Contains("An author that reports to nobody, twice.", StringComparison.Ordinal));
        Require(ReferenceEquals(pane, app.Find<UserControl>("Blueprint")), "An external write updates the mounted pane in place.");
        Console.WriteLine("PASS fork plugins/blueprint/blueprint-watch.spec.ts (pane): a terminal author's write reaches the pane through the watcher alone");
        app.Click(pane.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Raw"));
        Until(() => app.Window.Layout.Tabs(app.Center).Any(tab => tab.Path == "BLUEPRINT.md" && tab.Kind != "viewer"));
        Require(app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)).Any(tab => tab.Kind == "terminal" && tab.Id == "blueprint-author"),
            "Blueprint keeps a visible author terminal beside its raw source.");
        Console.WriteLine("PASS Blueprint UI: file redirects to author/companion, staged prose, confirm, checkbox, raw source and kept pane identity");
        BlueprintStartChecks.Run(root);
        BlueprintStartChecks.ClaudeGate(root);
        BlueprintRecoveryChecks.Run(root);
        BlueprintWorktreeStartChecks.Run(root);
    }

    public static async Task Run(string root)
    {
        Format();
        var directory = Path.Combine(root, "blueprint");
        var project = Path.Combine(directory, "project");
        Directory.CreateDirectory(project);
        var state = new HostStateStore(Path.Combine(directory, "state"));
        var terminals = new CapturedTerminals();
        await state.ChangeAsync([HostStateChange.OpenProject(project)]);
        PluginRuntime Create() => new(new()
        {
            StateDirectory = Path.Combine(directory, "state"),
            State = state,
            Terminals = terminals,
            Builtins = [(SpecDialectManifest.Manifest, new SpecDialectHost()), (BlueprintContract.Manifest, new BlueprintHost())],
            Worktrees = (_, _) => Task.FromResult<IReadOnlyList<WorktreeInfo>>([])
        });
        await using (var runtime = Create())
        {
            await runtime.Start();
            async Task<T> Call<P, T>(PluginMethod<P, T> method, P parameters) => PluginJson.Convert<T>(
                await runtime.CallAsync(new("blueprint", method.Name, parameters, "client")));
            var opened = await Call(BlueprintContract.Open, new(project, new BlueprintIdea("  an app to sell cats  "), BlueprintAgentId.Claude));
            Require(opened.State is { Phase: BlueprintPhase.Awaiting, Author: null, Brief: "an app to sell cats" } && opened.State.Doc.Blocks.Count == 0,
                "A new blueprint is awaiting the author with its normalized brief.");
            Require(opened.Opening.Contains("an app to sell cats", StringComparison.Ordinal) && opened.SystemPrompt.Contains("!control multi", StringComparison.Ordinal),
                "Opening carries the exact Blueprint instructions and interactive syntax.");
            await using var frames = new Frames(runtime.SubscribeAsync(new("blueprint", "changed", new BlueprintScope(project), "client")));
            File.WriteAllText(Path.Combine(project, "BLUEPRINT.md"), Document);
            await Call(BlueprintContract.SetAuthor, new(project, new BlueprintTerminalAuthor("author")));
            var ready = await frames.Next(frame => frame.State?.Phase == BlueprintPhase.Ready);
            Require(ready.State!.Changes.Count == 0 && BlueprintFormat.Controls(ready.State.Doc).Count == 2, "First sight of the file is ready without spurious changes.");
            await Call(BlueprintContract.Select, new(project, "language", "haskell"));
            var selected = await frames.Next(frame => BlueprintFormat.Controls(frame.State!.Doc)[0].SelectedIds.Contains("haskell"));
            Require(File.ReadAllText(Path.Combine(project, "BLUEPRINT.md")).Contains("= Haskell — strongest types", StringComparison.Ordinal) &&
                BlueprintFormat.Controls(selected.State!.Doc)[0].Locked, "Selecting a control writes it and locks the reader's choice.");
            Require(terminals.Writes.Last() == (new TerminalRef(project, "author"), "I changed BLUEPRINT.md from the panel:\n- language is now \"Haskell\"\n\nRe-read the file and bring the rest of the document back into line with that. Keep those changes exactly as they are, keep every control id byte-identical, and write the result back to BLUEPRINT.md. Answer here in one line — do not paste the document.\r"),
                "The host delivers the exact reconciliation text to its author, without an attached UI.");
            await Call(BlueprintContract.Select, new(project, "deploy-as", "nix-flake"));
            var multi = await frames.Next(frame => BlueprintFormat.Controls(frame.State!.Doc)[1].SelectedIds.Count == 2);
            Require(BlueprintFormat.Controls(multi.State!.Doc)[1].SelectedIds.SequenceEqual(["docker-image", "nix-flake"]), "Checkboxes toggle in document order.");
            await Call(BlueprintContract.Edit, new(project, new BlueprintProseTarget("prose-0"), "We are shipping this on Nix."));
            var staged = await frames.Next(frame => frame.State?.PendingEdits.Count == 1);
            Require(staged.State!.Doc.Blocks.OfType<BlueprintProse>().First().Text == "We are shipping this on Nix." &&
                File.ReadAllText(Path.Combine(project, "BLUEPRINT.md")).StartsWith("Intro.", StringComparison.Ordinal), "Text is staged without writing the file.");
            await Call(BlueprintContract.Edit, new(project, new BlueprintProseTarget("prose-0"), "We are shipping this on Nix and Docker."));
            var restaged = await frames.Next(frame => frame.State?.PendingEdits.FirstOrDefault()?.After.EndsWith("Docker.", StringComparison.Ordinal) == true);
            Require(restaged.State!.PendingEdits.Single().Before == "Intro.", "Repeated edits preserve the original text.");
            await Call(BlueprintContract.ConfirmEdits, new(project));
            await frames.Next(frame => frame.State?.PendingEdits.Count == 0);
            Require(File.ReadAllText(Path.Combine(project, "BLUEPRINT.md")).Contains("We are shipping this on Nix and Docker.", StringComparison.Ordinal), "Confirm writes the staged text.");
            Require(terminals.Writes.Last().Text.Contains("a passage now reads: We are shipping this on Nix and Docker.", StringComparison.Ordinal), "Confirm delivers the passage change host-side.");
            await Call(BlueprintContract.Edit, new(project, new BlueprintProseTarget("prose-0"), "Throw this away."));
            await frames.Next(frame => frame.State?.PendingEdits.Count == 1);
            await Call(BlueprintContract.DiscardEdits, new(project));
            var reverted = await frames.Next(frame => frame.State?.PendingEdits.Count == 0);
            Require(reverted.State!.Doc.Blocks.OfType<BlueprintProse>().First().Text == "We are shipping this on Nix and Docker.", "Revert rereads disk.");
            var check = runtime.McpTools(null, project).Single(tool => tool.Name == "blueprint_check");
            var report = await check.Call(new System.Text.Json.Nodes.JsonObject(), CancellationToken.None);
            Require(!report.Error && report.Text.Contains("2 controls, 0 notes", StringComparison.Ordinal), "The Blueprint tool reports the rendered controls.");
            terminals.Lifecycle!(new TerminalAgentChanged(new(project, "author"), new("claude", "claude") { SessionId = "conversation-1" }));
            await frames.Next(frame => frame.State?.Author is BlueprintTerminalAuthor { AgentSessionId: "conversation-1" });
            state.SetTerminalAgent(new(project, "author"), new("claude", "claude") { SessionId = "conversation-1" });
            Require(terminals.RevivePrefill!(new(project, "author")) is { Submit: true }, "The recorded author contributes submit-on-revival.");
        }
        await using (var restored = Create())
        {
            await restored.Start();
            async Task<T> Call<P, T>(PluginMethod<P, T> method, P parameters) => PluginJson.Convert<T>(
                await restored.CallAsync(new("blueprint", method.Name, parameters, "client")));
            var resumed = await Call(BlueprintContract.Get, new(project));
            Require(resumed.State is { Brief: "an app to sell cats", Author: BlueprintTerminalAuthor { AgentSessionId: "conversation-1" } },
                "The author session and brief survive a host restart.");
            await Call(BlueprintContract.Close, new(project));
            Require((await Call(BlueprintContract.Get, new(project))).State is null && File.Exists(Path.Combine(project, "BLUEPRINT.md")),
                "Close forgets the blueprint while leaving the file.");
        }
        Console.WriteLine("PASS fork Blueprint sessions: awaiting, author, selection, staged text, confirm, revert, tool, persistence and close");
        await Remote(Path.Combine(directory, "remote"));
    }

    private static async Task Remote(string directory)
    {
        var project = Path.Combine(directory, "project");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "BLUEPRINT.md"), Document);
        await using var server = RemoteServer.Create(project, IPAddress.Loopback, 0, "blueprint", Path.Combine(directory, "state"));
        await server.StartAsync();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var state = new RemoteStateAdapter(address, "blueprint");
            using var plugins = new RemotePluginAdapter(address, "blueprint");
            await state.ChangeAsync([HostStateChange.OpenProject(project)]);
            var runtime = server.Services.GetRequiredService<PluginRuntime>();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!(await plugins.ListAsync()).Any(entry => entry is { Id: "blueprint", Status: PluginStatus.Active }) && DateTime.UtcNow < deadline)
                await Task.Delay(25);
            Require((await plugins.ListAsync()).Any(entry => entry is { Id: "blueprint", Status: PluginStatus.Active }), "The remote Blueprint activates.");
            async Task<T> Call<P, T>(PluginMethod<P, T> method, P parameters) => PluginJson.Convert<T>(await plugins.CallAsync(new("blueprint", method.Name, parameters, "client")));
            var opened = await Call(BlueprintContract.Open, new(project, new BlueprintProduct(), BlueprintAgentId.Claude));
            Require(opened.State.Phase == BlueprintPhase.Ready, "An existing blueprint opens through gRPC.");
            await Call(BlueprintContract.SetAuthor, new(project, new BlueprintTerminalAuthor("author", "remote-session")));
            await Call(BlueprintContract.Edit, new(project, new BlueprintOptionAxisTarget("language", "python"), "available everywhere"));
            await Call(BlueprintContract.ConfirmEdits, new(project));
            var remote = await Call(BlueprintContract.Get, new(project));
            var local = PluginJson.Convert<BlueprintChangedPayload>(await runtime.CallAsync(new("blueprint", "get", new BlueprintScope(project), "client")));
            Require(JsonSerializer.Serialize(remote, PluginJson.Options) == JsonSerializer.Serialize(local, PluginJson.Options) &&
                BlueprintFormat.Controls(remote.State!.Doc)[0].Options[0].Axis == "available everywhere", "Polymorphic edits and state match local answers over gRPC.");
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS Blueprint transport: polymorphic authors, sources, edits and snapshots over gRPC");
    }

    private sealed class CapturedTerminals : IPluginTerminalSeams
    {
        public List<(TerminalRef Terminal, string Text)> Writes { get; } = [];
        public Func<TerminalRef, IReadOnlyDictionary<string, string>>? EnvironmentContributor { get; set; }
        public Action<TerminalEvent>? Lifecycle { get; set; }
        public Func<TerminalRef, TerminalPrefill?>? RevivePrefill { get; set; }
        public Action<string, TerminalRef?>? SessionClosed { get; set; }
        public string Token(TerminalRef terminal) => "fixture-token";
        public TerminalRef? ForToken(string token) => null;
        public void Write(TerminalRef terminal, string data) => Writes.Add((terminal, data));
        public IReadOnlyList<TerminalProcess> List() => [];
        public string? WorkspaceForProcess(int pid) => null;
    }

    private static void Format()
    {
        var parsed = BlueprintFormat.Read(Document);
        Require(parsed.Notes.Count == 0 && BlueprintFormat.Serialize(parsed.Doc) == Document, "The documented control syntax round-trips.");
        var chosen = BlueprintReconcile.ApplySelection(parsed.Doc, "language", "haskell");
        var rewrite = BlueprintFormat.Parse(Document.Replace("- Haskell — strongest types\n", "", StringComparison.Ordinal));
        var kept = BlueprintReconcile.CarryOverLocks(chosen, rewrite);
        Require(BlueprintFormat.Controls(kept)[0] is { Locked: true } control && control.Options.Any(option => option.Id == "haskell") && control.SelectedIds.SequenceEqual(["haskell"]),
            "A rewrite cannot remove or change a locked choice.");
        var renamed = BlueprintReconcile.ApplyTextEdit(chosen, new BlueprintOptionLabelTarget("language", "haskell"), "Python");
        Require(BlueprintFormat.Controls(renamed)[0].SelectedIds.SequenceEqual(["python-2"]), "Renaming a selected option moves its identity without colliding.");
        Require(BlueprintFormat.Read("!control multi targets\n[").Doc.Blocks.OfType<BlueprintControlBlock>().Single().Control.Pending,
            "An unfinished streaming option is a pending control.");
        var frontmatter = "---\nid: blueprint\ntype: goal-and-requirements\ntitle: A project\n---\n";
        var withMetadata = BlueprintFormat.Parse(frontmatter + Document);
        Require(BlueprintFormat.BlockLines(withMetadata)["prose-0"].StartLine == 6, "Serialized spans include all five frontmatter lines.");
        var changed = BlueprintReconcile.Diff(parsed.Doc, chosen);
        Require(changed.Single() is BlueprintControlReselected { From: "Python", To: "Haskell" }, "An author's rewritten decision reports its change.");
        Console.WriteLine("PASS fork Blueprint format/reconcile: round-trip, streamed partials, locked choices, renaming, spans and changes");
    }

    private sealed class Frames : IAsyncDisposable
    {
        private readonly Channel<BlueprintChangedPayload> frames = Channel.CreateUnbounded<BlueprintChangedPayload>();
        private readonly CancellationTokenSource stop = new();
        private readonly Task pump;
        public Frames(IAsyncEnumerable<object?> source) => pump = Pump(source);
        private async Task Pump(IAsyncEnumerable<object?> source)
        {
            try { await foreach (var frame in source.WithCancellation(stop.Token)) frames.Writer.TryWrite(PluginJson.Convert<BlueprintChangedPayload>(frame)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        public async Task<BlueprintChangedPayload> Next(Func<BlueprintChangedPayload, bool> accepts)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true) { var frame = await frames.Reader.ReadAsync(timeout.Token); if (accepts(frame)) return frame; }
        }
        public async ValueTask DisposeAsync() { await stop.CancelAsync(); await pump; stop.Dispose(); }
    }
}