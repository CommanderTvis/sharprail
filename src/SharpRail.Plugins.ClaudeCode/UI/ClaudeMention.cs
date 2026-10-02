using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>
/// The context-menu action that sends a file, or the lines selected in it, to Claude Code's prompt as an
/// <c>@path#L</c> reference: the CLI types it at the cursor, and nothing reaches the model until the user submits.
/// </summary>
internal static class ClaudeMention
{
    private static string[] Terminals(IPluginUIContext context, string workspaceId) =>
        [.. (context.Host().Terminals.GetValueOrDefault(workspaceId) ?? []).Where(tab => tab.Agent?.Kind == "claude").Select(tab => tab.TabKey)];

    /// <summary>Offered while the workspace runs a Claude terminal; names what would be referenced.</summary>
    public static string? Label(IPluginUIContext context, FileActionTarget target)
    {
        if (Terminals(context, target.WorkspaceId).Length == 0) return null;
        var subject = target switch
        {
            { IsDirectory: true } => "folder",
            { StartLine: { } start, EndLine: { } end } => start == end ? $"line {start}" : $"lines {start}–{end}",
            _ => "file"
        };
        return $"Mention {subject} in Claude Code";
    }

    public static async Task SendAsync(IPluginUIContext context, FileActionTarget target)
    {
        try
        {
            var result = await context.RequestAsync(ClaudeCodeContract.AtMention,
                new(target.WorkspaceId, target.Path) { StartLine = target.StartLine, EndLine = target.EndLine });
            if (result.Sessions == 0)
            {
                context.Notify(PluginNotificationKind.Error, "No Claude Code session took the reference",
                    "Turn on IDE context for the session with /ide, or start Claude from a SharpRail terminal.");
                return;
            }
            // With one Claude terminal the reference landed there, so the user continues typing in it.
            if (Terminals(context, target.WorkspaceId) is [var only]) await context.OpenTerminalAsync(target.WorkspaceId, new() { TabKey = only });
        }
        catch (Exception error) when (error is PluginCallException or InvalidOperationException)
        {
            context.Notify(PluginNotificationKind.Error, "Could not reach Claude Code", error.Message);
        }
    }
}