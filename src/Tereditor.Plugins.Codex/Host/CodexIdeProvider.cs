using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Codex.Host.IdeBridge;

namespace Tereditor.Plugins.Codex.Host;

/// <summary>
/// Joins the IPC transport to the plugin's request/reply channel: each Codex request asks connected apps for a fresh
/// snapshot of the requested workspace. A focused window answers at once; otherwise the last unfocused answer wins
/// when the deadline passes. No editor snapshot survives on the host.
/// </summary>
internal sealed class CodexIdeProvider : IDisposable
{
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(1200);

    private sealed class Pending(string workspaceId)
    {
        public readonly string WorkspaceId = workspaceId;
        public readonly TaskCompletionSource<CodexIdeContext?> Done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public volatile CodexIdeContext? Fallback;
    }

    private readonly IPluginHostContext context;
    private readonly ConcurrentDictionary<string, Pending> pending = new();
    private readonly IDisposable? bridge;

    public CodexIdeProvider(IPluginHostContext context)
    {
        this.context = context;
        context.Method(CodexContract.IdeReply, (reply, _, _) =>
        {
            if (pending.TryGetValue(reply.RequestId, out var request) && request.WorkspaceId == reply.WorkspaceId)
            {
                if (reply.Focused) request.Done.TrySetResult(reply.Context);
                else request.Fallback = reply.Context;
            }
            return ValueTask.FromResult(true);
        });
        if (OperatingSystem.IsWindows()) return;
        try { bridge = new CodexIdeBridge(SocketPath(), async root => await WorkspaceFor(root) is not null, ReadAsync, Warn); }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { Warn(error); }
    }

    private void Warn(Exception error) => context.Log.Warn("Codex IDE context connection failed", new Dictionary<string, string> { ["error"] = error.Message });

    private static string SocketPath()
    {
        var primary = Path.Combine(CodexConfig.Home(), "ipc", "ipc.sock");
        var uid = Unix.Uid;
        var legacy = Path.Combine(Path.GetTempPath(), "codex-ipc", uid != 0 ? $"ipc-{uid}.sock" : "ipc.sock");
        return Environment.GetEnvironmentVariable("CODEX_HOME") is not { Length: > 0 } && !File.Exists(primary) && File.Exists(legacy) ? legacy : primary;
    }

    // The deepest workspace containing the requested directory; a similarly named sibling does not match.
    private async Task<HostWorkspace?> WorkspaceFor(string root)
    {
        var cwd = Unix.RealPath(root);
        return (await context.WorkspacesAsync())
            .Select(workspace => (Workspace: workspace, Path: Unix.RealPath(workspace.Path)))
            .Where(item => cwd == item.Path || cwd.StartsWith(item.Path + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            .OrderByDescending(item => item.Path.Length)
            .Select(item => item.Workspace)
            .FirstOrDefault();
    }

    private async Task<JsonNode?> ReadAsync(string root)
    {
        var target = await WorkspaceFor(root) ?? throw new InvalidOperationException("Unknown workspace");
        var requestId = Guid.NewGuid().ToString();
        var request = new Pending(target.Id);
        pending[requestId] = request;
        try
        {
            context.Publish(CodexContract.IdeRequest, new CodexIdeRequest(requestId, target.Id));
            CodexIdeContext? answer;
            try { answer = await request.Done.Task.WaitAsync(Deadline); }
            catch (TimeoutException) { answer = request.Fallback; }
            if (answer is null) throw new InvalidOperationException("No tereditor window is showing this workspace");
            var cwd = Unix.RealPath(root);
            var worktree = Unix.RealPath(target.Path);
            string PathFor(string path) => Path.IsPathRooted(path) ? path : Path.GetRelativePath(cwd, Path.GetFullPath(path, worktree));
            // Written as the camel-case serializer wrote the records: a missing range or position is null.
            static JsonObject? Position(CodexIdePosition? at) => at is null ? null : new() { ["line"] = at.Line, ["character"] = at.Character };
            return new JsonObject
            {
                ["openTabs"] = new JsonArray([.. answer.OpenTabs.Select(file => new JsonObject { ["label"] = file.Label, ["path"] = PathFor(file.Path) })]),
                ["activeFile"] = answer.ActiveFile is { } active
                    ? new JsonObject
                    {
                        ["label"] = active.Label,
                        ["path"] = PathFor(active.Path),
                        ["selection"] = active.Selection is { } selection ? new JsonObject { ["start"] = Position(selection.Start), ["end"] = Position(selection.End) } : null,
                        ["activeSelectionContent"] = active.ActiveSelectionContent
                    }
                    : null
            };
        }
        finally { pending.TryRemove(requestId, out _); }
    }

    public void Dispose()
    {
        bridge?.Dispose();
        foreach (var request in pending.Values) request.Done.TrySetResult(null);
    }
}