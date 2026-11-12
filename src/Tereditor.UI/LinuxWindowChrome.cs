using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.UI;

/// <summary>The window manager facts that decide whether Linux draws its own window frame.</summary>
/// <param name="Override"><c>SHARPRAIL_WINDOW_DECORATIONS</c>: <c>system</c>, <c>client</c> or unset for automatic.</param>
/// <param name="Desktop"><c>XDG_CURRENT_DESKTOP</c>, a colon-separated list such as <c>ubuntu:GNOME</c>.</param>
/// <param name="Display"><c>DISPLAY</c>: the X11 or XWayland connection the windowing backend uses.</param>
/// <param name="Setting">The host's window decorations setting: empty or <c>auto</c>, <c>client</c> or <c>system</c>.</param>
public readonly record struct LinuxSession(string? Override, string? Desktop, string? Display, string? Setting = null)
{
    public static LinuxSession FromEnvironment() => new(
        Environment.GetEnvironmentVariable("SHARPRAIL_WINDOW_DECORATIONS"),
        Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP"),
        Environment.GetEnvironmentVariable("DISPLAY"));
}

/// <summary>One window-control button of the integrated header: its accessible name and 10px glyph outline.</summary>
public readonly record struct CaptionButton(string Name, string Glyph, bool Close = false);

/// <summary>
/// Linux client-side decorations: the header strip doubles as the title bar and the app draws the frame (shadow, border,
/// resize grips) and the window controls, so no separate window-manager title bar row is spent. Only the X11 backend
/// (including XWayland) is in use, and only on desktops known to honour its frame hints; elsewhere the system title bar stays.
/// </summary>
public static class LinuxWindowChrome
{
    /// <summary>Desktops whose window managers composite, honour <c>_GTK_FRAME_EXTENTS</c> shadows and <c>_NET_WM_MOVERESIZE</c>.</summary>
    private static readonly string[] Supported = ["GNOME", "KDE", "X-Cinnamon", "Budgie", "Pantheon", "UNITY", "Deepin", "COSMIC", "XFCE"];

    /// <summary>Tiling and minimal window managers, which tile or place windows themselves and expect no app-drawn frame.</summary>
    private static readonly string[] Tiling = ["i3", "sway", "Hyprland", "bspwm", "awesome", "dwm", "river", "niri", "qtile", "xmonad", "herbstluftwm", "leftwm", "spectrwm"];

    /// <summary>The environment override as <c>system</c> or <c>client</c>; null when unset or not one of those.</summary>
    public static string? ActiveOverride(LinuxSession session) => Choice(session.Override);

    private static string? Choice(string? value) => value?.Trim().ToLowerInvariant() is "system" or "client" ? value.Trim().ToLowerInvariant() : null;

    /// <summary>
    /// Whether to draw the frame ourselves. With no X11 connection the system title bar stays. Otherwise the environment override
    /// wins, then the host setting (<c>system</c>, <c>client</c>); automatic requires a known compositing desktop that is not tiling,
    /// and anything unknown or unset falls back to the system title bar.
    /// </summary>
    public static bool UseClientDecorations(LinuxSession session)
    {
        if (string.IsNullOrEmpty(session.Display)) return false;
        switch (Choice(session.Override) ?? Choice(session.Setting))
        {
            case "system": return false;
            case "client": return true;
        }
        var desktops = (session.Desktop ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Has(string[] names) => desktops.Any(desktop => names.Contains(desktop, StringComparer.OrdinalIgnoreCase));
        return !Has(Tiling) && Has(Supported);
    }

    /// <summary>What the Settings row says while the environment override decides; empty when it does not.</summary>
    public static string OverrideNote(LinuxSession session) => ActiveOverride(session) is { } choice
        ? $"SHARPRAIL_WINDOW_DECORATIONS={choice} is set in this app’s environment and overrides this choice."
        : "";

    /// <summary>Whether this process draws the Linux window frame itself ever: the X11 backend runs on Linux but not Android.</summary>
    public static bool Available { get; } = OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid();

    /// <summary>The decision for a window now, from the process environment and the host's setting.</summary>
    public static bool Client(string? setting) => Available && UseClientDecorations(LinuxSession.FromEnvironment() with { Setting = setting });

    /// <summary>The window controls left to right: minimize, maximize or restore, close.</summary>
    public static IReadOnlyList<CaptionButton> CaptionButtons(bool maximized) =>
    [
        new("Minimize", "M0,5 L10,5"),
        maximized ? new("Restore", "M2.5,2.5 L2.5,0.5 L9.5,0.5 L9.5,7.5 L7.5,7.5 M0.5,2.5 L7.5,2.5 L7.5,9.5 L0.5,9.5 Z") : new("Maximize", "M0.5,0.5 L9.5,0.5 L9.5,9.5 L0.5,9.5 Z"),
        new("Close", "M0,0 L10,10 M10,0 L0,10", Close: true)
    ];

    /// <summary>
    /// Whether a press on <paramref name="source"/> belongs to the title bar's drag area. Buttons and text boxes, and anything
    /// inside them, keep their own clicks; captions and the space between them start a move.
    /// </summary>
    public static bool StartsMove(Control source) =>
        source is not (Button or TextBox) && !source.GetLogicalAncestors().Any(ancestor => ancestor is Button or TextBox);
}

public sealed partial class WorkbenchWindow
{
    private Panel? linuxCaptionHost;
    private IBrush? linuxBackground;
    private bool? linuxClient;

    /// <summary>
    /// Adds the window controls to the header once; <see cref="ApplyLinuxChrome"/> shows them only while the header is the title bar.
    /// </summary>
    private void WireLinuxChrome(Panel host)
    {
        linuxCaptionHost = host;
        linuxBackground = Background;
        Button Control(CaptionButton caption)
        {
            var button = new Button
            {
                Classes = { "caption-button" },
                IsVisible = false,
                Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(caption.Glyph), Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            if (caption.Close) button.Classes.Add("caption-close");
            ToolTip.SetTip(button, caption.Name);
            AutomationProperties.SetName(button, caption.Name);
            button.Click += (_, _) =>
            {
                switch (caption.Name)
                {
                    case "Minimize": WindowState = WindowState.Minimized; break;
                    case "Close": Close(); break;
                    default: WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
                }
            };
            return button;
        }
        void Fill()
        {
            for (var at = host.Children.Count - 1; at >= 0; at--)
                if (host.Children[at].Classes.Contains("caption-button")) host.Children.RemoveAt(at);
            foreach (var caption in LinuxWindowChrome.CaptionButtons(WindowState == WindowState.Maximized))
            {
                var button = Control(caption);
                button.IsVisible = linuxClient == true;
                host.Children.Add(button);
            }
        }
        host.Children.Add(new Border { Name = "CaptionGap", Width = 8, IsHitTestVisible = false, IsVisible = false });
        Fill();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty) Fill();
            else if (e.Property == WindowDecorationMarginProperty) ApplyLinuxFrame();
        };
        ApplyLinuxChrome();
    }

    /// <summary>
    /// Makes the window match the host's setting now: app-drawn decorations (Avalonia's <see cref="WindowDecorations.BorderOnly"/>:
    /// border, shadow and resize grips) with the header as the title bar, or the window manager's title bar. The X11 backend
    /// is allowed to draw decorations for the whole process (see <c>Program</c>), so this toggles at run time; window managers
    /// that ignore a change of Motif hints after the window is mapped need a new window.
    /// </summary>
    private void ApplyLinuxChrome()
    {
        if (linuxCaptionHost is not { } host) return;
        var client = LinuxWindowChrome.Client(state.Current.Settings.WindowDecorations);
        if (linuxClient == client) return;
        linuxClient = client;
        ExtendClientAreaToDecorationsHint = client;
        WindowDecorations = client ? WindowDecorations.BorderOnly : WindowDecorations.Full;
        Background = client ? Brushes.Transparent : linuxBackground;
        this.FindControl<Grid>("WorkbenchRoot")!.Background = client ? Ui.Workspace : null;
        if (client) Classes.Add("client-decorations"); else Classes.Remove("client-decorations");
        foreach (var child in host.Children)
            if (child.Classes.Contains("caption-button") || child.Name == "CaptionGap") child.IsVisible = client;
        ApplyLinuxFrame();
    }

    /// <summary>The frame and shadow thickness Avalonia reports stays free around the content, transparent over the shadow.</summary>
    private void ApplyLinuxFrame() =>
        this.FindControl<LayoutTransformControl>("ZoomRoot")!.Margin = linuxClient == true ? WindowDecorationMargin : default;
}