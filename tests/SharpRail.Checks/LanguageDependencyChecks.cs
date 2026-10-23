using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.UI.State;

namespace SharpRail.Checks;

/// <summary>System dependencies of language servers: probed per declared tool with a fake PATH, shown on the row before any start, and fulfilled by a host-wide path.</summary>
internal static class LanguageDependencyChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static string Tool(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static void Run(string fixture)
    {
        var root = Path.Combine(fixture, "lsp-dependencies");
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "main.go"), "package main\n");
        var bin = Path.Combine(root, "bin"); Directory.CreateDirectory(bin);
        var savedPath = Environment.GetEnvironmentVariable("PATH"); var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE");
        Environment.SetEnvironmentVariable("PATH", bin); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        try
        {
            Host(root, workspace, bin);
            Ui(root, workspace);
            Settings(root, bin, savedPath);
        }
        finally { Environment.SetEnvironmentVariable("PATH", savedPath); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled); }
    }

    private static LanguageServerState Row(HostStateStore state, string workspace, string id) =>
        state.Languages.ExecuteAsync(new(workspace, Operation: "servers")).AsTask().GetAwaiter().GetResult().Servers.Single(row => row.Id == id);

    private static void Host(string root, string workspace, string bin)
    {
        var statePath = Path.Combine(root, "state");
        var state = new HostStateStore(statePath);
        var go = Row(state, workspace, "go").Dependencies!.Single();
        Require(go is { Executable: "go", State: "missing", Required: true, Override: false }, $"go was not reported missing and required: {go}.");
        var typescript = Row(state, workspace, "typescript").Dependencies!;
        Require(typescript.Select(item => item.Executable).SequenceEqual(["node", "npm"]) && typescript.All(item => item.State == "missing"), "TypeScript did not declare node and npm as missing.");
        state.Languages.ExecuteAsync(new(workspace, Operation: "retry", Server: "go")).AsTask().GetAwaiter().GetResult();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        var detail = "";
        while (DateTime.UtcNow < deadline && !(detail = Row(state, workspace, "go").Detail).Contains("Needs go", StringComparison.Ordinal)) Thread.Sleep(100);
        Require(detail.Contains("Needs go", StringComparison.Ordinal), "A start without go did not name the missing dependency: " + detail);
        Console.WriteLine("PASS dependency probe reports each declared tool missing on an empty PATH and a start names it");

        var onPath = Tool(bin, "node");
        Thread.Sleep(TimeSpan.FromSeconds(2.2));
        var node = Row(state, workspace, "typescript").Dependencies!.Single(item => item.Executable == "node");
        Require(node is { State: "found", Source: "path" } && node.Path == onPath, $"node on PATH was not found: {node}.");
        Console.WriteLine("PASS a tool that appears on PATH is found without a restart");

        var mine = Tool(Path.Combine(root, "mine"), "go");
        state.ChangeAsync([HostStateChange.Setting("language-tool:go", mine)]).GetAwaiter().GetResult();
        Require(LanguageServers.ParseTools(state.Current.Settings.LanguageTools).GetValueOrDefault("go") == mine, "The go path did not reach host state.");
        go = Row(state, workspace, "go").Dependencies!.Single();
        Require(go is { State: "found", Source: "override", Override: true } && go.Path == mine, $"The probe ignored the go path: {go}.");
        var servers = new LanguageServers(Path.Combine(root, "language")) { ToolPaths = () => state.Current.Settings.LanguageTools };
        var installation = servers.InstallationFor(LanguageServers.Catalog.Single(item => item.Id == "go"), workspace, "");
        Require(installation.RequireTool("go") == mine, "The launch did not use the go path.");
        Require(LanguageServers.ParseTools(new HostStateStore(statePath).Current.Settings.LanguageTools).GetValueOrDefault("go") == mine, "The go path was not persisted.");
        var refused = false;
        try { state.ChangeAsync([HostStateChange.Setting("language-tool:go", "relative/go")]).GetAwaiter().GetResult(); } catch (ArgumentException) { refused = true; }
        Require(refused, "A relative tool path was accepted.");
        state.ChangeAsync([HostStateChange.Setting("language-tool:go", "")]).GetAwaiter().GetResult();
        Require(state.Current.Settings.LanguageTools.Length == 0, "Clearing the go path left it in host state.");
        go = Row(state, workspace, "go").Dependencies!.Single();
        Require(go is { State: "missing", Override: false }, $"Clearing the go path did not revert to PATH discovery: {go}.");
        refused = false;
        try { installation.RequireTool("go"); } catch (IOException) { refused = true; }
        Require(refused, "The launch still found go after its path was cleared.");
        Console.WriteLine("PASS a host-wide tool path persists, is probed and launched, and clearing it reverts to PATH");
    }

    private static void Ui(string root, string workspace)
    {
        var state = new HostStateStore(Path.Combine(root, "ui-state"));
        using var shared = new SharedState(new LocalStateAdapter(state), new(), state.Current);
        shared.Start();
        var pane = new SharpRail.UI.Panels.LanguagePanel(new LocalProjectAdapter(new ProjectServices(workspace, state)), workspace, _ => Task.CompletedTask, shared);
        var window = new Window { Width = 520, Height = 900, Content = pane };
        var previous = SynchronizationContext.Current; SynchronizationContext.SetSynchronizationContext(null);
        window.Show();
        try
        {
            var servers = pane.FindControl<ListBox>("Servers")!;
            ListBoxItem GoRow()
            {
                for (var index = 0; index < servers.ItemCount; index++)
                    if (servers.Items[index]?.ToString() == "Go (gopls)" && servers.ContainerFromIndex(index) is ListBoxItem item) return item;
                return null!;
            }
            TextBlock Needs() => GoRow().GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ServerNeeds");
            bool Shows(string name, string text) => GoRow() is { } row && row.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.Name == name && block.IsEffectivelyVisible && (block.Text ?? "").Contains(text, StringComparison.Ordinal));
            E2E.E2eWorkspace.Until(() => GoRow() is not null && Shows("ServerNeeds", "Needs go"), "The Go row did not show Needs go before any start.");
            Require(GoRow().GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "DependencyUsePath" && button.IsEffectivelyVisible), "The Go row offered no Use path….");
            Require(GoRow().GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "DependencyCopyHint" && button.IsEffectivelyVisible), "The Go row offered no install hint.");
            Console.WriteLine("PASS the Go row shows Needs go with Use path… and the install hint before any start");
            var mine = Tool(Path.Combine(root, "ui-mine"), "go");
            shared.ChangeAsync(HostStateChange.Setting("language-tool:go", mine)).GetAwaiter().GetResult();
            E2E.E2eWorkspace.Until(() => GoRow() is not null && !Needs().IsEffectivelyVisible, "The Go row kept Needs go after its path was set.");
            shared.ChangeAsync(HostStateChange.Setting("language-tool:go", "")).GetAwaiter().GetResult();
            E2E.E2eWorkspace.Until(() => Shows("ServerNeeds", "Needs go"), "The Go row did not show Needs go again after the path was cleared.");
            Console.WriteLine("PASS the row follows the host-wide go path on broadcast, set and cleared");
        }
        finally { window.Close(); SynchronizationContext.SetSynchronizationContext(previous); }
    }

    /// <summary>Settings › Code Intelligence shows the same rows as the pane, marks the server in its rail, and both follow one host-wide path.</summary>
    private static void Settings(string root, string bin, string? savedPath)
    {
        // The workbench runs Git; keep its directory on the fixture PATH unless go lives there too.
        var git = (savedPath ?? "").Split(Path.PathSeparator).FirstOrDefault(directory => directory.Length > 0 && File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? "git.exe" : "git")));
        if (git is not null && !File.Exists(Path.Combine(git, OperatingSystem.IsWindows() ? "go.exe" : "go"))) Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + git);
        using var app = new E2E.E2eWorkspace(Path.Combine(root, "settings-workspace"), openFiles: false);
        var shared = app.Workbench.State;
        var pane = new SharpRail.UI.Panels.LanguagePanel(new LocalProjectAdapter(new ProjectServices(app.Root, app.State!)), app.Root, _ => Task.CompletedTask, shared);
        File.WriteAllText(Path.Combine(app.Root, "main.go"), "package main\n");
        var paneWindow = new Window { Width = 520, Height = 900, Content = pane };
        paneWindow.Show();
        var settings = new SharpRail.UI.Panels.SettingsWindow(app.Window, () => { }, section: "Code Intelligence");
        settings.Show(app.Window);
        try
        {
            T Find<T>(string name) where T : Control => settings.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
            var rail = Find<ListBox>("LspServer");
            E2E.E2eWorkspace.Until(() => rail.ItemCount == LanguageServers.Catalog.Count, "The settings rail did not list the catalog.");
            void Select(string id) => rail.SelectedItem = rail.Items.Cast<object>().First(item => item?.ToString() == id);
            bool Marked(string id) => rail.ContainerFromItem(rail.Items.Cast<object>().First(item => item?.ToString() == id)!) is { } item
                && item.GetLogicalDescendants().OfType<Avalonia.Controls.Shapes.Ellipse>().Any(marker => marker.Name == "LspMissingMarker" && marker.IsVisible);
            bool Shows(string text) => Find<StackPanel>("LspDependencySection").IsVisible
                && settings.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.Name == "DependencyState" && block.IsEffectivelyVisible && (block.Text ?? "").Contains(text, StringComparison.Ordinal));
            Select("go");
            E2E.E2eWorkspace.Until(() => Shows("Needs go (Go)") && Marked("go"), "Settings did not show Needs go and its rail marker before any start.");
            Require(settings.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "DependencyUsePath" && button.IsEffectivelyVisible), "Settings offered no Use path… for go.");
            Require(settings.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "DependencyCopyHint" && button.IsEffectivelyVisible), "Settings offered no install hint for go.");
            Require(!Marked("clangd"), "A server with no system dependencies carries the missing marker.");
            Select("clangd");
            E2E.E2eWorkspace.Until(() => Find<TextBlock>("LspServerHeading").Text == "clangd" && !Find<StackPanel>("LspDependencySection").IsVisible, "A server with no system dependencies showed the section.");
            Require(!settings.GetLogicalDescendants().OfType<CheckBox>().Any(control => control.Content?.ToString() == "Allow this server to start"), "The per-server allow checkbox came back.");
            Console.WriteLine("PASS Settings shows Needs go with the rail marker before any start, and nothing for a server without dependencies");
            Select("go");
            var servers = pane.FindControl<ListBox>("Servers")!;
            bool PaneNeeds() => Enumerable.Range(0, servers.ItemCount).Any(index => servers.Items[index]?.ToString() == "Go (gopls)" && servers.ContainerFromIndex(index) is ListBoxItem row
                && row.GetLogicalDescendants().OfType<TextBlock>().Any(block => block.Name == "ServerNeeds" && block.IsEffectivelyVisible));
            E2E.E2eWorkspace.Until(PaneNeeds, "The pane did not show Needs go next to Settings.");
            var mine = Tool(Path.Combine(root, "settings-mine"), "go");
            var update = Task.Run(async () => await shared.ChangeAsync(HostStateChange.Setting("language-tool:go", mine)));
            E2E.E2eWorkspace.Until(() => update.IsCompleted && Shows(mine + " (your path)") && !Marked("go") && !PaneNeeds(), "Setting the go path through the host did not update both Settings and the pane.");
            update.GetAwaiter().GetResult();
            var clear = settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "DependencyClearPath" && button.IsEffectivelyVisible);
            clear.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2E.E2eWorkspace.Until(() => app.State!.Current.Settings.LanguageTools.Length == 0 && Shows("Needs go (Go)") && Marked("go") && PaneNeeds(), "Clear path in Settings did not revert both surfaces to Needs go.");
            Console.WriteLine("PASS a go path set through the host and cleared from Settings updates Settings, its rail marker and the pane");
        }
        finally { settings.Close(); paneWindow.Close(); }
    }
}