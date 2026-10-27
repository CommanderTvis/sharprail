using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.Host;
using SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;
using SharpRail.Plugins.ClaudeCode.UI;
using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Panels;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class ClaudeCodeChecks
{
    private static readonly string ScratchProbe = Path.Combine(Path.GetTempPath(), "sharprail-scratch-probe");

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
                ["SHARPRAIL_STATE_DIR"] = Path.Combine(directory, "state"),
                [AgentSessionOwnership.DirectoryVariable] = Path.Combine(directory, "agent-sessions"),
                [ClaudeCodeContract.ScratchVariable] = ScratchProbe,
                [ClaudeCodeContract.PluginDirectoryVariable] = Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets", "marketplace", "claude-plugin")
            })
            {
                previous[name] = Environment.GetEnvironmentVariable(name);
                Directory.CreateDirectory(path);
                Environment.SetEnvironmentVariable(name, path);
            }
            // Checks run from a terminal SharpRail started Claude in inherit its launch switch; the shells they start must not.
            previous[ClaudeLaunch.AgentViewVariable] = Environment.GetEnvironmentVariable(ClaudeLaunch.AgentViewVariable);
            Environment.SetEnvironmentVariable(ClaudeLaunch.AgentViewVariable, null);
        }
        public void Dispose()
        {
            foreach (var (name, value) in previous) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private sealed class Terminals(TerminalRef terminal) : IPluginTerminalSeams
    {
        public Func<TerminalRef, IReadOnlyDictionary<string, string>>? EnvironmentContributor { get; set; }
        public Func<TerminalRef, CancellationToken, ValueTask>? EnvironmentPreparer { get; set; }
        public Action<TerminalEvent>? Lifecycle { get; set; }
        public Func<TerminalRef, TerminalPrefill?>? RevivePrefill { get; set; }
        public Func<TerminalRef, TerminalPrefill, bool>? ReviveGate { get; set; }
        public Action<string, TerminalRef?>? SessionClosed { get; set; }
        public string Token(TerminalRef owner) => "claude-fixture-token";
        public TerminalRef? ForToken(string token) => token == "claude-fixture-token" ? terminal : null;
        public void Write(TerminalRef owner, string data) { }
        public IReadOnlyList<TerminalProcess> List() => [];
        public string? WorkspaceForProcess(int pid) => pid == Environment.ProcessId ? terminal.WorkspaceId : null;
    }

    /// <summary>The running spinner turns through a style animation on the composition thread, with no timer to start or stop.</summary>
    private static void Spinner()
    {
        var spinner = new ClaudeSpinner();
        var window = new Window { Width = 100, Height = 100, Content = spinner };
        var arc = (Avalonia.Controls.Shapes.Arc)spinner.Child!;
        Require(spinner.Styles.Count == 1 && spinner.Styles[0] is Avalonia.Styling.Style { Animations.Count: 1 }, "The spinner's rotation is a style animation.");
        window.Show();
        try
        {
            static void Advance() { Thread.Sleep(25); Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Avalonia.Threading.Dispatcher.UIThread.RunJobs(); }
            E2eWorkspace.Until(() => { Advance(); return arc.RenderTransform is not null; });
            var first = arc.RenderTransform!.Value;
            E2eWorkspace.Until(() => { Advance(); return arc.RenderTransform!.Value != first; });
        }
        finally { window.Close(); }
    }

    public static void RunUi(string root)
    {
        var directory = Path.Combine(root, "claude-ui");
        using var environment = new IsolatedEnvironment(directory);
        using var app = new E2eWorkspace(Path.Combine(directory, "project"), openFiles: false, pluginTerminals: !OperatingSystem.IsWindows());
        E2eWorkspace.Until(() => app.Workbench.PluginLoader!.Registry.Entry("claude-code") is not null);
        var registry = app.Workbench.PluginLoader!.Registry;
        Require(!registry.Active.Contains("claude-code") && registry.LauncherList.All(launcher => launcher.Id != "claude"),
            "A disabled Claude plugin exposes neither launcher nor configuration controls.");
        app.State!.ChangeAsync([HostStateChange.PluginSettings("claude-code", "{\"enabled\":true,\"command\":\"/usr/bin/false\"}")]).AsTask().GetAwaiter().GetResult();
        E2eWorkspace.Until(() => registry.Active.Contains("claude-code"));
        Spinner();
        ClaudeContextChecks.Ui();
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
        bool Marked()
        {
            var tabs = app.Window.GetLogicalDescendants().OfType<Button>().Where(button =>
                button.Name == "Tab_claude-keyboard-check" || button.Name == "WorkspaceTabPreview" && Equals(button.Tag, terminalRef.TabKey)).ToArray();
            return tabs.Length > 0 && tabs.All(button => button.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, "asset:claude.svg")));
        }
        Require(!Marked(), "A plain shell must not wear the Claude mark.");
        app.State.SetTerminalAgent(terminalRef, new("claude", "/usr/bin/false"));
        E2eWorkspace.Until(() => backend.AgentNewline && Marked());
        string[] Mentions(FileActionTarget target) => [.. registry.FileActionsFor(target).Where(offer => offer.PluginId == "claude-code").Select(offer => offer.Label)];
        E2eWorkspace.Until(() => Mentions(new(app.Root, "README.md", false)) is ["Mention file in Claude Code"]);
        Require(Mentions(new(app.Root, "README.md", false) { StartLine = 2, EndLine = 4 }) is ["Mention lines 2–4 in Claude Code"] &&
            Mentions(new(app.Root, "README.md", false) { StartLine = 7, EndLine = 7 }) is ["Mention line 7 in Claude Code"] &&
            Mentions(new(app.Root, "styles", true)) is ["Mention folder in Claude Code"] && Mentions(new(app.Root + "-other", "README.md", false)).Length == 0,
            "A workspace running Claude offers to reference a file, a folder or the selected lines in its prompt.");
        SubmittedPromptKeepsChanges(app);
        CloseTabClosesOnlyClaudesTabs(app);
        Require(ReferenceEquals(accessory, terminalView.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "ClaudeTerminalAccessory")),
            "An agent-record update retains the accessory and its active picker state.");
        app.State.SetTerminalAgent(terminalRef, null);
        E2eWorkspace.Until(() => !backend.AgentNewline && !Marked() && Mentions(new(app.Root, "README.md", false)).Length == 0);
        var launcher = registry.LauncherList.Single(candidate => candidate.Id == "claude");
        var withImage = launcher.TerminalCommand(new() { InitialPrompt = "Explain this", Images = ["/tmp/quoted image.png"] });
        Require(withImage.Contains("Explain this", StringComparison.Ordinal) && withImage.Contains("Read the attached image: /tmp/quoted image.png", StringComparison.Ordinal),
            "Claude receives image-reading instructions along with its initial prompt.");
        var line = launcher.TerminalCommand(new() { Model = "opus", SystemPrompt = "Keep paths with spaces", InitialPrompt = "Draft the design" });
        Require(line.StartsWith("SHARPRAIL_UI_LAUNCH=claude CLAUDE_CODE_DISABLE_AGENT_VIEW=true /usr/bin/false --append-system-prompt-file", StringComparison.Ordinal) &&
            line.Contains("--model opus", StringComparison.Ordinal) && line.Contains("'Draft the design'", StringComparison.Ordinal) &&
            line.Contains("--append-system-prompt 'Keep paths with spaces'", StringComparison.Ordinal), "The launcher composes flags, prompt file and quoted caller prompts once.");
        Require(launcher.Models!().Select(model => model.Id).SequenceEqual(["opus", "fable", "sonnet", "haiku"]), "The launcher offers the fork's four model aliases.");
        File.WriteAllText(Path.Combine(app.Root, "CLAUDE.md"), "# Project\n\n@AGENTS.md\n");
        File.WriteAllText(Path.Combine(app.Root, "AGENTS.md"), "# Imported\n\nrules\n");
        File.WriteAllText(Path.Combine(app.Root, "CLAUDE.local.md"), "# Local instructions with a long enough row\n");
        app.Window.Layout.RestoreTool(ClaudeCodeManifest.ConfigTool);
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ClaudeConfigPanel"));
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ClaudeContextTotal"));
        app.Click(app.Window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "ClaudeSurface_capabilities"));
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ClaudeLspCapability"));
        var lsp = app.Window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "ClaudeLspCapability");
        var lspText = string.Join(" ", lsp.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));
        Require(new[] { "lsp_navigate", "lsp_diagnostics", "lsp_status" }.All(name => lspText.Contains(name, StringComparison.Ordinal)),
            "Claude capabilities must name all three LSP MCP tools.");
        app.Click(app.Window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "ClaudeSurface_context"));
        var contextButton = app.Window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "ClaudeSurface_context");
        var tabStrip = contextButton.GetVisualAncestors().OfType<ScrollViewer>().First();
        tabStrip.Width = 180;
        E2eWorkspace.Settle();
        var scrollBar = tabStrip.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ScrollBar>()
            .Single(bar => bar.Orientation == Avalonia.Layout.Orientation.Horizontal);
        Require(!scrollBar.IsVisible && tabStrip.Extent.Width > tabStrip.Viewport.Width,
            "The narrow Claude surface strip keeps its button height without a visible scrollbar.");
        tabStrip.Offset = new(tabStrip.Extent.Width - tabStrip.Viewport.Width, 0);
        E2eWorkspace.Settle();
        var accountButton = app.Window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "ClaudeSurface_account");
        Require(tabStrip.Offset.X > 0 && accountButton.TranslatePoint(new(accountButton.Bounds.Width, 0), tabStrip)!.Value.X <= tabStrip.Bounds.Width,
            "The narrow Claude surface strip can scroll to its last button.");
        tabStrip.ClearValue(Control.WidthProperty);
        E2eWorkspace.Settle();
        // The fixture sits inside this checkout, so Claude's ancestor walk also lists the checkout's own files.
        Control? ContextRow(string name) => app.Window.GetLogicalDescendants().OfType<Control>().SingleOrDefault(control => control.Name == "ClaudeContextLayer" &&
            control.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text?.EndsWith("/project/" + name, StringComparison.Ordinal) == true));
        E2eWorkspace.Until(() => ContextRow("AGENTS.md") is not null);
        var connectors = ContextRow("AGENTS.md")!.GetLogicalDescendants().OfType<Control>().Where(control => control.Name == "ClaudeContextConnector").ToArray();
        Require(connectors.Length == 1 && Equals(connectors[0].Tag, "last") &&
            connectors[0].GetLogicalDescendants().OfType<Border>().Any(arm => arm.Height == 1),
            "An imported instruction file is joined to its parent by an elbow, not merely pushed right.");
        var imported = ContextRow("AGENTS.md")!;
        E2eWorkspace.Settle(500);
        app.Click(app.Window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "ClaudeConfigRefresh"));
        E2eWorkspace.Settle(500);
        Require(ReferenceEquals(imported, ContextRow("AGENTS.md")), "An unchanged Claude configuration read must retain its context rows.");
        var rows = ((Panel)imported.Parent!).Children;
        Require(rows[rows.IndexOf(imported) - 1].GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ClaudeContextStem"),
            "A file that imports another draws the stem its branch hangs from.");
        Console.WriteLine("PASS fork plugins/claude-code/claude-config.spec.ts: an imported instruction file is drawn as a branch of the file that pulled it in");
        // SharpRail's own instructions are chosen section by section, never edited: ticking writes the project's disable-list.
        Control? Sections() => app.Window.GetLogicalDescendants().OfType<Control>().SingleOrDefault(control => control.Name == "InstructionSections");
        Control Popup() => ((SharpRail.Plugins.Agent.UI.InstructionSectionsView)Sections()!).SectionsPanel;
        CheckBox? SectionBox(string id) => Popup().GetLogicalDescendants().OfType<CheckBox>().SingleOrDefault(box => box.Name == "InstructionSectionCheckbox" && Equals(box.Tag, id));
        // They are one context entry with the PROJECT chip, not a launch-flag file row beside a separate card.
        E2eWorkspace.Until(() => Sections() is not null);
        Require(!app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name is "ClaudeLaunchFlagChip" ||
                control.Name == "ClaudeContextLayer" && control.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("thinkrail-prompt", StringComparison.Ordinal) == true)) &&
            Sections()!.GetLogicalDescendants().OfType<Border>().Any(chip => chip.Name == "InstructionSectionsScopeChip" && Equals(chip.Tag, "project")) &&
            !Sections()!.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "SHARED WITH THE TEAM"),
            "SharpRail's instructions are a single context entry with the PROJECT chip.");
        // The row is a button that counts its sections; the checkboxes live only in its popup, never inline in the table.
        TextBlock SectionCount() => Sections()!.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "InstructionSectionsCount");
        Button SectionsRow() => Sections()!.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "InstructionSectionsToggle");
        var view = (SharpRail.Plugins.Agent.UI.InstructionSectionsView)Sections()!;
        E2eWorkspace.Until(() => SectionCount() is { IsEffectivelyVisible: true, Text: { } count } && count.EndsWith(" sections · Choose…", StringComparison.Ordinal));
        Require(!Sections()!.GetLogicalDescendants().OfType<CheckBox>().Any() && SectionsRow().Flyout is Flyout && !view.IsOpen,
            "The entry is one compact row with a section count; its sections open in a popup.");
        app.Click(SectionsRow());
        E2eWorkspace.Until(() => view.IsOpen && SectionBox("core.workspaces") is { IsChecked: true, IsEnabled: true, IsEffectivelyVisible: true });
        Require(!app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name == "ClaudeSystemPromptEditPane") &&
            !Popup().GetLogicalDescendants().OfType<TextBox>().Any() &&
            Popup().GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("running sessions keep their instructions", StringComparison.Ordinal) == true) &&
            Popup().GetLogicalDescendants().OfType<SelectableTextBlock>().Any(text => text.Text?.Contains(Path.Combine(".sharprail", "instructions.toml"), StringComparison.Ordinal) == true),
            "The popup lists SharpRail's sections with the shared file and the restart note, and offers no way to edit text.");
        var instructionsFile = Path.Combine(app.Root, ".sharprail", "instructions.toml");
        SectionBox("core.workspaces")!.IsChecked = false;
        E2eWorkspace.Until(() => File.Exists(instructionsFile) && File.ReadAllText(instructionsFile).Contains("\"core.workspaces\"", StringComparison.Ordinal));
        E2eWorkspace.Until(() => SectionBox("core.workspaces") is { IsChecked: false, IsEnabled: true });
        SectionsRow().Flyout!.Hide();
        E2eWorkspace.Until(() => !view.IsOpen && SectionCount().Text?.EndsWith(" sections · 1 off · Choose…", StringComparison.Ordinal) == true);
        app.Click(SectionsRow());
        E2eWorkspace.Until(() => view.IsOpen && SectionBox("core.workspaces") is { IsEffectivelyVisible: true });
        // Each section is compact: no preview box and no visible identifier, only a summary that discloses the text.
        Require(!Popup().GetLogicalDescendants().OfType<Expander>().Any() &&
            !Popup().GetLogicalDescendants().OfType<TextBlock>().Any(text => text.IsEffectivelyVisible && text.Text is "core.workspaces" or "Preview") &&
            Equals(ToolTip.GetTip(SectionBox("core.workspaces")!), "core.workspaces"),
            "A section shows no Preview control and keeps its identifier to the tooltip.");
        SelectableTextBlock SectionText() => Popup().GetLogicalDescendants().OfType<SelectableTextBlock>().Single(text => text.Name == "InstructionSectionText" && Equals(text.Tag, "core.workspaces"));
        Require(!SectionText().IsVisible, "A section's text starts hidden behind its summary.");
        app.Click(Popup().GetLogicalDescendants().OfType<ToggleButton>().Single(toggle => toggle.Name == "InstructionSectionDisclosure" && Equals(toggle.Tag, "core.workspaces")));
        E2eWorkspace.Settle();
        Require(SectionText().IsVisible && SectionText().Text?.Contains("`workspace_create`", StringComparison.Ordinal) == true,
            "A section's summary discloses its text read-only.");
        // A change made elsewhere (another view, or the file on disk read by the host) reaches the view through the broadcast.
        SectionBox("core.workspaces")!.IsChecked = true;
        E2eWorkspace.Until(() => File.ReadAllText(instructionsFile).Contains("disabled = []", StringComparison.Ordinal));
        E2eWorkspace.Until(() => SectionBox("core.workspaces") is { IsChecked: true });
        SectionsRow().Flyout!.Hide();
        File.Delete(instructionsFile);
        Console.WriteLine("PASS instruction sections view: one PROJECT row counting its sections, a popup of compact checkboxes with disclosed text, toggles write .sharprail/instructions.toml");
        // The generated file itself opens read-only under a notice that points to the toggles, and the host refuses to save it.
        var promptPath = Path.Combine(GeneratedInstructions.StateDirectory(), ClaudeCodeContract.Id, "thinkrail-prompt-claude.md");
        E2eWorkspace.Until(() => File.Exists(promptPath));
        var openPrompt = app.Window.OpenDocumentAsync(promptPath, true);
        E2eWorkspace.Until(() => openPrompt.IsCompleted); openPrompt.GetAwaiter().GetResult();
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Border>().Any(border => border.Name == "ReadOnlyNotice" && border.IsEffectivelyVisible &&
            border.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == GeneratedInstructions.Notice)));
        var promptRead = app.Host.ReadFileAsync(promptPath).AsTask();
        E2eWorkspace.Until(() => promptRead.IsCompleted);
        Require(promptRead.Result is { ReadOnly: true, ReadOnlyNotice: GeneratedInstructions.Notice }, "The host serves the generated instructions read-only with the notice.");
        var promptSave = app.Host.SaveFileAsync(new(app.Root, promptPath, promptRead.Result.Text, "my own instructions\n")).AsTask();
        E2eWorkspace.Until(() => promptSave.IsCompleted);
        Require(promptSave.Exception?.InnerException is UnauthorizedAccessException && File.ReadAllText(promptPath) == promptRead.Result.Text,
            "Saving the generated instructions is refused.");
        Console.WriteLine("PASS generated instructions open read-only with the SharpRail notice and cannot be saved");
        foreach (var tab in app.Tabs.Where(tab => tab.Path == promptPath).ToArray()) app.Window.Layout.Close(app.Center, tab.Id);
        E2eWorkspace.Settle();
        var pane = app.Window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "ClaudeConfigPanel");
        foreach (var width in new[] { 260, 280, 360, 480 })
        {
            pane.Width = width;
            E2eWorkspace.Settle();
            var layerRows = app.Window.GetLogicalDescendants().OfType<Control>().Where(control => control.Name == "ClaudeContextLayer").ToArray();
            Require(layerRows.Length > 0 && layerRows.All(row =>
            {
                var size = row.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "ClaudeContextSize");
                var title = row.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "ClaudeContextTitle");
                Rect Bounds(Control control) => new(control.TranslatePoint(new(0, 0), row)!.Value, control.Bounds.Size);
                var titleBounds = title.GetVisualDescendants().OfType<Control>().Where(part => part.IsVisible && part.Bounds.Width > 0)
                    .Select(part => new Rect(part.TranslatePoint(new(0, 0), row)!.Value, part.Bounds.Size)).Append(Bounds(title));
                return titleBounds.All(part => !part.Intersects(Bounds(size))) && Bounds(title).Right <= Bounds(size).X;
            }), $"At {width} px the size column of every context row is clear of its name and badge.");
        }
        pane.ClearValue(Control.WidthProperty);
        E2eWorkspace.Settle();
        app.Click(app.Window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "ClaudeSurface_account"));
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(control => control.Name == "ClaudeAccount"));
        var refresh = app.Window.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "ClaudeConfigRefresh");
        app.Click(refresh);
        E2eWorkspace.Settle(500);
        app.Click(refresh);
        E2eWorkspace.Settle(500);
        Require(app.Window.GetLogicalDescendants().OfType<Control>().Count(control => control.Name == "ClaudeAccount") == 1,
            "The mounted account surface renders again after each read without renaming itself.");
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
        Require(!window.GetLogicalDescendants().OfType<Button>().Any(control => control.Name is "ClaudeSystemPromptEdit" or "ClaudeSystemPromptReset"),
            "Settings offer no way to edit SharpRail's instructions.");
        window.Close();
        E2eWorkspace.Until(() => !app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var compose = SettingValueDialog.ShowAsync(app.Window, new("ClaudeCheck", "model", JsonSerializer.SerializeToElement("sonnet"), ["model"], "Review change"));
        E2eWorkspace.Until(() => app.Window.OwnedWindows.Any(dialog => Equals(dialog.Tag, "ClaudeCheckValueDialog")));
        var composer = app.Window.OwnedWindows.Single(dialog => Equals(dialog.Tag, "ClaudeCheckValueDialog"));
        var value = composer.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "ClaudeCheckValueText");
        value.Text = "opus";
        app.Click(composer.GetLogicalDescendants().OfType<Button>().Single(control => control.Name == "ClaudeCheckValueContinue"), freshGesture: false);
        E2eWorkspace.Until(() => compose.IsCompleted);
        Require(compose.GetAwaiter().GetResult() is { Key: "model", Value: var composed } && composed.GetString() == "opus",
            "The value composer opens as an owned dialog and returns the edited value for review.");
        SuggestedSettingValues(app);
        ScopeChoices(app);
        if (!OperatingSystem.IsWindows()) LaunchedSession(app, directory, launcher);
        if (!OperatingSystem.IsWindows()) ResumeOffers(app, directory);
        app.State.ChangeAsync([HostStateChange.PluginEnabled("claude-code", false)]).AsTask().GetAwaiter().GetResult();
        E2eWorkspace.Until(() => !registry.Active.Contains("claude-code") && registry.LauncherList.All(candidate => candidate.Id != "claude"));
        Require(registry.SideTool(ClaudeCodeManifest.ConfigTool) is null, "Disabling removes Claude controls and launcher together.");
        Console.WriteLine("PASS Claude Code UI: enable, configuration pane, model launcher, compiled settings and disable");
    }

    /// <summary>
    /// A submitted prompt makes the CLI send closeAllDiffTabs over the IDE bridge: a Changes tab grouped with the Claude
    /// terminal is the user's own and stays open, with no undo-close toast.
    /// </summary>
    /// <summary>
    /// close_tab closes only a tab Claude's own openFile newly opened, by its exact path: never the user's tab of the
    /// same file, nor a file elsewhere sharing its bare name.
    /// </summary>
    private static void CloseTabClosesOnlyClaudesTabs(E2eWorkspace app)
    {
        Directory.CreateDirectory(Path.Combine(app.Root, "close-tab", "other"));
        foreach (var file in new[] { "close-tab/foo.cs", "close-tab/bar.cs", "close-tab/other/bar.cs" }) File.WriteAllText(Path.Combine(app.Root, file), "class C {}\n");
        var loader = app.Workbench.PluginLoader!;
        var entry = loader.Registry.Entry("claude-code")!;
        var context = new SharpRail.UI.Plugins.PluginUIContext(loader, entry, new SharpRail.UI.Plugins.PluginActivation(entry));
        using var actions = new IdeActions(context);
        bool Open(string path) => context.Editors.List(app.Root).Any(editor => editor.Path == path);
        void Run(IdeActionKind kind, IdeActionParams parameters)
        {
            var handled = actions.HandleAsync(new("close-tab-" + kind, app.Root, kind, parameters));
            E2eWorkspace.Until(() => handled.IsCompleted);
        }
        var users = context.Editors.OpenAsync(app.Root, "close-tab/foo.cs").AsTask();
        E2eWorkspace.Until(() => users.IsCompleted && Open("close-tab/foo.cs"));
        var otherBar = context.Editors.OpenAsync(app.Root, "close-tab/other/bar.cs").AsTask();
        E2eWorkspace.Until(() => otherBar.IsCompleted && Open("close-tab/other/bar.cs"));
        Run(IdeActionKind.OpenFile, new() { Path = Path.Combine(app.Root, "close-tab/foo.cs") });
        Run(IdeActionKind.CloseTab, new() { TabName = Path.Combine(app.Root, "close-tab/foo.cs") });
        Run(IdeActionKind.CloseTab, new() { TabName = "foo.cs" });
        Require(Open("close-tab/foo.cs"), "close_tab must not close a tab the user had open before Claude's openFile.");
        Run(IdeActionKind.OpenFile, new() { Path = Path.Combine(app.Root, "close-tab/bar.cs") });
        E2eWorkspace.Until(() => Open("close-tab/bar.cs"));
        Run(IdeActionKind.CloseTab, new() { TabName = "bar.cs" });
        Require(Open("close-tab/bar.cs") && Open("close-tab/other/bar.cs"), "close_tab must not close a tab by its bare file name.");
        Run(IdeActionKind.CloseTab, new() { TabName = Path.Combine(app.Root, "close-tab/bar.cs") });
        E2eWorkspace.Until(() => !Open("close-tab/bar.cs"));
        Require(Open("close-tab/other/bar.cs") && Open("close-tab/foo.cs"), "close_tab must close only the tab Claude opened, by its exact path.");
        foreach (var editor in context.Editors.List(app.Root).Where(editor => editor.Path.StartsWith("close-tab/", StringComparison.Ordinal)).ToArray()) context.Editors.Close(editor.Id);
        foreach (var toast in app.Window.Toasts.Items.ToArray()) app.Window.Toasts.Dismiss(toast.Id);
    }

    private static void SubmittedPromptKeepsChanges(E2eWorkspace app)
    {
        var changes = new SharpRail.UI.Docking.DockTab("changes:all:", "Changes · All changes", "changes", Scope: "all");
        app.Window.Layout.Open(changes, true, app.Center);
        E2eWorkspace.Until(() => app.Window.Layout.Tabs(app.Center).Any(tab => tab.Id == changes.Id));
        var loader = app.Workbench.PluginLoader!;
        var entry = loader.Registry.Entry("claude-code")!;
        var actions = new IdeActions(new SharpRail.UI.Plugins.PluginUIContext(loader, entry, new SharpRail.UI.Plugins.PluginActivation(entry)));
        var toasts = app.Window.Toasts.Items.Count;
        var handled = actions.HandleAsync(new("submitted-prompt", app.Root, IdeActionKind.CloseAllDiffTabs, new()));
        E2eWorkspace.Until(() => handled.IsCompleted);
        Require(app.Window.Layout.Tabs(app.Center).Any(tab => tab.Id == changes.Id) &&
            app.Window.Layout.Tabs(app.Center).Any(tab => tab.Id == "claude-keyboard-check") &&
            app.Window.Toasts.Items.Count == toasts && !app.Window.Toasts.Items.Any(toast => toast.Message.StartsWith("Closed ", StringComparison.Ordinal)),
            "A prompt submitted to Claude must not close the Changes tab beside its terminal or offer to undo a close.");
        app.Window.Layout.Close(app.Center, changes.Id);
    }

    /// <summary>
    /// Keys with a documented closed value set offer those values as chips, while the field still saves any string;
    /// a wildcard pattern matches one key segment, and a documented boolean is composed as on/off and written unquoted.
    /// </summary>
    private static void SuggestedSettingValues(E2eWorkspace app)
    {
        Window Composer() => app.Window.OwnedWindows.Single(dialog => Equals(dialog.Tag, "ClaudeCheckValueDialog"));
        T Named<T>(string name) where T : Control => Composer().GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        (string Key, JsonElement Value)? Compose(string key, JsonElement? value, Action fill)
        {
            var shown = SettingValueDialog.ShowAsync(app.Window, new("ClaudeCheck", key, value, ["permissions.defaultMode", "alwaysThinkingEnabled"], "Review change")
            {
                Suggestions = ClaudeSettingSuggestions.For,
                ShapeFor = ClaudeSettingSuggestions.ShapeFor
            });
            E2eWorkspace.Until(() => app.Window.OwnedWindows.Any(dialog => Equals(dialog.Tag, "ClaudeCheckValueDialog")));
            fill();
            E2eWorkspace.Settle();
            app.Click(Named<Button>("ClaudeCheckValueContinue"), freshGesture: false);
            E2eWorkspace.Until(() => shown.IsCompleted);
            return shown.GetAwaiter().GetResult();
        }

        string[] offered = [];
        var picked = Compose("permissions.defaultMode", JsonSerializer.SerializeToElement("default"), () =>
        {
            offered = [.. Named<WrapPanel>("ClaudeCheckValueSuggestions").Children.OfType<Control>().Select(chip => chip.Name!["ClaudeCheckValueSuggestion_".Length..])];
            app.Click(Named<ToggleButton>("ClaudeCheckValueSuggestion_acceptEdits"), freshGesture: false);
        });
        Require(new[] { "default", "acceptEdits", "plan", "auto", "dontAsk", "bypassPermissions" }.All(offered.Contains)
            && picked is { Value: { ValueKind: JsonValueKind.String } chosen } && chosen.GetString() == "acceptEdits",
            "The permission mode composer offers the documented modes and a tapped one is saved as a string.");
        var custom = Compose("permissions.defaultMode", JsonSerializer.SerializeToElement("plan"), () => Named<TextBox>("ClaudeCheckValueText").Text = "someFutureMode");
        Require(custom is { Value: { ValueKind: JsonValueKind.String } typed } && typed.GetString() == "someFutureMode",
            "A value outside the suggestions still saves as typed.");
        Require(ClaudeSettingSuggestions.For("modelSettings.claude-opus-4-8.effortLevel") is { } levels && levels.Contains("xhigh")
            && ClaudeSettingSuggestions.For("modelSettings.effortLevel") is null && ClaudeSettingSuggestions.For("someUnknownKey") is null,
            "A wildcard pattern matches exactly one key segment; unknown keys get no suggestions.");
        var flag = Compose("", null, () =>
        {
            Named<AutoCompleteBox>("ClaudeCheckValueKey").Text = "alwaysThinkingEnabled";
            E2eWorkspace.Settle();
            app.Click(Named<Button>("ClaudeCheckValueSwitch"), freshGesture: false);
        });
        Require(flag is { Key: "alwaysThinkingEnabled", Value.ValueKind: JsonValueKind.True },
            "A documented boolean key is composed as on/off and returned as a JSON boolean.");
        Console.WriteLine("PASS Claude setting suggestions: documented values offered, custom text kept, wildcard keys, typed booleans");
    }

    /// <summary>
    /// The edit dialog names the third scope "local", marks each option with the same scope chip the pane uses, and
    /// shows each option's description whole inside its option.
    /// </summary>
    private static void ScopeChoices(E2eWorkspace app)
    {
        // Choosing a scope would ask the host for a plan; this only inspects the options, so no context is needed.
        _ = ReviewDialogs.EditAsync(null!, app.Window, "check", null, new(new SettingEdit("model", JsonSerializer.SerializeToElement("opus")), "Change \"model\""));
        E2eWorkspace.Until(() => app.Window.OwnedWindows.Any(dialog => Equals(dialog.Tag, "ClaudeEditDialog")));
        var dialog = app.Window.OwnedWindows.Single(window => Equals(window.Tag, "ClaudeEditDialog"));
        E2eWorkspace.Settle();
        var options = dialog.GetLogicalDescendants().OfType<Button>().Where(button => button.Name?.StartsWith("ClaudeEditScope_", StringComparison.Ordinal) == true).ToArray();
        Require(options.Length == 3 && options.All(option =>
            {
                var scope = option.Name!["ClaudeEditScope_".Length..];
                var chip = option.GetLogicalDescendants().OfType<Border>().SingleOrDefault(border => border.Name == "ClaudeScopeChip");
                var description = option.GetLogicalDescendants().OfType<TextBlock>().Single(text => text.Name == "ClaudeEditScopeDescription");
                var at = description.TranslatePoint(new(0, 0), option)!.Value;
                return chip is not null && Equals(chip.Tag, scope) && at.Y >= 0 && at.Y + description.Bounds.Height <= option.Bounds.Height + 0.5;
            }) && options.Any(option => option.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "Local project settings")),
            "Each scope option carries its scope chip, the third is called local, and its description fits inside the option.");
        dialog.Close();
        E2eWorkspace.Until(() => !app.Window.OwnedWindows.Any(window => Equals(window.Tag, "ClaudeEditDialog")));
        Console.WriteLine("PASS Claude edit scopes: local wording, scope chips, descriptions unclipped");
    }

    /// <summary>
    /// A launched session runs as the terminal's program under the id SharpRail chose: the agent record names that id
    /// before any hook reports, and when the agent exits the tab is an interactive shell in the same directory. The fake
    /// is a process named <c>claude</c> (bash under that name, so detection sees it) that logs its arguments and waits.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void LaunchedSession(E2eWorkspace app, string directory, AgentLauncher launcher)
    {
        var bin = Path.Combine(directory, "fake-bin");
        Directory.CreateDirectory(bin);
        var named = Path.Combine(bin, "claude");
        if (!File.Exists(named)) File.CreateSymbolicLink(named, "/bin/bash");
        var log = Path.Combine(directory, "launched.log");
        var pidFile = Path.Combine(directory, "launched.pid");
        var fake = Path.Combine(bin, "launch-claude");
        // The plugin also runs the command for its own queries (`mcp list` and others), which must not overwrite the launch's log and pid.
        File.WriteAllText(fake, $"#!/bin/bash\ncase \" $* \" in *\" --session-id \"*) ;; *) exit 0 ;; esac\nprintf '%s\\n' \"$@\" > '{log}.part' && mv '{log}.part' '{log}'\n" +
            $"exec -a claude '{named}' -c 'echo $$ > \"$0\"; sleep 600; :' '{pidFile}' \"$@\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        app.State!.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(new { enabled = true, command = fake, appendSystemPrompt = false }))]).AsTask().GetAwaiter().GetResult();
        E2eWorkspace.Until(() => launcher.TerminalCommand(new()).Contains(fake, StringComparison.Ordinal));
        var line = launcher.TerminalCommand(new());
        var opened = app.Window.OpenPluginTerminalAsync(app.Root, new() { Command = line });
        E2eWorkspace.Until(() => opened.IsCompleted);
        var tab = new TerminalRef(app.Root, opened.GetAwaiter().GetResult());
        E2eWorkspace.Until(() => File.Exists(log) && File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0);
        var args = File.ReadAllLines(log);
        var at = Array.IndexOf(args, "--session-id");
        Require(at >= 0 && AgentResume.IsSessionId(args[at + 1]), "A new launch starts claude with a preassigned --session-id: " + string.Join(" ", args));
        var id = args[at + 1];
        E2eWorkspace.Until(() => app.State.Current.TerminalAgents.FirstOrDefault(agent => agent.Terminal == tab)?.Record.SessionId == id);
        Console.WriteLine("PASS Claude launch: the tab's agent record names the preassigned session before any hook reports");
        var view = app.Window.GetLogicalDescendants().OfType<SharpRail.UI.Terminal.TerminalView>().Single(candidate => candidate.Launch.TabKey == tab.TabKey);
        Require(view.Launch.Program is null, "The launch program runs once; restarting the tab opens a plain shell.");
        using (var agent = System.Diagnostics.Process.GetProcessById(int.Parse(File.ReadAllText(pidFile).Trim(), System.Globalization.CultureInfo.InvariantCulture)))
            agent.Kill(entireProcessTree: true);
        try { E2eWorkspace.Until(() => app.State.Current.TerminalAgents.All(agent => agent.Terminal != tab)); }
        catch (InvalidOperationException)
        {
            // Show what the agent watch still sees.
            using var ps = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ps", ["-Ao", "pid,ppid,pgid,stat,comm,args"]) { RedirectStandardOutput = true })!;
            var table = ps.StandardOutput.ReadToEnd();
            Console.WriteLine("Agent " + File.ReadAllText(pidFile).Trim() + " killed; processes:\n" +
                string.Join('\n', table.Split('\n').Where(line => line.Contains("claude", StringComparison.Ordinal) || line.Contains("bash", StringComparison.Ordinal) || line.Contains("sleep", StringComparison.Ordinal))));
            throw;
        }
        var marker = Path.Combine(directory, "after-agent");
        view.Write($"[ \"$PWD\" -ef '{app.Root}' ] && touch '{marker}'\r");
        E2eWorkspace.Until(() => File.Exists(marker));
        Require(view.Backend is { } backend && !backend.Exited.IsCompleted, "The terminal outlives its agent.");
        Console.WriteLine("PASS Claude launch: when the agent exits the tab is an interactive shell in the same directory");
    }

    /// <summary>
    /// A revived Claude tab never resumes on its own: its shell starts alone and the tab offers Resume and Start fresh. Resume
    /// launches <c>--resume</c> past a stale lock (dead pid); a session another SharpRail holds the lock of, or a running
    /// process names, is refused as open elsewhere with only Start fresh left, which launches a new session id.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void ResumeOffers(E2eWorkspace app, string directory)
    {
        var bin = Path.Combine(directory, "resume-bin");
        Directory.CreateDirectory(bin);
        // Linux ps reports the executed file's name, not argv[0]: the bash link itself must be named claude to be detected.
        var agentBin = Path.Combine(directory, "resume-agent-bin");
        Directory.CreateDirectory(agentBin);
        var named = Path.Combine(agentBin, "claude");
        if (!File.Exists(named)) File.CreateSymbolicLink(named, "/bin/bash");
        var log = Path.Combine(directory, "resume.log");
        var pids = Path.Combine(directory, "resume.pids");
        var fake = Path.Combine(bin, "claude");
        File.WriteAllText(fake, "#!/bin/bash\ncase \" $* \" in *\" --session-id \"*|*\" --resume \"*) ;; *) exit 0 ;; esac\n" +
            $"echo \"$*\" >> '{log}'\nexec -a claude '{named}' -c 'echo $$ >> \"$0\"; sleep 600; :' '{pids}' \"$@\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string[] Launches() => File.Exists(log) ? File.ReadAllLines(log) : [];
        var agents = app.State!.AgentSessions;
        var others = new List<System.Diagnostics.Process>();
        var spawned = new List<int>();
        try
        {
            (TerminalRef Tab, SharpRail.UI.Terminal.TerminalView View) Revive(string key, string id)
            {
                var tab = new TerminalRef(app.Root, key);
                var transcript = Path.Combine(directory, "claude-home", "projects", "resume-offers", id + ".jsonl");
                Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
                File.WriteAllText(transcript, "{}\n");
                app.State.SetTerminalAgent(tab, new("claude", fake + " --session-id " + id) { SessionId = id });
                var opened = app.Window.OpenPluginTerminalAsync(app.Root, new() { TabKey = key });
                E2eWorkspace.Until(() => opened.IsCompleted);
                E2eWorkspace.Until(() => app.State.Current.ResumeOffers.Any(offer => offer.Terminal == tab && offer.SessionId == id));
                var view = app.Window.GetLogicalDescendants().OfType<SharpRail.UI.Terminal.TerminalView>().Single(candidate => candidate.Launch.TabKey == key);
                E2eWorkspace.Until(() => view.ResumeOffer is { Visible: true, Resume: true, Fresh: true });
                Require(view.ResumeOffer.Text?.Contains("Claude session " + id[..8], StringComparison.Ordinal) == true, "The offer names the session: " + view.ResumeOffer.Text);
                return (tab, view);
            }

            // A stale lock (its process is gone) does not block Resume, which launches the recorded session.
            const string stale = "7d1c2b3a-1111-4c5d-8e9f-000000000001";
            var (first, firstView) = Revive("terminal:resume-offer-stale", stale);
            E2eWorkspace.Settle(1500);
            Require(Launches().Length == 0, "A revived tab does not launch its agent until asked: " + string.Join(" | ", Launches()));
            Console.WriteLine("PASS Claude revival: the revived tab offers its session and launches nothing on its own");
            using (var gone = System.Diagnostics.Process.Start("/bin/sh", ["-c", "exit 0"])!)
            {
                gone.WaitForExit();
                Directory.CreateDirectory(agents.Directory);
                File.WriteAllText(agents.PathFor("claude", stale), AgentSessionOwnership.Content(gone.Id, 1, new("/elsewhere", "terminal:other")));
            }
            firstView.AnswerResumeOffer(true);
            E2eWorkspace.Until(() => Launches().Any(line => line.Contains("--resume " + stale, StringComparison.Ordinal)));
            E2eWorkspace.Until(() => app.State.Current.ResumeOffers.All(offer => offer.Terminal != first) && !firstView.ResumeOffer.Visible);
            Require(AgentSessionOwnership.Holder(agents.PathFor("claude", stale)) == Environment.ProcessId, "Resuming takes the session's lock over the stale one.");
            Console.WriteLine("PASS Claude revival: Resume launches --resume past a stale lock and takes the lock");

            // Another SharpRail (a live process) holds the lock: the offer turns into "open elsewhere" with Start fresh only.
            const string held = "7d1c2b3a-2222-4c5d-8e9f-000000000002";
            var holder = System.Diagnostics.Process.Start("/bin/sleep", ["600"])!;
            others.Add(holder);
            File.WriteAllText(agents.PathFor("claude", held), AgentSessionOwnership.Content(holder.Id, AgentSessionOwnership.StartTicks(holder), new("/elsewhere", "terminal:other")));
            var (second, secondView) = Revive("terminal:resume-offer-held", held);
            var before = Launches().Length;
            secondView.AnswerResumeOffer(true);
            E2eWorkspace.Until(() => app.State.Current.ResumeOffers.Any(offer => offer.Terminal == second && offer.OpenElsewhere) &&
                secondView.ResumeOffer is { Visible: true, Resume: false, Fresh: true });
            Require(secondView.ResumeOffer.Text!.Contains("is open in another SharpRail", StringComparison.Ordinal) && Launches().Length == before,
                "A session another SharpRail holds is not resumed: " + secondView.ResumeOffer.Text);
            Require(!secondView.ResumeOffer.Quit && secondView.ResumeOffer.Text.Contains($"SharpRail window/instance (pid {holder.Id}), tab terminal:other", StringComparison.Ordinal),
                "A live SharpRail owner is named and never offered a quit: " + secondView.ResumeOffer.Text);
            Console.WriteLine("PASS Claude revival: a session locked by another live SharpRail is refused as open elsewhere");
            secondView.AnswerResumeOffer(false);
            E2eWorkspace.Until(() => Launches().Length > before);
            var fresh = Launches()[before];
            Require(fresh.Contains("--session-id ", StringComparison.Ordinal) && !fresh.Contains(held, StringComparison.Ordinal) && !fresh.Contains("--resume", StringComparison.Ordinal),
                "Start fresh launches a new session: " + fresh);
            E2eWorkspace.Until(() => !secondView.ResumeOffer.Visible && app.State.Current.TerminalAgents.FirstOrDefault(agent => agent.Terminal == second)?.Record.SessionId is { } next && next != held);
            Console.WriteLine("PASS Claude revival: Start fresh launches a new session id in the tab");

            // A process that names the session (a Claude started by hand, or by a SharpRail without locks) also blocks it.
            const string running = "7d1c2b3a-3333-4c5d-8e9f-000000000003";
            others.Add(System.Diagnostics.Process.Start("/bin/bash", ["-c", "sleep 600; :", "claude", "--resume", running])!);
            var (third, thirdView) = Revive("terminal:resume-offer-running", running);
            before = Launches().Length;
            thirdView.AnswerResumeOffer(true);
            E2eWorkspace.Until(() => app.State.Current.ResumeOffers.Any(offer => offer.Terminal == third && offer.OpenElsewhere) && thirdView.ResumeOffer is { Resume: false });
            Require(Launches().Length == before, "A session a running process names is not resumed.");
            Require(!thirdView.ResumeOffer.Quit && thirdView.ResumeOffer.Text!.Contains("pid ", StringComparison.Ordinal), "A process under a live SharpRail (this check run) is named and never offered a quit: " + thirdView.ResumeOffer.Text);
            Console.WriteLine("PASS Claude revival: a session a running process names is refused as open elsewhere");
            // A stray: nothing of a live SharpRail above it, a dead holder's lock left behind, and it ignores TERM and HUP.
            // Only processes this check spawned are ever signalled.
            agents.QuitGrace = TimeSpan.FromMilliseconds(400);
            (int Pid, int Child) Stray(string id)
            {
                var pidFile = Path.Combine(directory, "stray-" + id + ".pid");
                File.Delete(pidFile);
                const string body = "trap '' TERM HUP; echo $$ > \"$0\"; sleep 600 & echo $! >> \"$0\"; while :; do sleep 1; done";
                using (var launcher = System.Diagnostics.Process.Start("/bin/sh", ["-c", "\"$@\" </dev/null >/dev/null 2>&1 & exit 0", "x", "/bin/sh", "-c", body, pidFile, "claude", "--resume", id])!)
                    launcher.WaitForExit();
                E2eWorkspace.Until(() => File.Exists(pidFile) && File.ReadAllLines(pidFile).Length >= 2);
                var pids = File.ReadAllLines(pidFile).Select(line => int.Parse(line, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                spawned.AddRange(pids);
                using (var gone = System.Diagnostics.Process.Start("/bin/sh", ["-c", "exit 0"])!)
                {
                    gone.WaitForExit();
                    File.WriteAllText(agents.PathFor("claude", id), AgentSessionOwnership.Content(gone.Id, 1, new("/elsewhere", "terminal:gone")));
                }
                return (pids[0], pids[1]);
            }
            bool Alive(int pid) { try { using var process = System.Diagnostics.Process.GetProcessById(pid); return !process.HasExited; } catch (ArgumentException) { return false; } }

            // The owner of a tab's own shell tree is never quittable: here the stray is an ancestor of the asking shell.
            const string ancestor = "7d1c2b3a-5555-4c5d-8e9f-000000000005";
            var (ancestorPid, ancestorChild) = Stray(ancestor);
            var protectedOwner = agents.Inspect("claude", ancestor, [ancestorChild]);
            Require(protectedOwner is { Kind: SessionOwnerKind.Stray, CanQuit: false } && protectedOwner.Pid == ancestorPid, "A process that is an ancestor of the asking tab's shell is never quittable: " + protectedOwner);
            Console.WriteLine("PASS Claude revival: an ancestor of the asking tab's shell is not offered for quitting");

            const string stray = "7d1c2b3a-4444-4c5d-8e9f-000000000004";
            var (strayPid, strayChild) = Stray(stray);
            var (fourth, fourthView) = Revive("terminal:resume-offer-stray", stray);
            before = Launches().Length;
            fourthView.AnswerResumeOffer(true);
            E2eWorkspace.Until(() => fourthView.ResumeOffer is { Visible: true, Resume: false, Quit: true });
            Require(fourthView.ResumeOffer.Text!.Contains($"is open in pid {strayPid} (", StringComparison.Ordinal) &&
                fourthView.ResumeOffer.Text.Contains("SharpRail instance that started it is no longer running", StringComparison.Ordinal) && Launches().Length == before,
                "A stray owner is named by pid and offered a quit: " + fourthView.ResumeOffer.Text);
            Console.WriteLine("PASS Claude revival: a stray owner is named and offers Quit that process and resume");
            Require(fourthView.QuitResumeOffer(), "The quit button is pressable.");
            Button? quitConfirm = null;
            E2eWorkspace.Until(() => (quitConfirm = app.Window.OwnedWindows.SelectMany(window => window.GetLogicalDescendants().OfType<Button>())
                .FirstOrDefault(button => button.Name == "QuitSessionOwnerConfirm")) is not null);
            Require(Alive(strayPid) && Alive(strayChild), "Nothing is signalled before the user confirms.");
            app.Click(quitConfirm!);
            E2eWorkspace.Until(() => Launches().Skip(before).Any(line => line.Contains("--resume " + stray, StringComparison.Ordinal)));
            E2eWorkspace.Until(() => !Alive(strayPid) && !Alive(strayChild));
            Require(AgentSessionOwnership.Holder(agents.PathFor("claude", stray)) == Environment.ProcessId, "The stale lock is replaced by this host's before resuming.");
            Console.WriteLine("PASS Claude revival: a confirmed quit ends a TERM- and HUP-ignoring tree by SIGKILL and resumes");
            foreach (var tab in new[] { fourth })
            {
                var closedStray = Task.Run(async () => await app.State.Terminals!.CloseTabAsync(tab.WorkspaceId, tab.TabKey));
                E2eWorkspace.Until(() => closedStray.IsCompleted);
            }

            foreach (var tab in new[] { first, second, third })
            {
                var closed = Task.Run(async () => await app.State.Terminals!.CloseTabAsync(tab.WorkspaceId, tab.TabKey));
                E2eWorkspace.Until(() => closed.IsCompleted);
            }
            E2eWorkspace.Until(() => AgentSessionOwnership.Holder(agents.PathFor("claude", stale)) is null);
            Console.WriteLine("PASS Claude revival: closing the tab releases its session lock");
        }
        finally
        {
            foreach (var other in others) { try { other.Kill(true); } catch (InvalidOperationException) { } other.Dispose(); }
            foreach (var pid in spawned)
                try { using var left = System.Diagnostics.Process.GetProcessById(pid); left.Kill(true); }
                catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
            if (File.Exists(pids))
                foreach (var line in File.ReadAllLines(pids))
                    try { using var agent = System.Diagnostics.Process.GetProcessById(int.Parse(line, System.Globalization.CultureInfo.InvariantCulture)); agent.Kill(true); }
                    catch (ArgumentException) { }
                    catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// <c>workspace_emigrate</c>: a launched Claude tab may move only into a workspace it created, and only with the user's
    /// approval from the window's prompt. Refusals and a denial change nothing; an approval moves the tab into the target and
    /// relaunches the fake claude there resumed under the preassigned session id.
    /// </summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public static void Emigrate(string root)
    {
        var directory = Path.Combine(root, "claude-emigrate");
        using var environment = new IsolatedEnvironment(directory);
        using var app = new E2eWorkspace(Path.Combine(directory, "project"), openFiles: false, pluginTerminals: true);
        IsolatedGit.Run(app.Root, "init", "-b", "main");
        IsolatedGit.Run(app.Root, "add", ".");
        IsolatedGit.Run(app.Root, "commit", "-m", "Initial");
        E2eWorkspace.Until(() => app.Window.WorkspaceMounted && app.Workbench.PluginLoader!.Registry.Entry("claude-code") is not null);
        var registry = app.Workbench.PluginLoader!.Registry;
        var bin = Path.Combine(directory, "fake-bin");
        var agentBin = Path.Combine(directory, "agent-bin");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(agentBin);
        var named = Path.Combine(agentBin, "claude");
        if (!File.Exists(named)) File.CreateSymbolicLink(named, "/bin/bash");
        var log = Path.Combine(directory, "launched.log");
        var pidFile = Path.Combine(directory, "launched.pid");
        var fake = Path.Combine(bin, "claude");
        // Logs the directory and arguments of a session launch (a preassigned id or a resume), then runs as a process named claude.
        File.WriteAllText(fake, "#!/bin/bash\ncase \" $* \" in *\" --session-id \"*|*\" --resume \"*) ;; *) exit 0 ;; esac\n" +
            $"{{ pwd -P; printf '%s\\n' \"$@\"; }} > '{log}.part' && mv '{log}.part' '{log}'\n" +
            $"exec -a claude '{named}' -c 'echo $$ > \"$0\"; sleep 600; :' '{pidFile}' \"$@\"\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        app.State!.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(new { enabled = true, command = fake, appendSystemPrompt = false }))]).AsTask().GetAwaiter().GetResult();
        E2eWorkspace.Until(() => registry.LauncherList.FirstOrDefault(candidate => candidate.Id == "claude")?.TerminalCommand(new()).Contains(fake, StringComparison.Ordinal) == true);
        var opened = app.Window.OpenPluginTerminalAsync(app.Root, new() { Command = registry.LauncherList.Single(candidate => candidate.Id == "claude").TerminalCommand(new()) });
        E2eWorkspace.Until(() => opened.IsCompleted);
        var tab = new TerminalRef(app.Root, opened.GetAwaiter().GetResult());
        E2eWorkspace.Until(() => File.Exists(log) && File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0);
        var launched = File.ReadAllLines(log);
        var at = Array.IndexOf(launched, "--session-id");
        Require(at >= 0 && AgentResume.IsSessionId(launched[at + 1]), "The launch preassigns a session id: " + string.Join(" ", launched));
        var id = launched[at + 1];
        E2eWorkspace.Until(() => app.State.Current.TerminalAgents.FirstOrDefault(agent => agent.Terminal == tab)?.Record.SessionId == id);
        // The fake's process table shows bash's arguments; a real claude shows its own command, which the revival rebuilds from.
        app.State.SetTerminalAgent(tab, app.State.Current.TerminalAgents.Single(agent => agent.Terminal == tab).Record with { Command = fake + " --session-id " + id });
        // A saved conversation makes the relaunch a resume rather than a fresh start under the same id.
        Directory.CreateDirectory(Path.Combine(directory, "claude-home", "projects", "elsewhere"));
        File.WriteAllText(Path.Combine(directory, "claude-home", "projects", "elsewhere", id + ".jsonl"), "{}\n");

        var tools = new WorkspaceMcpTools(app.State) { EmigrationGrace = TimeSpan.FromMilliseconds(200) };
        (string Text, bool Error) Call(McpServer.McpTool tool, JsonObject arguments)
        {
            var call = Task.Run(async () => await tool.Call(arguments, default));
            E2eWorkspace.Until(() => call.IsCompleted);
            return call.GetAwaiter().GetResult();
        }
        string Created(JsonObject arguments, TerminalRef creator)
        {
            var created = Call(tools.Create(app.Root, creator), arguments);
            Require(!created.Error, "workspace_create must succeed: " + created.Text);
            return JsonNode.Parse(created.Text)!["path"]!.GetValue<string>();
        }
        var target = Created(new JsonObject { ["description"] = "Emigration target" }, tab);
        var foreign = Created(new JsonObject { ["description"] = "Another terminal's workspace" }, new TerminalRef(app.Root, "another-terminal"));
        Require(app.State.WorkspaceCreator(target) == tab && app.State.WorkspaceCreator(foreign) != tab, "workspace_create records the calling terminal as the creator.");
        bool Unchanged() => app.State.Current.PendingEmigrations.Count == 0 && app.State.Current.TerminalAgents.Any(agent => agent.Terminal == tab) &&
            app.State.Terminals!.Catalog.Workspaces.GetValueOrDefault(app.Root)?.Any(known => known.Key == tab.TabKey) == true &&
            app.State.Terminals.Catalog.Workspaces.GetValueOrDefault(target)?.Any(known => known.Key == tab.TabKey) != true;
        foreach (var (caller, path, expected) in new[]
        {
            (tab, foreign, "created with workspace_create"),
            (tab, app.Root, "Default workspace"),
            (tab, Path.Combine(directory, "nowhere"), "not a SharpRail workspace"),
            (new TerminalRef(app.Root, "plain-shell"), target, "only a Claude Code or Codex terminal")
        })
        {
            var refused = Call(tools.Emigrate(caller), new JsonObject { ["path"] = path });
            Require(refused.Error && refused.Text.Contains(expected, StringComparison.Ordinal) && Unchanged(), $"The tool refuses ({expected}) and changes nothing: {refused.Text}");
        }
        Console.WriteLine("PASS Workspace emigrate: refuses another terminal's workspace, Default, unknown paths and non-agent terminals");

        Toast Prompt() => app.Window.Toasts.Items.Single(toast => toast.Title == "Move agent terminal?");
        Task<(string Text, bool Error)> Ask() => Task.Run(async () => await tools.Emigrate(tab).Call(new JsonObject { ["path"] = target, ["reason"] = "The user asked for a new workspace" }, default));
        var denied = Ask();
        E2eWorkspace.Until(() => app.State.Current.PendingEmigrations.Count == 1 && app.Window.Toasts.Items.Any(toast => toast.Title == "Move agent terminal?"));
        Require(Prompt().Message.Contains(target, StringComparison.Ordinal) && Prompt().Message.Contains(app.Root, StringComparison.Ordinal) &&
            Prompt().Message.Contains("The user asked for a new workspace", StringComparison.Ordinal), "The prompt names both workspaces and the agent's reason: " + Prompt().Message);
        Prompt().Secondary!.Invoke();
        E2eWorkspace.Until(() => denied.IsCompleted);
        Require(denied.GetAwaiter().GetResult() is { Error: true } refusal && refusal.Text.Contains("denied", StringComparison.Ordinal) && Unchanged() && tools.LastEmigration is null,
            "A denial returns refused and changes nothing.");
        E2eWorkspace.Until(() => app.Window.Toasts.Items.All(toast => toast.Title != "Move agent terminal?"));
        Console.WriteLine("PASS Workspace emigrate: a denied prompt leaves the tab and its agent where they were");

        File.Delete(log);
        File.Delete(pidFile);
        var approved = Ask();
        E2eWorkspace.Until(() => app.Window.Toasts.Items.Any(toast => toast.Title == "Move agent terminal?"));
        Prompt().Action!.Invoke();
        E2eWorkspace.Until(() => approved.IsCompleted);
        Require(approved.GetAwaiter().GetResult() is { Error: false } reply && reply.Text.Contains("relaunched", StringComparison.Ordinal), "An approval tells the agent it will be relaunched.");
        E2eWorkspace.Until(() => tools.LastEmigration is { IsCompleted: true });
        Require(tools.LastEmigration!.IsCompletedSuccessfully, "The move completes.");
        var moved = new TerminalRef(target, tab.TabKey);
        var catalog = app.State.Terminals!.Catalog;
        Require(catalog.Workspaces.GetValueOrDefault(target)?.Any(known => known.Key == tab.TabKey) == true &&
            catalog.Workspaces.GetValueOrDefault(app.Root)?.Any(known => known.Key == tab.TabKey) != true, "The tab now belongs to the target workspace.");
        E2eWorkspace.Until(() => File.Exists(log) && File.Exists(pidFile) && File.ReadAllText(pidFile).Trim().Length > 0);
        var relaunched = File.ReadAllLines(log);
        var resume = Array.IndexOf(relaunched, "--resume");
        Require(resume > 0 && relaunched[resume + 1] == id, "The relaunch resumes the preassigned session: " + string.Join(" ", relaunched));
        Require(relaunched[0] == Real(target), $"The relaunch runs in the target workspace: {relaunched[0]} is not {Real(target)}");
        E2eWorkspace.Until(() => app.State.Current.TerminalAgents.FirstOrDefault(agent => agent.Terminal == moved)?.Record.SessionId == id &&
            app.State.Current.TerminalAgents.All(agent => agent.Terminal != tab));
        Console.WriteLine("PASS Workspace emigrate: an approved move relaunches the agent resumed in the target workspace's tab");
        var closed = Task.Run(async () => await app.State.Terminals.CloseTabAsync(target, tab.TabKey));
        E2eWorkspace.Until(() => closed.IsCompleted);

        static string Real(string path)
        {
            using var shell = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", "cd \"$0\" && pwd -P", path]) { RedirectStandardOutput = true })!;
            var output = shell.StandardOutput.ReadToEnd().Trim();
            shell.WaitForExit();
            return output;
        }
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
        File.WriteAllText(fake, $"#!/bin/sh\nif [ \"$1\" = --help ]; then printf '%s\\n' '  --permission-mode <mode>   Permission mode to use (choices: \"acceptEdits\", \"manual\",' '      \"plan\", \"bypassPermissions\")'; exit 0; fi\nprintf '%s\\n' \"agent-view=$CLAUDE_CODE_DISABLE_AGENT_VIEW\" \"$@\" > '{log}.part' && mv '{log}.part' '{log}'\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var app = new E2eWorkspace(Path.Combine(directory, "project"), openFiles: false);
        E2eWorkspace.Until(() => app.Workbench.PluginLoader!.Registry.Entry("claude-code") is not null);
        Require(Named(app.Window, "NewClaude").Count == 0, "No Claude launcher while the plugin is off.");
        void Settings(object settings) => app.State!.ChangeAsync([HostStateChange.PluginSettings("claude-code", JsonSerializer.Serialize(settings))]).AsTask().GetAwaiter().GetResult();
        Settings(new { enabled = true, command = fake });
        E2eWorkspace.Until(() => Named(app.Window, "NewClaude").Count > 0);
        Require(ToolTip.GetTip(Named(app.Window, "NewClaude")[0]) is string tip && tip.StartsWith("Start Claude Code", StringComparison.Ordinal), "The launcher says what it starts.");
        Require(Named(app.Window, "NewClaude").All(launcher => launcher.GetVisualAncestors().OfType<Panel>().Any(row =>
                row.Children.Any(child => child.Name?.StartsWith("NewTerminal_", StringComparison.Ordinal) == true)) &&
            launcher.GetVisualAncestors().Any(ancestor => ancestor.Name == "WorkspaceTabs")),
            "Workspace actions follow New terminal under their workspace in Projects.");

        int Terminals() => app.Window.Layout.State.Groups.Sum(group => app.Window.Layout.Tabs(group.Id).Count(tab => tab.Kind == "terminal"));
        Button? CloseConfirmation() => app.Window.OwnedWindows.SelectMany(window => window.GetLogicalDescendants().OfType<Button>())
            .FirstOrDefault(button => button.Name == "TerminalCloseBusyConfirm");
        // The preassigned id of the last launch, taken out of the arguments the assertions below compare.
        string? launchedId = null;
        string[] Launch(string preset, Action? choose = null)
        {
            File.Delete(log);
            E2eWorkspace.Settle(550);
            // The terminals the last run closed may still be asking whether to end a running process, and a tab that is still closing
            // would leave before-and-after counts equal.
            E2eWorkspace.Until(() => { app.ConfirmDialog("TerminalCloseBusyConfirm"); return Terminals() == 0; });
            app.UntilDialogsGone("TerminalCloseBusyConfirm");
            var before = Terminals();
            var button = (Button)Named(app.Window, "NewClaude").First(control => control.IsEffectivelyVisible);
            app.Click(button, freshGesture: false, mouseButton: MouseButton.Right);
            var menu = button.ContextMenu!;
            E2eWorkspace.Until(() => menu.IsOpen);
            var items = menu.Items.OfType<MenuItem>().ToDictionary(item => item.Name!);
            Require((string)items["ClaudeLaunch_continue"].Header! == "Continue the last conversation" && items.ContainsKey("ClaudeLaunch_model-opus") &&
                ((string)items["ClaudeLaunch_teleport"].Header!).StartsWith("Teleport", StringComparison.Ordinal), "The menu offers continue, teleport and the models.");
            if (preset.StartsWith("permission-", StringComparison.Ordinal) || preset.StartsWith("advisor-", StringComparison.Ordinal))
            {
                var submenu = items["ClaudeLaunch_" + preset.Split('-')[0]];
                submenu.IsSubMenuOpen = true;
                E2eWorkspace.Until(() => submenu.Items.OfType<MenuItem>().Any(item => item.Name == "ClaudeLaunch_" + preset));
                app.Click(submenu.Items.OfType<MenuItem>().Single(item => item.Name == "ClaudeLaunch_" + preset), freshGesture: false);
            }
            else app.Click(items["ClaudeLaunch_" + preset], freshGesture: false);
            choose?.Invoke();
            app.UntilDialogsGone();
            E2eWorkspace.Until(() => { app.ConfirmDialog("TerminalCloseBusyConfirm"); return !menu.IsOpen && Terminals() == before + 1; });
            Require(app.Window.Layout.Tabs(app.Center).Any(tab => tab.Kind == "terminal"), "Claude starts in the launcher's own group.");
            E2eWorkspace.Until(() => File.Exists(log));
            var lines = File.ReadAllLines(log);
            var at = Array.IndexOf(lines, "--session-id");
            launchedId = at >= 0 ? lines[at + 1] : null;
            if (at >= 0) lines = [.. lines[..at], .. lines[(at + 2)..]];
            // As the Codex launcher check does: close the started terminals so the empty group offers the launcher again.
            foreach (var group in app.Window.Layout.State.Groups.ToArray())
                foreach (var tab in app.Window.Layout.Tabs(group.Id).Where(tab => tab.Kind == "terminal").ToArray())
                    app.Window.Layout.Close(group.Id, tab.Id);
            // The fake may still be running when its log appears, so the close can rightly ask first: confirm it, or the
            // dialog stays open over the next launch's button.
            E2eWorkspace.Until(() => Terminals() == 0 || CloseConfirmation() is not null);
            if (CloseConfirmation() is { } confirm) app.Click(confirm, freshGesture: false);
            E2eWorkspace.Until(() => Terminals() == 0 && CloseConfirmation() is null);
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
        Require(sonnet.SequenceEqual(["agent-view=", "--plugin-dir", Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets", "marketplace", "claude-plugin"), "--add-dir", ScratchProbe, "--model", "sonnet"]), "Disabling the prompt appendix retains the directly loaded plugin: " + string.Join(" ", sonnet));
        Require(launchedId is { } assigned && AgentResume.IsSessionId(assigned), "A new conversation starts under a preassigned --session-id.");
        Require(Launch("continue")[^1] == "--continue" && launchedId is null, "Continue passes the per-run flag, and no preassigned id.");
        Settings(new { enabled = true, command = fake, disableAgentView = false, appendSystemPrompt = false, model = "opus", effort = "high" });
        Require(Launch("continue").SequenceEqual(["agent-view=", "--plugin-dir", Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets", "marketplace", "claude-plugin"), "--add-dir", ScratchProbe, "--model", "opus", "--effort", "high", "--continue"]),
            "The SharpRail defaults reach the shell for a new launch.");
        Require(Launch("model-haiku").SequenceEqual(["agent-view=", "--plugin-dir", Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets", "marketplace", "claude-plugin"), "--add-dir", ScratchProbe, "--effort", "high", "--model", "haiku"]),
            "A per-launch model replaces the saved model without duplicating flags.");
        // Any flags at all: typed into a dialog, they follow the prompt file like a preset's.
        var custom = Launch("custom", () =>
        {
            E2eWorkspace.Until(() => app.Window.OwnedWindows.Any(window => Equals(window.Tag, "ClaudeCustomLaunch")));
            var dialog = app.Window.OwnedWindows.Single(window => Equals(window.Tag, "ClaudeCustomLaunch"));
            dialog.GetLogicalDescendants().OfType<TextBox>().Single(box => box.Name == "ClaudeCustomArguments").Text = "--permission-mode plan --verbose";
            app.Click(dialog.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "ClaudeCustomStart"), freshGesture: false);
        });
        Require(custom[^3..].SequenceEqual(["--permission-mode", "plan", "--verbose"]), "Custom arguments reach the command line: " + string.Join(" ", custom));
        // Permission modes come from the fake CLI's help, not a built-in list: "manual" is only there.
        E2eWorkspace.Until(() => SharpRail.Plugins.ClaudeCode.UI.PermissionModeSource.Current.FromCli);
        Require(SharpRail.Plugins.ClaudeCode.UI.PermissionModeSource.Current.Modes.SequenceEqual(["acceptEdits", "manual", "plan", "bypassPermissions"]),
            "The launcher offers the modes the CLI's help lists.");
        var planned = Launch("permission-plan");
        Require(planned[^2..].SequenceEqual(["--permission-mode", "plan"]), "A per-launch mode passes --permission-mode: " + string.Join(" ", planned));
        Require(!Launch("continue").Contains("--permission-mode"), "Without a saved mode the flag is left off.");
        Settings(new { enabled = true, command = fake, disableAgentView = false, appendSystemPrompt = false, permissionMode = "acceptEdits" });
        var saved = Launch("continue");
        Require(saved[^3..].SequenceEqual(["--permission-mode", "acceptEdits", "--continue"]),
            "The saved mode reaches every launch: " + string.Join(" ", saved));
        Require(!Launch("permission-default").Contains("--permission-mode"), "Claude Code's default for one run omits even the saved mode.");
        Require(Launch("permission-bypassPermissions").Count(word => word == "--permission-mode") == 1, "A per-launch mode replaces the saved one without duplicating the flag.");
        {
            var button = (Button)Named(app.Window, "NewClaude").First(control => control.IsEffectivelyVisible);
            var bypass = button.ContextMenu!.Items.OfType<MenuItem>().Single(item => item.Name == "ClaudeLaunch_permission").Items.OfType<MenuItem>().Single(item => item.Name == "ClaudeLaunch_permission-bypassPermissions");
            Require(ReferenceEquals(bypass.Foreground, SharpRail.Plugins.UI.Kit.Ui.Danger) && ((string)bypass.Header!).Contains("dangerous", StringComparison.Ordinal),
                "Bypass permissions is marked dangerous in the launcher.");
        }
        Settings(new { enabled = true, command = fake, disableAgentView = false, appendSystemPrompt = false, permissionMode = "" });
        Require(Launch("advisor-opus")[^2..].SequenceEqual(["--advisor", "opus"]), "A per-launch advisor passes --advisor.");
        Require(Launch("chrome")[^1] == "--chrome", "The Chrome toggle turns it on for one run.");
        Require(ClaudeTerminalAccessory.Facts(new ClaudeSessionState(ClaudeCodeStatus.Idle), "claude --chrome").Any(fact => fact is { Kind: "chrome", Label: "chrome" }),
            "A run started with --chrome shows a lowercase chrome chip.");
        var modelFacts = ClaudeTerminalAccessory.Facts(new ClaudeSessionState(ClaudeCodeStatus.Idle) { Model = "claude-sonnet-5-5-20260101", Effort = "low" });
        Require(modelFacts.Any(fact => fact is { Kind: "model", Label: "sonnet-5-5" }) && modelFacts.Any(fact => fact is { Kind: "effort", Label: "low effort" }),
            "The model and effort chips show what Claude Code's hooks last reported.");
        Require(ClaudeTerminalAccessory.RowOrder(["attach", "advisor", "usage", "plan", "chrome", "ide", "effort", "model", "cwd"]).SequenceEqual(["cwd", "model", "effort", "ide", "chrome", "plan", "usage", "advisor", "attach"]) &&
            ClaudeTerminalAccessory.RowOrder(["attach", "ide", "model"]).SequenceEqual(["model", "ide", "attach"]),
            "The chip row reads folder, model, effort, IDE context, Chrome, plan, spending, advisor, attach.");
        const string Box = "────────────────────────────";
        string[] Empty = ["● done", Box, "❯ Message @agent…", Box, "  ? for shortcuts"];
        Require(ClaudeTerminalAccessory.Prompt(Empty) == PromptState.Empty && ClaudeTerminalAccessory.Prompt(["", Box, "❯ ", Box]) == PromptState.Empty &&
            ClaudeTerminalAccessory.Prompt([Box, "❯ Press up to edit queued messages", Box]) == PromptState.Empty, "an empty prompt, placeholder omitted or shown, is empty");
        Require(ClaudeTerminalAccessory.Prompt([Box, "❯ fix the build", Box]) == PromptState.Draft && ClaudeTerminalAccessory.Prompt([Box, "│ ❯ fix the build        │", Box]) == PromptState.Draft,
            "a one-line draft is a draft");
        Require(ClaudeTerminalAccessory.Prompt([Box, "❯", "  second line of a draft", Box]) == PromptState.Draft && ClaudeTerminalAccessory.Prompt([Box, "❯ first", "  second", Box]) == PromptState.Draft,
            "a multi-line draft is a draft");
        Require(ClaudeTerminalAccessory.Prompt([Box, "❯ /mo", Box, "  /model   Set the AI model for Claude Code"]) == PromptState.Draft, "an open slash-command menu means a draft");
        Require(ClaudeTerminalAccessory.Prompt(["Select model", "❯ 1. Default (recommended)  x", "  2. Opus"]) == PromptState.NotAtPrompt &&
            ClaudeTerminalAccessory.Prompt(["some unknown dialog", "Press Enter to continue"]) == PromptState.NotAtPrompt && ClaudeTerminalAccessory.Prompt([]) == PromptState.NotAtPrompt,
            "an open picker or an unknown screen is not the prompt");
        (bool Typed, TypingTerminal Terminal, string? Notice) Click(string[] screen, ClaudeCodeStatus status = ClaudeCodeStatus.Idle, bool current = true)
        {
            var terminal = new TypingTerminal { Screen = screen };
            string? notice = null;
            var typed = ClaudeTerminalAccessory.TypeIfPromptEmptyAsync(terminal, "/model\r", status, () => current, text => notice = text).GetAwaiter().GetResult();
            return (typed, terminal, notice);
        }
        var clicked = Click(Empty);
        Require(clicked.Typed && clicked.Terminal.Writes.SequenceEqual(["/model\r"]), "Clicking over an empty prompt types /model and Enter once, never a selection.");
        var overDraft = Click([Box, "❯ fix the build", Box]);
        Require(!overDraft.Typed && overDraft.Terminal.Writes.Count == 0 && overDraft.Notice == "Clear the prompt first: it holds a draft.", "A draft is never appended to.");
        var unknown = Click(["some dialog"]);
        Require(!unknown.Typed && unknown.Terminal.Writes.Count == 0 && unknown.Notice == "Not at the prompt.", "An unknown screen is never typed into.");
        Require((Click(Empty, ClaudeCodeStatus.Running)).Terminal.Writes.Count == 0 && (Click(Empty, current: false)).Terminal.Writes.Count == 0, "A running turn or a changed tab types nothing.");
        Require(((Border)Ui.Icon("chrome")).OpacityMask is Avalonia.Media.DrawingBrush, "The Chrome chip's icon is a vector asset.");
        var plainRun = Launch("continue");
        Require(!plainRun.Contains("--advisor") && !plainRun.Contains("--chrome") && !plainRun.Contains("--no-chrome"), "No advisor and Chrome off add no flags.");
        Settings(new { enabled = true, command = fake, disableAgentView = false, appendSystemPrompt = false, advisor = "sonnet", chrome = true });
        var always = Launch("continue");
        Require(always[^4..].SequenceEqual(["--advisor", "sonnet", "--chrome", "--continue"]), "Saved advisor and Chrome reach every launch: " + string.Join(" ", always));
        var withoutChrome = Launch("chrome");
        Require(withoutChrome.Contains("--no-chrome") && !withoutChrome.Contains("--chrome"), "With Chrome always on, the toggle turns it off for one run.");
        Require(!Launch("advisor-none").Contains("--advisor"), "None omits the advisor for one run.");
        Console.WriteLine("PASS fork plugins/claude-code/claude-launcher.spec.ts: the launcher starts Claude Code in its own group, and offers per-run flags on right-click");

        Settings(new { enabled = true, command = "claude" });
        app.Window.ShowSettings("plugin:claude-code:claude-code");
        E2eWorkspace.Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var window = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        TextBox Command() => window.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "ClaudeCommandInput");
        E2eWorkspace.Until(() => Command().Text == "claude");
        var defaultModel = window.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "ClaudeDefaultModel");
        defaultModel.Text = "sonnet";
        defaultModel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        E2eWorkspace.Until(() => app.State!.Current.PluginSettings["claude-code"].TryGetProperty("model", out var value) && value.GetString() == "sonnet");
        var permission = window.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "ClaudePermissionMode");
        E2eWorkspace.Until(() => permission.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, "bypassPermissions")));
        var dangerous = permission.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "bypassPermissions"));
        Require(ReferenceEquals(dangerous.Foreground, SharpRail.Plugins.UI.Kit.Ui.Danger), "Bypass permissions is marked dangerous in Settings.");
        permission.SelectedItem = dangerous;
        E2eWorkspace.Until(() => app.State!.Current.PluginSettings["claude-code"].TryGetProperty("permissionMode", out var value) && value.GetString() == "bypassPermissions");
        Require(ReferenceEquals(window.GetLogicalDescendants().OfType<TextBlock>().Single(control => control.Name == "ClaudePermissionModeNote").Foreground, SharpRail.Plugins.UI.Kit.Ui.Danger),
            "Settings warns while the default skips permission checks.");
        Require(app.Workbench.PluginLoader!.Registry.LauncherList.Single(item => item.Id == "claude").TerminalCommand(new()).Contains("--permission-mode bypassPermissions", StringComparison.Ordinal),
            "The saved default applies to the generic launcher too.");
        var advisorPicker = window.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "ClaudeDefaultAdvisor");
        advisorPicker.SelectedItem = advisorPicker.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "opus"));
        E2eWorkspace.Until(() => app.State!.Current.PluginSettings["claude-code"].TryGetProperty("advisor", out var value) && value.GetString() == "opus");
        window.GetLogicalDescendants().OfType<CheckBox>().Single(control => control.Name == "ClaudeChrome").IsChecked = true;
        E2eWorkspace.Until(() => app.State!.Current.PluginSettings["claude-code"].TryGetProperty("chrome", out var value) && value.GetBoolean());
        Require(app.Workbench.PluginLoader!.Registry.LauncherList.Single(item => item.Id == "claude").TerminalCommand(new()).Contains("--advisor opus --chrome", StringComparison.Ordinal),
            "The saved advisor and Chrome persist and reach the generic launcher.");
        advisorPicker.SelectedItem = advisorPicker.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, ""));
        E2eWorkspace.Until(() => app.State!.Current.PluginSettings["claude-code"].GetProperty("advisor").GetString() == "");
        window.GetLogicalDescendants().OfType<CheckBox>().Single(control => control.Name == "ClaudeChrome").IsChecked = false;
        E2eWorkspace.Until(() => app.State!.Current.PluginSettings["claude-code"].GetProperty("advisor").GetString() == "" && !app.State!.Current.PluginSettings["claude-code"].GetProperty("chrome").GetBoolean());
        permission.SelectedItem = permission.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, ""));
        E2eWorkspace.Until(() => app.State!.Current.PluginSettings["claude-code"].GetProperty("permissionMode").GetString() == "");
        Require(!app.Workbench.PluginLoader!.Registry.LauncherList.Single(item => item.Id == "claude").TerminalCommand(new()).Contains("--permission-mode", StringComparison.Ordinal),
            "Claude Code's default omits the flag.");
        var defaultEffort = window.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "ClaudeDefaultEffort");
        defaultEffort.SelectedItem = defaultEffort.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, "high"));
        E2eWorkspace.Until(() => app.State!.Current.PluginSettings["claude-code"].TryGetProperty("effort", out var value) && value.GetString() == "high");
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
        Require(window.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "ClaudeDefaultModel").Text == "sonnet" &&
            Equals((window.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "ClaudeDefaultEffort").SelectedItem as ComboBoxItem)?.Tag, "high"),
            "Reopened settings restore the saved model and effort.");
        Enter("   ");
        E2eWorkspace.Until(() => Command().Text == "claude" && Saved() == "claude");
        defaultModel = window.GetLogicalDescendants().OfType<TextBox>().Single(control => control.Name == "ClaudeDefaultModel");
        defaultModel.Text = "";
        defaultModel.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
        E2eWorkspace.Until(() => app.State!.Current.PluginSettings["claude-code"].GetProperty("model").GetString() == "");
        defaultEffort = window.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "ClaudeDefaultEffort");
        defaultEffort.SelectedItem = defaultEffort.Items.OfType<ComboBoxItem>().Single(item => Equals(item.Tag, ""));
        E2eWorkspace.Until(() => app.State!.Current.PluginSettings["claude-code"].GetProperty("effort").GetString() == "");
        var plain = app.Workbench.PluginLoader!.Registry.LauncherList.Single(item => item.Id == "claude").TerminalCommand(new());
        Require(!plain.Contains("--model", StringComparison.Ordinal) && !plain.Contains("--effort", StringComparison.Ordinal),
            "Clearing the defaults removes model and effort flags from subsequent launches.");
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
        Require((await Call(ClaudeCodeContract.ContextUsageGet, new ContextUsageParams(project))).Running, "The host measures a missing baseline in the background.");
        ClaudeContextUsage measured;
        do { await Task.Delay(50); measured = await Call(ClaudeCodeContract.ContextUsageGet, new ContextUsageParams(project)); } while (measured.Running);
        Require(measured is { Breakdown: null, Error.Length: > 0 }, "A CLI that prints no report leaves an error to show, not a stuck measurement.");
        var prompt = await Call(ClaudeCodeContract.SystemPromptGet, new NoParams());
        var shipped = File.ReadAllText(prompt.Path);
        Require(prompt.IgnoredEdits is null && shipped.Length > 0 && runtime.AllowsExternalFile(project, prompt.Path),
            "Activation writes the appended system prompt, which any workspace may open.");
        Require(shipped.Contains("`workspace_create`", StringComparison.Ordinal) && shipped.Contains("`description`", StringComparison.Ordinal),
            "The appended prompt directs workspace creation through the tool with a visible description.");
        Require(shipped.Contains("`set_title`", StringComparison.Ordinal) && shipped.Contains("`title`", StringComparison.Ordinal) && !shipped.Contains("workspace_name", StringComparison.Ordinal),
            "The appended prompt asks the agent to title its terminal, not to name its workspace.");
        Require(shipped.Contains("`workspace_delete`", StringComparison.Ordinal), "The appended prompt directs workspace cleanup through the tool.");
        Require(OperatingSystem.IsWindows() || File.GetAttributes(prompt.Path).HasFlag(FileAttributes.ReadOnly), "The generated system prompt is read-only on disk.");
        // Hand edits (forced past the read-only mode) are never used: the file is regenerated and the edits are kept aside once.
        File.SetAttributes(prompt.Path, File.GetAttributes(prompt.Path) & ~FileAttributes.ReadOnly);
        File.WriteAllText(prompt.Path, "my own instructions\n");
        await state.ChangeAsync([HostStateChange.PluginEnabled("claude-code", false)]);
        await runtime.Schedule();
        await state.ChangeAsync([HostStateChange.PluginEnabled("claude-code", true)]);
        await runtime.Schedule();
        var migrated = await Call(ClaudeCodeContract.SystemPromptGet, new NoParams());
        Require((await runtime.ListAsync()).Single().Status == PluginStatus.Active && File.ReadAllText(prompt.Path) == shipped &&
            migrated.IgnoredEdits is { } kept && File.ReadAllText(kept) == "my own instructions\n",
            "Reactivating regenerates the system prompt and keeps the user's earlier edits aside, unused.");
        Require(OperatingSystem.IsWindows() || File.GetAttributes(prompt.Path).HasFlag(FileAttributes.ReadOnly), "The regenerated system prompt is read-only again.");
        // Every launch writes its workspace's file from the sections: a hand edit there is overwritten too, atomically and read-only.
        var launchFile = SystemPrompt.WriteFor(project, [], ["only this"]);
        File.SetAttributes(launchFile, File.GetAttributes(launchFile) & ~FileAttributes.ReadOnly);
        File.WriteAllText(launchFile, "my own instructions\n");
        Require(SystemPrompt.WriteFor(project, [], ["only this"]) == launchFile && File.ReadAllText(launchFile) == SystemPrompt.Compose([], ["only this"]) &&
            (OperatingSystem.IsWindows() || File.GetAttributes(launchFile).HasFlag(FileAttributes.ReadOnly)) && GeneratedInstructions.Owns(launchFile) && GeneratedInstructions.Owns(prompt.Path) &&
            !GeneratedInstructions.Owns(migrated.IgnoredEdits!), "A launch regenerates its hand-edited workspace file read-only; the kept edits are not generated.");
        var listed = await Call(ClaudeCodeContract.InstructionsList, new InstructionSectionQuery { WorkspaceId = project });
        Require(listed.IgnoredEditsPath == migrated.IgnoredEdits && listed.Sections.Any(section => section.Id == "core.workspaces" && section.Enabled),
            "The instructions listing names the kept edits and offers the core sections, on by default.");
        var toggled = await Call(ClaudeCodeContract.InstructionsSet, new InstructionSectionToggle { WorkspaceId = project, Id = "core.workspaces", Enabled = false });
        Require(toggled.Sections.Single(section => section.Id == "core.workspaces").Enabled == false &&
            File.ReadAllText(Path.Combine(project, ".sharprail", "instructions.toml")).Contains("\"core.workspaces\"", StringComparison.Ordinal),
            "Turning a section off through the agent's contract writes the project's disable-list.");
        await Call(ClaudeCodeContract.InstructionsSet, new InstructionSectionToggle { WorkspaceId = project, Id = "core.workspaces", Enabled = true });
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
            File.Exists(variables[ClaudeCodeContract.PromptFileVariable]) &&
            File.Exists(Path.Combine(variables[ClaudeCodeContract.PluginDirectoryVariable], ".claude-plugin", "plugin.json")),
            "The shell receives its token-bound status route, prompt file and bundled plugin directory.");
        var transcript = Path.Combine(directory, "session.jsonl");
        File.WriteAllText(transcript, "{\"type\":\"assistant\",\"message\":{\"id\":\"turn\",\"usage\":{\"input_tokens\":5,\"output_tokens\":2}}}\n");
        async Task<int> Report(string token, string body, string method = "POST") =>
            (await runtime.ServeRouteAsync("claude-code", new(method, "status/" + token, "", new Dictionary<string, string>(), Encoding.UTF8.GetBytes(body))))!.Status;
        Require(await Report("unknown", "{}") == 404 && await Report("claude-fixture-token", "{}", "GET") == 405,
            "The status route refuses an unknown terminal and a non-POST method.");
        var report = JsonSerializer.Serialize(new { @event = "prompt_submit", session_id = "fixture-session", cwd = project, transcript_path = transcript, model = "sonnet", effort = "medium" });
        state.SetTerminalAgent(terminal, new("claude", "claude --model sonnet --effort medium") { LaunchedByUi = false });
        Require(await Report("claude-fixture-token", report) == 200, "A hook report reaches its terminal.");
        var rows = await Call(ClaudeCodeContract.StatusSnapshot, new StatusSnapshotParams(project));
        Require(rows is [{ Status: ClaudeCodeStatus.Running, Usage.Input: 5, Report.Model: "sonnet" }] &&
            state.Current.TerminalAgents.Single().Record is { SessionId: "fixture-session", Model: "sonnet", Effort: "medium", LaunchedByUi: false }, "A report persists session model and effort while retaining manual launch origin.");
        await Report("claude-fixture-token", "{\"event\":\"model_switch\",\"model\":\"opus\"}");
        await Report("claude-fixture-token", "{\"event\":\"stop\",\"effort\":\"high\"}");
        await Report("claude-fixture-token", "{\"event\":\"stop\",\"notify\":false}");
        rows = await Call(ClaudeCodeContract.StatusSnapshot, new StatusSnapshotParams(project));
        Require(rows.Single().Status == ClaudeCodeStatus.Done && rows.Single().Report.Notify == false, "A continuation Stop settles the badge while suppressing notification.");
        // Claude's own OSC 133 marks (a foreground program's) drive the badge where they arrive; the shell's do not, and an
        // idle mark keeps the hook's Done.
        async Task<ClaudeCodeStatus> Marked(bool atPrompt, bool foreground)
        {
            terminals.Lifecycle!(new TerminalPromptChanged(terminal, atPrompt, null, foreground));
            await Task.Delay(100);
            return (await Call(ClaudeCodeContract.StatusSnapshot, new StatusSnapshotParams(project))).Single().Status ?? ClaudeCodeStatus.Idle;
        }
        Require(await Marked(true, true) == ClaudeCodeStatus.Done, "An idle mark keeps the hook's Done.");
        Require(await Marked(false, false) == ClaudeCodeStatus.Done, "The shell's own running mark is not the agent's.");
        Require(await Marked(false, true) == ClaudeCodeStatus.Running, "Claude's running mark shows it working.");
        Require(await Marked(true, true) == ClaudeCodeStatus.Idle, "Claude's prompt mark shows it idle.");
        await Report("claude-fixture-token", "{\"event\":\"stop\",\"notify\":false}");
        Require(state.Current.TerminalAgents.Single().Record is { Model: "opus", Effort: "high" } &&
            terminals.RevivePrefill!(terminal)?.Text.Contains("--model opus --effort high", StringComparison.Ordinal) == true &&
            !terminals.RevivePrefill!(terminal)!.Text!.Contains("sonnet", StringComparison.Ordinal) && !terminals.RevivePrefill!(terminal)!.Text!.Contains("medium", StringComparison.Ordinal),
            "Revival restores the session's latest model and effort, including updates without a session id and sparse later reports.");
        // The record above says manual: a revival is SharpRail's own launch whatever started the session it resumes.
        // The environment is not in the process table the command is rebuilt from, so revival reapplies the agent-view setting too.
        Require(OperatingSystem.IsWindows() || terminals.RevivePrefill!(terminal)?.Text.StartsWith("SHARPRAIL_UI_LAUNCH=claude CLAUDE_CODE_DISABLE_AGENT_VIEW=true claude ", StringComparison.Ordinal) == true,
            "A revived session is launched by SharpRail, never labelled manual, with the agent view off as the setting says: " + terminals.RevivePrefill!(terminal)?.Text);
        Require(terminals.RevivePrefill!(terminal)?.Text.Contains("--continue", StringComparison.Ordinal) == true,
            "A Claude terminal with no surviving transcript gets the fork's continue offer.");
        await SessionIdRevival(state, terminals, terminal, Report);
        Require(ClaudePermissionModes.Parse("  --permission-mode <mode>   Permission mode to use for the session\n      (choices: \"acceptEdits\", \"auto\",\n      \"bypassPermissions\", \"plan\")\n  --print").SequenceEqual(["acceptEdits", "auto", "bypassPermissions", "plan"]) &&
            ClaudePermissionModes.Parse("no such option").Count == 0, "Permission modes are read from the help's choices, wrapped or not.");
        Require(AgentResume.ResumeCommand("claude --permission-mode plan --model opus", "11111111-2222-3333-4444-555555555555")!.Contains("--permission-mode plan --model opus --resume", StringComparison.Ordinal),
            "A revived session keeps the permission mode it was started with.");
        Require(AgentResume.ResumeCommand("claude --advisor opus --no-chrome", "11111111-2222-3333-4444-555555555555") == "claude --advisor opus --no-chrome --resume 11111111-2222-3333-4444-555555555555",
            "A revived session keeps its advisor and Chrome choice.");
        Require((await Call(ClaudeCodeContract.PermissionModesGet, new NoParams())) is { FromCli: false } fallback && fallback.Modes.SequenceEqual(ClaudePermissionModes.Fallback),
            "A CLI whose help cannot be read gets the fallback modes.");
        await IdeProtocol(runtime, project);
        await ConfigCases(runtime, state, project, directory);
        await Skills();
        await CodeIntelHooks();
        await state.ChangeAsync([HostStateChange.PluginEnabled("claude-code", false)]);
        await runtime.Schedule();
        Require(Directory.GetFiles(Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")!, "ide"), "*.lock").Length == 0 &&
            terminals.EnvironmentContributor!(terminal).Count == 0, "Disabling removes IDE discovery and terminal contributions.");
        Transcripts(directory);
        await ClaudeContextChecks.Host(Path.Combine(directory, "context"));
        Console.WriteLine("PASS Claude Code host: disabled lifecycle, configuration provenance, reviewed writes, token status, spending and revive");
    }

    /// <summary>
    /// The code-intelligence hooks: a reminder at session and subagent start, and an advisory, rate-limited nudge before a
    /// grep for a code symbol that names the equivalent lsp_navigate call. They never block and never fail.
    /// </summary>
    private static async Task CodeIntelHooks()
    {
        var plugin = Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets", "marketplace", "claude-plugin");
        var hooks = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(plugin, "hooks", "hooks.json"))).RootElement.GetProperty("hooks");
        Require(hooks.GetProperty("PreToolUse")[0].GetProperty("matcher").GetString() == "Grep|Glob|Bash" && hooks.TryGetProperty("SubagentStart", out _)
            && hooks.GetProperty("SessionStart").EnumerateArray().Any(group => group.GetProperty("hooks")[0].GetProperty("command").GetString()!.EndsWith("code-intel.sh session", StringComparison.Ordinal)),
            "The plugin does not register the code-intelligence hooks.");
        if (OperatingSystem.IsWindows()) return;
        var state = Path.Combine(Path.GetTempPath(), "sharprail-code-intel-check-" + Environment.ProcessId);
        try
        {
            async Task<(int Exit, string Output)> Hook(string mode, string input, bool managed = true, string? promptFile = null)
            {
                var start = new System.Diagnostics.ProcessStartInfo("sh") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
                start.ArgumentList.Add(Path.Combine(plugin, "scripts", "code-intel.sh"));
                start.ArgumentList.Add(mode);
                start.Environment["CODE_INTEL_STATE"] = state;
                if (promptFile is not null) start.Environment[ClaudeCodeContract.PromptFileVariable] = promptFile;
                else start.Environment.Remove(ClaudeCodeContract.PromptFileVariable);
                if (managed) start.Environment["THINKRAIL_MCP_URL"] = "http://127.0.0.1:1/mcp";
                else start.Environment.Remove("THINKRAIL_MCP_URL");
                using var process = System.Diagnostics.Process.Start(start)!;
                // A hook that does not read stdin (session start) may exit before the write: a broken pipe then only means it was done.
                try { await process.StandardInput.WriteAsync(input); process.StandardInput.Close(); }
                catch (IOException) { }
                var output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();
                return (process.ExitCode, output.Trim());
            }
            string Context((int Exit, string Output) result, string hookEvent)
            {
                Require(result.Exit == 0, hookEvent + " hook failed with exit " + result.Exit);
                var output = JsonDocument.Parse(result.Output).RootElement.GetProperty("hookSpecificOutput");
                Require(output.GetProperty("hookEventName").GetString() == hookEvent && !output.TryGetProperty("permissionDecision", out _), hookEvent + " hook answered with the wrong shape: " + result.Output);
                return output.GetProperty("additionalContext").GetString()!;
            }
            string Grep(string session, string pattern, string? path = null, string? glob = null) => JsonSerializer.Serialize(new { session_id = session, cwd = "/work/space", tool_name = "Grep", tool_input = new { pattern, path, glob } });
            string Bash(string session, string command) => JsonSerializer.Serialize(new { session_id = session, cwd = "/work/space", tool_name = "Bash", tool_input = new { command } });

            Require(Context(await Hook("session", "{}"), "SessionStart").Contains("lsp_navigate before grep", StringComparison.Ordinal), "The session reminder does not name lsp_navigate.");
            Require(Context(await Hook("subagent", "{}"), "SubagentStart").Contains("lsp_diagnostics", StringComparison.Ordinal), "The subagent reminder does not name lsp_diagnostics.");
            // With the session's prompt file the reminders repeat its project servers and its real example, nothing else.
            Directory.CreateDirectory(state);
            var promptFile = Path.Combine(state, "prompt.md");
            File.WriteAllText(promptFile, "# x\n\nThis project's language servers, by file count; `lsp_status {}` starts one:\n- `typescript` (JavaScript / TypeScript: .ts): 3 files\n- `css` (CSS: .css): 2 files\nOther servers exist.\n\nExamples in this project (JavaScript / TypeScript):\n- `{\"operation\": \"references\", \"query\": \"ShoppingCart\", \"workspace\": \"/work/space\"}` (also `definition`).\n");
            foreach (var (mode, hookEvent) in new[] { ("session", "SessionStart"), ("subagent", "SubagentStart") })
            {
                var project = Context(await Hook(mode, "{}", promptFile: promptFile), hookEvent);
                Require(project.Contains("{\"operation\":\"references\",\"query\":\"ShoppingCart\",\"workspace\":\"/work/space\"}", StringComparison.Ordinal) &&
                    project.Contains("Servers for this project: typescript, css.", StringComparison.Ordinal) && !project.Contains("TypeName", StringComparison.Ordinal) && !project.Contains("csharp", StringComparison.Ordinal),
                    "The reminder does not repeat the project's servers and example: " + project);
            }
            var nudge = Context(await Hook("pretool", Grep("grep", "TerminalView", "src", "*.cs")), "PreToolUse");
            Require(nudge.Contains("{\"operation\":\"references\",\"query\":\"TerminalView\",\"workspace\":\"/work/space\"}", StringComparison.Ordinal), "A code-symbol grep was not answered with the lsp_navigate call: " + nudge);
            Require(Context(await Hook("pretool", Bash("bash", "rg -n \"class LanguageService\\b\" src --type cs | head")), "PreToolUse").Contains("\"query\":\"LanguageService\"", StringComparison.Ordinal), "A shell rg for a declaration was not nudged.");
            Require(Context(await Hook("pretool", JsonSerializer.Serialize(new { session_id = "glob", cwd = "/work/space", tool_name = "Glob", tool_input = new { pattern = "**/*TerminalView*.cs" } })), "PreToolUse").Contains("\"operation\":\"workspaceSymbols\"", StringComparison.Ordinal), "A file glob for a type was not pointed at workspaceSymbols.");
            foreach (var (name, input) in new[] { ("docs", Grep("quiet-1", "TerminalView", glob: "*.md")), ("literal", Bash("quiet-2", "grep -rn 'error message' src")), ("status", Bash("quiet-3", "git status --short")), ("config", Bash("quiet-4", "rg -n Version Directory.Packages.props")), ("broken", "not json") })
                Require(await Hook("pretool", input) is (0, ""), "A non-code search was nudged: " + name);
            Require(await Hook("pretool", Grep("unmanaged", "TerminalView"), managed: false) is (0, ""), "The hook spoke outside a SharpRail session.");
            var answers = new List<bool>();
            for (var call = 0; call < 6; call++) answers.Add((await Hook("pretool", Grep("limited", "LanguageUsage"))).Output.Length > 0);
            Require(answers.SequenceEqual([true, false, false, false, false, true]), "The nudge is not rate-limited to the first and every fifth search: " + string.Join(",", answers));
        }
        finally { if (Directory.Exists(state)) Directory.Delete(state, true); }
        Console.WriteLine("PASS code-intelligence hooks: session and subagent reminders, advisory rate-limited grep nudges, silent for non-code and outside SharpRail");
    }

    /// <summary>
    /// The workflow skills ship inside the directly loaded plugin, in a shape both CLIs accept.
    /// </summary>
    private static Task Skills()
    {
        var assets = Path.Combine(AppContext.BaseDirectory, "plugins", ClaudeCodeContract.Id, "assets");
        var skills = Path.Combine(assets, "marketplace", "claude-plugin", "skills");
        string[] names = ["add-highlighting", "asking-user-questions", "brainstorming", "choosing-a-workflow", "importing-a-codebase", "setting-up-a-project", "shipping-a-pr", "spec-graph", "starting-a-new-project", "writing-specs"];
        Require(Directory.GetDirectories(skills).Select(Path.GetFileName).Order().SequenceEqual(names), "The plugin ships the ported workflow skills and custom highlighting skill.");
        foreach (var name in names)
        {
            var lines = File.ReadAllLines(Path.Combine(skills, name, "SKILL.md"));
            Require(lines is ["---", _, _, "---", ..] && lines[1] == "name: " + name && lines[2].StartsWith("description: \"Use ", StringComparison.Ordinal) &&
                lines[2].EndsWith('"') && lines[2].Length <= 1024, $"The {name} skill opens with the name and one-line description both CLIs read.");
            Require(name is "shipping-a-pr" or "add-highlighting" || lines[2].Contains("Use only when", StringComparison.Ordinal) || lines[2].Contains("Not for projects without specs", StringComparison.Ordinal),
                $"The {name} skill does not offer itself for ordinary work in a project without specs.");
        }
        Require(new[] { "body.md", "checks.md", "creating.md", "review-comments.md", "screenshots.md", "syncing.md" }.All(file => File.Exists(Path.Combine(skills, "shipping-a-pr", file))),
            "Shipping a PR carries its phase documents.");
        Require(new[] { "amending-specs.md", "fixing-a-bug.md" }.All(file => File.Exists(Path.Combine(skills, "brainstorming", file))),
            "Brainstorming carries its bug branch and spec amendment documents.");
        var text = string.Concat(Directory.GetFiles(skills, "*.md", SearchOption.AllDirectories).Select(File.ReadAllText));
        Require(!new[] { "thinkrail", "ask_user_question", "todo_", "set_title", "request_review", "web_search" }
            .Any(absent => text.Contains(absent, StringComparison.OrdinalIgnoreCase)), "The skills name no tool or skill these sessions lack.");

        const string sessionId = "11111111-2222-3333-4444-555555555555";
        const string pluginDirectory = "/Applications/Sharp Rail.app/plugins/claude-code/assets/marketplace/claude-plugin";
        var command = "claude --plugin-dir /old app/plugins/claude-code/assets/marketplace/claude-plugin --model opus";
        var resumed = AgentResume.ResumeCommand(command, sessionId, pluginDirectory: pluginDirectory)!;
        Require(resumed.Contains("--plugin-dir '" + pluginDirectory + "'", StringComparison.Ordinal) &&
            !resumed.Contains("/old app", StringComparison.Ordinal) && resumed.EndsWith("--resume " + sessionId, StringComparison.Ordinal),
            "Revival replaces the bundled plugin path with the current build's quoted directory.");
        var custom = AgentResume.ContinueCommand("claude --plugin-dir /custom/plugin", pluginDirectory: pluginDirectory)!;
        Require(custom.Contains("--plugin-dir /custom/plugin", StringComparison.Ordinal) && custom.EndsWith("--continue", StringComparison.Ordinal),
            "Revival preserves independently selected plugin directories.");
        Require(AgentResume.LaunchedSessionId($"claude --model opus --session-id {sessionId} 'a prompt'") == sessionId &&
            AgentResume.LaunchedSessionId($"claude --session-id={sessionId}") == sessionId && AgentResume.LaunchedSessionId($"claude --resume {sessionId}") == sessionId &&
            AgentResume.LaunchedSessionId($"claude --resume {sessionId} --fork-session") is null && AgentResume.LaunchedSessionId("claude --continue") is null &&
            AgentResume.LaunchedSessionId("claude --resume") is null, "A launch line names its session only through --session-id or --resume with an id, never through a fork.");
        var restarted = AgentResume.StartCommand($"claude --session-id {sessionId} --model opus 'old prompt'", sessionId)!;
        Require(restarted == $"claude --model opus --session-id {sessionId}", "Restarting an unsaved session drops the old id flag and prompt and names the id once: " + restarted);
        Require(AgentResume.ResumeCommand($"claude --session-id {sessionId}", sessionId) == $"claude --resume {sessionId}", "Resuming drops the launch's --session-id.");
        Require(AgentResume.ExpectedTranscriptPath("/private/tmp/a_b.c", sessionId) == Path.Combine(ClaudeEnvironment.ClaudeHome(), "projects", "-private-tmp-a-b-c", sessionId + ".jsonl"),
            "The transcript path is the Claude config directory's project slug, every character but a letter or digit as '-', plus the id.");
        Require(ClaudeLaunch.NewSessionId("--model opus") is { } fresh && AgentResume.IsSessionId(fresh) && ClaudeLaunch.NewSessionId("--model opus") != fresh &&
            new[] { "--continue", "--resume", "--resume x --fork-session", "--teleport", "-c", "-r", "--session-id x", "-p hi" }.All(args => ClaudeLaunch.NewSessionId(args) is null),
            "Only a new conversation gets a fresh preassigned id.");
        Require(ClaudeLaunch.SessionCommand("claude", "'Draft'", false, false, sessionId: sessionId).EndsWith($"--session-id {sessionId} 'Draft'", StringComparison.Ordinal),
            "The preassigned id precedes a positional prompt.");
        return Task.CompletedTask;
    }

    /// <summary>
    /// A tab that knows its session id revives exactly that session, as the terminal's program: two tabs in one directory
    /// each resume their own, a session that never saved a transcript starts again under the same id, a hook-reported
    /// change (/clear) moves the record and its revival, and only a legacy record without an id continues.
    /// </summary>
    private static async Task SessionIdRevival(HostStateStore state, Terminals terminals, TerminalRef own, Func<string, string, string, Task<int>> report)
    {
        var project = own.WorkspaceId;
        const string first = "0b4c1d6e-1111-4a2b-8c3d-000000000001", second = "0b4c1d6e-2222-4a2b-8c3d-000000000002", cleared = "0b4c1d6e-3333-4a2b-8c3d-000000000003";
        var neighbour = new TerminalRef(project, "claude-neighbour");
        state.SetTerminalAgent(own, new("claude", $"claude --model opus --session-id {first}") { SessionId = first });
        state.SetTerminalAgent(neighbour, new("claude", $"claude --session-id {second}") { SessionId = second });
        string Revival(TerminalRef terminal) => terminals.RevivePrefill!(terminal) is { } prefill && (prefill.Program || OperatingSystem.IsWindows())
            ? prefill.Text : throw new InvalidOperationException("A Claude revival runs as the terminal's program.");
        Require(Revival(own).EndsWith("--session-id " + first, StringComparison.Ordinal) && !Revival(own).Contains("--continue", StringComparison.Ordinal) &&
            !Revival(own).Contains("--resume", StringComparison.Ordinal), "A session that never saved a transcript starts again under its own id: " + Revival(own));
        foreach (var id in new[] { first, second })
        {
            var path = AgentResume.ExpectedTranscriptPath(project, id)!;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "{}\n");
        }
        Require(Revival(own).EndsWith("--resume " + first, StringComparison.Ordinal) && Revival(neighbour).EndsWith("--resume " + second, StringComparison.Ordinal) &&
            !Revival(own).Contains("--session-id", StringComparison.Ordinal) && Revival(own).Contains("--model opus", StringComparison.Ordinal),
            "Two Claude tabs in one directory each resume their own session: " + Revival(own) + " | " + Revival(neighbour));
        Require(await report("claude-fixture-token", JsonSerializer.Serialize(new { @event = "session_start", session_id = cleared, cwd = project }), "POST") == 200 &&
            state.Current.TerminalAgents.Single(agent => agent.Terminal == own).Record.SessionId == cleared && Revival(own).EndsWith("--session-id " + cleared, StringComparison.Ordinal),
            "A hook-reported session change (/clear) moves the record, and revival follows it: " + Revival(own));
        state.SetTerminalAgent(own, new("claude", "claude --model opus"));
        Require(Revival(own).EndsWith("--continue", StringComparison.Ordinal), "A legacy record without a session id keeps the continue fallback.");
        state.SetTerminalAgent(neighbour, null);
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
        // As the CLI does: a client that asks for the mcp subprotocol drops a handshake that does not confirm it.
        socket.Options.AddSubProtocol("mcp");
        await socket.ConnectAsync(endpoint, timeout.Token);
        Require(socket.SubProtocol == "mcp", "The IDE bridge confirms the mcp subprotocol the CLI requests.");
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
        var range = selected.GetProperty("selection");
        Require(range.GetProperty("start").GetProperty("line").GetInt32() == 0 && range.GetProperty("start").GetProperty("character").GetInt32() == 0 &&
            range.GetProperty("end").GetProperty("line").GetInt32() == 0 && range.GetProperty("end").GetProperty("character").GetInt32() == 7 &&
            !range.GetProperty("isEmpty").GetBoolean(), "The protocol counts lines and columns from zero; the plugin's contract from one.");
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
        Require(reply.GetProperty("id").GetInt32() == 4 &&
            reply.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString() == "Opened file: " + Path.Combine(project, "CLAUDE.md"),
            "The client's action result returns to the requesting CLI as MCP content.");
        async Task<string> Tool(int id, string name, object arguments, object? result)
        {
            await Send(new { jsonrpc = "2.0", id, method = "tools/call", @params = new { name, arguments } });
            if (result is not null)
            {
                Require(await owner.MoveNextAsync().AsTask().WaitAsync(timeout.Token), $"{name} reaches the owning client.");
                var request = PluginJson.Convert<IdeActionRequest>(owner.Current);
                Require(request.WorkspaceId == project, $"{name} acts in the session's workspace.");
                await runtime.CallAsync(new("claude-code", "actionReply", new IdeActionReply(request.Id, new(true) { Value = JsonSerializer.SerializeToElement(result) }), "claude-client"));
            }
            var answer = await Receive();
            Require(answer.TryGetProperty("id", out var answered) && answered.GetInt32() == id, $"{name} is answered before anything else is sent.");
            return answer.GetProperty("result").GetProperty("content")[0].GetProperty("text").GetString()!;
        }
        Require(await Tool(5, "close_tab", new { tab_name = "✻ [Claude Code] CLAUDE.md" }, new { closed = 1 }) == "TAB_CLOSED" &&
            await Tool(6, "close_tab", new { tab_name = "gone" }, new { closed = 0 }) == "Tab not found" &&
            await Tool(7, "closeAllDiffTabs", new { }, new { closed = 2 }) == "CLOSED_2_DIFF_TABS",
            "The tab tools answer with the words the CLI compares.");
        // The session names its own process; from then on it hears about its workspace and no other.
        await Send(new { jsonrpc = "2.0", method = "ide_connected", @params = new { pid = Environment.ProcessId } });
        async Task<int> Mention(string workspace) => PluginJson.Convert<IdeAtMentionResult>(await runtime.CallAsync(new("claude-code", "atMention",
            new IdeAtMention(workspace, "CLAUDE.md") { StartLine = 3, EndLine = 5 }, "claude-client"))).Sessions;
        Require(await Mention(Path.Combine(project, "elsewhere")) == 0, "A reference for another workspace reaches no session of this one.");
        var taken = 0;
        while (taken == 0) { taken = await Mention(project); if (taken == 0) await Task.Delay(20, timeout.Token); }
        var mention = await Receive();
        Require(taken == 1 && mention.GetProperty("method").GetString() == "at_mentioned" &&
            mention.GetProperty("params").GetProperty("filePath").GetString() == Path.Combine(project, "CLAUDE.md") &&
            mention.GetProperty("params").GetProperty("lineStart").GetInt32() == 2 && mention.GetProperty("params").GetProperty("lineEnd").GetInt32() == 4,
            "A file reference reaches the workspace's session with an absolute path and zero-based lines.");
        await runtime.CallAsync(new("claude-code", "documentClosed", new IdeDocumentClosed(project, "CLAUDE.md"), "claude-client"));
        Require(JsonSerializer.Deserialize<JsonElement>(await Tool(8, "getCurrentSelection", new { }, null)).GetProperty("success").GetBoolean() == false,
            "A closed tab's selection is forgotten, and the protocol is sent no notification about it.");
        socket.Abort();
        await timeout.CancelAsync();
        try { await peerNext; } catch (OperationCanceledException) { }
        Console.WriteLine("PASS Claude Code IDE: token authorization, initialization, catalogue, zero-based selection, addressed action replies and file references");
    }

    private sealed class TypingTerminal : SharpRail.Plugins.Api.UI.ITerminalAccessoryApi
    {
        public List<string> Writes { get; } = [];
        public int Focused { get; private set; }
        public string[] Screen { get; init; } = [];
        public string WorkspaceId => "workspace";
        public string TabKey => "tab";
        public void Write(string data) => Writes.Add(data);
        public Task<IReadOnlyList<string>> ReadScreenTailAsync(int lines) => Task.FromResult<IReadOnlyList<string>>(Screen);
        public IDisposable HoldInput() => throw new NotSupportedException();
        public void Focus() => Focused++;
        public void SetKeyEncoding(SharpRail.Plugins.Api.UI.TerminalKeyEncoding encoding) { }
    }

    private static void Transcripts(string directory)
    {
        var path = Path.Combine(directory, "usage.jsonl");
        static string Turn(string id, int input, int output) => JsonSerializer.Serialize(new
        { type = "assistant", message = new { id, usage = new { input_tokens = input, output_tokens = output } } }) + "\n";
        File.WriteAllText(path, Turn("one", 10, 1) + Turn("one", 10, 3));
        var usage = new TranscriptUsage();
        Require(usage.Read(path) == new AgentTokenUsage(10, 3, 0, 0) { Context = 10 }, "Streaming blocks count once per message at their last usage.");
        var tail = Turn("two", 4, 2);
        File.AppendAllText(path, tail[..^1]);
        Require(usage.Read(path).Input == 10, "Incomplete transcript lines wait for completion.");
        File.AppendAllText(path, "\n");
        var subagents = Path.Combine(path[..^6], "subagents");
        Directory.CreateDirectory(subagents);
        File.WriteAllText(Path.Combine(subagents, "agent-one.jsonl"), Turn("child", 7, 6));
        Require(usage.Read(path) == new AgentTokenUsage(21, 11, 0, 0) { Context = 4 }, "Appended turns and subagent spending are included without recounting prior messages.");
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