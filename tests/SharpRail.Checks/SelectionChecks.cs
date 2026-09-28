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
        foreach (var light in new[] { false, true })
        {
            Ui.SetLight(light);
            using var preview = new MarkdownPreview("A selectable paragraph.", "selection.md", host, new Preferences(), (_, _) => { });
            var input = new TextBox { Text = "Selectable input" };
            var window = new Window { Width = 400, Height = 250, Content = new StackPanel { Children = { input, preview } } };
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var paragraph = preview.GetLogicalDescendants().OfType<SelectableTextBlock>().Single();
            paragraph.SelectionStart = 0; paragraph.SelectionEnd = 10;
            input.SelectionStart = 0; input.SelectionEnd = 10;
            var expectedText = light ? Color.FromArgb(56, 107, 87, 255) : Color.FromArgb(38, 106, 200, 255);
            var expectedPreview = light ? Color.FromArgb(22, 107, 87, 255) : Color.FromArgb(15, 106, 200, 255);
            E2E.E2eWorkspace.Require(input.SelectionBrush is SolidColorBrush textBrush && textBrush.Color == expectedText &&
                paragraph.SelectionBrush is SolidColorBrush previewBrush && previewBrush.Color == expectedPreview &&
                input.SelectionForegroundBrush is null && paragraph.SelectionForegroundBrush is null,
                "Text selection must use the upstream translucent selection colours and retain foreground text.");
            window.Close();
        }
        Ui.SetLight(false);
        Console.WriteLine("PASS dark/light input and Markdown selection appearance");
    }
}
