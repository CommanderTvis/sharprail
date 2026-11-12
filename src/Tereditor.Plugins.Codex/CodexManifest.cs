using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Codex;

public static class CodexManifest
{
    public const string Id = "codex";
    public const string Icon = "asset:codex.svg";
    public static readonly string ConfigTool = PluginIdentity.ToolId(Id, "config");

    public static readonly PluginManifest Manifest = new(Id, "Codex", Icon, "0.1.0", PluginApi.Generation, CodexContract.WireVersion)
    {
        Category = PluginCategories.Agents,
        // Runs the codex CLI as a process on the host's operating system, which a browser tab does not have.
        UnavailableOn = [HostKind.PureWeb],
        Description = "Runs OpenAI Codex in the terminal with live status and tereditor's MCP tools.",
        EnabledByDefault = false,
        Headless = HeadlessSupport.No,
        Host = "Tereditor.Plugins.Codex.Host.dll",
        Ui = "Tereditor.Plugins.Codex.UI.dll",
        Assets = "assets",
        Contributes = new() { SideTools = [new("config", "Codex", Icon, PluginToolSide.Right)] }
    };
}