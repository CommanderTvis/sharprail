using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Xml.Linq;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;

using SharpRail.Checks.E2E;
using SharpRail.UI.Panels;
using SharpRail.UI.Rendering;

using SkiaSharp;

namespace SharpRail.Checks;

internal static class IconChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Vector icons: " + message); }

    internal static void Run()
    {
        var root = new Uri("avares://SharpRail.Plugins.UI.Kit/Assets/Icons/");
        var assets = AssetLoader.GetAssets(root, null).Where(uri => uri.AbsolutePath.EndsWith(".svg", StringComparison.Ordinal)).ToArray();
        Require(assets.Length == 70, "Every existing glyph alias plus Host's server, the terminal bell and the Chrome mark must have a vector asset.");
        foreach (var uri in assets)
        {
            var name = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            var icon = (Border)Ui.Icon(name, Brushes.Red, 15.5);
            Require(icon.OpacityMask is DrawingBrush { Drawing: DrawingGroup drawing } && drawing.Children.OfType<GeometryDrawing>().First().Geometry?.Bounds == new Rect(0, 0, 24, 24), name + " lost vector geometry or its full viewbox.");
            Require(ReferenceEquals(icon.OpacityMask, ((Border)Ui.Icon(name, Brushes.Blue, 31)).OpacityMask), name + " caches size or tint variants instead of shared geometry.");
        }
        Require(((Border)Ui.Icon("brand")).OpacityMask is ImageBrush, "The unavailable vector brand retains its original image.");
        foreach (var name in new[] { "discuss", "gitBranch", "server", "fileDiff", "alertImportant" })
            foreach (var size in new[] { 15.5, 22.25 })
                foreach (var scale in new[] { 1d, 2, 3 })
                    CompareOriginal(name, size, scale);
        Tint();
        var host = new Border();
        var window = new Window { Width = 120, Height = 80, Content = host };
        window.Show();
        var weak = Mount(host);
        host.Child = null;
        window.Close(); Dispatcher.UIThread.RunJobs();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Require(weak.Count(reference => reference.IsAlive) <= 1, "Shared glyph geometry retained detached icon controls.");
        Capture();
        Console.WriteLine("PASS all original vector aliases, full viewboxes, 1x/2x/3x fractional-size path pixels, brush/theme updates and detached-control collection");
    }

    internal static void HostSettings(string root)
    {
        using var app = new E2eWorkspace(Path.Combine(root, "host-icon"), openFiles: false);
        app.Click(app.Find<Button>("SettingsButton"));
        E2eWorkspace.Until(() => app.Window.OpenSettingsViews.Any());
        var settings = app.Window.OpenSettingsViews.Single();
        var host = settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "Settings_Host");
        var mask = ((Border)Ui.Icon("server")).OpacityMask;
        Require(host.GetLogicalDescendants().OfType<Border>().Any(icon => ReferenceEquals(icon.OpacityMask, mask)),
            "Host settings must use the server glyph rather than a terminal glyph.");
        Dispatcher.UIThread.RunJobs(); settings.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = TopLevel.GetTopLevel(settings)!.CaptureRenderedFrame()!;
        frame.Save(CheckRoot.Artifact("vector-icons", "settings.png"), PngBitmapEncoderOptions.Default);
        settings.Close();
        Console.WriteLine("PASS actual Settings Host navigation uses the original server vector glyph");
    }

    private static void CompareOriginal(string name, double size, double scale)
    {
        using var rendered = Render(Ui.Icon(name, Brushes.White, size), size, scale);
        var actual = Pixels(rendered);
        using var expected = new SKBitmap(rendered.PixelSize.Width, rendered.PixelSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(expected);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale((float)(size * scale / 24));
        using var stream = AssetLoader.Open(new Uri($"avares://SharpRail.Plugins.UI.Kit/Assets/Icons/{name}.svg"));
        var svg = XDocument.Load(stream).Root!;
        using var paint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        foreach (var element in svg.Elements().Where(element => element.Name.LocalName == "path"))
        {
            using var path = SKPath.ParseSvgPathData(element.Attribute("d")!.Value);
            path.FillType = SKPathFillType.Winding;
            canvas.DrawPath(path, paint);
        }
        bool Near(int x, int y, Func<int, int, byte> alpha)
        {
            for (var dy = -1; dy <= 1; dy++)
                for (var dx = -1; dx <= 1; dx++)
                    if (x + dx >= 0 && x + dx < expected.Width && y + dy >= 0 && y + dy < expected.Height && alpha(x + dx, y + dy) >= 128) return true;
            return false;
        }
        for (var y = 0; y < expected.Height; y++)
            for (var x = 0; x < expected.Width; x++)
            {
                Require(actual[y * expected.Width + x].A < 128 || Near(x, y, (px, py) => expected.GetPixel(px, py).Alpha),
                    $"{name} at {size} DIP/{scale}x painted outside the original contour.");
                Require(expected.GetPixel(x, y).Alpha < 128 || Near(x, y, (px, py) => actual[py * expected.Width + px].A),
                    $"{name} at {size} DIP/{scale}x lost part of the original contour.");
            }
        var ink = actual.Sum(pixel => (long)pixel.A);
        var originalInk = expected.Pixels.Sum(pixel => (long)pixel.Alpha);
        Require(Math.Abs(ink - originalInk) < originalInk * .2, $"{name} at {size} DIP/{scale}x changed the original filled area.");
    }

    private static void Tint()
    {
        var brush = new SolidColorBrush(Colors.Red);
        var icon = (Border)Ui.Icon("discuss", brush, 24);
        Color Ink()
        {
            using var frame = Render(icon, 24, 2);
            return Pixels(frame).First(pixel => pixel.A == 255);
        }
        Require(Ink() == Colors.Red, "Initial brush tint was lost.");
        brush.Color = Colors.Blue;
        Require(Ink() == Colors.Blue, "Mutating the existing brush must recolor vector pixels.");
        icon.Background = Brushes.Green;
        Require(Ink() == Colors.Green, "Replacing Border.Background must recolor vector pixels.");
        var original = Ui.Theme;
        try
        {
            icon.Background = Ui.Accent;
            foreach (var theme in Themes.All)
            {
                Ui.Apply(theme);
                Require(Ink() == Ui.Accent.Color, "A shared theme brush did not update the vector mask tint.");
            }
        }
        finally { Ui.Apply(original); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<WeakReference> Mount(Border host)
    {
        var weak = new List<WeakReference>();
        for (var index = 0; index < 100; index++)
        {
            var icon = Ui.Icon(index % 2 == 0 ? "discuss" : "gitBranch", Ui.Accent);
            host.Child = icon;
            host.UpdateLayout();
            weak.Add(new(icon));
            host.Child = null;
        }
        return weak;
    }

    private static RenderTargetBitmap Render(Control icon, double size, double scale)
    {
        icon.UseLayoutRounding = false;
        icon.Measure(new Size(size, size)); icon.Arrange(new Rect(0, 0, size, size));
        icon.InvalidateVisual(); Dispatcher.UIThread.RunJobs();
        var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(size * scale), (int)Math.Ceiling(size * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(icon);
        return bitmap;
    }

    private static Color[] Pixels(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var bytes = new byte[size.Width * size.Height * 4];
        var pinned = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try { bitmap.CopyPixels(new PixelRect(size), pinned.AddrOfPinnedObject(), bytes.Length, size.Width * 4); }
        finally { pinned.Free(); }
        var bgra = bitmap.Format == PixelFormats.Bgra8888;
        return Enumerable.Range(0, size.Width * size.Height).Select(index => Color.FromArgb(bytes[index * 4 + 3], bytes[index * 4 + (bgra ? 2 : 0)], bytes[index * 4 + 1], bytes[index * 4 + (bgra ? 0 : 2)])).ToArray();
    }

    private static void Capture()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 20, Margin = new Thickness(16), Background = Ui.Surface };
        foreach (var name in new[] { "discuss", "gitBranch", "server" })
        {
            var column = new StackPanel { Spacing = 8 };
            column.Children.Add(Ui.Icon(name, Ui.TextBrush, 16));
            column.Children.Add(Ui.Icon(name, Ui.TextBrush, 48));
            column.Children.Add(Ui.Text(name));
            panel.Children.Add(column);
        }
        var window = new Window { Width = 330, Height = 155, Background = Ui.Surface, Content = panel };
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = window.CaptureRenderedFrame()!;
        frame.Save(CheckRoot.Artifact("vector-icons", "rendered.png"), PngBitmapEncoderOptions.Default);
        window.Close();
    }
}