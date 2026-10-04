using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace SharpRail.Plugins.UI.Kit;

public static partial class Ui
{
    public static readonly LinearGradientBrush FadeFromElevated = Fade();
    public static readonly LinearGradientBrush FadeToElevated = Fade();
    /// <summary>Resource key for the selected-text foreground; null when the theme keeps the native foreground.</summary>
    public const string SelectionForegroundKey = "ThemeSelectionForeground";
    public static readonly FontFamily InterfaceFont = new("avares://SharpRail.Plugins.UI.Kit/Assets/Fonts#Geist");
    public const FontWeight InterfaceWeight = (FontWeight)370;
    public static readonly FontFamily CodeFont = new("avares://SharpRail.Plugins.UI.Kit/Assets/Fonts#JetBrains Mono");
    private static readonly Dictionary<string, Bitmap> Icons = [];

    /// <summary>The applied manifest. Consumers that paint outside these brushes rebuild on <see cref="ThemeChanged"/>. The kit has no
    /// default: the app applies one before anything reads it.</summary>
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
        var source = new Uri("avares://SharpRail.Plugins.UI.Kit/ControlStyles.axaml");
        if (!app.Styles.OfType<StyleInclude>().Any(style => style.Source == source))
            app.Styles.Add(new StyleInclude(source) { Source = source });
        foreach (var state in new[] { "", "PointerOver", "Pressed", "Disabled" })
        {
            var disabled = state == "Disabled";
            app.Resources["ButtonBackground" + state] = disabled ? ControlDisabledFill : state == "" ? ControlFill : Hover;
            app.Resources["ButtonForeground" + state] = disabled ? ControlDisabledText : TextBrush;
            app.Resources["ButtonBorderBrush" + state] = disabled ? ControlDisabledBorder : ControlBorder;
            app.Resources["RepeatButtonBackground" + state] = disabled || state == "" ? Brushes.Transparent : Hover;
            app.Resources["RepeatButtonForeground" + state] = disabled ? ControlDisabledText : state == "" ? Muted : TextBrush;
            app.Resources["ToggleButtonBackground" + state] = disabled ? ControlDisabledFill : state == "" ? ControlFill : Hover;
            app.Resources["ToggleButtonForeground" + state] = disabled ? ControlDisabledText : TextBrush;
            app.Resources["ToggleButtonBorderBrush" + state] = disabled ? ControlDisabledBorder : ControlBorder;
            app.Resources["ToggleButtonBackgroundChecked" + state] = disabled ? ControlDisabledFill : PrimaryMuted;
            app.Resources["ToggleButtonForegroundChecked" + state] = disabled ? ControlDisabledText : TextBrush;
            app.Resources["ToggleButtonBorderBrushChecked" + state] = disabled ? ControlDisabledBorder : Accent;
            foreach (var value in new[] { "Unchecked", "Checked", "Indeterminate" })
            {
                var selected = value != "Unchecked";
                var suffix = value + state;
                app.Resources["CheckBoxForeground" + suffix] = disabled ? ControlDisabledText : TextBrush;
                app.Resources["CheckBoxCheckBackgroundFill" + suffix] = disabled ? ControlDisabledFill : selected ? (state == "" ? PrimaryFill : PrimaryFillHover) : ControlFill;
                app.Resources["CheckBoxCheckBackgroundStroke" + suffix] = disabled ? ControlDisabledBorder : selected ? PrimaryFill : ControlBorder;
                app.Resources["CheckBoxCheckGlyphForeground" + suffix] = disabled ? ControlDisabledText : OnPrimary;
            }
        }
        foreach (var fluent in app.Styles.OfType<FluentTheme>())
            if (fluent.Palettes.TryGetValue(Theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark, out var palette))
                palette.Accent = Accent.Color;
    }

    private static Color ReadableForeground(Color preferred, params Color[] backgrounds)
    {
        static double Luminance(Color color)
        {
            static double Linear(byte channel)
            {
                var value = channel / 255.0;
                return value <= .04045 ? value / 12.92 : Math.Pow((value + .055) / 1.055, 2.4);
            }
            return .2126 * Linear(color.R) + .7152 * Linear(color.G) + .0722 * Linear(color.B);
        }
        double Contrast(Color foreground) => backgrounds.Min(background =>
        {
            var a = Luminance(foreground);
            var b = Luminance(background);
            return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
        });
        if (Contrast(preferred) >= 4.5) return preferred;
        return Contrast(Colors.Black) >= Contrast(Colors.White) ? Colors.Black : Colors.White;
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
            using var stream = AssetLoader.Open(new Uri($"avares://SharpRail.Plugins.UI.Kit/Assets/Icons/{name}.png"));
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
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        row.Children.Add(Icon(icon, color)); row.Children.Add(Text(text, color));
        return row;
    }

    public static Button Button(string label, Action action, string? icon = null)
    {
        var button = new Button
        {
            Content = icon is null ? Text(label) : Row(icon, label),
            Padding = new Thickness(12, 4),
            BorderBrush = BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new(4)
        };
        foreach (var text in ((Control)button.Content!).GetLogicalDescendants().Prepend((Control)button.Content!).OfType<TextBlock>())
            text.Bind(TextBlock.ForegroundProperty, new Binding(nameof(button.Foreground)) { Source = button });
        foreach (var glyph in ((Control)button.Content!).GetLogicalDescendants().Prepend((Control)button.Content!).OfType<Border>())
            glyph.Bind(Border.BackgroundProperty, new Binding(nameof(button.Foreground)) { Source = button });
        button.Click += (_, _) => action();
        FollowEnabled(button);
        return button;
    }

    /// <summary>
    /// Labels and icons built by <see cref="Text"/> and <see cref="Icon"/> carry their own colour, so a disabled
    /// button repaints them from the disabled role and restores what they wore when it is enabled again.
    /// </summary>
    public static void FollowEnabled(Button button)
    {
        (Control Part, IBrush? Brush)[]? resting = null;
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
                // Owners replace the content after construction, so the parts are read when the state changes.
                IEnumerable<Control> parts = button.Content is Panel panel ? panel.Children : button.Content is Control single ? [single] : [];
                resting ??= [.. parts.Where(part => part is TextBlock or Border).Select(part => (part, Read(part)))];
                foreach (var (part, _) in resting) Write(part, button.Classes.Contains("primary") ? PrimaryDisabledText : ControlDisabledText);
            }
            else if (resting is not null)
            {
                foreach (var (part, brush) in resting) Write(part, brush);
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
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new(0),
            CornerRadius = new(0)
        };
        ((Border)button.Content!).Bind(Border.BackgroundProperty, new Binding(nameof(button.Foreground)) { Source = button });
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

    /// <summary>How long ago <paramref name="time"/> was, measured against the given now rather than the clock.</summary>
    public static string RelativeTime(DateTimeOffset time, DateTimeOffset now)
    {
        var elapsed = now - time;
        if (elapsed < TimeSpan.FromMinutes(1)) return "just now";
        if (elapsed < TimeSpan.FromHours(1)) return $"{(int)elapsed.TotalMinutes}m ago";
        if (elapsed < TimeSpan.FromDays(1)) return $"{(int)elapsed.TotalHours}h ago";
        return $"{(int)elapsed.TotalDays}d ago";
    }

    public static MenuItem Menu(string label, Action action, bool enabled = true)
    {
        var item = new MenuItem { Header = label, IsEnabled = enabled };
        item.Click += (_, _) => action();
        return item;
    }

    /// <summary>One option of a segmented toggle: shared hover feedback and an accent outline when selected.</summary>
    public static ToggleButton Segment(string name, string label)
    {
        var button = new ToggleButton
        {
            Name = name,
            Content = label,
            Height = 20,
            Padding = new Thickness(8, 0),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Foreground = Muted
        };
        AutomationProperties.SetName(button, label);
        return button;
    }

    /// <summary>The elevated, rounded chip that holds a path or a list value.</summary>
    public static Border Chip(Control child, Thickness? padding = null) => new()
    {
        Background = Elevated,
        CornerRadius = new CornerRadius(4),
        Padding = padding ?? new Thickness(8, 2),
        Child = child
    };
}