using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

using SkiaSharp;

namespace SharpRail.UI.Resources;

internal sealed record ImageViewState(bool Fit, double Zoom);
internal sealed record ImageDiffState(string Mode, double Position);

/// <summary>Decoding shared by the raster and vector renderers: bytes become pixels and nothing else.</summary>
internal static class Pictures
{
    internal const double MinZoom = 0.1, MaxZoom = 16;
    private const long MaxDifferencePixels = 32L * 1024 * 1024;

    internal static IBrush Checkerboard() => new DrawingBrush
    {
        TileMode = TileMode.Tile,
        DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
        Drawing = new GeometryDrawing
        {
            Brush = Ui.Hover,
            Geometry = new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } }
        }
    };

    /// <summary>Raster bytes as a bitmap, or null when the side is absent, too large to preview or not decodable.</summary>
    internal static async Task<Bitmap?> RasterAsync(ResourceContent content, CancellationToken token)
    {
        if (content is not ResourceContent.Bytes { ByteLength: <= FileLimits.PreviewBytes } bytes) return null;
        var data = await bytes.Load(token);
        return await Task.Run(() =>
        {
            try { return new Bitmap(new MemoryStream(data)); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
        }, token);
    }

    /// <summary>Per-channel absolute difference on a canvas that holds both pictures; identical pixels are black.</summary>
    internal static Bitmap? Difference(Bitmap original, Bitmap modified)
    {
        int width = Math.Max(original.PixelSize.Width, modified.PixelSize.Width), height = Math.Max(original.PixelSize.Height, modified.PixelSize.Height);
        if ((long)width * height > MaxDifferencePixels) return null;
        using var left = Pixels(original, width, height);
        using var right = Pixels(modified, width, height);
        using var result = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var a = left.GetPixelSpan(); var b = right.GetPixelSpan(); var target = result.GetPixelSpan();
        for (var index = 0; index < target.Length; index += 4)
        {
            // A pixel that exists on one side only differs by its alpha, which would otherwise be invisible.
            var alpha = Math.Abs(a[index + 3] - b[index + 3]);
            for (var channel = 0; channel < 3; channel++)
                target[index + channel] = (byte)Math.Max(Math.Abs(a[index + channel] * a[index + 3] / 255 - b[index + channel] * b[index + 3] / 255), alpha);
            target[index + 3] = 255;
        }
        using var image = SKImage.FromBitmap(result);
        using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        return new Bitmap(encoded.AsStream());
    }

    // The picture's unpremultiplied pixels at the top left of a transparent canvas.
    private static SKBitmap Pixels(Bitmap bitmap, int width, int height)
    {
        var canvas = new SKBitmap(new SKImageInfo(width, height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        canvas.Erase(SKColors.Transparent);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        using var source = SKBitmap.Decode(stream, new SKImageInfo(bitmap.PixelSize.Width, bitmap.PixelSize.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        var from = source.GetPixelSpan(); var to = canvas.GetPixelSpan();
        for (var row = 0; row < source.Height; row++)
            from.Slice(row * source.RowBytes, source.Width * 4).CopyTo(to.Slice(row * canvas.RowBytes, source.Width * 4));
        return canvas;
    }
}

/// <summary>One picture on a transparency checkerboard: fit to the pane, natural size, or zoomed.</summary>
internal sealed class ImageView : Grid, IResourceBody, IDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<ResourceContent, CancellationToken, Task<Bitmap?>> decode;
    private readonly ToggleButton fit = ResourcePane.Segment("ImageFit", "Fit");
    private readonly ToggleButton natural = ResourcePane.Segment("ImageNatural", "100%");
    private readonly TextBlock details = Ui.Text("", Ui.Muted, 12);
    private readonly Image image = new() { Name = "ImagePicture" };
    private readonly Border checker = new() { Background = Pictures.Checkerboard(), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly ScrollViewer scroll = new();
    private Bitmap? bitmap;
    private ImageViewState state;
    private long byteLength;

    internal ImageView(ResourceView view, Func<ResourceContent, CancellationToken, Task<Bitmap?>> decode)
    {
        this.decode = decode;
        state = view.ViewState as ImageViewState ?? new(true, 1);
        Name = "ImageResource";
        RowDefinitions = new RowDefinitions("28,*");
        details.Name = "ImageDetails";
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center };
        var zoomOut = Step("ImageZoomOut", "−", 1 / 1.25);
        var zoomIn = Step("ImageZoomIn", "+", 1.25);
        bar.Children.AddRange([fit, natural, zoomOut, zoomIn, details]);
        details.Margin = new Thickness(8, 0, 0, 0);
        fit.Click += (_, _) => Apply(state with { Fit = true });
        natural.Click += (_, _) => Apply(new(false, 1));
        checker.Child = image;
        scroll.Content = checker;
        Ui.Place(this, bar);
        Ui.Place(this, scroll, 1);
        _ = LoadAsync(view.Content);
    }

    internal ImageViewState State => state;
    public object? ViewState => state;

    public bool Reload(ResourceContent content) { _ = LoadAsync(content); return true; }

    private Button Step(string name, string label, double factor)
    {
        var button = new Button { Name = name, Content = label, Height = 20, MinWidth = 24, Padding = new Thickness(8, 0), FontSize = 12, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(4), Background = Brushes.Transparent, Foreground = Ui.Muted };
        button.Click += (_, _) => Apply(new(false, Math.Clamp((state.Fit ? 1 : state.Zoom) * factor, Pictures.MinZoom, Pictures.MaxZoom)));
        return button;
    }

    private async Task LoadAsync(ResourceContent content)
    {
        try
        {
            var loaded = await decode(content, lifetime.Token);
            if (lifetime.IsCancellationRequested) { loaded?.Dispose(); return; }
            var previous = bitmap;
            bitmap = loaded;
            byteLength = (content as ResourceContent.Bytes)?.ByteLength ?? (content is ResourceContent.Text text ? System.Text.Encoding.UTF8.GetByteCount(text.Value) : 0);
            image.Source = bitmap;
            previous?.Dispose();
            if (bitmap is null) { details.Text = "This picture cannot be shown — " + ResourceCards.Bytes(byteLength); details.Foreground = Ui.Danger; return; }
            details.Foreground = Ui.Muted;
            Apply(state);
        }
        catch (OperationCanceledException) { }
        catch (Exception error) when (error is IOException or Grpc.Core.RpcException) { details.Text = "Could not load this picture: " + error.Message; details.Foreground = Ui.Danger; }
    }

    private void Apply(ImageViewState next)
    {
        state = next;
        fit.IsChecked = state.Fit; natural.IsChecked = !state.Fit && state.Zoom == 1;
        if (bitmap is null) return;
        var size = bitmap.PixelSize;
        if (state.Fit)
        {
            image.Width = image.Height = double.NaN;
            image.Stretch = Stretch.Uniform; image.StretchDirection = StretchDirection.DownOnly;
            scroll.HorizontalScrollBarVisibility = scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        else
        {
            image.Width = size.Width * state.Zoom; image.Height = size.Height * state.Zoom;
            image.Stretch = Stretch.Fill; image.StretchDirection = StretchDirection.Both;
            scroll.HorizontalScrollBarVisibility = scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        var zoom = state.Fit ? "fit" : (state.Zoom * 100).ToString("0", CultureInfo.InvariantCulture) + "%";
        details.Text = $"{size.Width} × {size.Height} · {ResourceCards.Bytes(byteLength)} · {zoom}";
    }

    public void Dispose()
    {
        lifetime.Cancel();
        image.Source = null;
        bitmap?.Dispose(); bitmap = null;
        lifetime.Dispose();
    }
}

/// <summary>Two pictures compared side by side, by a swipe line, blended, or as their per-pixel difference.</summary>
internal sealed class ImageDiffView : Grid, IDisposable
{
    internal static readonly string[] Modes = ["2-up", "swipe", "onion", "difference"];
    private static readonly string[] Labels = ["2-up", "Swipe", "Onion skin", "Difference"];
    private readonly Dictionary<string, ToggleButton> toggles = [];
    private readonly Slider slider = new() { Name = "ImageDiffPosition", Minimum = 0, Maximum = 1, Width = 160, VerticalAlignment = VerticalAlignment.Center };
    private readonly Grid body = new();
    private readonly Bitmap? original, modified;
    private readonly Action<object?> save;
    private Bitmap? difference;
    private ImageDiffState state;

    internal ImageDiffView(ResourceDiff diff, Bitmap? original, Bitmap? modified)
    {
        this.original = original; this.modified = modified; save = diff.SaveViewState;
        state = diff.ViewState as ImageDiffState is { } saved && Modes.Contains(saved.Mode) ? saved : new("2-up", 0.5);
        Name = "ImageDiff";
        RowDefinitions = new RowDefinitions("28,*");
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center };
        foreach (var mode in Modes)
        {
            var toggle = ResourcePane.Segment("ImageDiffMode_" + mode.Replace("-", ""), Labels[Array.IndexOf(Modes, mode)]);
            // Overlays need both pictures.
            toggle.IsEnabled = mode == "2-up" || original is not null && modified is not null;
            toggle.Click += (_, _) => Show(state with { Mode = mode });
            toggles[mode] = toggle; bar.Children.Add(toggle);
        }
        bar.Children.Add(slider);
        slider.Value = state.Position;
        slider.PropertyChanged += (_, change) => { if (change.Property == RangeBase.ValueProperty && slider.Value != state.Position) Show(state with { Position = slider.Value }); };
        Ui.Place(this, bar);
        Ui.Place(this, body, 1);
        if (original is null || modified is null) state = state with { Mode = "2-up" };
        Show(state, report: false);
    }

    internal ImageDiffState State => state;

    private void Show(ImageDiffState next, bool report = true)
    {
        state = next;
        if (report) save(state);
        foreach (var (mode, toggle) in toggles) toggle.IsChecked = mode == state.Mode;
        slider.IsVisible = state.Mode is "swipe" or "onion";
        body.Children.Clear();
        body.ColumnDefinitions = new ColumnDefinitions("*");
        switch (state.Mode)
        {
            case "difference":
                difference ??= Pictures.Difference(original!, modified!);
                Ui.Place(body, difference is null ? Ui.Text("These pictures are too large to compare pixel by pixel.", Ui.Muted, 12) : Framed("ImageDiffDifference", Picture(difference), Ui.DifferenceBase));
                break;
            case "swipe" or "onion":
                int width = Math.Max(original!.PixelSize.Width, modified!.PixelSize.Width), height = Math.Max(original.PixelSize.Height, modified.PixelSize.Height);
                var after = Picture(modified);
                if (state.Mode == "onion") after.Opacity = state.Position;
                else after.Clip = new RectangleGeometry(new Rect(width * state.Position, 0, width, height));
                var stack = new Grid { Width = width, Height = height };
                stack.Children.Add(Picture(original)); stack.Children.Add(after);
                Ui.Place(body, Framed("ImageDiffOverlay", new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = stack }, Pictures.Checkerboard()));
                break;
            default:
                body.ColumnDefinitions = new ColumnDefinitions("*,*");
                Ui.Place(body, Side("Original", original));
                Ui.Place(body, Side("Modified", modified), 0, 1);
                break;
        }
    }

    private static Image Picture(Bitmap bitmap) => new() { Source = bitmap, Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };

    private static Border Framed(string name, Control child, IBrush background) => new()
    {
        Name = name,
        Margin = new Thickness(16),
        Background = background,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Child = child
    };

    private static Control Side(string title, Bitmap? bitmap)
    {
        var panel = new DockPanel { Name = "ImageDiffSide_" + title, Margin = new Thickness(16) };
        var caption = Ui.Text(bitmap is null ? title + " — no picture" : $"{title} — {bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}", Ui.Muted, 12);
        DockPanel.SetDock(caption, Dock.Top);
        caption.Margin = new Thickness(0, 0, 0, 8);
        panel.Children.Add(caption);
        if (bitmap is not null)
            panel.Children.Add(new Border
            {
                Background = Pictures.Checkerboard(),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly }
            });
        return panel;
    }

    /// <summary>Decodes both sides, then builds the view; null when neither side is a picture this renderer can draw.</summary>
    internal static async Task<Control?> CreateAsync(ResourceDiff diff, Func<ResourceContent, CancellationToken, Task<Bitmap?>> decode, CancellationToken token)
    {
        Bitmap? original = null, modified = null;
        try
        {
            original = await decode(diff.Original, token);
            modified = await decode(diff.Modified, token);
            token.ThrowIfCancellationRequested();
            if (original is null && modified is null) return null;
            return new ImageDiffView(diff, original, modified);
        }
        catch { original?.Dispose(); modified?.Dispose(); throw; }
    }

    public void Dispose()
    {
        body.Children.Clear();
        original?.Dispose(); modified?.Dispose(); difference?.Dispose();
    }
}