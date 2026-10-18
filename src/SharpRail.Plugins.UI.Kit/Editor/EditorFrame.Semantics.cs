using System.Text;

using Avalonia.Threading;

namespace SharpRail.Plugins.UI.Kit.Editor;

/// <summary>A single-line semantic token in zero-based UTF-16 coordinates.</summary>
public sealed record SemanticToken(int Line, int Column, int Length, string Type)
{
    /// <summary>Uses an italic face without changing the token's semantic color role.</summary>
    public bool Italic { get; init; }
}

public sealed partial class EditorFrame
{
    private byte[]? semanticStyles;
    private string? semanticText;
    private SemanticToken[] lastSemanticTokens = [];

    /// <summary>Replaces syntax colors with semantic roles when available, rejecting results after edits or disposal.</summary>
    public async Task ApplySemanticTokensAsync(string text, IReadOnlyList<SemanticToken> tokens)
    {
        if (semanticStyles is not null && semanticText == text && tokens.SequenceEqual(lastSemanticTokens)) return;
        var snapshot = tokens.ToArray();
        var version = syntaxVersion;
        var styles = await Task.Run(() => SemanticStyles(text, snapshot));
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (syntaxDisposed || !syntaxAttached || version != syntaxVersion || Editor.Text != text) return;
            semanticStyles = styles; semanticText = text; lastSemanticTokens = snapshot;
            ApplySyntaxTheme();
        });
    }

    internal static byte[] SemanticStyles(string text, IReadOnlyList<SemanticToken> tokens)
    {
        var styles = new byte[Encoding.UTF8.GetByteCount(text)]; Array.Fill(styles, byte.MaxValue);
        var starts = new List<int> { 0 };
        for (var index = 0; index < text.Length; index++) if (text[index] == '\n') starts.Add(index + 1);
        var bytes = new int[text.Length + 1];
        for (var index = 0; index < text.Length;)
        {
            var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
            if (length == 2) bytes[index + 1] = bytes[index];
            bytes[index + length] = bytes[index] + Encoding.UTF8.GetByteCount(text.AsSpan(index, length)); index += length;
        }
        foreach (var token in tokens)
        {
            if (token.Line < 0 || token.Line >= starts.Count || token.Column < 0 || token.Length <= 0) continue;
            var lineEnd = token.Line + 1 < starts.Count ? starts[token.Line + 1] - 1 : text.Length;
            var start = starts[token.Line] + token.Column;
            if (start < starts[token.Line] || start > lineEnd || token.Length > lineEnd - start) continue;
            var role = token.Type switch
            {
                "namespace" or "type" or "class" or "enum" or "interface" or "struct" or "typeParameter" or "delegate" or "record" or "recordStruct" => "type",
                "parameter" or "variable" => "variable",
                "function" or "method" or "macro" or "extensionMethod" or "extensionFunction" => "function",
                "enumMember" => "constant",
                "event" or "property" or "field" => "property",
                "modifier" => "keyword",
                "decorator" => "annotation",
                _ => token.Type
            };
            var style = Array.IndexOf(TextMateHighlighter.Roles, role);
            if (style < 0) continue;
            Array.Fill(styles, (byte)(style + (token.Italic ? TextMateHighlighter.Roles.Length : 0)), bytes[start], bytes[start + token.Length] - bytes[start]);
        }
        return styles;
    }
}