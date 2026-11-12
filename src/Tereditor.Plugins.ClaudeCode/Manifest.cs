using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.ClaudeCode;

public static class ClaudeCodeManifest
{
    public static readonly string ConfigTool = PluginIdentity.ToolId(ClaudeCodeContract.Id, "config");

    public static readonly PluginManifest Manifest = new(ClaudeCodeContract.Id, "Claude Code", "asset:claude.svg", "0.2.0", PluginApi.Generation, 3)
    {
        Category = PluginCategories.Agents,
        // Runs the claude CLI as a process on the host's operating system, which a browser tab does not have.
        UnavailableOn = [HostKind.PureWeb],
        Description = "Runs Claude Code in the terminal with an IDE bridge and live status.",
        EnabledByDefault = false,
        Headless = HeadlessSupport.No,
        Assets = "assets",
        Contributes = new()
        {
            SideTools = [new("config", "Claude Code", "asset:claude.svg", PluginToolSide.Right)]
        }
    };
}