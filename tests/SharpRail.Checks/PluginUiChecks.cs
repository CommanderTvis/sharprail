using System.Net;
using System.Runtime.Loader;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI;
using SharpRail.UI.Panels;
using SharpRail.UI.Plugins;
using SharpRail.UI.State;

namespace SharpRail.Checks;

// The app's plugin runtime: the registry's rules on constructed rosters, the dependency walks Settings › Plugins
// uses, the loader and context against builtin modules on a stand-in host, and the fixture plugin installed from disk
// and driven end to end through the real host, in process and remote. Translates the fork's registry, loader,
// context and PluginsSettings tests and its external-plugin end-to-end spec.
internal static class PluginUiChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        Require(PluginApi.Generation == 1, "PluginApi.Generation changed; bump it deliberately and update this pin.");
        RegistryRules();
        DependencyWalks();
        Icons();
        Loader(Path.Combine(root, "plugin-loader"));
        Fixture(Path.Combine(root, "plugin-fixture-local"), remote: false);
        Fixture(Path.Combine(root, "plugin-fixture-remote"), remote: true);
        Console.WriteLine("PASS plugin UI runtime, Settings › Plugins and the fixture plugin through the in-process and a remote host");
    }

    private static PluginRosterEntry Entry(string id, PluginStatus status, params string[] dependsOn) =>
        new(id, id, "puzzle", "0.0.0", 1, PluginOrigin.Builtin, status) { DependsOn = dependsOn };

    private static PluginRosterEntry WithTools(PluginRosterEntry entry, params string[] tools) => entry with
    {
        Contributes = new() { SideTools = [.. tools.Select(tool => new PluginSideToolContribution(tool, tool.ToUpperInvariant(), "puzzle-2-line", PluginToolSide.Right))] }
    };

    private static PluginRosterEntry WithViewer(PluginRosterEntry entry, params string[] extensions) => entry with
    {
        Contributes = new() { FileViewers = [new() { Extensions = extensions }] }
    };

    private static void RegistryRules()
    {
        // selectToolCatalog: roster order, dormant unless the plugin is active and its row says so.
        var registry = new PluginRegistry();
        registry.SetRoster([WithTools(Entry("b", PluginStatus.Active), "two"), WithTools(Entry("a", PluginStatus.Disabled), "one")]);
        Require(registry.ToolCatalog.Select(tool => tool.Id).SequenceEqual(["plugin:b:two", "plugin:a:one"]), "The tool catalog must follow roster order.");
        Require(registry.ToolCatalog.All(tool => tool.Dormant), "A tool is dormant until its plugin activates.");
        registry.SetActive("b", true);
        Require(!registry.Tool("plugin:b:two")!.Dormant && registry.Tool("plugin:a:one")!.Dormant, "Only an active plugin's tool is live.");
        registry.RegisterManifest(new PluginManifest("c", "C", "puzzle", "1", PluginApi.Generation, 1)
        { Contributes = new() { SideTools = [new("three", "Three", "puzzle", PluginToolSide.Left)] } });
        Require(registry.Tool("plugin:c:three") is { Dormant: true, DefaultSide: PluginToolSide.Left, Label: "Three" },
            "A builtin manifest's tool is known before any roster lists it, so a persisted tab renders its placeholder.");

        // selectSideTool finds the registered control for a tool id.
        var board = new SideToolRegistration("two", _ => new Border());
        registry.AddSideTool("b", board);
        Require(ReferenceEquals(registry.SideTool("plugin:b:two"), board) && registry.SideTool("plugin:a:one") is null, "SideTool must find the registered control.");

        // selectFileViewer: registration order, the manifest's declared extensions when no predicate is given.
        var viewers = new PluginRegistry();
        viewers.SetRoster([WithViewer(Entry("first", PluginStatus.Active), "csv"), WithViewer(Entry("second", PluginStatus.Active), "csv", "tsv")]);
        var first = new FileViewerRegistration(_ => new Border());
        var second = new FileViewerRegistration(_ => new Border());
        viewers.AddFileViewer("first", first, PluginFileRead.Text);
        viewers.AddFileViewer("second", second, PluginFileRead.None);
        Require(ReferenceEquals(viewers.FileViewer("data/table.csv")?.Registration, first), "The first eligible viewer in registration order wins.");
        Require(viewers.FileViewer("data/table.tsv") is { PluginId: "second", Read: PluginFileRead.None }, "A declared extension makes a viewer eligible.");
        Require(viewers.FileViewer("notes.txt") is null, "An undeclared extension has no viewer.");
        // An explicit predicate is preferred over the declaration.
        var picky = new FileViewerRegistration(_ => new Border()) { Matches = path => path.EndsWith("special.csv", StringComparison.Ordinal) };
        var ordered = new PluginRegistry();
        ordered.SetRoster(viewers.Roster);
        ordered.AddFileViewer("second", picky, PluginFileRead.Text);
        ordered.AddFileViewer("first", first, PluginFileRead.Text);
        Require(ReferenceEquals(ordered.FileViewer("special.csv")?.Registration, picky) && ReferenceEquals(ordered.FileViewer("plain.csv")?.Registration, first),
            "A predicate decides its own eligibility; the next eligible registration takes what it declines.");

        // Referential stability: a write replaces only the tables it touches, and names them once.
        var tables = new List<PluginTables>();
        registry.Changed += tables.Add;
        var sideTools = registry.SideTools;
        var launchers = registry.LauncherList;
        registry.AddLauncher("b", new AgentLauncher("agent", "Agent", "terminal-line", _ => "agent", () => new(true)));
        Require(ReferenceEquals(registry.SideTools, sideTools) && !ReferenceEquals(registry.LauncherList, launchers) && registry.LauncherList.Count == 1,
            "Only the written table's list may change.");
        Require(tables.SequenceEqual([PluginTables.Launchers]), "A write raises one change naming its tables.");

        // removePlugin drops every row tagged with that id, across every table, in one write.
        registry.AddSettingsSection("b", new("B", "puzzle", () => new Border()));
        registry.AddCompanion("b", new("note", "Note", "puzzle", _ => true, _ => new Border()));
        registry.AddTabDecorator("b", _ => null);
        registry.AddWorkspaceAction("b", new WorkspaceScopedActionRegistration("go", (_, _) => new Border()));
        registry.AddProjectAction("b", new ProjectScopedActionRegistration("go", _ => new Border()));
        registry.AddTerminalAccessory("b", new(_ => new Border()));
        registry.AddFileIconSlot("b", (_, _) => "file-line");
        registry.AddDocumentLinkSlot("b", (_, _) => null);
        registry.AddFileViewer("b", first, PluginFileRead.Text);
        registry.AddSettingsSection("a", new("A", "puzzle", () => new Border()));
        tables.Clear();
        registry.RemovePlugin("b");
        Require(tables.Count == 1, "RemovePlugin must be one write.");
        Require(registry.SideTools.Count == 0 && registry.Companions.Count == 0 && registry.FileViewers.Count == 0 && registry.TabDecorators.Count == 0 &&
            registry.Launchers.Count == 0 && registry.LauncherList.Count == 0 && registry.WorkspaceActions.Count == 0 && registry.ProjectActions.Count == 0 &&
            registry.TerminalAccessories.Count == 0 && registry.FileIconSlots.Count == 0 && registry.DocumentLinkSlots.Count == 0 && !registry.Active.Contains("b"),
            "RemovePlugin must drop every row of the plugin.");
        Require(registry.SettingsSections.Count == 1 && registry.SettingsSections[0].PluginId == "a", "RemovePlugin must keep other plugins' rows.");
        Require(registry.Tool("plugin:b:two")!.Dormant, "A removed plugin's tool stays in the catalog, dormant.");

        // A throwing predicate answers nothing instead of breaking the reader.
        registry.AddFileIconSlot("a", (_, _) => throw new InvalidOperationException("broken"));
        registry.AddFileIconSlot("c", (path, _) => path.EndsWith(".x", StringComparison.Ordinal) ? "file-line" : null);
        Require(registry.FileIcon("y.x", FileIconKind.File) is ("c", "file-line"), "A throwing slot must fall through to the next resolver.");
        Console.WriteLine("PASS plugin registry tables, catalog, viewer order, stability and removal");
    }

    private static void DependencyWalks()
    {
        Require(PluginRegistry.ToEnable("blueprint", [Entry("blueprint", PluginStatus.Disabled, "spec-dialect"), Entry("spec-dialect", PluginStatus.Active)]).Count == 0,
            "Nothing to enable alongside when every dependency is on.");
        Require(PluginRegistry.ToEnable("c", [Entry("c", PluginStatus.Disabled, "b"), Entry("b", PluginStatus.Disabled, "a"), Entry("a", PluginStatus.Disabled)])
            .Order().SequenceEqual(["a", "b"]), "Disabled dependencies are listed transitively.");
        Require(PluginRegistry.ActiveDependents("spec-dialect", [Entry("spec-dialect", PluginStatus.Active), Entry("blueprint", PluginStatus.Active, "spec-dialect"),
            Entry("claude-code", PluginStatus.Disabled)]).SequenceEqual(["blueprint"]), "Enabled dependents are listed transitively.");
        Require(PluginRegistry.ActiveDependents("spec-dialect", [Entry("spec-dialect", PluginStatus.Active), Entry("blueprint", PluginStatus.Disabled, "spec-dialect")]).Count == 0,
            "A dependent that is itself disabled is not listed.");
        Console.WriteLine("PASS Settings › Plugins dependency enable and disable walks");
    }

    private static void Icons()
    {
        Require(PluginIcons.Glyph("puzzle-2-line") == "puzzle" && PluginIcons.Glyph("terminal-box-line") == "terminal" && PluginIcons.Glyph("no-such-icon") == PluginIcons.Fallback,
            "Remix names map to bundled glyphs and anything else to the puzzle glyph.");
        Require(PluginIcons.Resolve("asset:logo.svg", Entry("bare", PluginStatus.Active)) is Border,
            "An asset icon on a plugin that declares no assets falls back to a glyph.");
        Console.WriteLine("PASS plugin icon resolution and fallbacks");
    }

    private sealed class Probe : PluginUIModule
    {
        internal IPluginUIContext? Context;
        internal int Disposed;
        internal Action<IPluginUIContext>? During;
        public override PluginDisposer? Activate(IPluginUIContext context)
        {
            Context = context;
            context.SideTool(new("panel", _ => new Border { Name = "ProbePanel" }));
            During?.Invoke(context);
            return () => Disposed++;
        }
    }

    private sealed record Scope(string Workspace);
    private sealed record Count(string Workspace, int Value);

    private static void Loader(string root)
    {
        Directory.CreateDirectory(root);
        var profile = new ProfileStore(root + "-profile");
        var store = profile.OpenState();
        var probe = new Probe();
        var mismatched = new Probe();
        var broken = new Probe { During = _ => throw new InvalidOperationException("boom") };
        PluginManifest Manifest(string id, int wire) => new(id, id, "puzzle", "1", PluginApi.Generation, wire)
        { Contributes = new() { SideTools = [new("panel", "Panel", "puzzle", PluginToolSide.Right)] } };
        var probeEntry = new PluginRosterEntry("probe", "Probe", "puzzle", "1", 1, PluginOrigin.Builtin, PluginStatus.Disabled)
        {
            Contributes = Manifest("probe", 1).Contributes,
            Channels = new Dictionary<string, PluginRosterChannel> { ["count"] = new(PluginChannelKind.State, "count", ["workspace"]) }
        };
        var host = new FakePluginHost(new LocalStateAdapter(store), store.Current, probeEntry,
            new("mismatched", "Mismatched", "puzzle", "1", 2, PluginOrigin.Builtin, PluginStatus.Disabled),
            new("broken", "Broken", "puzzle", "1", 1, PluginOrigin.Builtin, PluginStatus.Disabled));
        var state = new SharedState(host, profile.Data.Preferences);
        using var workbench = new Workbench(profile, state, E2eTerminals.Plain, false, null, host);
        var loader = new PluginLoader(workbench, [(Manifest("probe", 1), () => probe), (Manifest("mismatched", 1), () => mismatched), (Manifest("broken", 1), () => broken)]);
        loader.Start();
        void Settle() { E2eWorkspace.Until(() => loader.Idle.IsCompleted); E2eWorkspace.Settle(50); E2eWorkspace.Until(() => loader.Idle.IsCompleted); }
        Settle();
        Require(loader.Registry.Tool("plugin:probe:panel") is { Dormant: true } && probe.Context is null, "A disabled builtin stays unloaded, its tool dormant.");

        _ = state.ChangeAsync(HostStateChange.PluginEnabled("probe", true), HostStateChange.PluginEnabled("mismatched", true), HostStateChange.PluginEnabled("broken", true));
        E2eWorkspace.Until(() => loader.Registry.Active.Contains("probe"));
        Settle();
        Require(loader.Registry.Tool("plugin:probe:panel") is { Dormant: false } && loader.Registry.SideTool("plugin:probe:panel") is not null,
            "Mounting an active roster entry registers its contributions and marks it active.");
        Require(mismatched.Context is null && !loader.Registry.Active.Contains("mismatched"),
            "A builtin whose wire version differs from the host's stays dormant without loading.");
        Require(!loader.Registry.Active.Contains("broken") && loader.Registry.SideTools.All(row => row.PluginId != "broken"),
            "A throwing activation stays dormant and leaves no rows behind.");

        var context = probe.Context!;
        var refused = false;
        try { context.SideTool(new("late", _ => new Border())); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused, "Registering a contribution outside Activate must be refused.");

        // watchHost fires on a real change to the selection, never for the initial value or an unrelated write.
        var seen = new List<(int, int)>();
        var watch = context.WatchHost(projection => projection.AppSettings.PluginPaths.Count, (next, previous) => seen.Add((next, previous)));
        _ = state.ChangeAsync(HostStateChange.Setting("theme", "light"));
        E2eWorkspace.Settle(100);
        Require(seen.Count == 0, "WatchHost must not fire for an unrelated write.");
        _ = state.ChangeAsync(HostStateChange.PluginPaths(["/plugins/extra"]));
        E2eWorkspace.Until(() => seen.Count == 1);
        Require(seen[0] == (1, 0), "WatchHost reports the new and previous selection.");
        watch.Dispose();
        _ = state.ChangeAsync(HostStateChange.PluginPaths([]));
        E2eWorkspace.Settle(100);
        Require(seen.Count == 1, "A disposed WatchHost stops.");

        // A state channel reads its snapshot with the scope as params, then streams; a push for another key is dropped.
        var counts = new List<int>();
        var subscription = context.Subscribe(PluginChannel<Count>.State("count", new PluginMethod<Scope, Count>("count"), "workspace"),
            count => counts.Add(count.Value), new Scope("/w1"));
        E2eWorkspace.Until(() => host.Calls.Any(call => call.PluginId == "probe" && call.Method == "count") && host.Subscriptions > 0);
        var snapshot = host.Calls.Last(call => call.Method == "count");
        Require(JsonSerializer.SerializeToElement(snapshot.Params, PluginJson.Options).GetProperty("workspace").GetString() == "/w1" && snapshot.ClientKey == loader.ClientKey,
            "The snapshot is requested with the scope as params and this app's client key.");
        host.Push("probe", "count", new { workspace = "/w2", value = 7 });
        host.Push("probe", "count", new { workspace = "/w1", value = 3 });
        E2eWorkspace.Until(() => counts.Contains(3));
        Require(!counts.Contains(7), "A push whose key fields differ from the scope must be dropped.");
        subscription.Dispose();
        E2eWorkspace.Until(() => host.Subscriptions == 0);

        // Disabling unmounts: the disposer runs and every row goes; leaving the roster does the same.
        _ = state.ChangeAsync(HostStateChange.PluginEnabled("probe", false));
        E2eWorkspace.Until(() => !loader.Registry.Active.Contains("probe"));
        Settle();
        Require(probe.Disposed == 1 && loader.Registry.SideTools.Count == 0 && loader.Registry.Tool("plugin:probe:panel")!.Dormant,
            "A plugin the roster marks disabled is unmounted, its disposer run and its rows cleared.");
        loader.Stop();
        Console.WriteLine("PASS plugin loader mount, guard, wire mismatch, failure, WatchHost, channel scope and unmount");
    }

    private static T Find<T>(Control scope, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs(); (TopLevel.GetTopLevel(scope) ?? scope).UpdateLayout();
        return scope.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
    }

    private static bool Has(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        return window.GetLogicalDescendants().OfType<Control>().Any(item => item.Name == name && item.IsEffectivelyVisible);
    }

    // The fork's external-plugin.spec.ts against a real host: the fixture plugin installed from disk into the host's
    // state directory, beside a copy built for another generation and one that is later deleted. Run once with the
    // app's own in-process runtime and once against a remote host over gRPC.
    private static void Fixture(string root, bool remote)
    {
        var mode = remote ? "remote" : "local";
        var workspace = Path.Combine(root, "workspace");
        var profileRoot = Path.Combine(root, "profile");
        var stateDirectory = remote ? Path.Combine(root, "host") : profileRoot;
        var plugins = Path.Combine(stateDirectory, "plugins");
        Require(File.Exists(Path.Combine(AppContext.BaseDirectory, "plugin-fixture", "fixture", "SharpRail.PluginFixture.UI.dll")),
            "The fixture plugin's UI half was not built into plugin-fixture/fixture.");
        PluginHostChecks.Install(plugins);
        PluginHostChecks.Install(plugins, "broken", text => text.Replace("\"id\": \"fixture\"", "\"id\": \"broken\"").Replace("\"apiGeneration\": 1", "\"apiGeneration\": 999"));
        PluginHostChecks.Install(plugins, "removable", text => text.Replace("\"id\": \"fixture\"", "\"id\": \"removable\""));
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "sample.fixture"), "fixture-viewer-text");
        File.WriteAllText(Path.Combine(workspace, "notes.txt"), "plain-text-fixture\n");

        const string token = "plugin-fixture";
        var server = remote ? RemoteServer.Create(workspace, IPAddress.Loopback, 0, token, stateDirectory) : null;
        if (server is not null) Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
        try
        {
            using var app = server is null
                ? new E2eWorkspace(workspace, profileRoot: profileRoot, plugins: true)
                : new E2eWorkspace(new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single()),
                    token, workspace, profileRoot, workspace, plugins: true);
            if (remote)
            {
                E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
                app.Click(app.Find<Button>("Tab_files"));
            }
            FixtureScenario(app, plugins);
        }
        finally
        {
            if (server is not null) Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
        }
        Console.WriteLine($"PASS {mode} fixture plugin end to end: install from disk, enable, settings update, contributions, call and channel, disable, refusal and rescan");
    }

    private static void FixtureScenario(E2eWorkspace app, string plugins)
    {
        var window = app.Window;
        var loader = app.Workbench.PluginLoader;
        E2eWorkspace.Until(() => loader.Registry.Roster.Count == 3);

        // The roster lists a discovered external plugin as disabled, with its version and origin; a refused one names both generations.
        window.ShowSettings("Plugins");
        E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
        var row = Find<Border>(settings, "PluginRow_fixture");
        string Texts(Control control) => string.Join(" ", control.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
        Require(Texts(row).Contains("external", StringComparison.Ordinal) && Texts(row).Contains("v1.0.0", StringComparison.Ordinal) &&
            Texts(row).Contains("disabled", StringComparison.Ordinal), "The fixture's row must show external, its version and disabled: " + Texts(row));
        var refusedRow = Find<Border>(settings, "PluginRow___refused:broken");
        Require(Texts(refusedRow).Contains("999", StringComparison.Ordinal) && Texts(refusedRow).Contains("expects 1", StringComparison.Ordinal),
            "A refused row names the plugin's generation and the host's: " + Texts(refusedRow));
        settings.Close();

        // A persisted plugin tool renders its dormant placeholder, with the manifest's label, until the plugin is on.
        window.Layout.RestoreTool("plugin:fixture:board");
        E2eWorkspace.Until(() => Has(window, "PluginToolDormant"));
        var tab = Find<Button>(window, "Tab_plugin_fixture_board");
        Require(Texts(Find<StackPanel>(window, "PluginToolDormant")).Contains("Fixture board is off", StringComparison.Ordinal), "The placeholder names the tool.");
        var assemblies = AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetName().Name == "SharpRail.Plugins.Api");

        // Enabling from Settings › Plugins mounts the UI half read from disk through the host.
        window.ShowSettings("Plugins");
        E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
        settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
        var toggle = Find<ToggleSwitch>(Find<Border>(settings, "PluginRow_fixture"), "PluginToggle");
        toggle.IsChecked = true;
        E2eWorkspace.Until(() => loader.Registry.Active.Contains("fixture"));
        E2eWorkspace.Until(() => Has(settings, "Settings_plugin_fixture_fixture"));
        settings.ShowSection("plugin:fixture:fixture");
        Require(Find<TextBlock>(settings, "FixtureGreeting").Text == "hello", "The fixture's Settings section reads its settings namespace.");
        // UpdateSettingsAsync completes once the host's snapshot carries the value; the host half then answers with it.
        app.Click(Find<Button>(settings, "FixtureGreet"), freshGesture: false);
        E2eWorkspace.Until(() => Find<TextBlock>(settings, "FixtureGreeting").Text == "hey");
        settings.Close();
        E2eWorkspace.Until(() => Has(window, "FixtureBoard"));
        Require(ReferenceEquals(Find<Button>(window, "Tab_plugin_fixture_board"), tab), "Turning live must keep the tool's tab instance.");
        var fixtureAssembly = AppDomain.CurrentDomain.GetAssemblies().Single(assembly => assembly.GetName().Name == "SharpRail.PluginFixture.UI");
        Require(AssemblyLoadContext.GetLoadContext(fixtureAssembly) != AssemblyLoadContext.Default, "The external UI half loads into its own context.");
        Require(AppDomain.CurrentDomain.GetAssemblies().Count(assembly => assembly.GetName().Name == "SharpRail.Plugins.Api") == assemblies,
            "A shared assembly shipped in the plugin directory must not load a second time.");

        // A state subscription round-trips through the host half: snapshot, then the push a bump publishes.
        E2eWorkspace.Until(() => Find<TextBlock>(window, "FixtureCounter").Text == "0");
        app.Click(Find<Button>(window, "FixtureBump"));
        E2eWorkspace.Until(() => Find<TextBlock>(window, "FixtureCounter").Text == "1");

        // A method call round-trips from a workspace-scoped start action, answered with the host half's settings.
        E2eWorkspace.Until(() => Has(window, "FixtureAction"));
        app.Click(Find<Button>(window, "FixtureAction"));
        E2eWorkspace.Until(() => Find<TextBlock>(window, "FixtureActionResult").Text == "hey " + window.WorkspaceRoot);

        // The file viewer claims its declared extension, with the file's text, and the icon slot answers for it.
        _ = window.OpenDocumentAsync("sample.fixture", keep: true);
        E2eWorkspace.Until(() => Has(window, "FixtureViewer"));
        Require(Find<TextBlock>(window, "FixtureViewerText").Text == "fixture-viewer-text", "The viewer receives the file's text.");
        Require(window.Layout.Tabs(window.Layout.View.FocusedCenter).Any(item => item.Kind == "viewer" && item.Path == "sample.fixture"), "The open uses a viewer tab.");

        window.Layout.Select(window.Layout.State.Groups.First(group => group.Tools.Any(item => item.Id == "files")).Id, "files");
        E2eWorkspace.Until(() => ((Grid)app.FileRow("sample.fixture")).Children[0] is ContentControl);
        Require(((Grid)app.FileRow("notes.txt")).Children[0] is Border, "The file-icon slot answers only for the paths it claims; core's glyph stays for the rest.");

        // A terminal shows the accessory row.
        window.Layout.NewTerminal(window.Layout.View.FocusedCenter);
        E2eWorkspace.Until(() => Has(window, "FixtureAccessory"));

        // Disabling from Settings › Plugins unmounts every contribution: placeholders return and calls report disabled.
        window.ShowSettings("Plugins");
        E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
        settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
        Find<ToggleSwitch>(Find<Border>(settings, "PluginRow_fixture"), "PluginToggle").IsChecked = false;
        E2eWorkspace.Until(() => !loader.Registry.Active.Contains("fixture"));
        E2eWorkspace.Until(() => !Has(settings, "Settings_plugin_fixture_fixture"));
        Require(loader.Registry.SideTools.Count == 0 && loader.Registry.SettingsSections.Count == 0 && loader.Registry.FileViewers.Count == 0 &&
            loader.Registry.WorkspaceActions.Count == 0 && loader.Registry.TerminalAccessories.Count == 0 && loader.Registry.FileIconSlots.Count == 0,
            "Disabling must drop every registration of the plugin.");
        E2eWorkspace.Until(() => !Has(window, "FixtureAccessory") && !Has(window, "FixtureViewer"));
        window.Layout.Select(window.Layout.State.Groups.First(group => group.Tools.Any(item => item.Id == "plugin:fixture:board")).Id, "plugin:fixture:board");
        E2eWorkspace.Until(() => Has(window, "PluginToolDormant"));
        var disabled = false;
        try { Task.Run(() => app.Workbench.Plugins!.CallAsync(new("fixture", "echo", new { text = "hi" }, loader.ClientKey)).AsTask()).GetAwaiter().GetResult(); }
        catch (PluginCallException error) when (error.Error == PluginCallError.Disabled) { disabled = true; }
        Require(disabled, "A disabled plugin's method reports disabled.");

        // Rescan re-reads the plugin roots; a plugin deleted from disk leaves the roster.
        Directory.Delete(Path.Combine(plugins, "removable"), recursive: true);
        app.Click(Find<Button>(settings, "PluginsRescan"), freshGesture: false);
        E2eWorkspace.Until(() => !Has(settings, "PluginRow_removable") && Has(settings, "PluginRow_fixture"));
        settings.Close();
        E2eWorkspace.Until(() => !window.OwnedWindows.Any());

        // A tab whose viewer has gone offers to open the file as text.
        window.Layout.Select(window.Layout.View.FocusedCenter, "viewer:sample.fixture");
        E2eWorkspace.Until(() => Has(window, "FileViewerGone"));
        app.Click(Find<Button>(window, "FileViewerOpenAsText"));
        E2eWorkspace.Until(() => window.Layout.Tabs(window.Layout.View.FocusedCenter).Any(item => item.Kind == "file" && item.Path == "sample.fixture"));
    }
}
