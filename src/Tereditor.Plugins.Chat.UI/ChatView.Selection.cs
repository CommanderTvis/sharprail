using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Chat.UI;

internal sealed partial class ChatView
{
    private readonly WrapPanel selectionRow = new() { Name = "ChatSelectionRow", Orientation = Orientation.Horizontal, IsVisible = false };
    private string? removedMention;
    private IDisposable? selectionWatch;

    /// <summary>The longest quote a chip label shows; the tooltip holds all of it.</summary>
    internal const int QuoteLabelLength = 32;

    /// <summary>What the next prompt carries from the editor: the active selection (or file) while IDE context is on, unless it was already sent or the chip was removed.</summary>
    private ChatSelection? ContextSelection()
    {
        if (!ide || env.ActiveSelection() is not { } selection) return null;
        var mention = selection.Mention();
        return mention == lastMention || mention == removedMention ? null : selection;
    }

    private void WatchEditorSelection()
    {
        selectionWatch ??= env.WatchSelection(() => Dispatcher.UIThread.Post(RefreshSelectionChip));
        RefreshSelectionChip();
    }

    private void UnwatchEditorSelection()
    {
        selectionWatch?.Dispose();
        selectionWatch = null;
    }

    /// <summary>The chip for what the next prompt carries: "name, N lines" with #Lstart-end, the quote of a preview selection, or the file alone, lighter.</summary>
    internal void RefreshSelectionChip()
    {
        selectionRow.Children.Clear();
        var selection = ContextSelection();
        selectionRow.IsVisible = selection is not null;
        ShowContextRow();
        if (selection is not null)
        {
            var name = System.IO.Path.GetFileName(selection.Path);
            var lighter = selection.Quote is null && !selection.HasRange;
            string label, tip;
            string? range = null;
            if (selection.Quote is { } quote)
            {
                var flat = Regex.Replace(quote, @"\s+", " ").Trim();
                label = $"{name}: “{(flat.Length > QuoteLabelLength ? flat[..QuoteLabelLength] + "…" : flat)}”";
                tip = $"{selection.Path}, selected text:\n{quote}";
            }
            else if (selection.HasRange)
            {
                var lines = Math.Max(1, selection.EndLine - selection.StartLine + 1);
                label = $"{name}, {lines} {(lines == 1 ? "line" : "lines")}";
                range = $"#L{selection.StartLine}-{selection.EndLine}";
                tip = $"{selection.Path}, lines {selection.StartLine} to {selection.EndLine}";
            }
            else
            {
                label = name;
                tip = $"{selection.Path}, the open file";
            }
            // The icon, range and close mark keep their size; the label between them gives way, trimmed with an ellipsis (the tooltip has it whole).
            var content = new DockPanel { LastChildFill = true };
            var icon = Ui.Icon("code", Ui.Muted, 12);
            icon.Margin = new Thickness(0, 0, 4, 0);
            DockPanel.SetDock(icon, Dock.Left);
            content.Children.Add(icon);
            var remove = Ui.Text("×", Ui.Muted, 11);
            remove.Name = "ChatSelectionChipRemove";
            remove.Margin = new Thickness(4, 0, 0, 0);
            DockPanel.SetDock(remove, Dock.Right);
            content.Children.Add(remove);
            if (range is not null)
            {
                var rangeText = Ui.Text(range, Ui.Muted, 11);
                rangeText.Margin = new Thickness(4, 0, 0, 0);
                DockPanel.SetDock(rangeText, Dock.Right);
                content.Children.Add(rangeText);
            }
            var text = Ui.Text(label, lighter ? Ui.Hint : Ui.TextBrush, 11);
            text.Name = "ChatSelectionChipLabel";
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.TextWrapping = TextWrapping.NoWrap;
            content.Children.Add(text);
            var chip = TerminalFacts.ChipButton("ChatSelectionChip", content, tip + " \u2014 click to leave out", () =>
            {
                removedMention = selection.Mention();
                RefreshSelectionChip();
            });
            chip.Background = Ui.Hover;
            selectionRow.Children.Add(chip);
        }
        ApplyDensity();
    }
}