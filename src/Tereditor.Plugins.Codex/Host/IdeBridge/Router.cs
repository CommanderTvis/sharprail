using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.Codex.Host.IdeBridge;

/// <summary>
/// The router a bridge owns when none is listening: client initialization, discovery, request/reply forwarding with
/// fresh ids checked against the answering socket, and broadcasts. Disconnects, timeouts and disposal settle every
/// pending exchange.
/// </summary>
internal sealed class Router(Socket listener) : IDisposable
{
    private static readonly TimeSpan ExchangeTimeout = TimeSpan.FromSeconds(2);

    private sealed record Client(string Id, JsonNode? Type);

    private sealed record Exchange(Peer Source, Peer Target, TaskCompletionSource<JsonObject> Reply);

    private readonly Lock gate = new();
    private readonly Dictionary<Peer, Client> clients = [];
    private readonly HashSet<Peer> peers = [];
    private readonly Dictionary<string, Exchange> pending = [];
    private readonly CancellationTokenSource lifetime = new();

    public async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var socket = await listener.AcceptAsync(lifetime.Token);
                var peer = new Peer(socket);
                lock (gate) peers.Add(peer);
                _ = Task.Run(() => ServeAsync(peer));
            }
        }
        catch (Exception error) when (error is SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    private async Task ServeAsync(Peer peer)
    {
        await peer.ReadAsync(message => Receive(peer, message));
        Client? left;
        Exchange[] settled;
        lock (gate)
        {
            peers.Remove(peer);
            left = clients.GetValueOrDefault(peer);
            settled = [.. pending.Values.Where(exchange => exchange.Source == peer || exchange.Target == peer)];
        }
        if (left is not null) Status(peer, left, "disconnected");
        lock (gate) clients.Remove(peer);
        foreach (var exchange in settled) exchange.Reply.TrySetResult(Error("client-disconnected"));
    }

    private static JsonObject Error(string error) => new() { ["type"] = "response", ["resultType"] = "error", ["error"] = error };

    private void Status(Peer peer, Client client, string state)
    {
        Peer[] others;
        lock (gate) others = [.. clients.Keys.Where(other => other != peer)];
        foreach (var target in others)
            target.Send(new JsonObject
            {
                ["type"] = "broadcast",
                ["method"] = "client-status-changed",
                ["sourceClientId"] = client.Id,
                ["version"] = 0,
                ["params"] = new JsonObject { ["clientId"] = client.Id, ["clientType"] = client.Type?.DeepClone(), ["status"] = state }
            });
    }

    private void Receive(Peer peer, JsonObject message)
    {
        var type = Peer.Text(message["type"]);
        var requestId = Peer.Text(message["requestId"]);
        if (type == "request" && requestId is not null)
        {
            if (Peer.Text(message["method"]) == "initialize")
            {
                Client client;
                lock (gate)
                {
                    if (!clients.TryGetValue(peer, out client!))
                        clients[peer] = client = new(Guid.NewGuid().ToString(), Peer.Record(message["params"])?["clientType"]?.DeepClone());
                }
                Status(peer, client, "connected");
                peer.Send(new JsonObject
                {
                    ["type"] = "response",
                    ["requestId"] = requestId,
                    ["resultType"] = "success",
                    ["method"] = "initialize",
                    ["handledByClientId"] = client.Id,
                    ["result"] = new JsonObject { ["clientId"] = client.Id }
                });
            }
            else _ = RouteAsync(peer, message, requestId);
        }
        else if (type is "response" or "client-discovery-response" && requestId is not null)
        {
            Exchange? exchange;
            lock (gate) exchange = pending.GetValueOrDefault(requestId);
            if (exchange?.Target == peer) exchange.Reply.TrySetResult(message);
        }
        else if (type == "broadcast")
        {
            Client? sender;
            Peer[] others;
            lock (gate)
            {
                sender = clients.GetValueOrDefault(peer);
                others = [.. clients.Keys.Where(other => other != peer)];
            }
            if (sender is null) return;
            foreach (var target in others)
            {
                var copy = (JsonObject)message.DeepClone();
                copy["sourceClientId"] = sender.Id;
                target.Send(copy);
            }
        }
    }

    private async Task<JsonObject> ExchangeAsync(Peer source, Peer target, JsonObject message)
    {
        var requestId = Guid.NewGuid().ToString();
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (gate) pending[requestId] = new(source, target, reply);
        var copy = (JsonObject)message.DeepClone();
        copy["requestId"] = requestId;
        target.Send(copy);
        try { return await reply.Task.WaitAsync(ExchangeTimeout); }
        catch (TimeoutException) { return Error("request-timeout"); }
        finally { lock (gate) pending.Remove(requestId); }
    }

    private async Task RouteAsync(Peer source, JsonObject request, string requestId)
    {
        var wanted = Peer.Text(request["targetClientId"]);
        Peer[] candidates;
        lock (gate) candidates = [.. clients.Where(pair => pair.Key != source && (wanted is null || wanted == pair.Value.Id)).Select(pair => pair.Key)];
        var discoveries = candidates.Select(async candidate =>
        {
            var reply = await ExchangeAsync(source, candidate, new JsonObject { ["type"] = "client-discovery-request", ["request"] = request.DeepClone() });
            return Peer.Record(reply["response"])?["canHandle"] is JsonValue can && can.TryGetValue<bool>(out var yes) && yes ? candidate : null;
        }).ToList();
        Peer? target = null;
        while (discoveries.Count > 0 && target is null)
        {
            var done = await Task.WhenAny(discoveries);
            discoveries.Remove(done);
            target = await done;
        }
        if (source.IsClosed) return;
        if (target is null)
        {
            source.Failure(requestId, "no-client-found");
            return;
        }
        if (target.IsClosed)
        {
            source.Failure(requestId, "client-disconnected");
            return;
        }
        var answer = await ExchangeAsync(source, target, request);
        answer["requestId"] = requestId;
        source.Send(answer);
    }

    public void Dispose()
    {
        lifetime.Cancel();
        Exchange[] settled;
        Peer[] all;
        lock (gate)
        {
            settled = [.. pending.Values];
            all = [.. peers];
        }
        foreach (var exchange in settled) exchange.Reply.TrySetResult(Error("server-closed"));
        foreach (var peer in all) peer.Dispose();
    }
}