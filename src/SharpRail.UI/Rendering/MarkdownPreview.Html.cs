using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Layout;
using Avalonia.Media;

using Markdig.Syntax;

namespace SharpRail.UI.Rendering;

// Raw HTML in a Markdown document is read, never run: a short list of tags maps to native controls, text inside any
// other tag is shown as text, and script, style and every attribute outside those read here are dropped.
public sealed partial class MarkdownPreview
{
    private sealed record HtmlToken(string? Tag, bool Closing, Dictionary<string, string> Attributes, string Text);

    private static readonly HashSet<string> Dropped = new(StringComparer.OrdinalIgnoreCase) { "script", "style", "iframe", "object", "embed", "template", "noscript", "svg", "math" };
    private static readonly HashSet<string> Breaking = new(StringComparer.OrdinalIgnoreCase)
        { "p", "div", "center", "br", "hr", "li", "ul", "ol", "tr", "table", "h1", "h2", "h3", "h4", "h5", "h6", "blockquote", "pre", "summary", "details", "figure", "figcaption", "picture" };

    [GeneratedRegex(@"<!--.*?-->|<(/?)([a-zA-Z][a-zA-Z0-9]*)((?:\s+[^\s""'<>/=]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s""'=<>`]+))?)*)\s*/?>", RegexOptions.Singleline)]
    private static partial Regex HtmlTagPattern();

    [GeneratedRegex(@"([^\s""'<>/=]+)(?:\s*=\s*(?:""([^""]*)""|'([^']*)'|([^\s""'=<>`]+)))?")]
    private static partial Regex HtmlAttributePattern();

    private static List<HtmlToken> HtmlTokens(string html)
    {
        var tokens = new List<HtmlToken>();
        var position = 0;
        void Text(int end)
        {
            if (end > position) tokens.Add(new(null, false, [], WebUtility.HtmlDecode(html[position..end])));
        }
        foreach (Match match in HtmlTagPattern().Matches(html))
        {
            Text(match.Index);
            position = match.Index + match.Length;
            if (!match.Groups[2].Success) continue;
            var attributes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match attribute in HtmlAttributePattern().Matches(match.Groups[3].Value))
                attributes[attribute.Groups[1].Value] = WebUtility.HtmlDecode(attribute.Groups[2].Success ? attribute.Groups[2].Value
                    : attribute.Groups[3].Success ? attribute.Groups[3].Value : attribute.Groups[4].Value);
            tokens.Add(new(match.Groups[2].Value.ToLowerInvariant(), match.Groups[1].Length > 0, attributes, ""));
        }
        Text(html.Length);
        return tokens;
    }

    private static HorizontalAlignment? Alignment(HtmlToken token) => token.Tag == "center" ? HorizontalAlignment.Center
        : token.Attributes.GetValueOrDefault("align")?.ToLowerInvariant() switch
        {
            "center" or "middle" => HorizontalAlignment.Center,
            "right" => HorizontalAlignment.Right,
            "left" => HorizontalAlignment.Left,
            _ => null
        };

    private static double? Pixels(HtmlToken token, string name) =>
        double.TryParse(token.Attributes.GetValueOrDefault(name)?.Replace("px", "", StringComparison.OrdinalIgnoreCase), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && value is > 0 and < 10000 ? value : null;

    // A picture holder for <img>: http(s) and worktree-relative sources only, sized and placed by its attributes.
    private Border? HtmlImage(HtmlToken token, HorizontalAlignment alignment)
    {
        var source = token.Attributes.GetValueOrDefault("src")?.Trim() ?? "";
        if (source.Length == 0 || Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme is not ("http" or "https")) return null;
        var holder = new Border
        {
            Child = Ui.Text(token.Attributes.GetValueOrDefault("alt") ?? ""),
            Margin = new Thickness(0, 8),
            MaxWidth = Pixels(token, "width") ?? 800,
            HorizontalAlignment = Alignment(token) ?? alignment
        };
        if (Pixels(token, "height") is { } height) holder.MaxHeight = height;
        holder.Classes.Add("html-image");
        _ = LoadImageAsync(holder, source);
        return holder;
    }

    /// <summary>The controls for a run of raw HTML tokens; alignment comes from the enclosing block tags.</summary>
    private List<Control> HtmlControls(IReadOnlyList<HtmlToken> tokens)
    {
        var controls = new List<Control>();
        var alignment = new Stack<HorizontalAlignment>([HorizontalAlignment.Left]);
        SelectableTextBlock? paragraph = null;
        int bold = 0, italic = 0, code = 0, heading = 0;
        string? link = null;

        void Flush()
        {
            if (paragraph?.Inlines is { } inlines && inlines.Any(inline => inline is InlineUIContainer || inline is Run { Text: { } text } && text.Trim().Length > 0)) controls.Add(paragraph);
            paragraph = null;
        }

        InlineCollection Inlines()
        {
            paragraph ??= new SelectableTextBlock
            {
                Foreground = Ui.TextBrush,
                FontSize = heading > 0 ? heading switch { 1 => 24, 2 => 20, 3 => 18, _ => 16 } : preferences.FontSize,
                FontWeight = heading > 0 ? FontWeight.SemiBold : Ui.InterfaceWeight,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = preferences.FontSize * 1.6,
                Margin = new Thickness(0, 12),
                TextAlignment = alignment.Peek() switch { HorizontalAlignment.Center => TextAlignment.Center, HorizontalAlignment.Right => TextAlignment.Right, _ => TextAlignment.Left }
            };
            return paragraph.Inlines!;
        }

        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Tag is null)
            {
                var text = Regex.Replace(token.Text, @"\s+", " ");
                if (text.Trim().Length == 0 && paragraph is null) continue;
                if (link is { } url)
                {
                    var label = Ui.Text(text, Ui.Accent, preferences.FontSize);
                    var button = new MarkdownLink(label) { Content = label, MinHeight = 0, MinWidth = 0, Padding = new Thickness(0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
                    ToolTip.SetTip(button, url);
                    button.Click += (_, _) => Follow(url);
                    Inlines().Add(new InlineUIContainer(button));
                }
                else Inlines().Add(new Run(text)
                {
                    FontWeight = bold > 0 ? FontWeight.Medium : heading > 0 ? FontWeight.SemiBold : Ui.InterfaceWeight,
                    FontStyle = italic > 0 ? FontStyle.Italic : FontStyle.Normal,
                    FontFamily = code > 0 ? Ui.CodeFont : Ui.InterfaceFont
                });
                continue;
            }
            if (Dropped.Contains(token.Tag))
            {
                // Everything up to the matching close is dropped with the tag.
                if (!token.Closing) while (index + 1 < tokens.Count && !(tokens[index + 1].Closing && tokens[index + 1].Tag == token.Tag)) index++;
                continue;
            }
            if (Breaking.Contains(token.Tag)) Flush();
            switch (token.Tag)
            {
                case "img" when !token.Closing:
                    if (HtmlImage(token, alignment.Peek()) is not { } image) break;
                    // A picture alone on its line is its own block, so it can be centred or sent to a side.
                    if (paragraph is null) controls.Add(image);
                    else Inlines().Add(new InlineUIContainer(image));
                    break;
                case "br": Inlines().Add(new Run("\n")); break;
                case "hr" when !token.Closing: controls.Add(new Border { Height = 1, Background = Ui.BorderBrush, Margin = new Thickness(0, 24) }); break;
                case "p" or "div" or "center" or "figure":
                    if (!token.Closing) alignment.Push(Alignment(token) ?? alignment.Peek());
                    else if (alignment.Count > 1) alignment.Pop();
                    break;
                case "b" or "strong": bold = Math.Max(0, bold + (token.Closing ? -1 : 1)); break;
                case "i" or "em": italic = Math.Max(0, italic + (token.Closing ? -1 : 1)); break;
                case "code" or "kbd" or "samp" or "tt": code = Math.Max(0, code + (token.Closing ? -1 : 1)); break;
                case "h1" or "h2" or "h3" or "h4" or "h5" or "h6": heading = token.Closing ? 0 : token.Tag[1] - '0'; break;
                case "a":
                    link = token.Closing ? null : token.Attributes.GetValueOrDefault("href") is { Length: > 0 } href && !href.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ? href : null;
                    break;
            }
        }
        Flush();
        return controls;
    }

    private Control Html(string html)
    {
        var panel = new StackPanel();
        panel.Classes.Add("html-block");
        panel.Children.AddRange(HtmlControls(HtmlTokens(html)));
        CollapseMargins(panel);
        return panel;
    }

    /// <summary>
    /// Renders sibling blocks. A &lt;details&gt; that Markdown splits across blocks (its body is ordinary Markdown
    /// between two raw HTML blocks) becomes one disclosure holding everything up to its close.
    /// </summary>
    private IEnumerable<Control> RenderBlocks(IReadOnlyList<Block> blocks)
    {
        for (var index = 0; index < blocks.Count; index++)
        {
            if (blocks[index] is not HtmlBlock html || HtmlTokens(html.Lines.ToString()) is not [{ Tag: "details", Closing: false } open, ..] tokens)
            { yield return Render(blocks[index]); continue; }
            var depth = Depth(tokens);
            var inner = new List<Block>();
            var tail = new List<HtmlToken>();
            while (depth > 0 && index + 1 < blocks.Count)
            {
                var next = blocks[++index];
                if (next is HtmlBlock other && HtmlTokens(other.Lines.ToString()) is var more && more.Any(token => token.Tag == "details"))
                {
                    depth += Depth(more);
                    if (depth <= 0) { tail.AddRange(more.Skip(more.FindLastIndex(token => token is { Tag: "details", Closing: true }) + 1)); break; }
                }
                inner.Add(next);
            }
            yield return Details(open, tokens, inner);
            foreach (var control in HtmlControls(tail)) yield return control;
        }

        static int Depth(List<HtmlToken> tokens) => tokens.Count(token => token is { Tag: "details", Closing: false }) - tokens.Count(token => token is { Tag: "details", Closing: true });
    }

    private Control Details(HtmlToken open, List<HtmlToken> tokens, List<Block> inner)
    {
        var summaryStart = tokens.FindIndex(token => token is { Tag: "summary", Closing: false });
        var summaryEnd = tokens.FindIndex(token => token is { Tag: "summary", Closing: true });
        var title = summaryStart >= 0 && summaryEnd > summaryStart
            ? string.Concat(tokens.Skip(summaryStart + 1).Take(summaryEnd - summaryStart - 1).Select(token => token.Text)).Trim() : "Details";
        var close = tokens.FindLastIndex(token => token is { Tag: "details", Closing: true });
        var rest = tokens.Skip(Math.Max(summaryEnd, 0) + 1).Take((close < 0 ? tokens.Count : close) - Math.Max(summaryEnd, 0) - 1).ToList();
        var body = new StackPanel { Margin = new Thickness(preferences.FontSize * 1.2, 4, 0, 0), IsVisible = open.Attributes.ContainsKey("open") };
        body.Children.AddRange(HtmlControls(rest));
        body.Children.AddRange(RenderBlocks(inner));
        CollapseMargins(body);
        var label = Ui.Text((body.IsVisible ? "▾ " : "▸ ") + title, Ui.TextBrush, preferences.FontSize);
        var toggle = new Button
        {
            Content = label,
            Padding = new Thickness(0),
            MinHeight = 0,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        toggle.Classes.Add("html-summary");
        toggle.Click += (_, _) => { body.IsVisible = !body.IsVisible; label.Text = (body.IsVisible ? "▾ " : "▸ ") + title; };
        var panel = new StackPanel { Margin = new Thickness(0, 12) };
        panel.Classes.Add("html-details");
        panel.Children.Add(toggle); panel.Children.Add(body);
        return panel;
    }
}