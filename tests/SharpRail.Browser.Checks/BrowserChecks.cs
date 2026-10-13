using System.Runtime.InteropServices.JavaScript;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Ghostty.Avalonia;
using SharpRail.Scintilla;
using SharpRail.Host.Abstractions;
using SharpRail.UI;
using SharpRail.UI.Browser;
using SkiaSharp;

namespace SharpRail.Browser.Checks;

// Compiled only by the browser acceptance build, never by the shipped client.
internal static partial class BrowserChecks
{
    private static Workbench Workbench => BrowserSession.Current!.Workbench!;

    [JSExport]
    public static async Task<string> ConnectFields()
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var windows = (IEnumerable<BrowserWindow>)typeof(BrowserWindows).GetField("windows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(BrowserWindows.Instance)!;
            return JsonSerializer.Serialize(windows.Select(item => item.Window).OfType<ConnectWindow>()
                .SelectMany(window => window.GetVisualDescendants().OfType<TextBox>().Select(field => new { field.Name, field.Text, field.SelectionStart, field.SelectionEnd, field.IsFocused, Position = field.TranslatePoint(default, window) })).ToArray());
        });
    }

    [JSExport]
    public static async Task<string> Snapshot()
    {
        if (BrowserSession.Current?.Workbench is null) return "[]";
        return await Dispatcher.UIThread.InvokeAsync(() => JsonSerializer.Serialize(Workbench.Windows.Select(window => new
        {
            window.Title,
            window.ProjectRoot,
            window.IsActive,
            window.Preferences.VerticalCenterTabs,
            window.Preferences.VerticalTabsInProjects,
            TerminalRegions = window.Layout.State.Workspaces.GetValueOrDefault(window.ProjectRoot)?.Documents
                .Where(group => group.Value.Any(tab => tab.Kind == "terminal"))
                .Select(group => window.Layout.State.Groups.First(item => item.Id == group.Key).Region).ToArray(),
            Plugins = Workbench.PluginRegistry.Active.ToArray(),
            Messages = window.GetVisualDescendants().OfType<TextBlock>().Where(text => text.IsVisible).Select(text => text.Text).ToArray(),
            Editors = window.GetVisualDescendants().OfType<ScintillaEditor>().Select(editor => new { editor.Text, editor.IsModified, Position = editor.TranslatePoint(default, window), Width = editor.Bounds.Width, Height = editor.Bounds.Height, editor.IsFocused, Recordings = typeof(ScintillaEditor).GetProperty("Recordings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor) }).ToArray(),
            Terminals = window.GetVisualDescendants().OfType<GhosttySkiaView>().Select(terminal => new { Text = terminal.ReadScreen(), Position = terminal.TranslatePoint(default, window), Width = terminal.Bounds.Width, Height = terminal.Bounds.Height, terminal.IsFocused, terminal.Frames, Grid = terminal.Size, Frame = TerminalFrame(terminal) }).ToArray()
        }).ToArray()));
    }

    [JSExport]
    public static async Task SetTabMode(bool vertical, bool inProjects)
    {
        await Workbench.State.ChangeAsync(HostStateChange.Setting("vertical-tabs", vertical.ToString()),
            HostStateChange.Setting("tabs-in-projects", inProjects.ToString()));
    }

    private static unsafe object TerminalFrame(GhosttySkiaView terminal)
    {
        var frame = typeof(GhosttySkiaView).GetField("last", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
        var type = frame.GetType();
        var columns = (ushort)type.GetField("Columns")!.GetValue(frame)!;
        var rows = (ushort)type.GetField("Rows")!.GetValue(frame)!;
        var cells = (byte*)Pointer.Unbox(type.GetField("Cells")!.GetValue(frame)!);
        var cellType = type.GetField("Cells")!.FieldType.GetElementType()!;
        var stride = Marshal.SizeOf(cellType);
        var foreground = (int)Marshal.OffsetOf(cellType, "Foreground");
        var colors = new HashSet<uint>();
        if (cells != null)
            for (var i = 0; i < columns * rows; i++) colors.Add(*(uint*)(cells + i * stride + foreground));
        return new { Columns = columns, Rows = rows, Colors = colors.ToArray() };
    }

    [JSExport]
    public static async Task CheckTerminalDrawing()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            using var view = new GhosttySkiaView { FontSize = 14 };
            var type = typeof(GhosttySkiaView);
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
            view.Measure(new Size(1200, 200));
            view.Arrange(new Rect(0, 0, 1200, 200));
            foreach (var style in new[] { "0", "1", "3", "1;3", "2", "7", "8", "4", "9" })
            foreach (var row in new[] { 0, view.Size.Rows / 2, view.Size.Rows - 1 })
            {
                var text = new System.Text.StringBuilder($"\e[?25l\e[?7l\e[0m\e[2J\e[{row + 1};1H");
                for (var x = 0; x < view.Size.Columns; x++)
                {
                    var c = 32 + x % 95;
                    text.Append($"\e[0;{style};38;2;{c * 19 % 256};{c * 37 % 256};{c * 53 % 256};48;2;{c * 11 % 256};{c * 7 % 256};{c * 3 % 256}m{(char)c}");
                }
                view.Write(System.Text.Encoding.UTF8.GetBytes(text.ToString()));
                var vt = type.GetField("vt", flags)!.GetValue(view)!;
                object?[] arguments = [null];
                if (!(bool)vt.GetType().GetMethod("Snapshot", flags)!.Invoke(vt, arguments)!) throw new InvalidOperationException("Terminal snapshot failed.");
                var frame = arguments[0];
                var fonts = type.GetField("fonts", flags)!.GetValue(view);
                SKColor[] Pixels(bool batch)
                {
                    type.GetField("batchAscii", flags)!.SetValue(view, batch);
                    using var picture = (SKPicture)type.GetMethod("Record", flags)!.Invoke(view, [frame, fonts, 1200f, 200f, row])!;
                    using var surface = SKSurface.Create(new SKImageInfo(1200, 200));
                    surface.Canvas.Clear(SKColors.Transparent);
                    surface.Canvas.DrawPicture(picture);
                    using var image = surface.Snapshot();
                    using var bitmap = SKBitmap.FromImage(image);
                    return bitmap.Pixels;
                }
                if (!Pixels(true).SequenceEqual(Pixels(false))) throw new InvalidOperationException($"Batched terminal drawing changed pixels for SGR {style}, row {row}.");
            }
        });
    }

    [JSExport]
    public static async Task OpenDocument(string path)
    {
        await Dispatcher.UIThread.InvokeAsync(() => Workbench.ActiveWindow!.OpenDocumentAsync(path, keep: true));
    }

    [JSExport]
    public static async Task OpenSettings(string section)
    {
        await Dispatcher.UIThread.InvokeAsync(() => Workbench.ActiveWindow!.ShowSettings(section));
    }

    [JSExport]
    public static async Task EnablePlugin(string id)
    {
        await Dispatcher.UIThread.InvokeAsync(() => Workbench.State.ChangeAsync(HostStateChange.PluginEnabled(id, true)));
    }

    [JSExport]
    public static async Task<string> SettingsText()
    {
        return await Dispatcher.UIThread.InvokeAsync(() => JsonSerializer.Serialize(Workbench.ActiveWindow!.OwnedWindows
            .SelectMany(window => window.GetVisualDescendants().OfType<TextBlock>()).Select(text => text.Text).ToArray()));
    }

    [JSExport]
    public static async Task ClickSettingsButton(string name)
    {
        await Dispatcher.UIThread.InvokeAsync(() => Workbench.ActiveWindow!.OwnedWindows
            .SelectMany(window => window.GetVisualDescendants().OfType<Button>()).Single(button => button.Name == name)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));
    }
}
