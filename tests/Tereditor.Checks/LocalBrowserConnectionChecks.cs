using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

internal static class LocalBrowserConnectionChecks
{
    internal const string Origin = "http://localhost:54125";

    internal static async Task<HttpResponseMessage> Request(HttpClient client, string? origin = Origin, CancellationToken cancellationToken = default, string? host = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/browser/connect");
        if (origin is not null) request.Headers.Add("Origin", origin);
        if (host is not null) request.Headers.Host = host;
        return await client.SendAsync(request, cancellationToken);
    }

    internal static async Task<RpcPeer> Connect(Uri endpoint, string? origin)
    {
        var socket = new ClientWebSocket();
        socket.Options.AddSubProtocol(RpcWire.Subprotocol);
        if (origin is not null) socket.Options.SetRequestHeader("Origin", origin);
        try
        {
            await socket.ConnectAsync(new UriBuilder(endpoint) { Scheme = "ws", Path = "/rpc" }.Uri, CancellationToken.None);
            return new RpcPeer(socket);
        }
        catch { socket.Dispose(); throw; }
    }

    internal static CallContext Context(string token) => new(new CallOptions(new Metadata { { "authorization", "Bearer " + token } }, deadline: DateTime.UtcNow.AddSeconds(15)));

    internal static async Task Handshake(Uri endpoint, string token, string? origin, bool allowed)
    {
        await using var peer = await Connect(endpoint, origin);
        try
        {
            await using var call = await peer.Open("IStateRpc/HandshakeAsync", Context(token));
            await call.Send(new HandshakeRequest());
            await call.End();
            await foreach (var reply in call.Read<HandshakeReply>()) Require(allowed && reply.ProtocolVersion > 0, "An unapproved browser reached RPC.");
            Require(allowed, "An unapproved browser completed an RPC call.");
        }
        catch (RpcException error) when (!allowed && error.StatusCode == StatusCode.Unauthenticated) { }
    }

    internal static async Task RunHost(string root)
    {
        foreach (var address in new[] { "http://localhost:1", "https://127.0.0.1:2", "http://[::1]:3" })
            Require(LocalBrowserApprovalClient.IsLoopback(new Uri(address)), "Local browser approval must support literal IPv4/IPv6 loopback and localhost.");
        foreach (var address in new[] { "http://localhost.evil:1", "http://192.0.2.1:2", "https://example.com", "ftp://localhost" })
            Require(!LocalBrowserApprovalClient.IsLoopback(new Uri(address)), "Local browser approval must not resolve or accept remote targets.");
        try { await LocalBrowserApprovalClient.RequestAsync(new Uri("http://example.com")); throw new InvalidOperationException("Remote target reached local approval bootstrap."); }
        catch (ArgumentException) { }
        var directory = Path.Combine(root, "local-browser-host");
        Directory.CreateDirectory(directory);
        var state = new HostStateStore(null);
        await using var plugins = new PluginRuntime(new() { StateDirectory = null, State = state, Builtins = [] });
        await using var listener = new HostListener(directory, state, null, plugins);
        var requested = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConfirmLocalBrowserConnection = (origin, ct) => { requested.TrySetResult(origin); return decision.Task.WaitAsync(ct); };
        await listener.StartAsync(IPAddress.Loopback, 0, "local-browser-master");
        using var client = new HttpClient { BaseAddress = listener.Endpoint };
        foreach (var origin in new string?[] { null, "null", "https://evil.example", "http://localhost.evil:1", "http://localhost:1/path", "http://user@localhost:1", "http://localhost:1/" })
        {
            using var invalid = await Request(client, origin);
            Require(invalid.StatusCode == HttpStatusCode.Forbidden && !invalid.Headers.Contains("Access-Control-Allow-Origin"), "Untrusted origins must be rejected before prompting or enabling CORS.");
        }
        using (var rebound = await Request(client, host: "evil.example")) Require(rebound.StatusCode == HttpStatusCode.Forbidden, "DNS-rebound hosts must not request approval.");
        using (var preflight = await Request(client, method: HttpMethod.Options))
            Require(preflight.StatusCode == HttpStatusCode.NoContent && preflight.Headers.GetValues("Access-Control-Allow-Origin").Single() == Origin, "Preflight must allow only the validated origin.");
        Require(!requested.Task.IsCompleted, "Invalid requests and preflight must never prompt.");
        var denied = Request(client);
        Require(await requested.Task.WaitAsync(TimeSpan.FromSeconds(10)) == Origin && !denied.IsCompleted, "The real origin must await consent.");
        using (var duplicate = await Request(client)) Require(duplicate.StatusCode == HttpStatusCode.TooManyRequests, "Only one approval prompt may be pending.");
        decision.SetResult(false);
        using (var response = await denied) Require(response.StatusCode == HttpStatusCode.Forbidden, "Deny must issue no credential.");
        using (var cooldown = await Request(client)) Require(cooldown.StatusCode == HttpStatusCode.TooManyRequests, "Denied prompts must have a cooldown.");
        await Task.Delay(2100);
        listener.ConfirmLocalBrowserConnection = (_, _) => Task.FromResult(true);
        using var approved = await Request(client);
        var grant = (await approved.Content.ReadFromJsonAsync<LocalBrowserApprovalReply>())!.Token;
        Require(approved.IsSuccessStatusCode && grant.Length == 64 && grant != listener.Token && approved.Headers.CacheControl?.NoStore == true, "Approval must issue a private temporary credential, never the master token.");
        Require(listener.BrowserConnections!.Origins.SequenceEqual([Origin]), "Only approved origins should be listed.");
        await Handshake(listener.Endpoint!, grant, Origin, true);
        await Handshake(listener.Endpoint!, grant, Origin, true);
        await Handshake(listener.Endpoint!, grant, "http://localhost:54126", false);
        await Handshake(listener.Endpoint!, grant, null, false);
        await Handshake(listener.Endpoint!, "wrong", Origin, false);
        await Handshake(listener.Endpoint!, listener.Token, null, true);
        await using (var peer = await Connect(listener.Endpoint!, Origin))
        {
            await using var call = await peer.Open("IStateRpc/WatchAsync", Context(grant));
            await call.Send(new StateRequest());
            await call.End();
            await using var replies = call.Read<StateReply>().GetAsyncEnumerator();
            Require(await replies.MoveNextAsync(), "Approved browser subscriptions must start.");
            listener.BrowserConnections.Revoke(Origin);
            try { await replies.MoveNextAsync(); throw new InvalidOperationException("Revocation left an active subscription open."); }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated) { }
        }
        await Handshake(listener.Endpoint!, grant, Origin, false);
        using var second = await Request(client);
        var oldGrant = (await second.Content.ReadFromJsonAsync<LocalBrowserApprovalReply>())!.Token;
        await listener.StopAsync();
        await listener.StartAsync(IPAddress.Loopback, 0, listener.Token);
        await Handshake(listener.Endpoint!, oldGrant, Origin, false);
        Require(listener.BrowserConnections!.Origins.Count == 0, "Restart must forget every browser approval.");
        using var restarted = new HttpClient { BaseAddress = listener.Endpoint };
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        requested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var ignoredDecision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        listener.ConfirmLocalBrowserConnection = (origin, ct) =>
        {
            requested.TrySetResult(origin);
            ct.Register(() => cancelled.TrySetResult());
            return ignoredDecision.Task;
        };
        using var cancel = new CancellationTokenSource();
        var pending = Request(restarted, cancellationToken: cancel.Token);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancel.Cancel();
        try { using var ignored = await pending; throw new InvalidOperationException("A cancelled request completed."); }
        catch (OperationCanceledException) { }
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Require(listener.BrowserConnections.Origins.Count == 0, "Cancellation must issue no approval.");
        Console.WriteLine("PASS local browser approval: origin/Host gates, CORS, consent, denial, bounded pending requests, temporary tokens, RPC origin binding, reconnect, revocation, restart and cancellation");
    }

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}