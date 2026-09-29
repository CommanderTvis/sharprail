using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Media;

namespace SharpRail.UI.Rendering;

/// <summary>Renders Mermaid source to SVG through Merman's C ABI.</summary>
internal static class MermaidRenderer
{
    internal sealed record Result(string? Svg, string? Error);

    /// <summary>Captures the current appearance as Mermaid's base theme, as the reference derives it from its CSS tokens.</summary>
    internal static string Options() => new JsonObject
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

    /// <summary>Blocks on native parsing and layout; call it off the UI thread.</summary>
    internal static Result Render(string source, string options)
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
            if (result.Code == 0) return new Result(text, null);
            return new Result(null, JsonNode.Parse(text)?["message"]?.GetValue<string>() ?? text);
        }
        catch (JsonException) { return new Result(null, "The diagram renderer returned an unreadable error."); }
        finally { Native.Free(new MermanBuffer { Data = result.Data, Length = result.Length }); }
    }

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
