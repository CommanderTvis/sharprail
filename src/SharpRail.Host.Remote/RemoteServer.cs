using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using ProtoBuf.Grpc.Server;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;

namespace SharpRail.Host.Remote;

public static class RemoteServer
{
    /// <summary>Starts a host; shared state persists in <paramref name="stateDirectory"/>, or only in memory without one.
    /// The host owns its terminals unless the caller supplies and owns them.</summary>
    public static WebApplication Create(string root, IPAddress address, int port, string token, string? stateDirectory = null, ITerminalService? terminals = null)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = FileLimits.SaveMessageBytes;
            options.Listen(address, port, endpoint => endpoint.Protocols = HttpProtocols.Http2);
        });
        builder.Services.AddSingleton<IWorkspaceHost>(new WorkspaceHost(root));
        var state = new HostStateStore(stateDirectory);
        builder.Services.AddSingleton<IHostStateService>(state);
        builder.Services.AddSingleton(new ProjectSessions(root, state));
        // Sessions belong to the host: they outlive client connections and end when the host stops.
        if (terminals is null) builder.Services.AddSingleton<ITerminalService>(_ => new PtyTerminalService());
        else builder.Services.AddSingleton(terminals);
        builder.Services.AddCodeFirstGrpc(options => options.MaxReceiveMessageSize = FileLimits.SaveMessageBytes);
        var app = builder.Build();
        RequireToken(app, token);
        app.MapGrpcService<WorkspaceRpc>();
        app.MapGrpcService<ProjectRpc>();
        app.MapGrpcService<StateRpc>();
        app.MapGrpcService<TerminalRpc>();
        return app;
    }

    // Serves only the given terminals on a private Unix socket, for the relays that embedded terminals of
    // this process run. The caller owns the terminals, so stopping the relay leaves their sessions alive.
    public static WebApplication CreateTerminalRelay(ITerminalService terminals, string socketPath, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A relay token is required.", nameof(token));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.ListenUnixSocket(socketPath, endpoint => endpoint.Protocols = HttpProtocols.Http2));
        builder.Services.AddSingleton(terminals);
        builder.Services.AddCodeFirstGrpc();
        var app = builder.Build();
        RequireToken(app, token);
        app.MapGrpcService<TerminalRpc>();
        return app;
    }

    private static void RequireToken(WebApplication app, string token)
    {
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes($"Bearer {token}"));
        app.Use(async (context, next) =>
        {
            var actual = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(expected, actual))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            await next(context);
        });
    }
}
