using Avalonia.Controls;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>Settings › Codex: the launch command, the default permissions mode, Start with IDE context and the MCP hand-off.</summary>
public static class CodexSettingsSection
{
    public static Control Create(IPluginUIContext context) => new CodexSettingsView(context);
}