using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Media;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>
/// The Codex start action: a click starts <c>codex</c>; the right-click menu continues, resumes or forks a session,
/// picks a model from the local catalog for this launch, a sandbox mode, workspace write with approval, or web search.
/// </summary>
public static class CodexLauncher
{
    private static readonly IReadOnlyList<IReadOnlyList<CodexLaunchPreset>> Menu = CodexLaunch.Menu(CodexConfigDocs.EnumValues);

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
        }
        Fill();
        menu.Opening += (_, _) => Fill();
        button.ContextMenu = menu;
        return button;
    }
}