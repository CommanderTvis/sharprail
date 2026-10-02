using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.ClaudeCode;

public static class ClaudeCodeManifest
{
    public static readonly string ConfigTool = PluginIdentity.ToolId(ClaudeCodeContract.Id, "config");

    public static readonly PluginManifest Manifest = new(ClaudeCodeContract.Id, "Claude Code", "asset:claude.svg", "0.2.0", PluginApi.Generation, 1)
    {
        Description = "Runs Claude Code in the terminal with an IDE bridge and live status.",
        EnabledByDefault = false,
        Assets = "assets",
        Contributes = new()
        {
            SideTools = [new("config", "Claude Code", "asset:claude.svg", PluginToolSide.Right)]
        }
    };
}