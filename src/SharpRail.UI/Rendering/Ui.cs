using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace SharpRail.UI.Rendering;

public static class Ui
{
    public static readonly SolidColorBrush Sidebar = new(Color.Parse("#101013"));
    public static readonly SolidColorBrush Surface = new(Color.Parse("#18181b"));
    public static readonly SolidColorBrush Header = new(Color.Parse("#18181b"));
    public static readonly SolidColorBrush Elevated = new(Color.Parse("#09090b"));
    public static readonly SolidColorBrush TextBrush = new(Color.Parse("#f4f4f5"));
    public static readonly SolidColorBrush Muted = new(Color.Parse("#babac1"));
    public static readonly SolidColorBrush Hint = new(Color.Parse("#71717a"));
    public static readonly SolidColorBrush Accent = new(Color.Parse("#8dff4f"));
    public static readonly SolidColorBrush PrimaryMuted = new(Color.FromArgb(102, 141, 255, 79));
    public static readonly SolidColorBrush BorderBrush = new(Color.Parse("#3f3f46"));
    public static readonly SolidColorBrush Hover = new(Color.Parse("#27272a"));
    public static readonly SolidColorBrush TextSelection = new(Color.FromArgb(38, 106, 200, 255));
    public static readonly SolidColorBrush PreviewSelection = new(Color.FromArgb(15, 106, 200, 255));
    public static readonly SolidColorBrush Success = new(Color.Parse("#41cb66"));
    public static readonly SolidColorBrush Danger = new(Color.Parse("#ff4b75"));
    public static readonly SolidColorBrush Info = new(Color.Parse("#6ac8ff"));
    public static readonly SolidColorBrush Warning = new(Color.Parse("#ffd54b"));
    public static readonly FontFamily InterfaceFont = new("avares://SharpRail.UI/Assets/Fonts#Geist");
    public const FontWeight InterfaceWeight = (FontWeight)370;
    public static readonly FontFamily CodeFont = new("avares://SharpRail.UI/Assets/Fonts#JetBrains Mono");
    private static readonly Dictionary<string, Bitmap> Icons = [];
    public static void SetLight(bool light)
    {
        Sidebar.Color = Color.Parse(light ? "#fafafa" : "#101013");
        Surface.Color = Color.Parse(light ? "#e4e4e7" : "#18181b");
        Header.Color = Color.Parse(light ? "#f4f4f5" : "#18181b");
        Elevated.Color = Color.Parse(light ? "#ffffff" : "#09090b");
        TextBrush.Color = Color.Parse(light ? "#27272a" : "#f4f4f5");
        Muted.Color = Color.Parse(light ? "#52525b" : "#babac1");
        Hint.Color = Color.Parse(light ? "#71717a" : "#71717a");
        Accent.Color = Color.Parse(light ? "#2a7314" : "#8dff4f");
        PrimaryMuted.Color = Color.FromArgb(102, Accent.Color.R, Accent.Color.G, Accent.Color.B);
        BorderBrush.Color = Color.Parse(light ? "#d4d4d8" : "#3f3f46");
        Hover.Color = Color.Parse(light ? "#d4d4d8" : "#27272a");
        TextSelection.Color = light ? Color.FromArgb(56, 107, 87, 255) : Color.FromArgb(38, 106, 200, 255);
        PreviewSelection.Color = light ? Color.FromArgb(22, 107, 87, 255) : Color.FromArgb(15, 106, 200, 255);
        Info.Color = Color.Parse(light ? "#2265cf" : "#6ac8ff");
        Warning.Color = Color.Parse(light ? "#946300" : "#ffd54b");
        Success.Color = Color.Parse(light ? "#167230" : "#41cb66");
        Danger.Color = Color.Parse(light ? "#d02533" : "#ff4b75");
    }

    public static Control Icon(string name, IBrush? color = null, double size = 16)
    {
        if (!Icons.TryGetValue(name, out var bitmap))
        {
            using var stream = AssetLoader.Open(new Uri($"avares://SharpRail.UI/Assets/Icons/{name}.png"));
            bitmap = new Bitmap(stream);
            Icons[name] = bitmap;
        }
        return new Border
        {
            Width = size,
            Height = size,
            Background = color ?? Muted,
            OpacityMask = new ImageBrush(bitmap),
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    public static double FontSize { get; set; } = 14;
    public static TextBlock Text(string text, IBrush? color = null, double? size = null) => new()
    {
        Text = text,
        Foreground = color ?? Muted,
        FontSize = size ?? FontSize,
        FontWeight = InterfaceWeight,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    public static StackPanel Row(string icon, string text, IBrush? color = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(Icon(icon, color)); row.Children.Add(Text(text, color));
        return row;
    }

    public static Button Button(string label, Action action, string? icon = null)
    {
        var button = new Button
        {
            Content = icon is null ? Text(label) : Row(icon, label),
            Padding = new Thickness(10, 5),
            Background = Elevated,
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new(4)
        };
        button.Click += (_, _) => action();
        return button;
    }

    public static Button IconButton(string icon, string tooltip, Action action)
    {
        var button = new Button
        {
            Content = Icon(icon),
            Width = 32,
            Height = 32,
            Padding = new Thickness(7),
            Background = Brushes.Transparent,
            BorderThickness = new(0),
            CornerRadius = new(0)
        };
        ToolTip.SetTip(button, tooltip);
        AutomationProperties.SetName(button, tooltip);
        button.Click += (_, _) => action();
        return button;
    }

    public static Border Frame(Control child, IBrush? background = null) => new()
    {
        Child = child,
        Background = background ?? Sidebar,
        BorderBrush = BorderBrush,
        BorderThickness = new Thickness(0, 0, 1, 1)
    };

    public static void Place(Grid grid, Control child, int row = 0, int column = 0)
    {
        Grid.SetRow(child, row); Grid.SetColumn(child, column); grid.Children.Add(child);
    }

    public static MenuItem Menu(string label, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = label, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }
}
