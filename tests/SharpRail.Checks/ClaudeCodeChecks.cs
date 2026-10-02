using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.Host;
using SharpRail.Plugins.ClaudeCode.UI;
using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Panels;

namespace SharpRail.Checks;

internal static class ClaudeCodeChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class IsolatedEnvironment : IDisposable
    {
        private readonly Dictionary<string, string?> previous = [];
        public IsolatedEnvironment(string directory)
        {
            foreach (var (name, path) in new Dictionary<string, string>
            {
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(directory, "claude-home"),
                ["SHARPRAIL_STATE_DIR"] = Path.Combine(directory, "state")
            })
            {
                previous[name] = Environment.GetEnvironmentVariable(name);
                Directory.CreateDirectory(path);
                Environment.SetEnvironmentVariable(name, path);
            }
        }
        public void Dispose()
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class Terminals(TerminalRef terminal) : IPluginTerminalSeams
    {
        public Func<TerminalRef, IReadOnlyDictionary<string, string>>? EnvironmentContributor { get; set; }
        public Action<TerminalEvent>? Lifecycle { get; set; }
        public Func<TerminalRef, TerminalPrefill?>? RevivePrefill { get; set; }
        public Action<string, TerminalRef?>? SessionClosed { get; set; }
        public string Token(TerminalRef owner) => "claude-fixture-token";
        public TerminalRef? ForToken(string token) => token == "claude-fixture-token" ? terminal : null;
        public void Write(TerminalRef owner, string data) { }
        public IReadOnlyList<TerminalProcess> List() => [];
        public string? WorkspaceForProcess(int pid) => pid == Environment.ProcessId ? terminal.WorkspaceId : null;
    }

    public static void RunUi(string root)
    {
        var directory = Path.Combine(root, "claude-ui");
        using var environment = new IsolatedEnvironment(directory);
        using var app = new E2eWorkspace(Path.Combine(directory, "project"), openFiles: false);
        E2eWorkspace.Until(() => app.Workbench.PluginLoader!.Registry.Entry("claude-code") is not null);
        var registry = app.Workbench.PluginLoader!.Registry;
        Require(!registry.Active.Contains("claude-code") && registry.LauncherList.All(launcher => launcher.Id != "claude"),
            "A disabled Claude plugin exposes neither launcher nor configuration controls.");
        app.State!.ChangeAsync([HostStateChange.PluginSettings("claude-code", "{\"enabled\":true,\"command\":\"/usr/bin/false\"}")]).AsTask().GetAwaiter().GetResult();
        E2eWorkspace.Until(() => registry.Active.Contains("claude-code"));
        var launcherIcon = registry.LauncherList.Single(launcher => launcher.Id == "claude").CreateIcon!;
        var firstIcon = (ContentControl)launcherIcon(14, SharpRail.Plugins.UI.Kit.Ui.Accent);
        var secondIcon = (ContentControl)launcherIcon(20, SharpRail.Plugins.UI.Kit.Ui.TextBrush);
        Require(!ReferenceEquals(firstIcon, secondIcon) && firstIcon.Width == 14 && secondIcon.Width == 20,
            "Claude's launcher icon factory creates independently mountable controls at the requested size.");
        E2eWorkspace.Until(() => firstIcon.Content is SharpRail.Plugins.UI.Kit.SvgAsset && secondIcon.Content is SharpRail.Plugins.UI.Kit.SvgAsset);
        app.Window.Layout.NewTerminal(app.Center, "claude-keyboard-check");
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<SharpRail.UI.Terminal.TerminalView>().Any(view =>
            view.Launch.TabKey == "claude-keyboard-check" && view.Backend?.Started.IsCompletedSuccessfully == true));
        var terminalView = app.Window.GetLogicalDescendants().OfType<SharpRail.UI.Terminal.TerminalView>().Single(view => view.Launch.TabKey == "claude-keyboard-check");
        var backend = (HostTerminal)terminalView.Backend!;
        E2eWorkspace.Until(() => terminalView.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ClaudeTerminalAccessory"));
        var accessory = terminalView.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "ClaudeTerminalAccessory");
        var terminalRef = new TerminalRef(app.Root, "claude-keyboard-check");
        app.State.SetTerminalAgent(terminalRef, new("claude", "/usr/bin/false"));
        E2eWorkspace.Until(() => backend.AgentNewline);
        Require(ReferenceEquals(accessory, terminalView.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "ClaudeTerminalAccessory")),
            "An agent-record update retains the accessory and its active picker state.");
        app.State.SetTerminalAgent(terminalRef, null);
        E2eWorkspace.Until(() => !backend.AgentNewline);
        var launcher = registry.LauncherList.Single(candidate => candidate.Id == "claude");
        var line = launcher.TerminalCommand(new() { Model = "opus", SystemPrompt = "Keep paths with spaces", InitialPrompt = "Draft the design" });
        Require(line.StartsWith("CLAUDE_CODE_DISABLE_AGENT_VIEW=true /usr/bin/false --append-system-prompt-file", StringComparison.Ordinal) &&
            line.Contains("--model opus", StringComparison.Ordinal) && line.Contains("'Draft the design'", StringComparison.Ordinal) &&
            line.Contains("--append-system-prompt 'Keep paths with spaces'", StringComparison.Ordinal), "The launcher composes flags, prompt file and quoted caller prompts once.");
        Require(launcher.Models!().Select(model => model.Id).SequenceEqual(["opus", "fable", "sonnet", "haiku"]), "The launcher offers the fork's four model aliases.");
        app.Window.Layout.RestoreTool(ClaudeCodeManifest.ConfigTool);
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ClaudeConfigPanel"));
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ClaudeContextTotal"));
        app.Window.ShowSettings("plugin:claude-code:claude-code");
        E2eWorkspace.Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var window = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        var command = window.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "ClaudeCommandInput");
        command.Text = "custom-claude --verbose";
        command.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        E2eWorkspace.Until(() => app.State.Current.PluginSettings["claude-code"].GetProperty("command").GetString() == "custom-claude --verbose");
        var switcher = window.GetLogicalDescendants().OfType<CheckBox>().Single(control => control.Name == "ClaudeAppendSystemPrompt");
        switcher.IsChecked = false;
        E2eWorkspace.Until(() => !launcher.TerminalCommand(new()).Contains("--append-system-prompt-file", StringComparison.Ordinal));
        window.Close();
        var compose = SettingValueDialog.ShowAsync(app.Window, new("ClaudeCheck", "model", JsonSerializer.SerializeToElement("sonnet"), ["model"], "Review change"));
        E2eWorkspace.Until(() => app.Window.OwnedWindows.Any(dialog => Equals(dialog.Tag, "ClaudeCheckValueDialog")));
        var composer = app.Window.OwnedWindows.Single(dialog => Equals(dialog.Tag, "ClaudeCheckValueDialog"));
        var value = composer.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "ClaudeCheckValueText");
        value.Text = "opus";
        app.Click(composer.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "ClaudeCheckValueContinue"), freshGesture: false);
        E2eWorkspace.Until(() => compose.IsCompleted);
        Require(compose.GetAwaiter().GetResult() is { Key: "model", Value: var composed } && composed.GetString() == "opus",
            "The value composer opens as an owned dialog and returns the edited value for review.");
        app.State.ChangeAsync([HostStateChange.PluginEnabled("claude-code", false)]).AsTask().GetAwaiter().GetResult();
        E2eWorkspace.Until(() => !registry.Active.Contains("claude-code") && registry.LauncherList.All(candidate => candidate.Id != "claude"));
        Require(registry.SideTool(ClaudeCodeManifest.ConfigTool) is null, "Disabling removes Claude controls and launcher together.");
        Console.WriteLine("PASS Claude Code UI: enable, configuration pane, model launcher, compiled settings and disable");
    }

    private static List<Control> Named(Control scope, string name) =>
        [.. scope.GetLogicalDescendants().OfType<Control>().Where(control => control.Name == name && control.IsEffectivelyVisible)];

    /// <summary>
    /// The fork's <c>claude-launcher.spec.ts</c>: the launcher appears only with the plugin, its right-click menu offers
    /// continue/resume/teleport and the models, a model starts Claude in a new terminal of the group with the prompt file
    /// and the agent-view switch as the settings say; and the launch command never persists blank.
    /// </summary>
    public static void Launcher(string root)
    {
        var directory = Path.Combine(root, "claude-launcher");
        using var environment = new IsolatedEnvironment(directory);
        Directory.CreateDirectory(directory);
        var log = Path.Combine(directory, "launch.log");
        var fake = Path.Combine(directory, "fake-claude");
        File.WriteAllText(fake, $"#!/bin/sh\nprintf '%s\\n' \"agent-view=$CLAUDE_CODE_DISABLE_AGENT_VIEW\" \"$@\" > '{log}.part' && mv '{log}.part' '{log}'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var app = new E2eWorkspace(Path.Combine(directory, "project"), openFiles: false);
        E2eWorkspace.Until(() => app.Workbench.PluginLoader!.Registry.Entry("claude-code") is not null);
        Require(Named(app.Window, "NewClaude").Count == 0, "No Claude launcher while the plugin is off.");
        void Settings(object settings) => app.State!.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(settings))]).AsTask().GetAwaiter().GetResult();
        Settings(new { enabled = true, command = fake });
        E2eWorkspace.Until(() => Named(app.Window, "NewClaude").Count > 0);
        Require(ToolTip.GetTip(Named(app.Window, "NewClaude")[0]) is string tip && tip.StartsWith("Start Claude Code", StringComparison.Ordinal), "The launcher says what it starts.");
        Require(Named(app.Window, "NewClaude").All(launcher => launcher.GetVisualAncestors().OfType<StackPanel>().Any(row =>
                row.Children.Any(child => child.Name?.StartsWith("NewTerminal_", StringComparison.Ordinal) == true)) &&
            !launcher.GetVisualAncestors().Any(ancestor => ancestor.Name == "WorkspacePlaceholder")),
            "Workspace actions follow New terminal in the center tab strip, not the empty workspace view.");

        int Terminals() => app.Window.Layout.State.Groups.Sum(group => app.Window.Layout.Tabs(group.Id).Count(tab => tab.Kind == "terminal"));
        string[] Launch(string preset)
        {
            File.Delete(log);
            var before = Terminals();
            var button = (Button)Named(app.Window, "NewClaude")[0];
            app.Click(button, mouseButton: MouseButton.Right);
            var menu = button.ContextMenu!;
            E2eWorkspace.Until(() => menu.IsOpen);
            var items = menu.Items.OfType<MenuItem>().ToDictionary(item => item.Name!);
            Require((string)items["ClaudeLaunch_continue"].Header! == "Continue the last conversation" && items.ContainsKey("ClaudeLaunch_model-opus") &&
                ((string)items["ClaudeLaunch_teleport"].Header!).StartsWith("Teleport", StringComparison.Ordinal), "The menu offers continue, teleport and the models.");
            app.Click(items["ClaudeLaunch_" + preset], freshGesture: false);
            E2eWorkspace.Until(() => !menu.IsOpen && Terminals() == before + 1);
            Require(app.Window.Layout.Tabs(app.Center).Any(tab => tab.Kind == "terminal"), "Claude starts in the launcher's own group.");
            E2eWorkspace.Until(() => File.Exists(log));
            var lines = File.ReadAllLines(log);
            // As the Codex launcher check does: close the started terminals so the empty group offers the launcher again.
            foreach (var group in app.Window.Layout.State.Groups.ToArray())
                foreach (var tab in app.Window.Layout.Tabs(group.Id).Where(tab => tab.Kind == "terminal").ToArray())
                    app.Window.Layout.Close(group.Id, tab.Id);
            E2eWorkspace.Until(() => Named(app.Window, "NewClaude").Count > 0);
            return lines;
        }
        var opus = Launch("model-opus");
        Require(opus[0] == "agent-view=true" && opus[1] == "--append-system-prompt-file" && opus[^2..].SequenceEqual(["--model", "opus"]),
            "A model run starts with the prompt file and the agent view off: " + string.Join(" ", opus));
        Settings(new { enabled = true, command = fake, disableAgentView = false });
        var haiku = Launch("model-haiku");
        Require(haiku[0] == "agent-view=" && haiku[1] == "--append-system-prompt-file" && haiku[^2..].SequenceEqual(["--model", "haiku"]),
            "With the agent-view switch off the line is the plain command: " + string.Join(" ", haiku));
        Settings(new { enabled = true, command = fake, disableAgentView = false, appendSystemPrompt = false });
        var sonnet = Launch("model-sonnet");
        Require(sonnet.SequenceEqual(["agent-view=", "--model", "sonnet"]), "Without the prompt file the session starts on Claude's own: " + string.Join(" ", sonnet));
        Require(Launch("continue")[^1] == "--continue", "Continue passes the per-run flag.");
        Console.WriteLine("PASS fork plugins/claude-code/claude-launcher.spec.ts: the launcher starts Claude Code in its own group, and offers per-run flags on right-click");

        Settings(new { enabled = true, command = "claude" });
        app.Window.ShowSettings("plugin:claude-code:claude-code");
        E2eWorkspace.Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var window = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        TextBox Command() => window.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "ClaudeCommandInput");
        E2eWorkspace.Until(() => Command().Text == "claude");
        void Enter(string text)
        {
            Command().Text = text;
            Command().RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        }
        string Saved() => app.State!.Current.PluginSettings["claude-code"].TryGetProperty("command", out var value) ? value.GetString()! : "claude";
        Enter("claude --model opus");
        E2eWorkspace.Until(() => Saved() == "claude --model opus");
        window.Close();
        app.Window.ShowSettings("plugin:claude-code:claude-code");
        E2eWorkspace.Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        window = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        E2eWorkspace.Until(() => Command().Text == "claude --model opus");
        Enter("   ");
        E2eWorkspace.Until(() => Command().Text == "claude" && Saved() == "claude");
        window.Close();
        Console.WriteLine("PASS fork plugins/claude-code/claude-launcher.spec.ts: the launch command is a command line, and never reaches a shell blank");
    }

    public static async Task Run(string root)
    {
        var directory = Path.Combine(root, "claude-code");
        using var environment = new IsolatedEnvironment(directory);
        var project = Path.Combine(directory, "project");
        Directory.CreateDirectory(Path.Combine(project, ".claude"));
        File.WriteAllText(Path.Combine(project, "CLAUDE.md"), "# Fixture instructions\n");
        var settingsPath = Path.Combine(project, ".claude", "settings.json");
        File.WriteAllText(settingsPath, "{\"model\":\"sonnet\",\"permissions\":{\"allow\":[\"Read\"]}}\n");
        var state = new HostStateStore(Path.Combine(directory, "state"));
        await state.ChangeAsync([HostStateChange.OpenProject(project)]);
        var terminal = new TerminalRef(project, "claude-tab");
        var terminals = new Terminals(terminal);
        await using var runtime = new PluginRuntime(new()
        {
            StateDirectory = Path.Combine(directory, "state"),
            State = state,
            Terminals = terminals,
            PublicBaseUrl = () => "http://127.0.0.1:1",
            Builtins = [(ClaudeCodeManifest.Manifest, new ClaudeCodeHost())],
            Worktrees = (_, _) => Task.FromResult<IReadOnlyList<WorktreeInfo>>([])
        });
        await runtime.Start();
        Require((await runtime.ListAsync()).Single().Status == PluginStatus.Disabled &&
            !Directory.Exists(Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")!, "ide")),
            "Claude Code is disabled by default and starts no IDE bridge.");
        await state.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(new { enabled = true, command = "/usr/bin/false" }))]);
        await runtime.Schedule();
        Require((await runtime.ListAsync()).Single().Status == PluginStatus.Active, "A bare enable activates the Claude Code host.");
        async Task<T> Call<P, T>(PluginMethod<P, T> method, P parameters) =>
            PluginJson.Convert<T>(await runtime.CallAsync(new("claude-code", method.Name, parameters, "claude-client")));
        var config = await Call(ClaudeCodeContract.ConfigGet, new WorkspaceParams(project));
        Require(config.Root == project && config.Context.Any(layer => layer.Path == Path.Combine(project, "CLAUDE.md")) &&
            config.Settings.Any(setting => setting.Key == "model" && setting.Value.GetString() == "sonnet" && setting.Origin.Scope == ClaudeConfigScope.Project),
            "Configuration exposes instructions and the winning value's source scope.");
        var edit = new SettingEdit("model", JsonSerializer.SerializeToElement("opus"));
        var plan = await Call(ClaudeCodeContract.PlanEdit, new ClaudeEditRequest(project, ClaudeWritableScope.Project, edit));
        Require(plan.Path == settingsPath && File.ReadAllText(settingsPath).Contains("sonnet", StringComparison.Ordinal),
            "Planning shows the selected destination without writing it.");
        File.AppendAllText(settingsPath, " ");
        try
        {
            await Call(ClaudeCodeContract.ApplyEdit, new ClaudeApplyRequest(project, ClaudeWritableScope.Project, edit, plan.BaseHash));
            throw new InvalidOperationException("A stale edit unexpectedly applied.");
        }
        catch (PluginCallException error) { Require(error.Message.Contains("changed", StringComparison.Ordinal), "A stale preview is refused."); }
        plan = await Call(ClaudeCodeContract.PlanEdit, new ClaudeEditRequest(project, ClaudeWritableScope.Project, edit));
        await Call(ClaudeCodeContract.ApplyEdit, new ClaudeApplyRequest(project, ClaudeWritableScope.Project, edit, plan.BaseHash));
        using (var file = JsonDocument.Parse(File.ReadAllText(settingsPath)))
            Require(file.RootElement.GetProperty("model").GetString() == "opus" && file.RootElement.GetProperty("permissions").GetProperty("allow")[0].GetString() == "Read",
                "Applying the approved preview preserves unrelated settings.");
        var variables = terminals.EnvironmentContributor!(terminal);
        Require(variables[ClaudeCodeContract.StatusUrlVariable].EndsWith("/plugin/claude-code/status/claude-fixture-token", StringComparison.Ordinal) &&
            File.Exists(variables[ClaudeCodeContract.PromptFileVariable]), "The shell receives its own token-bound status route and the host's prompt file.");
        var transcript = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(transcript, "{\"type\":\"assistant\",\"message\":{\"id\":\"turn\",\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}}\n");
        async Task<int> Report(string token, string body, string method = "POST") =>
            (await runtime.ServeRouteAsync("claude-code", new(method, "status/" + token, "", new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body))))!.Status;
        Require(await Report("unknown", "{}") == 404 && await Report("claude-fixture-token", "{}", "GET") == 405,
            "The status route refuses an unknown terminal and a non-POST method.");
        var report = JsonSerializer.Serialize(new { @event = "prompt_submit", session_id = "fixture-session", cwd = project, transcript_path = transcript, model = "sonnet" });
        Require(await Report("claude-fixture-token", report) == 200, "A hook report reaches its terminal.");
        var rows = await Call(ClaudeCodeContract.StatusSnapshot, new StatusSnapshotParams(project));
        Require(rows is [{ Status: ClaudeCodeStatus.Running, Usage.Input: 5, Report.Model: "sonnet" }] &&
            state.Current.TerminalAgents.Single().Record.SessionId == "fixture-session", "A report updates status, real transcript usage and the terminal's session identity.");
        await Report("claude-fixture-token", "{\"event\":\"stop\",\"notify\":false}");
        rows = await Call(ClaudeCodeContract.StatusSnapshot, new StatusSnapshotParams(project));
        Require(rows.Single().Status == ClaudeCodeStatus.Done && rows.Single().Report.Notify == false, "A continuation Stop settles the badge while suppressing notification.");
        Require(terminals.RevivePrefill!(terminal)?.Text.Contains("--continue", StringComparison.Ordinal) == true,
            "A Claude terminal with no surviving transcript gets the fork's continue offer.");
        await IdeProtocol(runtime, project);
        await ConfigCases(runtime, state, project, directory);
        await state.ChangeAsync([HostStateChange.PluginEnabled("claude-code", false)]);
        await runtime.Schedule();
        Require(Directory.GetFiles(Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")!, "ide"), "*.lock").Length == 0 &&
            terminals.EnvironmentContributor!(terminal).Count == 0, "Disabling removes IDE discovery and terminal contributions.");
        Transcripts(directory);
        Console.WriteLine("PASS Claude Code host: disabled lifecycle, configuration provenance, reviewed writes, token status, spending and revive");
    }

    /// <summary>
    /// The host halves of the fork's <c>claude-config.spec.ts</c> mutation cases, driven through the same plan/apply
    /// methods the pane's composers call: an import drawn under its importer, an MCP server added then denied, a skill,
    /// hook and plugin each written to their scope, Claude's CLI-only servers, and a local override of a user setting.
    /// </summary>
    private static async Task ConfigCases(PluginRuntime runtime, HostStateStore state, string project, string directory)
    {
        async Task<T> Call<P, T>(PluginMethod<P, T> method, P parameters) =>
            PluginJson.Convert<T>(await runtime.CallAsync(new("claude-code", method.Name, parameters, "claude-client")));
        async Task<ClaudeEditPlan> Apply(ClaudeWritableScope scope, ClaudeEdit edit)
        {
            var plan = await Call(ClaudeCodeContract.PlanEdit, new ClaudeEditRequest(project, scope, edit));
            await Call(ClaudeCodeContract.ApplyEdit, new ClaudeApplyRequest(project, scope, edit, plan.BaseHash));
            return plan;
        }
        string Diff(ClaudeEditPlan plan) => string.Join("\n", plan.Diff.Select(line => line.Text));
        JsonElement Read(string path) { using var document = JsonDocument.Parse(File.ReadAllText(path)); return document.RootElement.Clone(); }

        File.WriteAllText(Path.Combine(project, "CLAUDE.md"), "# Project\n\n@AGENTS.md\n");
        File.WriteAllText(Path.Combine(project, "AGENTS.md"), "# Imported\n\nrules\n");
        var config = await Call(ClaudeCodeContract.ConfigGet, new WorkspaceParams(project));
        Require(config.Context.Any(layer => layer.Path == Path.Combine(project, "AGENTS.md") && layer.Kind == ClaudeContextKind.Import && layer.Depth == 1),
            "An @-imported instruction file is a depth-one branch of the file that pulled it in.");
        Console.WriteLine("PASS fork plugins/claude-code/claude-config.spec.ts (host): an imported instruction file is drawn as a branch of the file that pulled it in");

        var add = new McpAddEdit("notes", new(ClaudeMcpTransport.Stdio) { Command = "notes-mcp", Args = ["--root", "."] });
        Require(Diff(await Apply(ClaudeWritableScope.Project, add)).Contains("notes-mcp", StringComparison.Ordinal) &&
            Read(Path.Combine(project, ".mcp.json")).GetProperty("mcpServers").GetProperty("notes").GetProperty("command").GetString() == "notes-mcp",
            "An MCP server is written to the project's own .mcp.json.");
        config = await Call(ClaudeCodeContract.ConfigGet, new WorkspaceParams(project));
        Require(config.Capabilities.Single(item => item is { Kind: ClaudeCapabilityKind.Mcp, Name: "notes" }).Enabled, "The added server is on.");
        Require(Diff(await Apply(ClaudeWritableScope.Local, new McpEdit("notes", false))).Contains("deniedMcpServers", StringComparison.Ordinal),
            "Switching the server off previews a local deny list.");
        config = await Call(ClaudeCodeContract.ConfigGet, new WorkspaceParams(project));
        Require(config.Capabilities.Single(item => item is { Kind: ClaudeCapabilityKind.Mcp, Name: "notes" }) is { Enabled: false, DisabledBy.Scope: ClaudeConfigScope.Local } &&
            Read(Path.Combine(project, ".claude", "settings.local.json")).GetProperty("deniedMcpServers").EnumerateArray().Select(item => item.GetString()).SequenceEqual(["notes"]),
            "The local deny list switches the row off and names its scope.");
        Console.WriteLine("PASS fork plugins/claude-code/claude-config.spec.ts (host): an MCP server is added to the project's own file, then switched off locally");

        Require(Diff(await Apply(ClaudeWritableScope.Project, new SkillCreateEdit("Reviewing A Migration", "Use when a change moves data between schema versions.")))
            .Contains("reviewing-a-migration", StringComparison.Ordinal) &&
            File.ReadAllText(Path.Combine(project, ".claude", "skills", "reviewing-a-migration", "SKILL.md")).Contains("name: reviewing-a-migration", StringComparison.Ordinal),
            "A skill is slugged and written under the project's skills.");
        var settingsPath = Path.Combine(project, ".claude", "settings.json");
        Require(Diff(await Apply(ClaudeWritableScope.Project, new HookEdit(ClaudeHookEvent.PostToolUse, "Edit|Write", "./format.sh"))).Contains("./format.sh", StringComparison.Ordinal) &&
            Read(settingsPath).GetProperty("hooks").GetProperty("PostToolUse").GetArrayLength() == 1, "A hook is written to the project settings.");
        Require(Diff(await Apply(ClaudeWritableScope.Project, new PluginAddEdit("claude-code-plugins", new GithubMarketplaceSource("anthropics/claude-code"), "typescript-lsp")))
            .Contains("anthropics/claude-code", StringComparison.Ordinal) &&
            Read(settingsPath).GetProperty("enabledPlugins").GetProperty("typescript-lsp@claude-code-plugins").GetBoolean(), "A plugin is enabled in the project settings with its marketplace.");
        config = await Call(ClaudeCodeContract.ConfigGet, new WorkspaceParams(project));
        Require(config.Capabilities.Any(item => item.Kind == ClaudeCapabilityKind.Hook && (item.Name + item.Detail).Contains("PostToolUse", StringComparison.Ordinal)) &&
            config.Capabilities.Any(item => item is { Kind: ClaudeCapabilityKind.Plugin, Name: "typescript-lsp@claude-code-plugins" }), "The hook and plugin appear as rows.");
        var fake = Path.Combine(directory, "fake-claude");
        var log = Path.Combine(directory, "claude-cli.log");
        File.WriteAllText(fake, $"#!/bin/sh\nif [ \"$1 $2\" = \"mcp list\" ]; then\n  echo 'Checking MCP server health…'\n  echo ''\n" +
            "  echo 'claude.ai Uber Eats: https://mcp.ubereats.com/eats-claude/mcp - ⊘ Disabled for this project (re-enable via /mcp)'\n" +
            "  echo 'claude.ai Gmail: https://gmailmcp.googleapis.com/mcp/v1 - ✔ Connected'\n" +
            "  echo 'plugin:cloudflare:cloudflare: https://mcp.cloudflare.com/mcp (HTTP) - ! Needs authentication'\n  exit 0\nfi\n" +
            $"echo \"$@\" >> '{log}'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(log, "");
        await state.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(new { enabled = true, command = fake }))]);
        await runtime.Schedule();
        var move = await Call(ClaudeCodeContract.PluginMovePlan, new PluginMovePlanParams(project, "typescript-lsp@claude-code-plugins", ClaudeWritableScope.Project, ClaudeWritableScope.User));
        Require(move.Commands.Select(command => string.Join(" ", command)).SequenceEqual([
            $"{fake} plugin install typescript-lsp@claude-code-plugins --scope user --yes",
            $"{fake} plugin uninstall typescript-lsp@claude-code-plugins --scope project --yes"]), "A move is install at the target, then uninstall at the source.");
        await Call(ClaudeCodeContract.PluginMove, new PluginMoveParams(project, "typescript-lsp@claude-code-plugins", ClaudeWritableScope.Project, ClaudeWritableScope.User));
        Require(File.ReadAllText(log).Contains("plugin install typescript-lsp@claude-code-plugins --scope user --yes\nplugin uninstall typescript-lsp@claude-code-plugins --scope project --yes", StringComparison.Ordinal),
            "The move runs both commands, in order.");
        var market = await Call(ClaudeCodeContract.MarketplacePlan, new MarketplacePlanParams(project, new AddMarketplace("acme/other", ClaudeWritableScope.Project)));
        Require(string.Join(" ", market.Command) == $"{fake} plugin marketplace add acme/other --scope project", "A marketplace add previews Claude's command.");
        Console.WriteLine("PASS fork plugins/claude-code/claude-config.spec.ts (host): a skill, a hook and a plugin are each composed, then written where the scope says; moves and marketplaces run Claude's CLI");

        var servers = (await Call(ClaudeCodeContract.McpList, new WorkspaceParams(project))).Capabilities;
        Require(servers.Single(item => item.Name == "claude.ai Gmail") is { Enabled: true } gmail && (gmail.Detail ?? "").Contains("Connected", StringComparison.Ordinal) &&
            servers.Single(item => item.Name == "claude.ai Uber Eats") is { Enabled: false } &&
            (servers.Single(item => item.Name == "plugin:cloudflare:cloudflare").Detail ?? "").Contains("Needs authentication", StringComparison.Ordinal),
            "Claude's connectors and plugin servers are parsed from its health check.");
        Console.WriteLine("PASS fork plugins/claude-code/claude-config.spec.ts (host): claude.ai connectors and plugin servers appear as rows from Claude's own health check");

        var userPath = Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")!, "settings.json");
        var source = "{\"cleanupPeriodDays\":30}";
        File.WriteAllText(userPath, source);
        var local = Path.Combine(project, ".claude", "settings.local.json");
        Require(Diff(await Apply(ClaudeWritableScope.Local, new SettingEdit("cleanupPeriodDays", JsonSerializer.SerializeToElement(7)))).Contains("\"cleanupPeriodDays\": 7", StringComparison.Ordinal),
            "The local override previews the new value.");
        config = await Call(ClaudeCodeContract.ConfigGet, new WorkspaceParams(project));
        Require(config.Settings.Single(item => item.Key == "cleanupPeriodDays") is { Origin.Scope: ClaudeConfigScope.Local } winner && winner.Value.GetInt32() == 7 &&
            winner.Shadowed.Any(item => item.Origin.Scope == ClaudeConfigScope.User && item.Value.GetInt32() == 30) &&
            Read(local).GetProperty("cleanupPeriodDays").GetInt32() == 7 && File.ReadAllText(userPath) == source,
            "A local override wins, shows the user value it shadows, and leaves the user file untouched.");
        Console.WriteLine("PASS fork plugins/claude-code/claude-config.spec.ts (host): a user setting can be overridden locally without changing its source");
    }

    private static async Task IdeProtocol(PluginRuntime runtime, string project)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lockPath = Directory.GetFiles(Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")!, "ide"), "*.lock").Single();
        using var discovery = JsonDocument.Parse(File.ReadAllText(lockPath));
        var port = Path.GetFileNameWithoutExtension(lockPath);
        var endpoint = new Uri($"ws://127.0.0.1:{port}/");
        using (var denied = new ClientWebSocket())
        {
            try
            {
                await denied.ConnectAsync(endpoint, timeout.Token);
                throw new InvalidOperationException("The IDE bridge accepted a connection without its token.");
            }
            catch (WebSocketException) { }
        }
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("x-claude-code-ide-authorization", discovery.RootElement.GetProperty("authToken").GetString());
        await socket.ConnectAsync(endpoint, timeout.Token);
        async Task Send(object message) => await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, timeout.Token);
        async Task<JsonElement> Receive()
        {
            using var body = new MemoryStream();
            var buffer = new byte[4096];
            WebSocketReceiveResult chunk;
            do { chunk = await socket.ReceiveAsync(buffer, timeout.Token); body.Write(buffer, 0, chunk.Count); } while (!chunk.EndOfMessage);
            return JsonSerializer.Deserialize<JsonElement>(body.ToArray());
        }
        await Send(new { jsonrpc = "2.0", id = 1, method = "initialize", @params = new { } });
        var initialized = await Receive();
        Require(initialized.GetProperty("result").GetProperty("protocolVersion").GetString() == "2024-11-05", "The authenticated bridge answers the CLI's MCP initialization.");
        await Send(new { jsonrpc = "2.0", id = 2, method = "tools/list" });
        var catalogue = (await Receive()).GetProperty("result").GetProperty("tools");
        Require(catalogue.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "openFile") &&
            catalogue.EnumerateArray().Any(tool => tool.GetProperty("name").GetString() == "getCurrentSelection"), "The IDE catalogue exposes its editor actions and selections.");
        await runtime.CallAsync(new("claude-code", "selectionChanged", new IdeSelectionChanged(project, "CLAUDE.md", "Fixture", new(1, 1, 1, 8)), "claude-client"));
        await Send(new { jsonrpc = "2.0", id = 3, method = "tools/call", @params = new { name = "getCurrentSelection", arguments = new { } } });
        var content = (await Receive()).GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        var selected = JsonSerializer.Deserialize<JsonElement>(content);
        Require(selected.GetProperty("text").GetString() == "Fixture" && selected.GetProperty("filePath").GetString() == Path.Combine(project, "CLAUDE.md"),
            "Editor selection reaches the IDE protocol with the host's absolute file path.");
        await using var owner = runtime.SubscribeAsync(new("claude-code", "ideAction", null, "claude-client"), timeout.Token).GetAsyncEnumerator(timeout.Token);
        await using var peer = runtime.SubscribeAsync(new("claude-code", "ideAction", null, "other-client"), timeout.Token).GetAsyncEnumerator(timeout.Token);
        var next = owner.MoveNextAsync().AsTask();
        var peerNext = peer.MoveNextAsync().AsTask();
        await Send(new { jsonrpc = "2.0", id = 4, method = "tools/call", @params = new { name = "openFile", arguments = new { filePath = Path.Combine(project, "CLAUDE.md"), preview = true } } });
        Require(await next.WaitAsync(timeout.Token), "The IDE action reaches the owning app client.");
        var action = PluginJson.Convert<IdeActionRequest>(owner.Current);
        Require(action.Kind == IdeActionKind.OpenFile && action.WorkspaceId == project && action.Params.Preview == true && !peerNext.IsCompleted,
            "IDE actions are addressed to the client that reported editor context, not broadcast.");
        await runtime.CallAsync(new("claude-code", "actionReply", new IdeActionReply(action.Id, new(true) { Value = JsonSerializer.SerializeToElement(new { success = true }) }), "claude-client"));
        var reply = await Receive();
        Require(reply.GetProperty("id").GetInt32() == 4 && reply.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!.Contains("true", StringComparison.Ordinal),
            "The client's action result returns to the requesting CLI as MCP content.");
        socket.Abort();
        await timeout.CancelAsync();
        try { await peerNext; } catch (OperationCanceledException) { }
        Console.WriteLine("PASS Claude Code IDE: token authorization, initialization, catalogue, selection and addressed action reply");
    }

    private static void Transcripts(string directory)
    {
        var path = Path.Combine(directory, "usage.jsonl");
        static string Turn(string id, int input, int output) => JsonSerializer.Serialize(new
        { type = "assistant", message = new { id, usage = new { input_tokens = input, output_tokens = output } } }) + "\n";
        File.WriteAllText(path, Turn("one", 10, 1) + Turn("one", 10, 3));
        var usage = new TranscriptUsage();
        Require(usage.Read(path) == new AgentTokenUsage(10, 3, 0, 0), "Streaming blocks count once per message at their last usage.");
        var tail = Turn("two", 4, 2);
        File.AppendAllText(path, tail[..^1]);
        Require(usage.Read(path).Input == 10, "Incomplete transcript lines wait for completion.");
        File.AppendAllText(path, "\n");
        var subagents = Path.Combine(path[..^6], "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllText(Path.Combine(subagents, "agent-one.jsonl"), Turn("child", 7, 6));
        Require(usage.Read(path) == new AgentTokenUsage(21, 11, 0, 0), "Appended turns and subagent spending are included without recounting prior messages.");
        var stamp = DateTimeOffset.UtcNow;
        File.WriteAllText(path, JsonSerializer.Serialize(new { type = "user", timestamp = stamp, message = new { content = "[Request interrupted by user]" } }) +
            "\n{\"type\":\"file-history-snapshot\"}\n");
        Require(InterruptWatch.InterruptedSince(path, stamp.AddSeconds(-1)) && !InterruptWatch.InterruptedSince(path, stamp.AddSeconds(1)),
            "Interrupt detection ignores bookkeeping and rejects an older turn's marker.");
        var store = new ClaudeCodeStore();
        store.Apply(new("workspace", "tab", new("stop") { Model = "sonnet", Todos = [new("Task", AgentTodoStatus.Pending)] }, ClaudeCodeStatus.Done) { Usage = new(10, 3, 0, 0) });
        store.Apply(new("workspace", "tab", new("model_switch") { Model = "opus" }));
        Require(store.Session("workspace", "tab") is { Status: ClaudeCodeStatus.Done, Model: "opus", Usage.Input: 10, Todos.Count: 1 },
            "A facts-only push retains the status, plan and usage in the client store.");
    }
}