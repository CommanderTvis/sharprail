using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Editor;

// Quick fixes: the language server's code actions for the caret, in a list at the caret (Alt+Enter, Option+Enter on macOS).
internal sealed partial class CodeDocumentView
{
    private sealed record QuickFixRow(LanguageItem Item)
    {
        public string Title => Item.Text;
        public FontWeight Weight => Item.Preview == "preferred" ? FontWeight.SemiBold : FontWeight.Normal;
    }

    private CancellationTokenSource? quickFixRequest;
    private long quickFixVersion;

    private void WireQuickFixes()
    {
        var list = this.FindControl<ListBox>("QuickFixItems")!;
        Editor.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key != Key.Enter || e.KeyModifiers != KeyModifiers.Alt) return;
            e.Handled = true;
            ShowQuickFixes();
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await ApplyQuickFixAsync(); }
            else if (e.Key == Key.Escape) { e.Handled = true; CloseQuickFixes(); }
        };
        list.DoubleTapped += async (_, _) => await ApplyQuickFixAsync();
        this.FindControl<Popup>("QuickFixPopup")!.Closed += (_, _) => { if (list.IsVisible) Editor.Focus(); };
    }

    /// <summary>Asks the language server for the code actions at the caret and lists them; nothing happens without a server.</summary>
    internal void ShowQuickFixes() => _ = ShowQuickFixesAsync();

    private void CloseQuickFixes()
    {
        quickFixRequest?.Cancel();
        this.FindControl<Popup>("QuickFixPopup")!.IsOpen = false;
        Editor.Focus();
    }

    private async Task ShowQuickFixesAsync()
    {
        if (disposed || LanguageSourceServer.Length > 0 || Editor.IsReadOnly || Editor.Text.Length > 1_048_576) return;
        quickFixRequest?.Cancel();
        var cancellation = quickFixRequest = new CancellationTokenSource();
        var token = cancellation.Token;
        var version = quickFixVersion = languageVersion;
        var selection = Editor.Selection;
        try
        {
            var result = await host.LanguageAsync(new(workspace, path, "codeActions", selection.StartLine - 1, selection.StartColumn - 1, Editor.Text, languageDocument, version, Dirty: Editor.IsModified), token);
            if (disposed || token.IsCancellationRequested || version != languageVersion) return;
            if (result.State == "ready" && result.Actions.Count > 0) ShowQuickFixPopup(result.Actions.Select(item => new QuickFixRow(item)).ToArray(), "");
            else ShowQuickFixPopup([], result.State == "ready" ? "No quick fixes" : result.Detail.Length > 0 ? result.Detail : "No quick fixes");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!disposed && !token.IsCancellationRequested) ShowQuickFixPopup([], error.Message); }
    }

    // With rows: a focused list. Without: only the message, which closes by itself and never takes focus from the editor.
    private void ShowQuickFixPopup(QuickFixRow[] rows, string message)
    {
        var popup = this.FindControl<Popup>("QuickFixPopup")!;
        var list = this.FindControl<ListBox>("QuickFixItems")!;
        list.ItemsSource = rows; list.IsVisible = rows.Length > 0;
        this.FindControl<TextBlock>("QuickFixKeys")!.IsVisible = rows.Length > 0;
        var status = this.FindControl<TextBlock>("QuickFixStatus")!;
        status.Text = message; status.IsVisible = message.Length > 0;
        popup.PlacementTarget = Editor;
        popup.PlacementRect = Editor.CaretBounds;
        popup.IsOpen = true;
        if (rows.Length > 0) { list.SelectedIndex = 0; list.Focus(); return; }
        var shown = quickFixRequest;
        DispatcherTimer.RunOnce(() => { if (ReferenceEquals(shown, quickFixRequest) && !list.IsVisible) popup.IsOpen = false; }, TimeSpan.FromSeconds(2));
    }

    private async Task ApplyQuickFixAsync()
    {
        if (this.FindControl<ListBox>("QuickFixItems")!.SelectedItem is not QuickFixRow row) return;
        var popup = this.FindControl<Popup>("QuickFixPopup")!;
        popup.IsOpen = false;
        Editor.Focus();
        var cancellation = quickFixRequest = new CancellationTokenSource();
        var token = cancellation.Token;
        var version = quickFixVersion;
        var text = Editor.Text;
        try
        {
            if (version != languageVersion) return;
            var result = await host.LanguageAsync(new(workspace, path, "applyCodeAction", row.Item.Line, row.Item.Column, text, languageDocument, version, row.Item.Source, Dirty: Editor.IsModified), token);
            // A newer edit makes the answer's positions meaningless: drop it.
            if (disposed || token.IsCancellationRequested || version != languageVersion || Editor.Text != text) return;
            if (result.State != "ready") { ShowQuickFixPopup([], result.Detail); return; }
            await ApplyEditsAsync(result.Edits, text, version, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!disposed && !token.IsCancellationRequested) ShowQuickFixPopup([], error.Message); }
    }

    private static bool SameFile(string left, string right) => left.Replace('\\', '/') == right.Replace('\\', '/');

    // Other files first: they can fail on a conflicting change, while the open buffer cannot, so a refusal leaves this editor untouched.
    private async Task ApplyEditsAsync(IReadOnlyList<LanguageItem> edits, string text, long version, CancellationToken token)
    {
        foreach (var file in edits.Where(edit => !SameFile(edit.Path, path)).GroupBy(edit => edit.Path))
        {
            var current = await host.ReadFileAsync(file.Key, token);
            var changed = await Task.Run(() => ToRanges(current.Text, file) is { } ranges ? Splice(current.Text, ranges) : null, token);
            if (changed is null) { ShowQuickFixPopup([], "The quick fix no longer fits " + file.Key + "."); return; }
            await host.SaveFileAsync(new(workspace, file.Key, current.Text, changed), token);
        }
        var own = edits.Where(edit => SameFile(edit.Path, path)).ToArray();
        if (own.Length == 0) return;
        var offsets = await Task.Run(() => ToRanges(text, own), token);
        if (disposed || token.IsCancellationRequested || version != languageVersion || Editor.Text != text) return;
        if (offsets is null) { ShowQuickFixPopup([], "The quick fix no longer fits this document."); return; }
        Editor.ReplaceRanges(offsets);
    }

    private static string Splice(string text, IReadOnlyList<(int Start, int Length, string Text)> ranges)
    {
        foreach (var range in ranges.OrderByDescending(range => range.Start)) text = text[..range.Start] + range.Text + text[(range.Start + range.Length)..];
        return text;
    }

    /// <summary>Turns zero-based UTF-16 line and column edits of <paramref name="text"/> into offset ranges; null when one lies outside the text or two overlap.</summary>
    internal static (int Start, int Length, string Text)[]? ToRanges(string text, IEnumerable<LanguageItem> edits)
    {
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++) if (text[index] == '\n') starts.Add(index + 1);
        int Offset(int line, int column)
        {
            if (line < 0 || column < 0 || line >= starts.Count) return -1;
            var end = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
            return column <= end - starts[line] ? starts[line] + column : -1;
        }
        var ranges = new List<(int Start, int Length, string Text)>();
        foreach (var edit in edits)
        {
            var (start, end) = (Offset(edit.Line, edit.Column), Offset(edit.EndLine, edit.EndColumn));
            if (start < 0 || end < start) return null;
            ranges.Add((start, end - start, edit.Text));
        }
        ranges.Sort((left, right) => left.Start != right.Start ? left.Start.CompareTo(right.Start) : left.Length.CompareTo(right.Length));
        for (var index = 1; index < ranges.Count; index++)
            if (ranges[index].Start < ranges[index - 1].Start + ranges[index - 1].Length) return null;
        return ranges.ToArray();
    }
}