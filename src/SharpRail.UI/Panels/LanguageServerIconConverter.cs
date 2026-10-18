using System.Globalization;

using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Panels;

internal sealed class LanguageServerIconConverter : IValueConverter
{
    private static readonly Lazy<byte[]> IntelliJ = new(() => ReadBrand("intellij-idea.svg"));

    private static readonly Dictionary<string, Bitmap> Bitmaps = [];
    private static readonly Lazy<byte[]> Air = new(() => ReadBrand("air.svg"));

    internal static Control CreateAirIcon() => new SvgAsset(Air.Value, 24);

    private static byte[] ReadBrand(string name)
    {
        using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://SharpRail.UI/Assets/Brands/" + name));
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    internal static Control Create(string server)
    {
        var provider = server switch
        {
            "python" or "ruby" or "scala" or "php" or "shell" => server + "-provider.png",
            "clangd" or "rust" => server + "-provider.svg",
            _ => ""
        };
        if (provider.Length > 0)
        {
            var uri = new Uri("avares://SharpRail.UI/Assets/LanguageIcons/" + provider);
            if (provider.EndsWith(".png", StringComparison.Ordinal))
            {
                if (!Bitmaps.TryGetValue(provider, out var bitmap))
                {
                    using var input = Avalonia.Platform.AssetLoader.Open(uri);
                    Bitmaps.Add(provider, bitmap = Bitmap.DecodeToWidth(input, 48));
                }
                var frame = new Grid { Width = 24, Height = 24, Tag = "language:" + server };
                frame.Children.Add(new Image { Source = bitmap, Width = 24, Height = 24 });
                return frame;
            }
            using var inputSvg = Avalonia.Platform.AssetLoader.Open(uri);
            using var svgBuffer = new MemoryStream();
            inputSvg.CopyTo(svgBuffer);
            var providerIcon = new AssetIcon("language:" + server, "code", Ui.TextBrush, 24);
            providerIcon.Show(svgBuffer.ToArray(), Ui.TextBrush, 24);
            return providerIcon;
        }
        var asset = server switch
        {
            "typescript" => "typescript",
            "python" => "python",
            "php" => "php",
            "css" => "css",
            "shell" => "console",
            "csharp" => "csharp",
            "clangd" => "cpp",
            "java" => "java",
            "kotlin" => "kotlin",
            "ruby" => "ruby",
            "go" => "go",
            "rust" => "rust",
            "scala" => "scala",
            "r" => "r",
            "powershell" => "powershell",
            "swift" => "swift",
            "dart" => "dart",
            _ => ""
        };
        if (server != "intellij")
        {
            if (asset.Length == 0) return Ui.Icon("code", Ui.TextBrush);
            var languageIcon = new AssetIcon("language:" + server, "code", Ui.TextBrush, 24);
            using var stream = Avalonia.Platform.AssetLoader.Open(new Uri("avares://SharpRail.UI/Assets/LanguageIcons/" + asset + ".svg"));
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            languageIcon.Show(buffer.ToArray(), Ui.TextBrush, 24);
            return languageIcon;
        }
        var icon = new AssetIcon("language:intellij", "code", Ui.TextBrush, 24);
        icon.Show(IntelliJ.Value, Ui.TextBrush, 24);
        return icon;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string server ? Create(server) : null;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}