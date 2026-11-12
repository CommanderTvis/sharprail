using System.Runtime.InteropServices.JavaScript;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Reflection;

using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
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
            window.WorkspaceMounted,
            CatalogKnown = Workbench.TerminalCatalog?.Workspaces.ContainsKey(window.WorkspaceRoot) == true,
            ProjectsScroll = window.GetVisualDescendants().OfType<ScrollViewer>().Where(scroll => scroll.Content is StackPanel { Name: "ProjectsTree" })
                .Select(scroll => new { Offset = scroll.Offset.Y, Extent = scroll.Extent.Height, Viewport = scroll.Viewport.Height, Position = scroll.TranslatePoint(default, window), Width = scroll.Bounds.Width, Height = scroll.Bounds.Height }).SingleOrDefault(),
            ProjectsTabs = window.GetLogicalDescendants().OfType<StackPanel>().Any(panel => panel.Name == "CenterTabsInProjects"),
            BottomZones = window.Layout.State.Groups.Count(group => group.Region == "bottom"),
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
    public static async Task PrepareWheelFixture()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var tree = Workbench.ActiveWindow!.GetVisualDescendants().OfType<StackPanel>().Single(panel => panel.Name == "ProjectsTree");
            tree.MinHeight = 3000;
            tree.Background = Avalonia.Media.Brushes.Transparent;
        });
    }

    [JSExport]
    public static async Task<string> CheckWatchedReplay()
    {
        var endpoint = BrowserSession.Current!.Endpoint!;
        using var adapter = new SharpRail.Host.Client.RemoteTerminalAdapter(endpoint.Uri!, endpoint.Token);
        var root = Workbench.ActiveWindow!.ProjectRoot;
        var id = "browser-watched-replay-" + Guid.NewGuid().ToString("N");
        await using var holder = await adapter.AttachAsync(new(id, root, "acceptance-holder", 100, 30));
        using var reading = new CancellationTokenSource();
        var recorded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = Task.Run(async () =>
        {
            var text = new System.Text.StringBuilder();
            try
            {
                await foreach (var bytes in holder.ReadAsync(reading.Token))
                {
                    text.Append(System.Text.Encoding.UTF8.GetString(bytes.Span));
                    if (text.ToString().Contains("RECORDED_SCREEN", StringComparison.Ordinal)) recorded.TrySetResult();
                }
            }
            catch (OperationCanceledException) when (reading.IsCancellationRequested) { }
        });
        BrowserTerminal? watching = null;
        Window? window = null;
        try
        {
            await holder.WriteAsync(System.Text.Encoding.UTF8.GetBytes("printf '\\033[?1049h\\033[2J\\033[HRECORDED_%s\\033[3;1HWorking 4' SCREEN; read -r _; printf '\\033[3;9H5UPDATE_%s' SCREEN; read -r _\r"));
            await recorded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                watching = new BrowserTerminal(new(root, id, "", "acceptance-watcher") { Yield = true, Watch = true }, adapter);
                window = new Window { Width = 600, Height = 360, Content = watching.View };
                window.Show(Workbench.ActiveWindow!);
            });
            await watching!.Started.WaitAsync(TimeSpan.FromSeconds(35));
            await WaitFor("RECORDED_SCREEN");
            if (!watching.Watching || holder.Detached.IsCompleted || holder.Grid != (100, 30))
                throw new InvalidOperationException("Browser replay displaced or resized its holder.");
            await holder.WriteAsync("\r"u8.ToArray());
            await WaitFor("Working 5UPDATE_SCREEN");
            return await Dispatcher.UIThread.InvokeAsync(() => JsonSerializer.Serialize(new { watching.Watching, Text = watching.ReadScreen(), Renderer = watching.GetVisualDescendants().Any(control => control.Name == "TerminalSkiaView") ? "skia" : "restty", Columns = holder.Grid.Columns, Rows = holder.Grid.Rows }));
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() => { window?.Close(); watching?.Dispose(); });
            reading.Cancel();
            await output;
            await adapter.CloseAsync(id);
        }

        async Task WaitFor(string marker)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (await Dispatcher.UIThread.InvokeAsync(() => watching!.ReadScreen().Contains(marker, StringComparison.Ordinal))) return;
                await Task.Delay(50);
            }
            throw new InvalidOperationException("Browser watcher did not show " + marker);
        }
    }

    [JSExport]
        public static async Task OpenTerminal()
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var window = Workbench.ActiveWindow!;
            window.Layout.NewTerminal(window.Layout.View.FocusedCenter);
        });
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
        return await Dispatcher.UIThread.InvokeAsync(() => JsonSerializer.Serialize(Workbench.ActiveWindow!.GetVisualDescendants().OfType<SharpRail.UI.Panels.SettingsView>()
            .SelectMany(window => window.GetVisualDescendants().OfType<TextBlock>()).Select(text => text.Text).ToArray()));
    }

    [JSExport]
    public static async Task ClickSettingsButton(string name)
    {
        await Dispatcher.UIThread.InvokeAsync(() => Workbench.ActiveWindow!.GetVisualDescendants().OfType<SharpRail.UI.Panels.SettingsView>()
            .SelectMany(window => window.GetVisualDescendants().OfType<Button>()).Single(button => button.Name == name)
            .RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));
    }
    private static readonly List<string> droppedFiles = [];
    private static bool acceptsFileDrops;

    [JSExport]
    public static async Task FileDropProbe(bool accepts)
    {
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            acceptsFileDrops = accepts;
            var terminal = Workbench.ActiveWindow!.GetVisualDescendants().OfType<SharpRail.UI.Terminal.TerminalView>().First();
            if (terminal.Accessories.Children.Any(control => control.Name == "FileDropProbe")) return;
            var receiver = new Border { Name = "FileDropProbe" };
            receiver.SetValue(SharpRail.Plugins.UI.Kit.FileDrop.AcceptsProperty, () => acceptsFileDrops);
            receiver.SetValue(SharpRail.Plugins.UI.Kit.FileDrop.AttachProperty, paths => droppedFiles.AddRange(paths));
            terminal.Accessories.Children.Add(receiver);
        });
    }

    [JSExport]
    public static async Task<string> DroppedFiles() =>
        await Dispatcher.UIThread.InvokeAsync(() => JsonSerializer.Serialize(droppedFiles));

}
