using System.Reflection;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Host.Abstractions;
using SharpRail.UI;
using SharpRail.UI.Browser;
using SharpRail.UI.Docking;

namespace SharpRail.Browser.Checks;

internal static partial class BrowserChecks
{
    [JSExport]
    public static async Task<string> MermaidState()
    {
        if (BrowserSession.Current?.Workbench is null) return "null";
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = Workbench.ActiveWindow!;
            return JsonSerializer.Serialize(new
            {
                ActiveKind = window.Layout.Selected(window.Layout.View.FocusedCenter)?.Kind,
                RenderedDiff = window.GetVisualDescendants().Any(control => control.Name == "RenderedDiff" && control.IsEffectivelyVisible),
                Images = window.GetVisualDescendants().OfType<Image>().Where(image => image.Name == "MermaidDiagram" && image.IsEffectivelyVisible)
                    .Select(image => new { image.Bounds.Width, image.Bounds.Height, SourceWidth = image.Source?.Size.Width, SourceHeight = image.Source?.Size.Height }).ToArray(),
                Errors = window.GetVisualDescendants().OfType<Control>().Where(control => control.Name == "MermaidError" && control.IsEffectivelyVisible)
                    .Select(control => string.Join(" ", control.GetVisualDescendants().OfType<TextBlock>().Select(text => text.Text))).ToArray()
            });
        });
    }

    [JSExport]
    public static async Task OpenMermaidDiff(string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() => (Task)typeof(WorkbenchWindow).GetMethod("OpenDiffAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(Workbench.ActiveWindow!, [new GitChange(path, "?", "?", null, 0, 0), new DockTab("diff:untracked::" + path, path + " · Diff", "diff", path, Scope: "untracked"), false])!);
    }
}
