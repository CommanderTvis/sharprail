using Avalonia.Controls;
using Avalonia.Controls.Primitives;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Chat.UI;

namespace SharpRail.Plugins.ClaudeCode.UI.Chat;

/// <summary>Starts Claude Code as a chat tab when the setting says so, from the same presets the terminal launcher offers.</summary>
internal static class ChatLauncher
{
    /// <summary>Presets a chat can honour; anything else (teleport, free-form flags) still starts the terminal.</summary>
    public static bool Handles(string args)
    {
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length == 0 || words is ["--continue"] or ["--resume"] or ["--resume", "--fork-session"] or ["--model", _];
    }

    public static ChatOpenParams Params(string workspaceId, string chatId, ClaudeCodeSettings settings, string args, string? permissionMode)
    {
        var words = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var model = words is ["--model", var named] ? named : settings.Model;
        return new ChatOpenParams(workspaceId, chatId)
        {
            Model = model.Length > 0 ? model : null,
            Effort = settings.Effort.Length > 0 ? settings.Effort : null,
            PermissionMode = permissionMode is { Length: > 0 } ? permissionMode : null
        };
    }

    /// <summary>The launcher's <c>Open</c>: Create-workspace and Project Home starts, in Chat mode, open a chat tab with the opening prompt.</summary>
    public static ValueTask<bool> OpenFromLauncher(IPluginUIContext context, string workspaceId, LauncherCommandOptions options)
    {
        var settings = context.Settings<ClaudeCodeSettings>();
        if (settings.Mode != ClaudeCodeMode.Chat) return ValueTask.FromResult(false);
        var open = Params(workspaceId, ChatDocuments.NewId(), settings, options.Model is { } model ? "--model " + model : "", settings.PermissionMode) with { ResumeSessionId = options.ResumeSessionId };
        ChatDocuments.Open(context, ClaudeChat.Agent(context), workspaceId, open.ChatId, "Claude Code", open, options.InitialPrompt, options.Images);
        return ValueTask.FromResult(true);
    }

    public static async Task StartAsync(IPluginUIContext context, IChatClient client, string workspaceId, string args, string? permissionMode, Control anchor)
    {
        var settings = context.Settings<ClaudeCodeSettings>();
        var open = Params(workspaceId, ChatDocuments.NewId(), settings, args, permissionMode);
        if (args.StartsWith("--continue", StringComparison.Ordinal) || args.StartsWith("--resume", StringComparison.Ordinal))
        {
            ChatSessionList sessions;
            try { sessions = await client.RequestAsync(ChatContract.Sessions, new ChatSessionsParams(workspaceId)); }
            catch (Exception error) when (error is not OperationCanceledException)
            {
                context.Notify(PluginNotificationKind.Error, "Couldn't list sessions", ChatErrors.Text(error));
                return;
            }
            var fork = args.Contains("--fork-session", StringComparison.Ordinal);
            if (args.StartsWith("--continue", StringComparison.Ordinal))
            {
                // A session another tab runs is focused, never started a second time.
                if (sessions.Sessions.FirstOrDefault() is { ChatId: { } running } busy) ChatDocuments.Open(context, ClaudeChat.Agent(context), workspaceId, running, busy.Title);
                else if (sessions.Sessions.FirstOrDefault() is { } latest) ChatDocuments.Open(context, ClaudeChat.Agent(context), workspaceId, open.ChatId, latest.Title, open with { ResumeSessionId = latest.SessionId });
                else ChatDocuments.Open(context, ClaudeChat.Agent(context), workspaceId, open.ChatId, "Claude Code", open);
                return;
            }
            var menu = new ContextMenu { Name = "ChatResumeMenu", Placement = PlacementMode.BottomEdgeAlignedLeft };
            foreach (var session in sessions.Sessions.Take(30))
            {
                var when = DateTime.TryParse(session.LastModified, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed) ? ChatView.RelativeTime(parsed.ToLocalTime(), DateTime.Now) : session.LastModified;
                var item = new MenuItem { Header = $"{session.Title}  ·  {when}" + (session.ChatId is null ? "" : "  ·  open in another tab") };
                item.Click += (_, _) =>
                {
                    if (session.ChatId is { } running && !fork) ChatDocuments.Open(context, ClaudeChat.Agent(context), workspaceId, running, session.Title);
                    else ChatDocuments.Open(context, ClaudeChat.Agent(context), workspaceId, open.ChatId, session.Title, open with { ResumeSessionId = session.SessionId, Fork = fork ? true : null });
                };
                menu.Items.Add(item);
            }
            if (menu.Items.Count == 0) menu.Items.Add(new MenuItem { Header = "No earlier sessions", IsEnabled = false });
            menu.Open(anchor);
            return;
        }
        ChatDocuments.Open(context, ClaudeChat.Agent(context), workspaceId, open.ChatId, "Claude Code", open);
    }
}