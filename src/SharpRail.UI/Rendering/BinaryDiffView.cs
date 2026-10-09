using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Rendering;

/// <summary>
/// A diff of bytes the host classes as non-text: a card per side with its identity, and for raster images the two
/// pictures side by side. The bytes are decoded only as pixels; active types such as SVG and HTML are never drawn here.
/// </summary>
public sealed class BinaryDiffView : ScrollViewer, IDisposable
{
    private static readonly HashSet<string> Raster = ["image/png", "image/jpeg", "image/gif", "image/webp", "image/bmp"];
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Bitmap> bitmaps = [];

    /// <summary>Whether a unified diff says Git saw bytes it will not show as lines.</summary>
    public static bool IsBinaryDiff(string diff) => diff.Contains("Binary files ", StringComparison.Ordinal) && !diff.Contains("\n@@", StringComparison.Ordinal);

    public BinaryDiffView(IProjectServices host, string path, string scope, string comparison)
    {
        Name = "BinaryDiff";
        var panel = new StackPanel { Margin = new Thickness(24), Spacing = 12 };
        Content = panel;
        panel.Children.Add(Ui.Text("Loading…", Ui.Muted, 12));
        _ = LoadAsync(host, path, scope, comparison, panel);
    }

    private async Task LoadAsync(IProjectServices host, string path, string scope, string comparison, StackPanel panel)
    {
        try
        {
            var token = lifetime.Token;
            var sides = await Task.Run(async () => await host.GetDiffSidesAsync(path, scope, comparison, token), token);
            var pictures = new Bitmap?[2];
            var revisions = new[] { sides.OriginalRevision, sides.ModifiedRevision };
            var infos = new[] { sides.OriginalInfo, sides.ModifiedInfo };
            for (var side = 0; side < 2; side++)
                if (infos[side] is { Sha256: not null, MediaType: { } media, ByteLength: <= FileLimits.PreviewBytes } && Raster.Contains(media))
                    pictures[side] = await Task.Run(async () =>
                    {
                        var content = await host.ReadContentBytesAsync(path, revisions[side], token);
                        // The hash is the identity: bytes that moved since the diff was read are not what this card describes.
                        if (content.Info.Sha256 != infos[side]!.Sha256) return null;
                        try { return new Bitmap(new MemoryStream(content.Data)); }
                        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException) { return null; }
                    }, token);
            if (token.IsCancellationRequested) { foreach (var picture in pictures) picture?.Dispose(); return; }
            bitmaps.AddRange(pictures.OfType<Bitmap>());
            panel.Children.Clear();
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16 };
            for (var side = 0; side < 2; side++)
            {
                var card = Card(side == 0 ? "Original" : "Modified", infos[side], pictures[side]);
                Grid.SetColumn(card, side);
                row.Children.Add(card);
            }
            panel.Children.Add(row);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            panel.Children.Clear();
            panel.Children.Add(Ui.Text("Could not load this file's bytes: " + error.Message, Ui.Danger, 12));
        }
    }

    private static Control Card(string title, ContentMetadata? info, Bitmap? picture)
    {
        var card = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Top, Name = "BinarySide_" + title };
        card.Children.Add(Ui.Text(title, Ui.TextBrush, 13));
        if (info?.Sha256 is null) { card.Children.Add(Ui.Text("No file", Ui.Muted, 12)); return card; }
        if (picture is not null)
            card.Children.Add(new Border
            {
                Background = Ui.Elevated,
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Child = new Image { Source = picture, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 480 }
            });
        card.Children.Add(Ui.Text($"{info.MediaType ?? "binary data"}, {info.ByteLength:N0} bytes", Ui.Muted, 12));
        card.Children.Add(Ui.Text("SHA-256 " + info.Sha256[..12], Ui.Muted, 12));
        return card;
    }

    public void Dispose()
    {
        lifetime.Cancel();
        foreach (var bitmap in bitmaps) bitmap.Dispose();
        bitmaps.Clear();
        lifetime.Dispose();
    }
}