using System.Net;
using System.Net.WebSockets;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using SharpRail.Host.Client;
using SharpRail.Host.Protocol;

namespace SharpRail.Checks;

internal static class WebSocketFlowChecks
{
    public static async Task Run()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var produced = 0;
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        app.UseWebSockets();
        app.Map("/rpc", branch => branch.Run(async context =>
        {
            using var socket = await context.WebSockets.AcceptWebSocketAsync(RpcWire.Subprotocol);
            await using var peer = new RpcPeer(socket, async (stream, open) =>
            {
                if (open.Method == "blocked-input") { await Task.Delay(Timeout.Infinite, stream.Token); return; }
                await foreach (var _ in stream.Read<WorkspaceRequest>()) { }
                try
                {
                    if (open.Method == "slow")
                    {
                        for (var i = 0; i < RpcWire.CallWindow * 2; i++)
                        {
                            await stream.Send(new WorkspaceReply { Name = new string('x', 512 * 1024) });
                            Interlocked.Increment(ref produced);
                        }
                    }
                    else
                    {
                        await stream.Send(new WorkspaceReply { Name = open.Method });
                        if (open.Method == "cancel" || open.Method == "deadline") await Task.Delay(Timeout.Infinite, stream.Token);
                    }
                    await stream.Finish();
                }
                finally { if (open.Method == "cancel") cancelled.TrySetResult(); }
            });
            await peer.Completion;
        }));
        await app.StartAsync();
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            await using var proxy = new TcpProxy(address);
            using var channel = new RpcChannel(proxy.Address);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var context = new CallContext(new(cancellationToken: timeout.Token));
            using (var alreadyCancelled = new CancellationTokenSource())
            {
                alreadyCancelled.Cancel();
                await using var stopped = channel.Stream<WorkspaceRequest, WorkspaceReply>("fast", new(), default).GetAsyncEnumerator(alreadyCancelled.Token);
                await Expect(StatusCode.Cancelled, async () => { await stopped.MoveNextAsync(); });
            }
            await using (var slow = channel.Stream<WorkspaceRequest, WorkspaceReply>("slow", new(), context).GetAsyncEnumerator())
            {
                Require(await slow.MoveNextAsync() && slow.Current.Name.Length == 512 * 1024, "Chunked messages must be reassembled intact.");
                while (Volatile.Read(ref produced) < RpcWire.CallWindow) await Task.Delay(10, timeout.Token);
                Require((await channel.Unary<WorkspaceRequest, WorkspaceReply>("fast", new(), context)).Name == "fast", "A stalled consumer must not block another call.");
                Require(produced == RpcWire.CallWindow && proxy.Connections == 1, "Calls must multiplex on one socket and stop a producer when its receive credit is exhausted.");
            }
            using (var cancel = new CancellationTokenSource())
            {
                await using var stream = channel.Stream<WorkspaceRequest, WorkspaceReply>("cancel", new(), new(new(cancellationToken: cancel.Token))).GetAsyncEnumerator();
                Require(await stream.MoveNextAsync(), "The cancellable call must start.");
                cancel.Cancel();
                await Expect(StatusCode.Cancelled, async () => { await stream.MoveNextAsync(); });
            }
            await cancelled.Task.WaitAsync(timeout.Token);
            await using (var stream = channel.Stream<WorkspaceRequest, WorkspaceReply>("deadline", new(), new(new(deadline: DateTime.UtcNow.AddMilliseconds(200)))).GetAsyncEnumerator())
            {
                Require(await stream.MoveNextAsync(), "The deadline call must start.");
                await Expect(StatusCode.DeadlineExceeded, async () => { await stream.MoveNextAsync(); });
            }
            Require((await channel.Unary<WorkspaceRequest, WorkspaceReply>("after-cancel", new(), context)).Name == "after-cancel" && proxy.Connections == 1,
                "Cancellation and deadlines must preserve other calls and the connection.");

            using var malformed = new ClientWebSocket();
            malformed.Options.AddSubProtocol(RpcWire.Subprotocol);
            await malformed.ConnectAsync(new UriBuilder(address) { Scheme = "ws", Path = "/rpc" }.Uri, timeout.Token);
            await malformed.SendAsync(RpcWire.Encode(new RpcFrame { Kind = RpcFrameKind.Open, Id = 1, Method = "blocked-input" }), WebSocketMessageType.Binary, true, timeout.Token);
            for (var i = 0; i <= RpcWire.CallWindow; i++)
                await malformed.SendAsync(RpcWire.Encode(new RpcFrame { Kind = RpcFrameKind.Data, Id = 1, Last = true }), WebSocketMessageType.Binary, true, timeout.Token);
            try
            {
                var result = await malformed.ReceiveAsync(new byte[1024], timeout.Token);
                Require(result.MessageType == WebSocketMessageType.Close, "An over-credit sender must be disconnected.");
            }
            catch (WebSocketException) { }
            Console.WriteLine("PASS WebSocket multiplexing, chunking, slow-consumer isolation, remote cancellation, deadlines and receive-credit enforcement");
        }
        finally { await app.StopAsync(); }
    }
    private static async Task Expect(StatusCode code, Func<Task> action)
    {
        try { await action(); throw new InvalidOperationException("Expected " + code); }
        catch (RpcException error) when (error.StatusCode == code) { }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}