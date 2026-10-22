using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>Settings › Claude Code: the launch command, the agent-view switch and the system prompt switch.</summary>
internal sealed partial class ClaudeCodeSettingsSection : UserControl
{
    private readonly IPluginUIContext context;
    private readonly TextBox command;
    private readonly TextBox model;
    private readonly ComboBox effort;
    private readonly ComboBox permissionMode;
    private readonly TextBlock permissionNote;
    private readonly CheckBox agentView;
    private readonly CheckBox systemPrompt;
    private bool applying;

    public ClaudeCodeSettingsSection(IPluginUIContext context)
    {
        AvaloniaXamlLoader.Load(this);
        this.context = context;
        command = this.FindControl<TextBox>("ClaudeCommandInput")!;
        model = this.FindControl<TextBox>("ClaudeDefaultModel")!;
        effort = this.FindControl<ComboBox>("ClaudeDefaultEffort")!;
        effort.Items.Add(new ComboBoxItem { Content = "Use CLI default", Tag = "" });
        foreach (var level in EffortPicker.Levels) effort.Items.Add(new ComboBoxItem { Content = level, Tag = level });
        void SaveModel()
        {
            var value = model.Text?.Trim() ?? "";
            model.Text = value;
            if (value != context.Settings<ClaudeCodeSettings>().Model)
                Update(settings => settings with { Model = value }, "Couldn't change the default model");
        }
        model.LostFocus += (_, _) => SaveModel();
        model.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { SaveModel(); e.Handled = true; } };
        effort.SelectionChanged += (_, _) =>
        {
            if (effort.SelectedItem is ComboBoxItem { Tag: string value })
                Update(settings => settings with { Effort = value }, "Couldn't change the default effort");
        };
        permissionMode = this.FindControl<ComboBox>("ClaudePermissionMode")!;
        permissionNote = this.FindControl<TextBlock>("ClaudePermissionModeNote")!;
        FillPermissionModes(PermissionModeSource.Current, context.Settings<ClaudeCodeSettings>().PermissionMode);
        permissionMode.SelectionChanged += (_, _) =>
        {
            if (permissionMode.SelectedItem is ComboBoxItem { Tag: string value } && value != context.Settings<ClaudeCodeSettings>().PermissionMode)
                Update(settings => settings with { PermissionMode = value }, "Couldn't change the default permission mode");
        };
        agentView = this.FindControl<CheckBox>("ClaudeDisableAgentView")!;
        systemPrompt = this.FindControl<CheckBox>("ClaudeAppendSystemPrompt")!;
        this.FindControl<Button>("ClaudeCommandBrowse")!.Click += (_, _) => _ = Browse();
        command.LostFocus += (_, _) => SaveCommand(command.Text ?? "");
        command.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { SaveCommand(command.Text ?? ""); e.Handled = true; } };
        agentView.IsCheckedChanged += (_, _) => Update(settings => settings with { DisableAgentView = agentView.IsChecked == true }, "Couldn't change the agent-view setting");
        systemPrompt.IsCheckedChanged += (_, _) => Update(settings => settings with { AppendSystemPrompt = systemPrompt.IsChecked == true }, "Couldn't change the system prompt setting");
        Show(context.Settings<ClaudeCodeSettings>());
        IDisposable? watch = null;
        AttachedToVisualTree += async (_, _) =>
        {
            var modes = await PermissionModeSource.RefreshAsync(context);
            applying = true;
            FillPermissionModes(modes, context.Settings<ClaudeCodeSettings>().PermissionMode);
            applying = false;
        };
        AttachedToVisualTree += (_, _) => watch ??= context.OnSettings<ClaudeCodeSettings>(Show);
        DetachedFromVisualTree += (_, _) => { watch?.Dispose(); watch = null; };
    }

    private void Show(ClaudeCodeSettings settings)
    {
        applying = true;
        command.Text = settings.Command;
        if (!model.IsFocused) model.Text = settings.Model;
        effort.SelectedItem = effort.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, settings.Effort ?? ""));
        if (!permissionMode.Items.OfType<ComboBoxItem>().Any(item => Equals(item.Tag, settings.PermissionMode)))
            FillPermissionModes(PermissionModeSource.Current, settings.PermissionMode);
        permissionMode.SelectedItem = permissionMode.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, settings.PermissionMode));
        ShowPermissionNote(settings.PermissionMode);
        agentView.IsChecked = settings.DisableAgentView;
        systemPrompt.IsChecked = settings.AppendSystemPrompt;
        applying = false;
    }

    // Rebuilt in place from the modes the host's CLI lists; the caller holds `applying` so the rebuild saves nothing.
    private void FillPermissionModes(PermissionModesResult modes, string saved)
    {
        permissionMode.Items.Clear();
        permissionMode.Items.Add(new ComboBoxItem { Name = "ClaudePermissionMode_default", Content = PermissionModeSource.DefaultLabel(modes), Tag = "" });
        foreach (var mode in PermissionModeSource.Modes(modes, saved))
        {
            var item = new ComboBoxItem { Name = "ClaudePermissionMode_" + mode.Id, Content = PermissionModeSource.Label(mode), Tag = mode.Id };
            PermissionModeSource.Mark(item, mode);
            permissionMode.Items.Add(item);
        }
        permissionMode.SelectedItem = permissionMode.Items.OfType<ComboBoxItem>().FirstOrDefault(item => Equals(item.Tag, saved));
    }

    private void ShowPermissionNote(string saved)
    {
        var dangerous = ClaudePermissionModes.IsDangerous(saved);
        permissionNote.Text = dangerous
            ? "Every session SharpRail starts will skip all permission checks. Only use this in a sandbox with no internet access."
            : "Passed as --permission-mode to sessions SharpRail starts. Claude Code's default leaves the flag off, so your own Claude settings decide. The launcher's right-click menu picks a mode for one run.";
        permissionNote.Foreground = dangerous ? Ui.Danger : Ui.Muted;
    }

    private void Update(Func<ClaudeCodeSettings, ClaudeCodeSettings> change, string failure)
    {
        if (applying) return;
        _ = Save(change(context.Settings<ClaudeCodeSettings>()), failure);
    }

    private async Task Save(ClaudeCodeSettings settings, string failure)
    {
        try { await context.UpdateSettingsAsync(settings); }
        catch (Exception error) when (error is not OperationCanceledException) { context.Notify(PluginNotificationKind.Error, failure, ClaudeParts.ErrorText(error)); }
    }

    // Never persists blank: a value nobody can launch is worse than the one it replaces.
    private void SaveCommand(string next)
    {
        var normalized = next.Trim() is { Length: > 0 } text ? text : "claude";
        command.Text = normalized;
        if (applying || normalized == context.Settings<ClaudeCodeSettings>().Command) return;
        _ = Save(context.Settings<ClaudeCodeSettings>() with { Command = normalized }, "Couldn't change the Claude Code launch command");
    }

    private async Task Browse()
    {
        try
        {
            if (await context.PickFileAsync() is { Length: > 0 } path) SaveCommand(ClaudeLaunch.ShellQuotePath(path));
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't open the file picker on the host", ClaudeParts.ErrorText(error));
        }
    }
}

/// <summary>
/// Starts Claude Code in a terminal of this group. A click runs the configured command; a right click offers the flags
/// that choose what this run is (continue, resume, teleport, a model) rather than how the CLI is invoked.
/// </summary>
internal static class ClaudeLauncherAction
{
    public static Control Create(IPluginUIContext context, ClaudeGlyph glyph, string workspaceId, string groupId)
    {
        // A null mode is the saved default; an empty one is Claude Code's own, so the flag is left off for this run.
        void Launch(string args = "", string? mode = null)
        {
            var settings = context.Settings<ClaudeCodeSettings>();
            var started = ClaudeLaunch.SessionCommand(settings.Command, args, settings.DisableAgentView, settings.AppendSystemPrompt,
                context.Host().HostPlatform, model: settings.Model, effort: settings.Effort, contributions: context.AgentLaunchContributions("claude"),
                sessionId: ClaudeLaunch.NewSessionId(args), permissionMode: mode ?? settings.PermissionMode);
            if (started.Length == 0) return;
            _ = context.OpenTerminalAsync(workspaceId, new TerminalOpenOptions { Command = started, GroupId = groupId }).AsTask();
        }
        var button = new Button
        {
            Name = "NewClaude",
            Content = glyph.Create(16),
            Width = 32,
            Height = 32,
            Padding = new Thickness(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0)
        };
        Avalonia.Automation.AutomationProperties.SetName(button, "Start Claude Code");
        var menu = new ContextMenu { Name = "ClaudeLaunchMenu", Placement = PlacementMode.BottomEdgeAlignedLeft };
        // A touch screen has no right click: a tap opens the options, and the plain start is the first of them.
        if (OperatingSystem.IsAndroid())
        {
            button.Click += (_, _) => menu.Open(button);
            var plain = new MenuItem { Name = "ClaudeLaunch_default", Header = "Start Claude Code", Icon = glyph.Create(14) };
            plain.Click += (_, _) => Launch();
            menu.Items.Add(plain);
            menu.Items.Add(new Separator());
        }
        else
        {
            ToolTip.SetTip(button, "Start Claude Code (right-click for options)");
            button.Click += (_, _) => Launch();
        }
        for (var index = 0; index < ClaudeLaunch.Menu.Count; index++)
        {
            if (index > 0) menu.Items.Add(new Separator());
            foreach (var preset in ClaudeLaunch.Menu[index])
            {
                var item = new MenuItem { Name = "ClaudeLaunch_" + preset.Id, Header = preset.Label, Icon = preset.Model is null ? null : glyph.Create(14) };
                item.Click += (_, _) => Launch(preset.Args);
                menu.Items.Add(item);
            }
        }
        // This run's permission mode, from the modes the host's CLI lists; the default leaves Claude's own settings in charge.
        menu.Items.Add(new Separator());
        var permissions = new MenuItem { Name = "ClaudeLaunch_permission", Header = "Permission mode" };
        void FillPermissions(PermissionModesResult modes)
        {
            permissions.Items.Clear();
            var plain = new MenuItem { Name = "ClaudeLaunch_permission-default", Header = PermissionModeSource.DefaultLabel(modes) };
            plain.Click += (_, _) => Launch(mode: "");
            permissions.Items.Add(plain);
            foreach (var mode in PermissionModeSource.Modes(modes))
            {
                var item = new MenuItem { Name = "ClaudeLaunch_permission-" + mode.Id, Header = PermissionModeSource.Label(mode), Tag = mode.Id };
                PermissionModeSource.Mark(item, mode);
                item.Click += (_, _) => Launch(mode: mode.Id);
                permissions.Items.Add(item);
            }
        }
        FillPermissions(PermissionModeSource.Current);
        menu.Opening += async (_, _) => FillPermissions(await PermissionModeSource.RefreshAsync(context));
        menu.Items.Add(permissions);
        // Any flags the presets do not cover, typed per run; the last ones typed come back prefilled.
        menu.Items.Add(new Separator());
        var custom = new MenuItem { Name = "ClaudeLaunch_custom", Header = "With custom arguments…" };
        custom.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(button) is not Window owner) return;
            if (await AskArgumentsAsync(owner) is { } args) { lastArguments = args; Launch(args); }
        };
        menu.Items.Add(custom);
        button.ContextMenu = menu;
        return button;
    }

    private static string lastArguments = "";

    private static async Task<string?> AskArgumentsAsync(Window owner)
    {
        var dialog = DialogWindow.Create("Start Claude Code with arguments", 520);
        dialog.Tag = "ClaudeCustomLaunch";
        dialog.FindControl<TextBlock>("DialogExplanation")!.Text =
            "Flags added to this run's command line, after SharpRail's own, for example --permission-mode plan or --add-dir ../other.";
        var input = new TextBox { Name = "ClaudeCustomArguments", Text = lastArguments, PlaceholderText = "--flag value …", FontFamily = Ui.CodeFont };
        dialog.FindControl<StackPanel>("DialogFields")!.Children.Add(input);
        string? chosen = null;
        void Start() { chosen = input.Text?.Trim() ?? ""; dialog.Close(); }
        var actions = dialog.FindControl<StackPanel>("DialogActions")!;
        actions.Children.Add(Ui.Button("Cancel", () => dialog.Close()));
        var start = new Button
        {
            Name = "ClaudeCustomStart",
            Content = Ui.Text("Start", Ui.TextBrush),
            Padding = new Thickness(10, 5),
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            IsDefault = true
        };
        start.Click += (_, _) => Start();
        actions.Children.Add(start);
        dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
        await dialog.ShowDialog(owner);
        return chosen;
    }
}