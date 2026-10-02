using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.FileIcons;

public static class FileIconsManifest
{
    public const string Id = "file-icons";

    /// <summary>A UI-only plugin: the host lists it from this manifest and serves its assets, and runs nothing.</summary>
    public static readonly PluginManifest Manifest = new(Id, "File Icons", "file-text", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "File-type glyphs from material-icon-theme for the file tree and tabs.",
        EnabledByDefault = true,
        Ui = "SharpRail.Plugins.FileIcons.UI.dll",
        Assets = "assets"
    };
}