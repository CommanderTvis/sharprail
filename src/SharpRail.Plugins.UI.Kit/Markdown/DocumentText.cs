using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.LogicalTree;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// Selectable document text that behaves like an editor's: its selection outlives focus, and links and other controls
/// inside it count as their words, highlighted, copied and reported with the text around them.
/// </summary>
internal sealed class DocumentText : SelectableTextBlock
{
    private const char Placeholder = '￼';
    private readonly Dictionary<Control, IBrush?> restingFills = [];

    public DocumentText()
    {
        CopyingToClipboard += async (_, e) =>
        {
            e.Handled = true;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(Selection);
        };
    }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    /// <summary>True while focus loss clears and this block restores its selection; the change is not a real one.</summary>
    internal bool Restoring { get; private set; }

    /// <summary>The selected text, with each embedded control (a link, an image) standing in as its own words.</summary>
    internal string Selection
    {
        get
        {
            var selected = SelectedText;
            if (!selected.Contains(Placeholder)) return selected;
            var controls = Embedded().ToArray();
            var index = (Inlines?.Text ?? "")[..Math.Min(SelectionStart, SelectionEnd)].Count(character => character == Placeholder);
            return string.Concat(selected.Select(character => character != Placeholder ? character.ToString()
                : index < controls.Length ? Words(controls[index++]) : ""));
        }
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        var (start, end) = (SelectionStart, SelectionEnd);
        Restoring = true;
        try
        {
            base.OnLostFocus(e);
            SelectionStart = start; SelectionEnd = end;
        }
        finally { Restoring = false; }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectionStartProperty || change.Property == SelectionEndProperty) PaintEmbedded();
    }

    // Embedded controls are not glyphs, so the text block's highlight passes under them; they take the selection fill themselves.
    private void PaintEmbedded()
    {
        var (from, to) = (Math.Min(SelectionStart, SelectionEnd), Math.Max(SelectionStart, SelectionEnd));
        var position = 0;
        foreach (var inline in Flatten(Inlines))
        {
            if (inline is InlineUIContainer { Child: { } child })
            {
                var inside = position >= from && position < to;
                if (!restingFills.ContainsKey(child)) restingFills[child] = Fill(child);
                SetFill(child, inside ? SelectionBrush : restingFills[child]);
                position++;
            }
            else if (inline is Run run) position += run.Text?.Length ?? 0;
            else if (inline is LineBreak) position += Environment.NewLine.Length;
        }
    }

    private IEnumerable<Control> Embedded() =>
        Flatten(Inlines).OfType<InlineUIContainer>().Select(container => container.Child).OfType<Control>();

    private static IEnumerable<Inline> Flatten(InlineCollection? inlines)
    {
        foreach (var inline in inlines ?? [])
        {
            yield return inline;
            if (inline is Span span) foreach (var nested in Flatten(span.Inlines)) yield return nested;
        }
    }

    private static string Words(Control control) =>
        control is TextBlock text ? text.Text ?? "" : string.Concat(control.GetLogicalDescendants().OfType<TextBlock>().Select(text => text.Text));

    private static IBrush? Fill(Control control) => control switch
    {
        Avalonia.Controls.Primitives.TemplatedControl templated => templated.Background,
        Border border => border.Background,
        _ => null
    };

    private static void SetFill(Control control, IBrush? fill)
    {
        if (control is Avalonia.Controls.Primitives.TemplatedControl templated) templated.Background = fill;
        else if (control is Border border) border.Background = fill;
    }
}