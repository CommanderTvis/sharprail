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
public readonly record struct LinuxSession(string? Override, string? Desktop, string? Display)
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

    /// <summary>
    /// Whether to draw the frame ourselves. <c>system</c> always keeps the title bar; <c>client</c> forces ours when there is an
    /// X11 connection to draw through; automatic requires an X11 connection and a known compositing desktop that is not tiling,
    /// and anything unknown or unset falls back to the system title bar.
    /// </summary>
    public static bool UseClientDecorations(LinuxSession session)
    {
        if (string.IsNullOrEmpty(session.Display)) return false;
        switch (session.Override?.Trim().ToLowerInvariant())
        {
            case "system": return false;
            case "client": return true;
        }
        var desktops = (session.Desktop ?? "").Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool Has(string[] names) => desktops.Any(desktop => names.Contains(desktop, StringComparer.OrdinalIgnoreCase));
        return !Has(Tiling) && Has(Supported);
    }

    /// <summary>The decision for this process, taken before the windowing backend starts and fixed for its lifetime.</summary>
    public static bool Client { get; } = OperatingSystem.IsLinux() && !OperatingSystem.IsAndroid() && UseClientDecorations(LinuxSession.FromEnvironment());

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
    /// <summary>
    /// Switches the window to app-drawn decorations: the border, shadow and resize grips come from Avalonia
    /// (<see cref="WindowDecorations.BorderOnly"/>), the header stays the title bar and gains the window controls.
    /// The frame and shadow thickness it reports is kept free around the content, transparent over the shadow.
    /// </summary>
    private void UseLinuxClientDecorations(Panel host)
    {
        Classes.Add("client-decorations");
        WindowDecorations = WindowDecorations.BorderOnly;
        Background = Brushes.Transparent;
        var root = this.FindControl<Grid>("WorkbenchRoot")!;
        root.Background = Ui.Workspace;
        var zoom = this.FindControl<LayoutTransformControl>("ZoomRoot")!;
        void Frame() => zoom.Margin = WindowDecorationMargin;
        Frame();
        PropertyChanged += (_, e) => { if (e.Property == WindowDecorationMarginProperty) Frame(); };
        Button Control(CaptionButton caption)
        {
            var button = new Button
            {
                Classes = { "caption-button" },
                Content = new Avalonia.Controls.Shapes.Path { Data = Geometry.Parse(caption.Glyph), Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
            };
            if (caption.Close) button.Classes.Add("caption-close");
            ToolTip.SetTip(button, caption.Name);
            AutomationProperties.SetName(button, caption.Name);
            return button;
        }
        void Fill()
        {
            for (var at = host.Children.Count - 1; at >= 0; at--)
                if (host.Children[at].Classes.Contains("caption-button")) host.Children.RemoveAt(at);
            foreach (var caption in LinuxWindowChrome.CaptionButtons(WindowState == WindowState.Maximized))
            {
                var button = Control(caption);
                button.Click += (_, _) =>
                {
                    switch (caption.Name)
                    {
                        case "Minimize": WindowState = WindowState.Minimized; break;
                        case "Close": Close(); break;
                        default: WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized; break;
                    }
                };
                host.Children.Add(button);
            }
        }
        host.Children.Add(new Border { Width = 8, IsHitTestVisible = false });
        Fill();
        PropertyChanged += (_, e) => { if (e.Property == WindowStateProperty) Fill(); };
    }
}