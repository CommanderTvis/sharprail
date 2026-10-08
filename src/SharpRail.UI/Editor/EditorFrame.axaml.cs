using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;

using SharpRail.Scintilla;
using SharpRail.UI.Rendering;

using SkiaSharp;

namespace SharpRail.UI.Editor;

/// <summary>A Scintilla editor with the workbench's font, theme colours and auto-hiding scrollbars.</summary>
internal sealed partial class EditorFrame : UserControl
{
    // Shared by every editor for the application's lifetime.
    private static readonly Lazy<SKTypeface> Typeface = new(() =>
    {
        using var stream = AssetLoader.Open(new Uri("avares://SharpRail.UI/Assets/Fonts/JetBrainsMono-Regular.ttf"));
        return SKTypeface.FromStream(stream);
    });
    private readonly ScrollBar vertical;
    private readonly ScrollBar horizontal;
    private bool syncingScroll;
    internal ScintillaEditor Editor { get; }

    /// <summary>Raised on theme changes after the editor colours update, for owners that derive line styles.</summary>
    internal event Action? ThemeApplied;

    internal EditorFrame(string text, string name)
    {
        AvaloniaXamlLoader.Load(this);
        vertical = this.FindControl<ScrollBar>("EditorVerticalScroll")!;
        horizontal = this.FindControl<ScrollBar>("EditorHorizontalScroll")!;
        Editor = new(text, Typeface.Value) { Name = name, Colors = ThemeColors() };
        this.FindControl<ContentControl>("EditorBody")!.Content = Editor;
        Editor.ScrollChanged += (_, _) => SyncScrollBars();
        vertical.ValueChanged += (_, e) => { if (!syncingScroll) Editor.ScrollToLine(e.NewValue); };
        horizontal.ValueChanged += (_, e) => { if (!syncingScroll) Editor.ScrollToX(e.NewValue); };
    }

    private static ScintillaColors ThemeColors() => new(Ui.TextBrush.Color, Ui.Surface.Color, Ui.Muted.Color,
        Ui.Over(Ui.EditorSelection, Ui.Surface.Color), Ui.EditorSelectionText);

    private void ApplyTheme() { Editor.Colors = ThemeColors(); ThemeApplied?.Invoke(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); ApplyTheme(); Ui.ThemeChanged += ApplyTheme; }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { Ui.ThemeChanged -= ApplyTheme; base.OnDetachedFromVisualTree(e); }

    private void SyncScrollBars()
    {
        syncingScroll = true;
        try
        {
            Apply(vertical, Editor.VerticalScroll);
            Apply(horizontal, Editor.HorizontalScroll);
        }
        finally { syncingScroll = false; }

        static void Apply(ScrollBar bar, EditorScroll scroll)
        {
            bar.Maximum = scroll.Maximum; bar.ViewportSize = scroll.Viewport;
            bar.LargeChange = Math.Max(1, scroll.Viewport); bar.Value = scroll.Value;
            bar.IsVisible = scroll.Maximum > 0;
        }
    }
}