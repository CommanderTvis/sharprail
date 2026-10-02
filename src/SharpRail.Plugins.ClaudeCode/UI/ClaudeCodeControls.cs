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
    private readonly CheckBox agentView;
    private readonly CheckBox systemPrompt;
    private bool applying;

    public ClaudeCodeSettingsSection(IPluginUIContext context)
    {
        AvaloniaXamlLoader.Load(this);
        this.context = context;
        command = this.FindControl<TextBox>("ClaudeCommandInput")!;
        agentView = this.FindControl<CheckBox>("ClaudeDisableAgentView")!;
        systemPrompt = this.FindControl<CheckBox>("ClaudeAppendSystemPrompt")!;
        this.FindControl<Button>("ClaudeCommandBrowse")!.Click += (_, _) => _ = Browse();
        command.LostFocus += (_, _) => SaveCommand(command.Text ?? "");
        command.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { SaveCommand(command.Text ?? ""); e.Handled = true; } };
        agentView.IsCheckedChanged += (_, _) => Update(settings => settings with { DisableAgentView = agentView.IsChecked == true }, "Couldn't change the agent-view setting");
        systemPrompt.IsCheckedChanged += (_, _) => Update(settings => settings with { AppendSystemPrompt = systemPrompt.IsChecked == true }, "Couldn't change the system prompt setting");
        var reset = this.FindControl<Button>("ClaudeSystemPromptReset")!;
        // The tab opens in the workbench behind Settings, so Settings steps aside once it is there.
        this.FindControl<Button>("ClaudeSystemPromptEdit")!.Click += async (_, _) =>
        {
            if (await ClaudeParts.EditSystemPromptAsync(context)) (TopLevel.GetTopLevel(this) as Window)?.Close();
        };
        reset.Click += (_, _) => _ = ResetSystemPrompt(reset);
        Show(context.Settings<ClaudeCodeSettings>());
        IDisposable? watch = null;
        AttachedToVisualTree += (_, _) => _ = ShowEdited(reset);
        AttachedToVisualTree += (_, _) => watch ??= context.OnSettings<ClaudeCodeSettings>(Show);
        DetachedFromVisualTree += (_, _) => { watch?.Dispose(); watch = null; };
    }

    private void Show(ClaudeCodeSettings settings)
    {
        applying = true;
        command.Text = settings.Command;
        agentView.IsChecked = settings.DisableAgentView;
        systemPrompt.IsChecked = settings.AppendSystemPrompt;
        applying = false;
    }

    // Reset is offered only once the user's edits have taken the file over.
    private async Task ShowEdited(Button reset)
    {
        try { reset.IsVisible = (await context.RequestAsync(ClaudeCodeContract.SystemPromptGet, new NoParams())).Edited; }
        catch (Exception error) when (error is not OperationCanceledException) { reset.IsVisible = false; }
    }

    private async Task ResetSystemPrompt(Button reset)
    {
        try { reset.IsVisible = (await context.RequestAsync(ClaudeCodeContract.SystemPromptReset, new NoParams())).Edited; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Notify(PluginNotificationKind.Error, "Couldn't reset the instructions", ClaudeParts.ErrorText(error));
        }
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
        void Launch(string args = "")
        {
            var settings = context.Settings<ClaudeCodeSettings>();
            var started = ClaudeLaunch.SessionCommand(settings.Command, args, settings.DisableAgentView, settings.AppendSystemPrompt, context.Host().HostPlatform);
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
        ToolTip.SetTip(button, "Start Claude Code (right-click for options)");
        Avalonia.Automation.AutomationProperties.SetName(button, "Start Claude Code");
        button.Click += (_, _) => Launch();
        var menu = new ContextMenu { Name = "ClaudeLaunchMenu", Placement = PlacementMode.BottomEdgeAlignedLeft };
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