using System.Security.Cryptography;

using Grpc.Core;

using ProtoBuf;
using ProtoBuf.Grpc;

using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

/// <summary>
/// Runs each (client, request id) once and answers a replay of it with the first run's outcome, so a
/// mutation whose reply died with its connection neither fails falsely nor runs twice. A result stays
/// until its client stops naming the request as unresolved.
/// </summary>
public sealed class RequestReplayCache(IHostApplicationLifetime lifetime, int maxRequestsPerClient = 512, long maxWeightPerClient = 16 * 1024 * 1024, int maxClients = 64)
{
    private sealed class Entry(string fingerprint)
    {
        internal string Fingerprint { get; } = fingerprint;
        /// <summary>Null once settled beyond the client's retention budget.</summary>
        internal Task<object>? Result { get; set; }
        internal bool Settled { get; set; }
        internal long Weight { get; set; }
    }

    private sealed class Client
    {
        internal Dictionary<string, Entry> Requests { get; } = new(StringComparer.Ordinal);
        internal long Weight { get; set; }
        internal long Used { get; set; }
    }

    /// <summary>Counts a reply's serialized bytes without keeping them.</summary>
    private sealed class Counter : Stream
    {
        private long length;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => length;
        public override long Position { get => length; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => length += count;
    }

    private readonly Dictionary<string, Client> clients = new(StringComparer.Ordinal);
    private long clock;

    /// <summary>
    /// Without replay metadata this is a plain call cancelled with its connection. With it, the work is
    /// detached from the call so a dropped connection cannot abandon it half done.
    /// </summary>
    public ValueTask<T> RunAsync<TRequest, T>(CallContext context, TRequest request, Func<CancellationToken, ValueTask<T>> execute) where T : class
    {
        var headers = context.ServerCallContext?.RequestHeaders;
        if (headers?.GetValue(ReplayHeaders.Client) is not { Length: > 0 } clientKey || headers.GetValue(ReplayHeaders.Request) is not { Length: > 0 } id)
            return execute(context.CancellationToken);
        var fingerprint = context.ServerCallContext!.Method + ":" + Fingerprint(request);
        Task<object> result;
        lock (clients)
        {
            if (!clients.TryGetValue(clientKey, out var client))
            {
                if (clients.Count >= maxClients) EvictIdleClient();
                clients[clientKey] = client = new();
            }
            client.Used = ++clock;
            if (headers.GetValue(ReplayHeaders.Resume) is { } resume) Retain(client, resume.Split(',', StringSplitOptions.RemoveEmptyEntries));
            if (client.Requests.TryGetValue(id, out var existing))
            {
                if (existing.Fingerprint != fingerprint)
                    throw new RpcException(new Status(StatusCode.InvalidArgument, $"Request id \"{id}\" was reused with a different payload."));
                result = existing.Result ?? throw new RpcException(new Status(StatusCode.FailedPrecondition,
                    $"Request id \"{id}\" already ran; its response exceeded the retention budget."));
            }
            else
            {
                if (client.Requests.Count >= maxRequestsPerClient)
                    throw new RpcException(new Status(StatusCode.ResourceExhausted, "Too many unresolved requests: read their results first."));
                var entry = new Entry(fingerprint);
                client.Requests[id] = entry;
                entry.Result = result = Task.Run(async () =>
                {
                    long weight = 1;
                    try
                    {
                        var value = await execute(lifetime.ApplicationStopping);
                        var size = new Counter();
                        Serializer.Serialize(size, value);
                        weight = Math.Max(1, size.Length);
                        return (object)value;
                    }
                    finally { Settle(client, entry, weight); }
                });
            }
        }
        return new(Await<T>(result, context.CancellationToken));
    }

    private static async Task<T> Await<T>(Task<object> result, CancellationToken cancellationToken) => (T)await result.WaitAsync(cancellationToken);

    private static string Fingerprint<TRequest>(TRequest request)
    {
        using var sha = SHA256.Create();
        using (var hashing = new CryptoStream(Stream.Null, sha, CryptoStreamMode.Write)) Serializer.Serialize(hashing, request);
        return Convert.ToHexString(sha.Hash!);
    }

    private void Settle(Client client, Entry entry, long weight)
    {
        lock (clients)
        {
            entry.Settled = true;
            if (client.Weight + weight > maxWeightPerClient) { entry.Result = null; return; }
            entry.Weight = weight;
            client.Weight += weight;
        }
    }

    private static void Retain(Client client, string[] unresolved)
    {
        foreach (var (id, entry) in client.Requests.Where(pair => pair.Value.Settled && !unresolved.Contains(pair.Key)).ToArray())
        {
            client.Requests.Remove(id);
            client.Weight -= entry.Weight;
        }
    }

    /// <summary>A client that went away leaves its last results behind; the least recently seen one without running work goes first.</summary>
    private void EvictIdleClient()
    {
        var idle = clients.Where(pair => pair.Value.Requests.Values.All(entry => entry.Settled)).OrderBy(pair => pair.Value.Used).Select(pair => pair.Key).FirstOrDefault();
        if (idle is not null) clients.Remove(idle);
    }
}