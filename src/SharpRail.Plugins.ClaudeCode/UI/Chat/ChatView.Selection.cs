using System.Text.RegularExpressions;

using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;

using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI.Chat;

internal sealed partial class ChatView
{
    private readonly StackPanel selectionRow = new() { Name = "ChatSelectionRow", Orientation = Orientation.Horizontal, IsVisible = false };
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
            var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            content.Children.Add(Ui.Icon("code", Ui.Muted, 12));
            var text = Ui.Text(label, lighter ? Ui.Hint : Ui.TextBrush, 11);
            text.Name = "ChatSelectionChipLabel";
            content.Children.Add(text);
            if (range is not null) content.Children.Add(Ui.Text(range, Ui.Muted, 11));
            var remove = Ui.Text("×", Ui.Muted, 11);
            remove.Name = "ChatSelectionChipRemove";
            content.Children.Add(remove);
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