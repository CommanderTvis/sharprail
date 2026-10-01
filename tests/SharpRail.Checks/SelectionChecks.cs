using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;
using SharpRail.UI.State;

namespace SharpRail.Checks;

internal static class SelectionChecks
{
    internal static void Run(IProjectServices host)
    {
        var original = Ui.Theme;
        foreach (var theme in Themes.All)
        {
            Ui.Apply(theme);
            using var preview = new MarkdownPreview("A selectable paragraph.", "selection.md", host, new Preferences(), (_, _) => { });
            var input = new TextBox { Text = "Selectable input" };
            var window = new Window { Width = 400, Height = 250, Content = new StackPanel { Children = { input, preview } } };
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var paragraph = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().Single();
            paragraph.SelectionStart = 0; paragraph.SelectionEnd = 10;
            input.SelectionStart = 0; input.SelectionEnd = 10;
            var (expectedText, expectedPreview) = theme.Id switch
            {
                "light" => (Color.FromArgb(56, 107, 87, 255), Color.FromArgb(22, 107, 87, 255)),
                "dark" => (Color.FromArgb(38, 106, 200, 255), Color.FromArgb(15, 106, 200, 255)),
                _ => (theme["selection"], Ui.Alpha(theme["selection"], 40))
            };
            var foreground = theme.Colors["selectionForeground"];
            E2E.E2eWorkspace.Require(input.SelectionBrush is SolidColorBrush textBrush && textBrush.Color == expectedText &&
                paragraph.SelectionBrush is SolidColorBrush previewBrush && previewBrush.Color == expectedPreview,
                $"Text selection in {theme.Id} must use the upstream selection colours.");
            E2E.E2eWorkspace.Require(foreground is null
                ? input.SelectionForegroundBrush is null && paragraph.SelectionForegroundBrush is null
                : input.SelectionForegroundBrush is ISolidColorBrush inputText && inputText.Color == foreground &&
                    paragraph.SelectionForegroundBrush is ISolidColorBrush previewText && previewText.Color == foreground,
                $"Selected text in {theme.Id} must retain its foreground unless the manifest overrides it.");
            window.Close();
        }
        Ui.Apply(original);
        Console.WriteLine("PASS input and Markdown selection appearance under every bundled theme");
    }
}