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

    /// <summary>Captures the current appearance as Mermaid's base theme, as the reference derives it from its CSS tokens.</summary>
    public static string Options() => new JsonObject
    {
        ["site_config"] = new JsonObject
        {
            ["theme"] = "base",
            ["themeVariables"] = new JsonObject
            {
                ["background"] = Hex(Ui.Surface),
                ["mainBkg"] = Hex(Ui.Elevated),
                ["primaryColor"] = Hex(Ui.Elevated),
                ["primaryTextColor"] = Hex(Ui.TextBrush),
                ["primaryBorderColor"] = Hex(Ui.BorderBrush),
                ["secondaryColor"] = Hex(Ui.Hover),
                ["tertiaryColor"] = Hex(Ui.Surface),
                ["lineColor"] = Hex(Ui.Muted),
                ["textColor"] = Hex(Ui.TextBrush),
                ["nodeBorder"] = Hex(Ui.BorderBrush),
                ["clusterBkg"] = Hex(Ui.Surface),
                ["clusterBorder"] = Hex(Ui.BorderBrush),
                ["titleColor"] = Hex(Ui.TextBrush)
            }
        },
        // Svg.Skia cannot draw <foreignObject> labels; the preview supplies the surface colour.
        ["svg"] = new JsonObject { ["pipeline"] = "resvg-safe", ["root_background_color"] = "transparent" }
    }.ToJsonString();

    /// <summary>Runs the platform renderer or native parsing and layout off the caller's UI thread.</summary>
    public static async Task<Result> RenderAsync(string source, string options, CancellationToken cancellationToken = default)
        => PlatformRender is { } platform
            ? WithoutTreeRoot(await platform(source, options))
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
            if (result.Code == 0) return WithoutTreeRoot(new Result(text, null));
            return new Result(null, JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? text);
        }
        catch (JsonException) { return new Result(null, "The diagram renderer returned an unreadable error."); }
        finally { Native.Free(new MermanBuffer { Data = result.Data, Length = result.Length }); }
    }

    /// <summary>Whether rendered SVG is a tree view, whose text reads as an indented outline.</summary>
    internal static bool IsTreeView(string svg) => svg.Contains("aria-roledescription=\"treeView\"", StringComparison.Ordinal);

    private static Result WithoutTreeRoot(Result result)
        => result.Svg is { } svg && IsTreeView(svg) ? result with { Svg = WithoutTreeRoot(svg) } : result;

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