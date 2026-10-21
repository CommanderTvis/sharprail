using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;

using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit.Visualization;

/// <summary>Renders Mermaid source to SVG through Merman's C ABI.</summary>
public static class MermaidRenderer
{
    public sealed record Result(string? Svg, string? Error);

    /// <summary>A platform renderer with the same Merman options contract, such as the browser WASM runtime.</summary>
    public static Func<string, string, Task<Result>>? PlatformRender { private get; set; }

    /// <summary>
    /// Captures the current appearance as Merman's host theme: its appearance plus the design tokens as semantic roles,
    /// which Merman expands into every diagram's theme variables. Mermaid's base theme derives sequence, state, gantt
    /// and the rest from a few primary colours in JavaScript; Merman does not, and its tree view reads only its own
    /// <c>treeView</c> variables (black by default), so only the roles reach every diagram type.
    /// </summary>
    public static string Options() => new JsonObject
    {
        ["host_theme"] = new JsonObject
        {
            ["appearance"] = Ui.Theme.IsLight ? "light" : "dark",
            ["roles"] = new JsonObject
            {
                ["canvas"] = Hex(Ui.Surface),
                ["surface"] = Hex(Ui.Elevated),
                ["surface_alt"] = Hex(Ui.Hover),
                ["surface_muted"] = Hex(Ui.Surface),
                ["text"] = Hex(Ui.TextBrush),
                ["subtle_text"] = Hex(Ui.Muted),
                ["border"] = Hex(Ui.BorderBrush),
                ["line"] = Hex(Ui.Muted),
                ["edge_label_background"] = Hex(Ui.Surface),
                ["cluster_background"] = Hex(Ui.Surface),
                ["cluster_border"] = Hex(Ui.BorderBrush),
                ["note_background"] = Hex(Ui.WarningWash),
                ["note_border"] = Hex(Ui.Warning),
                ["note_text"] = Hex(Ui.TextBrush),
                ["actor_background"] = Hex(Ui.Elevated),
                ["actor_border"] = Hex(Ui.BorderBrush),
                ["actor_text"] = Hex(Ui.TextBrush),
                ["error"] = Hex(Ui.Danger),
                ["warning"] = Hex(Ui.Warning),
                ["success"] = Hex(Ui.Success)
            },
            // Mermaid's stylesheets pin some text to #333 with !important; stripping it lets the roles win.
            ["output"] = new JsonObject { ["pipeline"] = "resvg-safe", ["css_override_policy"] = "strip-existing-important" }
        },
        // Svg.Skia cannot draw <foreignObject> labels; the preview supplies the surface colour.
        ["svg"] = new JsonObject { ["pipeline"] = "resvg-safe", ["root_background_color"] = "transparent" }
    }.ToJsonString();

    /// <summary>Runs the platform renderer or native parsing and layout off the caller's UI thread.</summary>
    public static async Task<Result> RenderAsync(string source, string options, CancellationToken cancellationToken = default)
        => PlatformRender is { } platform
            ? Finish(await platform(source, options), options)
            : await Task.Run(() => Render(source, options), cancellationToken);

    /// <summary>Blocks on native parsing and layout; call it off the UI thread.</summary>
    public static Result Render(string source, string options)
    {
        var sourceBytes = Encoding.UTF8.GetBytes(source);
        var optionsBytes = Encoding.UTF8.GetBytes(options);
        MermanResult result;
        try { result = Native.RenderSvg(sourceBytes, (nuint)sourceBytes.Length, optionsBytes, (nuint)optionsBytes.Length); }
        catch (DllNotFoundException) { return new Result(null, "Mermaid diagrams are not available on this platform."); }
        try
        {
            var bytes = new byte[checked((int)result.Length)];
            Marshal.Copy(result.Data, bytes, 0, bytes.Length);
            var text = Encoding.UTF8.GetString(bytes);
            if (result.Code == 0) return Finish(new Result(text, null), options);
            return new Result(null, JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? text);
        }
        catch (JsonException) { return new Result(null, "The diagram renderer returned an unreadable error."); }
        finally { Native.Free(new MermanBuffer { Data = result.Data, Length = result.Length }); }
    }

    /// <summary>Whether rendered SVG is a tree view, whose text reads as an indented outline.</summary>
    internal static bool IsTreeView(string svg) => svg.Contains("aria-roledescription=\"treeView\"", StringComparison.Ordinal);

    private static Result Finish(Result result, string options)
        => result.Svg is not { } svg ? result : result with { Svg = WithReadableInk(IsTreeView(svg) ? WithoutTreeRoot(svg) : svg, options) };

    // Mermaid's own default ink: near-black text and strokes that Merman 0.7 still writes as literals in some diagrams.
    private static readonly System.Text.RegularExpressions.Regex InkAttribute =
        new(@"(?<![-\w])(fill|stroke|color)=""(?:#000|#000000|#333|#333333|black)""", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    private static readonly System.Text.RegularExpressions.Regex InkDeclaration =
        new(@"(?<![-\w])(fill|stroke|color)\s*:\s*(?:#000|#000000|#333|#333333|black|rgb\(\s*0\s*,\s*0\s*,\s*0\s*\))(?=\s*[;}!""])", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Merman 0.7 ignores the host theme in places and keeps Mermaid's default near-black ink: gantt axis ticks,
    /// mindmap labels, packet and sequence markers. On a dark theme that ink is unreadable, so it becomes the theme's
    /// text colour; light themes keep it, as it reads on their surfaces.
    /// </summary>
    internal static string WithReadableInk(string svg, string options)
    {
        if (JsonNode.Parse(options)?["host_theme"] is not { } host
            || (string?)host["appearance"] != "dark"
            || (string?)host["roles"]?["text"] is not { } ink) return svg;
        svg = InkAttribute.Replace(svg, match => $"{match.Groups[1].Value}=\"{ink}\"");
        return InkDeclaration.Replace(svg, match => $"{match.Groups[1].Value}:{ink}");
    }

    /// <summary>
    /// Mermaid's tree view (and Merman, which matches it) hangs every top-level node under an implicit root it draws as
    /// a "/" row. The source's first node is the root a reader means, so the implicit row, its connectors and its indent
    /// come out: the drawing shifts up one row and left one level, and the view box shrinks to match.
    /// </summary>
    internal static string WithoutTreeRoot(string svg)
    {
        XDocument document;
        try { document = XDocument.Parse(svg, LoadOptions.PreserveWhitespace); }
        catch (XmlException) { return svg; }
        var root = document.Root!;
        var tree = root.Descendants().FirstOrDefault(element => element.Name.LocalName == "g" && (string?)element.Attribute("class") == "tree-view");
        var labels = tree?.Elements().Where(element => element.Name.LocalName == "text").ToList() ?? [];
        if (tree is null || labels.Count < 2 || labels[0].Value != "/") return svg;
        if (Number(labels[0], "x") is not { } rootX || Number(labels[0], "y") is not { } rootY || Number(labels[1], "x") is not { } childX) return svg;
        // The implicit root's row starts at the top, so its label sits half a row down; children indent one level.
        var row = rootY * 2;
        var indent = childX - rootX;
        labels[0].Remove();
        foreach (var line in tree.Elements().Where(element => element.Name.LocalName == "line").ToList())
        {
            var (x1, y1, x2, y2) = (Number(line, "x1"), Number(line, "y1"), Number(line, "x2"), Number(line, "y2"));
            // Its own connector stub on its row, and the spine that runs from it down to its last child.
            if (y1 == rootY && y2 == rootY || x1 == rootX && x2 == rootX && y1 == row) line.Remove();
        }
        var existing = (string?)tree.Attribute("transform");
        tree.SetAttributeValue("transform", $"translate({Format(-indent)},{Format(-row)})" + (existing is null ? "" : " " + existing));
        if (((string?)root.Attribute("viewBox"))?.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries) is [var minX, var minY, var width, var height]
            && double.TryParse(width, System.Globalization.CultureInfo.InvariantCulture, out var boxWidth)
            && double.TryParse(height, System.Globalization.CultureInfo.InvariantCulture, out var boxHeight))
        {
            var (newWidth, newHeight) = (Math.Max(1, boxWidth - indent), Math.Max(1, boxHeight - row));
            root.SetAttributeValue("viewBox", $"{minX} {minY} {Format(newWidth)} {Format(newHeight)}");
            if ((string?)root.Attribute("style") is { } style)
                root.SetAttributeValue("style", System.Text.RegularExpressions.Regex.Replace(style, @"max-width:\s*[0-9.]+px", $"max-width:{Format(newWidth)}px"));
            if (Number(root, "width") is not null) root.SetAttributeValue("width", Format(newWidth));
            if (Number(root, "height") is not null) root.SetAttributeValue("height", Format(newHeight));
        }
        return root.ToString(SaveOptions.DisableFormatting);
    }

    private static double? Number(XElement element, string name)
        => double.TryParse((string?)element.Attribute(name), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;

    private static string Format(double value) => value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    private static string Hex(ISolidColorBrush brush) => $"#{brush.Color.R:x2}{brush.Color.G:x2}{brush.Color.B:x2}";

    [StructLayout(LayoutKind.Sequential)]
    private struct MermanBuffer
    {
        public nint Data;
        public nuint Length;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MermanResult
    {
        public int Code;
        public nint Data;
        public nuint Length;
    }

    private static class Native
    {
        private const string Library = "SharpRailMermaid";
        [DllImport(Library, EntryPoint = "merman_render_svg")]
        internal static extern MermanResult RenderSvg(byte[] source, nuint sourceLength, byte[] options, nuint optionsLength);
        [DllImport(Library, EntryPoint = "merman_buffer_free")]
        internal static extern void Free(MermanBuffer buffer);
    }
}