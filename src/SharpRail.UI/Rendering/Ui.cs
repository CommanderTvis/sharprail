using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace SharpRail.UI.Rendering;

public static class Ui
{
    public static readonly SolidColorBrush Sidebar = new();
    public static readonly SolidColorBrush Surface = new();
    public static readonly SolidColorBrush Header = new();
    public static readonly SolidColorBrush Elevated = new();
    public static readonly SolidColorBrush TextBrush = new();
    public static readonly SolidColorBrush Muted = new();
    public static readonly SolidColorBrush Hint = new();
    public static readonly SolidColorBrush Accent = new();
    // The reference's control-primary tokens: the solid primary button fill, its hover step and its label.
    public static readonly SolidColorBrush PrimaryFill = new();
    public static readonly SolidColorBrush PrimaryFillHover = new();
    public static readonly SolidColorBrush OnPrimary = new();
    public static readonly SolidColorBrush DialogShadow = new();
    public static readonly SolidColorBrush PrimarySubtle = new();
    public static readonly SolidColorBrush PrimaryMuted = new();
    public static readonly SolidColorBrush BorderBrush = new();
    public static readonly SolidColorBrush Hover = new();
    public static readonly SolidColorBrush TextSelection = new();
    public static readonly SolidColorBrush PreviewSelection = new();
    public static readonly SolidColorBrush Success = new();
    public static readonly SolidColorBrush Danger = new();
    public static readonly SolidColorBrush Info = new();
    public static readonly SolidColorBrush Warning = new();
    public static readonly SolidColorBrush SuccessWash = new();
    public static readonly SolidColorBrush DangerWash = new();
    public static readonly SolidColorBrush InfoWash = new();
    public static readonly SolidColorBrush WarningWash = new();
    public static readonly LinearGradientBrush FadeFromElevated = Fade();
    public static readonly LinearGradientBrush FadeToElevated = Fade();
    /// <summary>Resource key for the selected-text foreground; null when the theme keeps the native foreground.</summary>
    public const string SelectionForegroundKey = "ThemeSelectionForeground";
    public static readonly FontFamily InterfaceFont = new("avares://SharpRail.UI/Assets/Fonts#Geist");
    public const FontWeight InterfaceWeight = (FontWeight)370;
    public static readonly FontFamily CodeFont = new("avares://SharpRail.UI/Assets/Fonts#JetBrains Mono");
    private static readonly Dictionary<string, Bitmap> Icons = [];

    static Ui() => Apply(Themes.Resolve(Themes.DefaultId));

    /// <summary>The applied manifest. Consumers that paint outside these brushes rebuild on <see cref="ThemeChanged"/>.</summary>
    public static ThemeManifest Theme { get; private set; } = null!;

    /// <summary>Raised after every brush of a new theme has been written, so no consumer observes half a palette.</summary>
    public static event Action? ThemeChanged;

    public static void Apply(ThemeManifest theme)
    {
        if (ReferenceEquals(theme, Theme)) return;
        Theme = theme;
        Sidebar.Color = theme["sidebar"];
        Surface.Color = theme["content"];
        Header.Color = theme["header"];
        Elevated.Color = theme["elevated"];
        TextBrush.Color = theme["text"];
        Muted.Color = theme["muted"];
        Hint.Color = theme["hint"];
        Accent.Color = theme["accent"];
        PrimaryFill.Color = theme["accentSolid"];
        PrimaryFillHover.Color = theme["accentHover"];
        OnPrimary.Color = theme["onAccent"];
        DialogShadow.Color = Color.FromArgb(theme.IsLight ? (byte)36 : (byte)102, 0, 0, 0);
        PrimarySubtle.Color = Alpha(theme["accent"], 10);
        PrimaryMuted.Color = Alpha(theme["accent"], 40);
        BorderBrush.Color = theme["borderStrong"];
        Hover.Color = theme["hover"];
        TextSelection.Color = theme["selection"];
        PreviewSelection.Color = Alpha(theme["selection"], 40);
        Info.Color = theme["info"];
        Warning.Color = theme["warning"];
        Success.Color = theme["success"];
        Danger.Color = theme["danger"];
        InfoWash.Color = Alpha(theme["info"], 12);
        WarningWash.Color = Alpha(theme["warning"], 12);
        SuccessWash.Color = Alpha(theme["success"], 12);
        DangerWash.Color = Alpha(theme["danger"], 12);
        FadeFromElevated.GradientStops[0].Color = FadeToElevated.GradientStops[1].Color = theme["elevated"];
        FadeFromElevated.GradientStops[1].Color = FadeToElevated.GradientStops[0].Color = Alpha(theme["elevated"], 0);
        if (Application.Current is { } app) ApplyResources(app);
        ThemeChanged?.Invoke();
    }

    /// <summary>Writes the theme values that application styles read as resources rather than through these brushes.</summary>
    public static void ApplyResources(Application app)
    {
        app.Resources[SelectionForegroundKey] = Theme.Colors["selectionForeground"] is { } foreground ? new SolidColorBrush(foreground) : null;
        foreach (var fluent in app.Styles.OfType<FluentTheme>())
            if (fluent.Palettes.TryGetValue(Theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark, out var palette))
                palette.Accent = Theme["accent"];
    }

    private static LinearGradientBrush Fade() => new()
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
        GradientStops = [new GradientStop { Offset = 0 }, new GradientStop { Offset = 1 }]
    };

    /// <summary>The reference's alpha scale: a percentage of the colour's own alpha.</summary>
    public static Color Alpha(Color color, int percent) =>
        Color.FromArgb((byte)Math.Round(color.A * percent / 100.0, MidpointRounding.AwayFromZero), color.R, color.G, color.B);

    /// <summary>A translucent colour composited over an opaque background, for native surfaces without alpha.</summary>
    public static Color Over(Color color, Color background)
    {
        byte Mix(byte top, byte bottom) => (byte)Math.Round((top * color.A + bottom * (255 - color.A)) / 255.0);
        return Color.FromRgb(Mix(color.R, background.R), Mix(color.G, background.G), Mix(color.B, background.B));
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