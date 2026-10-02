using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Codex.UI;

internal sealed partial class CodexSettingsView : StackPanel
{
    internal CodexSettingsView(IPluginUIContext context)
    {
        AvaloniaXamlLoader.Load(this);
        void Update(Func<CodexSettings, CodexSettings> change) => _ = UpdateAsync(change);
        async Task UpdateAsync(Func<CodexSettings, CodexSettings> change)
        {
            try { await context.UpdateSettingsAsync(change(context.Settings<CodexSettings>())); }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't change the Codex setting"); }
        }

        var input = this.FindControl<TextBox>("CodexCommandInput")!;
        var picker = this.FindControl<ComboBox>("CodexPermissionMode")!;
        var ide = this.FindControl<ToggleButton>("CodexIdeContextToggle")!;
        var mcp = this.FindControl<ToggleButton>("CodexMcpToggle")!;
        var prompt = this.FindControl<CheckBox>("CodexAppendSystemPrompt")!;
        var ideCheck = this.FindControl<ContentControl>("IdeCheck")!;
        var mcpCheck = this.FindControl<ContentControl>("McpCheck")!;
        ideCheck.Content = Ui.Icon("check", Ui.Accent, 16);
        mcpCheck.Content = Ui.Icon("check", Ui.Accent, 16);
        void SaveCommand()
        {
            var normalized = input.Text?.Trim() is { Length: > 0 } typed ? typed : "codex";
            input.Text = normalized;
            if (normalized != context.Settings<CodexSettings>().Command) Update(current => current with { Command = normalized });
        }
        input.LostFocus += (_, _) => SaveCommand();
        input.KeyDown += (_, e) => { if (e.Key == Key.Enter) { SaveCommand(); e.Handled = true; } };
        foreach (var mode in CodexLaunch.PermissionModes)
            picker.Items.Add(new ComboBoxItem { Name = "CodexPermissionMode_" + mode.Id, Content = mode.Label, Tag = mode.Id });
        picker.SelectionChanged += (_, _) =>
        {
            if (picker.SelectedItem is ComboBoxItem { Tag: string id } && id != context.Settings<CodexSettings>().PermissionMode)
                Update(current => current with { PermissionMode = id });
        };
        ide.Click += (_, _) => Update(current => current with { IdeContext = !current.IdeContext });
        mcp.Click += (_, _) => Update(current => current with { Mcp = !current.Mcp });
        prompt.Click += (_, _) => Update(current => current with { AppendSystemPrompt = prompt.IsChecked == true });
        var reset = this.FindControl<Button>("CodexSystemPromptReset")!;
        // The tab opens in the workbench behind Settings, so Settings steps aside once it is there.
        this.FindControl<Button>("CodexSystemPromptEdit")!.Click += async (_, _) =>
        {
            if (await CodexPanel.EditSystemPromptAsync(context)) (TopLevel.GetTopLevel(this) as Window)?.Close();
        };
        // Reset is offered only once the user's edits have taken the file over.
        async Task ShowEdited(PluginMethod<CodexNoParams, CodexSystemPromptFile> method)
        {
            try { reset.IsVisible = (await context.RequestAsync(method, new CodexNoParams())).Edited; }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't read the Codex instructions"); }
        }
        reset.Click += (_, _) => _ = ShowEdited(CodexContract.SystemPromptReset);

        // Settings arrive with the host's snapshot; the controls update in place so typing and focus survive.
        void Render(CodexSettings settings)
        {
            if (!input.IsFocused) input.Text = settings.Command;
            picker.SelectedIndex = Math.Max(0, CodexLaunch.PermissionModes.ToList().FindIndex(mode => mode.Id == settings.PermissionMode));
            ide.IsChecked = ideCheck.IsVisible = settings.IdeContext;
            mcp.IsChecked = mcpCheck.IsVisible = settings.Mcp;
            prompt.IsChecked = settings.AppendSystemPrompt;
        }

        Render(context.Settings<CodexSettings>());
        IDisposable? subscription = null;
        AttachedToVisualTree += (_, _) =>
        {
            _ = ShowEdited(CodexContract.SystemPromptGet);
            Render(context.Settings<CodexSettings>());
            subscription ??= context.OnSettings<CodexSettings>(Render);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            subscription?.Dispose();
            subscription = null;
        };
    }
}