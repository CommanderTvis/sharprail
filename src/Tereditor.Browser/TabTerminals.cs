using System.Runtime.CompilerServices;
using System.Runtime.InteropServices.JavaScript;
using System.Text;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Browser;

/// <summary>The tab's terminals: a simulated bash (<c>shell*</c> in <c>tabhost.js</c>) over the tab's own file system, one per terminal tab.</summary>
internal sealed partial class TabTerminals : ITerminalService
{
    [JSImport("shellOpen", "tabhost")]
    private static partial Task<string> Open(string root);
    [JSImport("shellWrite", "tabhost")]
    private static partial void Send(string id, string data);
    [JSImport("shellNext", "tabhost")]
    private static partial Task<string?> Next(string id);
    [JSImport("shellReplay", "tabhost")]
    private static partial string Replay(string id);
    [JSImport("shellClose", "tabhost")]
    private static partial void End(string id);

    private readonly Dictionary<string, string> shells = [];

    public async ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        TerminalGrid.Require(request.Columns, request.Rows);
        string? existing;
        lock (shells) shells.TryGetValue(request.SessionId, out existing);
        if (existing is not null) return new Session(existing, false, await TabGit.OnPage(() => Task.FromResult(Replay(existing))));
        var id = await TabGit.OnPage(() => Open(request.WorkspaceRoot)).WaitAsync(cancellationToken);
        lock (shells) shells[request.SessionId] = id;
        return new Session(id, true, "");
    }

    public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default) => ValueTask.FromResult(false);

    public ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        string? id;
        lock (shells) { shells.Remove(sessionId, out id); }
        return id is null ? ValueTask.CompletedTask : new(TabGit.OnPage(() => { End(id); return Task.FromResult(0); }));
    }

    private sealed class Session(string id, bool created, string replay) : ITerminalSession
    {
        private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string Id => id;
        public bool Created => created;
        public ReadOnlyMemory<byte> Replay { get; } = Encoding.UTF8.GetBytes(replay);
        public long Position => 0;
        public bool Watching => false;
        public (int Columns, int Rows) Grid => (80, 24);
        public Task<int> Exit => exit.Task;
        public Task Detached { get; } = new TaskCompletionSource().Task;
        public TerminalPrefill? Prefill => null;

        public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var chunk = await TabGit.OnPage(() => Next(id)).WaitAsync(cancellationToken);
                if (chunk is null) { exit.TrySetResult(0); yield break; }
                yield return Encoding.UTF8.GetBytes(chunk);
            }
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            var text = Encoding.UTF8.GetString(data.Span);
            return new(TabGit.OnPage(() => { Send(id, text); return Task.FromResult(0); }));
        }

        // The shell is line-oriented and has no grid of its own.
        public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
        {
            TerminalGrid.Require(columns, rows);
            return ValueTask.CompletedTask;
        }

        public ValueTask KillAsync(CancellationToken cancellationToken = default) => new(TabGit.OnPage(() => { End(id); return Task.FromResult(0); }));

        // Detaching leaves the shell running for the tab's next view.
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
