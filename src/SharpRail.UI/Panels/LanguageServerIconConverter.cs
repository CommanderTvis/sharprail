using System.Globalization;

using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Panels;

internal sealed class LanguageServerIconConverter : IValueConverter
{
    private static readonly Dictionary<string, Bitmap> Bitmaps = [];

    /// <summary>The mark the server's plugin provides, or a generic one.</summary>
    internal static Control Create(string server)
    {
        if (LanguageServerNameConverter.Presentation(server) is not { Icon: { Length: > 0 } data } presentation) return Ui.Icon("code", Ui.TextBrush);
        if (presentation.IconFormat == "png")
        {
            if (!Bitmaps.TryGetValue(server, out var bitmap))
            {
                using var input = new MemoryStream(data);
                Bitmaps.Add(server, bitmap = Bitmap.DecodeToWidth(input, 48));
            }
            var frame = new Grid { Width = 24, Height = 24, Tag = "language:" + server };
            frame.Children.Add(new Image { Source = bitmap, Width = 24, Height = 24 });
            return frame;
        }
        var icon = new AssetIcon("language:" + server, "code", Ui.TextBrush, 24);
        icon.Show(data, Ui.TextBrush, 24);
        return icon;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string server ? Create(server) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}