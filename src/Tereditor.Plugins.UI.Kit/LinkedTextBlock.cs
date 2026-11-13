using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace Tereditor.Plugins.UI.Kit;

/// <summary>Raised when a link inside a <see cref="LinkedTextBlock"/> is activated; the hosting surface opens it through the app's one open-URL path.</summary>
public sealed class LinkClickedEventArgs(Uri url) : RoutedEventArgs(LinkedTextBlock.LinkClickedEvent) { public Uri Url { get; } = url; }

/// <summary>
/// Plain text whose http(s) URLs are inline links (accent colour, underline on hover, pointer cursor, focusable, named by the URL). Trailing
/// punctuation stays outside the link. Set <see cref="Source"/> instead of <c>Text</c>; clicks bubble as <see cref="LinkClickedEvent"/>.
/// </summary>
public class LinkedTextBlock : SelectableTextBlock
{
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<LinkedTextBlock, string?>(nameof(Source));

    public static readonly RoutedEvent<LinkClickedEventArgs> LinkClickedEvent =
        RoutedEvent.Register<LinkedTextBlock, LinkClickedEventArgs>(nameof(LinkClicked), RoutingStrategies.Bubble);

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    public event EventHandler<LinkClickedEventArgs>? LinkClicked { add => AddHandler(LinkClickedEvent, value); remove => RemoveHandler(LinkClickedEvent, value); }

    /// <summary>Splits text into plain parts and http(s) URLs, leaving trailing punctuation and an unmatched closing bracket in the plain part.</summary>
    public static IReadOnlyList<(string Text, Uri? Url)> Split(string? text)
    {
        var parts = new List<(string, Uri?)>();
        text ??= "";
        var plain = 0;
        for (var at = 0; at < text.Length;)
        {
            var start = text.IndexOf("http", at, StringComparison.OrdinalIgnoreCase);
            if (start < 0) break;
            var scheme = text.AsSpan(start).StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? 8 : text.AsSpan(start).StartsWith("http://", StringComparison.OrdinalIgnoreCase) ? 7 : 0;
            if (scheme == 0 || (start > 0 && char.IsLetterOrDigit(text[start - 1]))) { at = start + 4; continue; }
            var end = start + scheme;
            while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '"' or '`')) end++;
            while (end > start + scheme)
            {
                var last = text[end - 1];
                var open = last switch { ')' => '(', ']' => '[', '}' => '{', _ => '\0' };
                if (".,;:!?'".Contains(last) || (open != '\0' && text.AsSpan(start, end - start).Count(open) < text.AsSpan(start, end - start).Count(last))) end--;
                else break;
            }
            if (end > start + scheme && Uri.TryCreate(text[start..end], UriKind.Absolute, out var url))
            {
                if (start > plain) parts.Add((text[plain..start], null));
                parts.Add((text[start..end], url));
                plain = end;
            }
            at = Math.Max(end, start + 4);
        }
        if (plain < text.Length) parts.Add((text[plain..], null));
        return parts;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsVisibleProperty) Rebuild();
    }

    private void Rebuild()
    {
        var parts = Split(Source);
        // Without a URL (or while hidden) the block is an ordinary text block, so Text reads as it always did.
        // A hidden block is never laid out, yet may still render at width 0, which would resize embedded links mid-render: it stays plain until shown.
        if (!IsVisible || parts.All(part => part.Url is null)) { Inlines?.Clear(); Text = Source; return; }
        Text = null;
        var inlines = Inlines ??= new InlineCollection();
        inlines.Clear();
        foreach (var (text, url) in parts)
        {
            if (url is null) { inlines.Add(new Run(text)); continue; }
            var link = new LinkLabel(text);
            link.Activated += () => RaiseEvent(new LinkClickedEventArgs(url));
            inlines.Add(new InlineUIContainer(link));
        }
    }
}

/// <summary>
/// One link inside the text: a focusable accent label with no visual children (a templated control would be applied while the text renders),
/// underlined on hover or keyboard focus, activated by click, Enter or Space.
/// </summary>
internal sealed class LinkLabel : TextBlock
{
    private bool pressed;

    internal LinkLabel(string url)
    {
        Text = url;
        Foreground = Ui.Accent;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        AutomationProperties.SetName(this, url);
        ToolTip.SetTip(this, url);
        GotFocus += (_, _) => TextDecorations = Avalonia.Media.TextDecorations.Underline;
        LostFocus += (_, _) => TextDecorations = null;
    }

    internal event Action? Activated;

    protected override Type StyleKeyOverride => typeof(TextBlock);

    // An embedded control sits with its bottom on the line's baseline unless it names its own baseline, which lifted the link by its descent.
    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (TextLayout.TextLines.Count > 0) SetBaselineOffset(this, TextLayout.TextLines[0].Baseline + Padding.Top);
        return size;
    }

    protected override void OnPointerEntered(PointerEventArgs e) { base.OnPointerEntered(e); TextDecorations = Avalonia.Media.TextDecorations.Underline; }
    protected override void OnPointerExited(PointerEventArgs e) { base.OnPointerExited(e); if (!IsKeyboardFocusWithin) TextDecorations = null; }
    protected override void OnPointerPressed(PointerPressedEventArgs e) { base.OnPointerPressed(e); pressed = e.GetCurrentPoint(this).Properties.IsLeftButtonPressed; e.Handled = pressed; }
    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (pressed && new Rect(Bounds.Size).Contains(e.GetPosition(this))) Activated?.Invoke();
        pressed = false;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key is Key.Enter or Key.Space) { e.Handled = true; Activated?.Invoke(); }
    }
}