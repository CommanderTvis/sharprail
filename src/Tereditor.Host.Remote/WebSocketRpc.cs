using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

using SharpRail.Host.Protocol;

namespace SharpRail.Host.Remote;

/// <summary>Authenticated WebSocket dispatch directly into the existing host adapters.</summary>
public static class WebSocketRpc
{
    public static void Map(WebApplication app, string token, Func<string, bool>? authenticate = null, Action<CallContext>? observe = null, LocalBrowserConnections? browsers = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        app.UseWebSockets();
        app.Map("/rpc", branch => branch.Run(async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest || !context.WebSockets.WebSocketRequestedProtocols.Contains(RpcWire.Subprotocol))
            { context.Response.StatusCode = StatusCodes.Status400BadRequest; return; }
            using var socket = await context.WebSockets.AcceptWebSocketAsync(RpcWire.Subprotocol);
            var expected = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token));
            await using var peer = new RpcPeer(socket, async (stream, open) =>
            {
                observe?.Invoke(stream.Context(open.Method));
                var authorization = open.Headers.GetValueOrDefault("authorization", "");
                var master = CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(Encoding.UTF8.GetBytes(authorization)));
                var credential = authorization.StartsWith("Bearer ", StringComparison.Ordinal) ? authorization[7..] : "";
                bool Approved() => authenticate?.Invoke(credential) == true || browsers?.Authenticate(credential, context) == true;
                var paired = !master && authenticate?.Invoke(credential) == true;
                if (!master && !paired && browsers?.Authenticate(credential, context) != true)
                    throw new RpcException(new(StatusCode.Unauthenticated, "The host rejected the session token."));
                // Tagged per call so the host, not the client, decides what this connection may turn off.
                RpcConnection.ThroughTailscale = paired || RpcConnection.IsTailnetAddress(context.Connection.RemoteIpAddress);
                using var watching = CancellationTokenSource.CreateLinkedTokenSource(stream.Token);
                var revocation = master ? Task.CompletedTask : Watch();
                try { await RpcDispatch.Run(app.Services, stream, open); }
                finally { watching.Cancel(); await revocation; }
                async Task Watch()
                {
                    try
                    {
                        while (true)
                        {
                            await Task.Delay(TimeSpan.FromSeconds(1), watching.Token);
                            if (Approved()) continue;
                            stream.Fail(new RpcException(new(StatusCode.Unauthenticated, "The host credential was revoked.")));
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (watching.IsCancellationRequested) { }
                }
            });
            using var aborted = context.RequestAborted.Register(socket.Abort);
            using var stopping = app.Lifetime.ApplicationStopping.Register(socket.Abort);
            await peer.Completion;
        }));
    }
}