using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Codex;

public static class CodexManifest
{
    public const string Id = "codex";
    public const string Icon = "asset:codex.svg";
    public static readonly string ConfigTool = PluginIdentity.ToolId(Id, "config");

    public static readonly PluginManifest Manifest = new(Id, "Codex", Icon, "0.1.0", PluginApi.Generation, CodexContract.WireVersion)
    {
        Description = "Runs OpenAI Codex in the terminal with live status and SharpRail's MCP tools.",
        EnabledByDefault = false,
        Host = "SharpRail.Plugins.Codex.Host.dll",
        Ui = "SharpRail.Plugins.Codex.UI.dll",
        Assets = "assets",
        Contributes = new() { SideTools = [new("config", "Codex", Icon, PluginToolSide.Right)] }
    };
}