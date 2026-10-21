using System.Text;

using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Scintilla;

namespace SharpRail.Plugins.UI.Kit.Editor;

public sealed partial class EditorFrame
{
    private string? syntaxPath;
    private IReadOnlyList<SyntaxLine>? syntaxLines;
    private IReadOnlyList<int>? syntaxLineStyles;
    private readonly SemaphoreSlim syntaxGate = new(1);
    private TextMateHighlighter? highlighter;
    private CancellationTokenSource? syntaxCancellation;
    private byte[]? syntaxStyles;
    private bool syntaxAttached, syntaxDisposed;
    private int syntaxVersion;
    private string? highlightedText;
    private readonly TaskCompletionSource firstHighlight = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes after the first syntax pass or its reported failure, before revealing the editor.</summary>
    public Task HighlightingReady => firstHighlight.Task;

    private bool deferSyntaxColors;

    /// <summary>Holds lexical style application until semantic tokens arrive, preserving existing native styles while results are pending.</summary>
    public bool DeferSyntaxColors
    {
        get => deferSyntaxColors;
        set { deferSyntaxColors = value; ApplySyntaxTheme(); }
    }

    private void RevealHighlighting()
    {
        this.FindControl<Avalonia.Controls.ContentControl>("EditorBody")!.Opacity = 1;
        firstHighlight.TrySetResult();
    }

    /// <summary>Enables background TextMate highlighting, optionally projecting diff rows and their whole-line styles.</summary>
    public void Highlight(string path, IReadOnlyList<SyntaxLine>? lines = null, IReadOnlyList<int>? lineStyles = null)
    {
        if (lineStyles is not null && lineStyles.Any(style => style < -1 || style >= Editor.LineStyles.Count))
            throw new ArgumentException("Line style index is outside the editor palette.", nameof(lineStyles));
        if (lineStyles is not null) ArgumentOutOfRangeException.ThrowIfGreaterThan(Editor.LineStyles.Count, 11);
        if (lines is not null && lines.Any(line => line.PrefixLength < 0))
            throw new ArgumentException("Source prefix length cannot be negative.", nameof(lines));
        if (!deferSyntaxColors && !firstHighlight.Task.IsCompleted) this.FindControl<Avalonia.Controls.ContentControl>("EditorBody")!.Opacity = 0;
        syntaxPath = path;
        syntaxLines = lines?.ToArray();
        syntaxLineStyles = lineStyles?.ToArray();
        syntaxStyles = null;
        semanticStyles = null;
        RequestSyntax();
    }

    private void SyntaxChanged(object? sender, EventArgs e) { syntaxStyles = null; RequestSyntax(); }
    private void GrammarChanged() { syntaxStyles = null; RequestSyntax(); }

    private async void RequestSyntax()
    {
        syntaxCancellation?.Cancel();
        if (!syntaxAttached || syntaxDisposed || syntaxPath is null) return;
        var cancellation = new CancellationTokenSource();
        syntaxCancellation = cancellation;
        var token = cancellation.Token;
        var version = ++syntaxVersion;
        var text = Editor.Text;
        if (syntaxStyles is not null && highlightedText == text)
        {
            ApplySyntaxTheme(); RevealHighlighting();
            syntaxCancellation = null; cancellation.Dispose();
            return;
        }
        if (text.Length > TextMateHighlighter.MaximumCharacters)
        {
            if (syntaxLineStyles is null) Editor.ClearTextStyles();
            else Editor.StyleLines(syntaxLineStyles);
            RevealHighlighting();
            syntaxCancellation = null;
            cancellation.Dispose();
            return;
        }
        var path = syntaxPath;
        var lines = syntaxLines;
        var lineStyles = syntaxLineStyles;
        try
        {
            var styles = await Task.Run(async () =>
            {
                await Task.Delay(60, token).ConfigureAwait(false);
                await syntaxGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    highlighter ??= new TextMateHighlighter();
                    var result = highlighter.Highlight(text, path, lines, token);
                    if (lineStyles is not null)
                    {
                        var row = 0;
                        var offset = 0;
                        var byteOffset = 0;
                        for (var index = 0; index < text.Length; index++)
                        {
                            if (text[index] is not ('\r' or '\n')) continue;
                            var end = index + (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n' ? 2 : 1);
                            var bytes = Encoding.UTF8.GetByteCount(text.AsSpan(offset, end - offset));
                            AddLineStyle(result, row++, byteOffset, bytes, lineStyles);
                            byteOffset += bytes;
                            offset = end;
                            index = end - 1;
                        }
                        AddLineStyle(result, row, byteOffset, Encoding.UTF8.GetByteCount(text.AsSpan(offset)), lineStyles);
                    }
                    return result;
                }
                finally { syntaxGate.Release(); }
            }, token).ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (token.IsCancellationRequested || !syntaxAttached || version != syntaxVersion) return;
                syntaxStyles = styles; highlightedText = text;
                ApplySyntaxTheme(); RevealHighlighting();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && syntaxAttached && version == syntaxVersion)
                {
                    Editor.ClearTextStyles(); RevealHighlighting();
                    Editor.ReportOperationFailure(error);
                }
            });
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (ReferenceEquals(syntaxCancellation, cancellation)) syntaxCancellation = null;
                cancellation.Dispose();
            });
        }
    }

    private static void AddLineStyle(byte[] styles, int row, int offset, int count, IReadOnlyList<int> lineStyles)
    {
        var kind = row < lineStyles.Count ? lineStyles[row] : -1;
        if (kind < 0) return;
        for (var i = offset; i < offset + count; i++) styles[i] += (byte)((kind + 1) * TextMateHighlighter.Roles.Length);
    }

    private void ApplySyntaxTheme()
    {
        // Stale results are dropped without touching the document: its current styles (inserted text inherits its
        // neighbour's) stay painted until the pending pass lands, so colours never blink off while typing.
        if (syntaxStyles is not null && highlightedText != Editor.Text) syntaxStyles = null;
        if (semanticStyles is not null && semanticText != Editor.Text) RebaseSemanticStyles(Editor.Text);
        if (syntaxDisposed || (deferSyntaxColors && semanticStyles is null) || (syntaxStyles is null && semanticStyles is null)) return;
        if (syntaxStyles is null && syntaxPath is not null && !deferSyntaxColors && Editor.Text.Length <= TextMateHighlighter.MaximumCharacters) return;
        var palette = new List<ScintillaLineStyle>();
        for (var kind = -1; kind < (syntaxLineStyles is null ? 0 : Editor.LineStyles.Count); kind++)
        {
            var line = kind < 0 ? null : Editor.LineStyles[kind];
            foreach (var role in TextMateHighlighter.Roles)
            {
                var foreground = role == "foreground" && line is not null ? line.Foreground
                    : Ui.Theme.Syntax.GetValueOrDefault(role, Ui.TextBrush.Color);
                palette.Add(new(foreground, line?.Background));
            }
        }
        if (semanticStyles is not null && syntaxLineStyles is null)
            palette.AddRange(palette.ToArray().Select(style => style with { Italic = true }));
        var styles = syntaxStyles ?? new byte[semanticStyles!.Length];
        if (semanticStyles is { } semantic && semantic.Length == styles.Length && syntaxLineStyles is null)
        {
            styles = (byte[])styles.Clone();
            for (var index = 0; index < styles.Length; index++)
                if (semantic[index] != byte.MaxValue) styles[index] = semantic[index];
        }
        Editor.StyleText(styles, palette);
    }

    private void AttachSyntax()
    {
        syntaxAttached = true;
        Editor.TextChanged += SyntaxChanged;
        CustomHighlighting.Changed += GrammarChanged;
        RequestSyntax();
    }

    private void DetachSyntax()
    {
        if (syntaxDisposed) firstHighlight.TrySetResult();
        syntaxAttached = false;
        syntaxVersion++;
        syntaxCancellation?.Cancel();
        Editor.TextChanged -= SyntaxChanged;
        CustomHighlighting.Changed -= GrammarChanged;
    }
}