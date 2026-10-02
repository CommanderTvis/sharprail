using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.PdfPreview;

public static class PdfPreviewPlugin
{
    public const string Id = "pdf-preview";

    /// <summary>
    /// UI only: no host half, no methods, channels or settings. The file viewer is declared here so the open path
    /// knows the extension, and that its bytes are never read as text, before the UI half loads.
    /// </summary>
    public static readonly PluginManifest Manifest = new(Id, "PDF Preview", "file-pdf-2", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Opens PDF files in a viewer tab.",
        EnabledByDefault = true,
        Ui = "SharpRail.Plugins.PdfPreview.UI.dll",
        Contributes = new() { FileViewers = [new() { Extensions = ["pdf"], Read = PluginFileRead.None }] }
    };
}