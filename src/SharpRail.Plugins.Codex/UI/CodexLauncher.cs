using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>
/// The Codex start action: a click starts <c>codex</c>; the right-click menu continues, resumes or forks a session,
/// picks a model from the local catalog for this launch, a sandbox mode, workspace write with approval, or web search.
/// </summary>
public static class CodexLauncher
{
    private static readonly IReadOnlyList<IReadOnlyList<CodexLaunchPreset>> Menu = CodexLaunch.Menu(CodexConfigDocs.EnumValues);
    private static string lastArguments = "";

    /// <summary>The menu's groups: the session presets, then this catalog's models, then the permission and search presets.</summary>
    public static IReadOnlyList<IReadOnlyList<CodexLaunchPreset>> Groups(IReadOnlyList<CodexModel> models) =>
    [
        Menu[0],
        .. models.Count > 0 ? [models.Select(model => new CodexLaunchPreset($"model-{model.Id}", model.Label) { Args = $"--model {model.Id}", Model = model.Id }).ToArray()] : Array.Empty<IReadOnlyList<CodexLaunchPreset>>(),
        .. Menu.Skip(1)
    ];

    public static Control Create(IPluginUIContext context, CodexStore store, Action<CodexLaunchPreset?> start)
    {
        var button = new Button
        {
            Name = "NewCodex",
            Content = new CodexGlyph(context, 16),
            Width = 32,
            Height = 32,
            Padding = new Thickness(8),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        ToolTip.SetTip(button, "Start Codex (right-click for options)");
        AutomationProperties.SetName(button, "Start Codex");
        button.Click += (_, _) => start(null);
        var menu = new ContextMenu { Name = "CodexLaunchMenu" };
        void Fill()
        {
            menu.Items.Clear();
            var groups = Groups(store.Models);
            for (var index = 0; index < groups.Count; index++)
            {
                if (index > 0) menu.Items.Add(new Separator());
                foreach (var preset in groups[index])
                {
                    var item = new MenuItem { Name = "CodexLaunch_" + preset.Id, Header = preset.Label };
                    if (preset.Model is not null) item.Icon = CodexGlyph.Gpt();
                    item.Click += (_, _) => start(preset);
                    menu.Items.Add(item);
                }
            }
            menu.Items.Add(new Separator());
            var custom = new MenuItem { Name = "CodexLaunch_custom", Header = "With custom arguments…" };
            custom.Click += async (_, _) =>
            {
                if (TopLevel.GetTopLevel(button) is not Window owner) return;
                var dialog = DialogWindow.Create("Start Codex with arguments", 520);
                dialog.Tag = "CodexCustomLaunch";
                dialog.FindControl<TextBlock>("DialogExplanation")!.Text = "Flags added to this run's command line, for example --model my-model or --add-dir ../other.";
                dialog.FindControl<TextBlock>("DialogExplanation")!.IsVisible = true;
                dialog.FindControl<StackPanel>("DialogActions")!.IsVisible = false;
                var fields = new CodexLaunchArguments();
                dialog.FindControl<StackPanel>("DialogFields")!.Children.Add(fields);
                var input = fields.FindControl<TextBox>("CodexCustomArguments")!;
                input.Text = lastArguments;
                string? chosen = null;
                fields.FindControl<Button>("CodexCustomCancel")!.Click += (_, _) => dialog.Close();
                fields.FindControl<Button>("CodexCustomStart")!.Click += (_, _) => { chosen = input.Text?.Trim() ?? ""; dialog.Close(); };
                dialog.Opened += (_, _) => { input.Focus(); input.SelectAll(); };
                await dialog.ShowDialog(owner);
                if (chosen is null) return;
                lastArguments = chosen;
                start(new("custom", "With custom arguments…") { Args = chosen });
            };
            menu.Items.Add(custom);
        }
        Fill();
        menu.Opening += (_, _) => Fill();
        button.ContextMenu = menu;
        return button;
    }
}