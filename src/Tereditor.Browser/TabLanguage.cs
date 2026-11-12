using System.Runtime.InteropServices.JavaScript;
using System.Text;

using SharpRail.Host.Core.Language;

namespace SharpRail.UI.Browser;

/// <summary>A language server in a Web Worker of this tab (<c>lsp*</c> in <c>tabhost.js</c>), reached as the framed bytes it would have on a pipe.</summary>
internal sealed partial class TabLanguageServer : ILanguageProcess
{
    [JSImport("lspOpen", "tabhost")]
    private static partial string? Open(string server);
    [JSImport("lspSend", "tabhost")]
    private static partial void Send(string id, string message);
    [JSImport("lspNext", "tabhost")]
    private static partial Task<string?> Next(string id);
    [JSImport("lspUsage", "tabhost")]
    private static partial Task<string?> ReadUsage(string id);
    [JSImport("lspClose", "tabhost")]
    private static partial void End(string id);

    private static int count;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, TabLanguageServer> Live = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, (TimeSpan Cpu, long MemoryBytes)> Usages = new();
    private readonly string id;
    private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int ended;

    private TabLanguageServer(string id)
    {
        this.id = id;
        Id = Interlocked.Increment(ref count);
        Input = new Writing(this);
        Output = new Reading(this);
        Live[Id] = this;
    }

    /// <summary>The usage the last <see cref="RefreshUsageAsync"/> read for a worker: the time it spent on messages and its JS heap (zero where the browser reports none).</summary>
    public static (TimeSpan Cpu, long MemoryBytes)? UsageOf(int id) => Usages.TryGetValue(id, out var usage) ? usage : null;

    /// <summary>Asks every live worker for its usage, one at a time, on the page's thread; the Processes pane reads the answers with <see cref="UsageOf"/>.</summary>
    public static async Task RefreshUsageAsync(CancellationToken cancellationToken)
    {
        foreach (var (key, server) in Live.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (server.HasExited) continue;
            var json = await TabGit.OnPage(() => ReadUsage(server.id)).WaitAsync(cancellationToken);
            if (json is null) continue;
            using var document = System.Text.Json.JsonDocument.Parse(json);
            Usages[key] = (TimeSpan.FromMilliseconds(document.RootElement.GetProperty("busyMs").GetDouble()), (long)document.RootElement.GetProperty("heapBytes").GetDouble());
        }
    }

    /// <summary>Starts the server with this catalog id, or answers null for one the page does not carry.</summary>
    public static async Task<ILanguageProcess?> StartAsync(string server, CancellationToken cancellationToken)
    {
        var id = await TabGit.OnPage(() => Task.FromResult(Open(server))).WaitAsync(cancellationToken);
        return id is null ? null : new TabLanguageServer(id);
    }

    public int Id { get; }
    public bool HasExited => Volatile.Read(ref ended) != 0;
    public int ExitCode => 0;
    public Task WaitForExitAsync() => exited.Task;
    public Stream Input { get; }
    public Stream Output { get; }
    public TextReader Errors => TextReader.Null;

    public void Kill()
    {
        if (Interlocked.Exchange(ref ended, 1) != 0) return;
        Live.TryRemove(Id, out _);
        Usages.TryRemove(Id, out _);
        _ = TabGit.OnPage(() => { End(id); return Task.FromResult(0); });
        exited.TrySetResult();
    }

    public void Dispose() => Kill();

    // The client writes Content-Length frames; the worker takes the JSON inside each.
    private sealed class Writing(TabLanguageServer server) : Stream
    {
        private readonly List<byte> pending = [];

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            pending.AddRange(buffer.Span);
            while (true)
            {
                var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending);
                var headerEnd = span.IndexOf("\r\n\r\n"u8);
                if (headerEnd < 0) return;
                var header = Encoding.ASCII.GetString(span[..headerEnd]);
                var line = header.Split("\r\n").FirstOrDefault(item => item.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                if (line is null || !int.TryParse(line[15..].Trim(), out var length)) throw new IOException("A language message had no Content-Length.");
                var total = headerEnd + 4 + length;
                if (span.Length < total) return;
                var message = Encoding.UTF8.GetString(span.Slice(headerEnd + 4, length));
                pending.RemoveRange(0, total);
                await TabGit.OnPage(() => { Send(server.id, message); return Task.FromResult(0); });
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // The worker's messages come back as the framed bytes the client reads.
    private sealed class Reading(TabLanguageServer server) : Stream
    {
        private byte[] current = [];
        private int at;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (at >= current.Length)
            {
                var message = await TabGit.OnPage(() => Next(server.id)).WaitAsync(cancellationToken);
                if (message is null) { server.Kill(); return 0; }
                var body = Encoding.UTF8.GetBytes(message);
                current = [.. Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n"), .. body];
                at = 0;
            }
            var count = Math.Min(buffer.Length, current.Length - at);
            current.AsMemory(at, count).CopyTo(buffer);
            at += count;
            return count;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
