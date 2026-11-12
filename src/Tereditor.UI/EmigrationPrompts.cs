using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>Asks the user about each agent's pending <c>workspace_emigrate</c> request; any client's answer settles it for all.</summary>
public sealed partial class WorkbenchWindow
{
    // The toast raised for each pending request, by request id, so an answer from elsewhere takes it down.
    private readonly Dictionary<string, long> emigrationToasts = [];

    /// <summary>Tells the user once about each approved move whose relaunch failed; the tab is back in its workspace.</summary>
    private void NotifyEmigrationFailures(HostState previous, HostState next)
    {
        foreach (var failed in next.FailedEmigrations.Where(failure => previous.FailedEmigrations.All(known => known.Id != failure.Id)))
            Toasts.Push(ToastVariant.Error, $"The agent could not be relaunched in {failed.Target}; its tab is back in {failed.Terminal.WorkspaceId}. {failed.Reason}",
                "Agent terminal not moved");
    }

    private void PromptEmigrations(HostState next)
    {
        foreach (var request in next.PendingEmigrations)
        {
            if (emigrationToasts.ContainsKey(request.Id)) continue;
            var tab = next.TerminalTitles.FirstOrDefault(title => title.Terminal == request.Terminal)?.Title ?? "An agent terminal";
            var message = $"{tab} asks to move from {WorkspaceName(next, request.Terminal.WorkspaceId)} into {WorkspaceName(next, request.Target)} " +
                "and resume there. It will no longer work in the first workspace." + (request.Reason.Length > 0 ? " Reason: " + request.Reason : "");
            var id = request.Id;
            emigrationToasts[id] = Toasts.Push(ToastVariant.Info, message, "Move agent terminal?", duration: TimeSpan.FromMinutes(10),
                action: new("Move", () => _ = ShareAsync(HostStateChange.AnswerEmigration(id, true))),
                secondary: new("Keep here", () => _ = ShareAsync(HostStateChange.AnswerEmigration(id, false))));
        }
        foreach (var settled in emigrationToasts.Keys.Where(id => next.PendingEmigrations.All(request => request.Id != id)).ToArray())
        {
            Toasts.Dismiss(emigrationToasts[settled]);
            emigrationToasts.Remove(settled);
        }

        static string WorkspaceName(HostState host, string path) =>
            host.WorkspaceLabels.GetValueOrDefault(path) is { Length: > 0 } label ? $"\"{label}\" ({path})" : path;
    }
}