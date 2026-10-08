using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace SharpRail.UI.Rendering;

public static partial class Ui
{
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
        ApplyRoles(theme);
        FadeFromElevated.GradientStops[0].Color = FadeToElevated.GradientStops[1].Color = Elevated.Color;
        FadeFromElevated.GradientStops[1].Color = FadeToElevated.GradientStops[0].Color = Alpha(Elevated.Color, 0);
        if (Application.Current is { } app) ApplyResources(app);
        ThemeChanged?.Invoke();
    }

    /// <summary>Writes the theme values that application styles read as resources rather than through these brushes.</summary>
    public static void ApplyResources(Application app)
    {
        app.Resources[SelectionForegroundKey] = SelectionText is { } foreground ? new SolidColorBrush(foreground) : null;
        foreach (var fluent in app.Styles.OfType<FluentTheme>())
            if (fluent.Palettes.TryGetValue(Theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark, out var palette))
                palette.Accent = Accent.Color;
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
        FollowEnabled(button);
        return button;
    }

    // Labels and icons built here carry their own colour, so a disabled button repaints them from the
    // disabled role and restores whatever they wore when it is enabled again.
    private static void FollowEnabled(Button button)
    {
        Control[] parts = button.Content is Panel panel ? [.. panel.Children] : [(Control)button.Content!];
        IBrush?[]? resting = null;
        static IBrush? Read(Control part) => part is TextBlock label ? label.Foreground : ((Border)part).Background;
        static void Write(Control part, IBrush? brush)
        {
            if (part is TextBlock label) label.Foreground = brush; else ((Border)part).Background = brush;
        }
        button.PropertyChanged += (_, e) =>
        {
            if (e.Property != InputElement.IsEffectivelyEnabledProperty) return;
            if (!button.IsEffectivelyEnabled)
            {
                resting ??= [.. parts.Select(Read)];
                foreach (var part in parts) Write(part, button.Classes.Contains("primary") ? PrimaryDisabledText : ControlDisabledText);
            }
            else if (resting is not null)
            {
                for (var index = 0; index < parts.Length; index++) Write(parts[index], resting[index]);
                resting = null;
            }
        };
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
        FollowEnabled(button);
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