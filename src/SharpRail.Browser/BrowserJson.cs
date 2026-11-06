using System.Text.Json;
using System.Text.Json.Serialization;

using SharpRail.UI.Resources.Pdf;

namespace SharpRail.UI.Browser;

/// <summary>
/// Source-generated JSON metadata, with the serializer's default options, for what the tab hands to and takes from its
/// scripts and keeps in its own storage: the saved endpoint, PDF pages and rasters, Git answers and Tailscale status.
/// </summary>
[JsonSerializable(typeof(HostEndpoint))]
[JsonSerializable(typeof(PdfPlatformPage[]))]
[JsonSerializable(typeof(PdfPlatformRaster[]))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(TabGit.Answer))]
[JsonSerializable(typeof(BrowserTailscaleStatus))]
internal sealed partial class BrowserJson : JsonSerializerContext
{
    /// <summary>The metadata reading names in any case, as Tailscale's status arrives.</summary>
    internal static BrowserJson CaseInsensitive { get; } = new(new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

    /// <summary>The metadata with the web defaults (camel-case names read in any case), as Git answers arrive.</summary>
    internal static BrowserJson Web { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web));
}