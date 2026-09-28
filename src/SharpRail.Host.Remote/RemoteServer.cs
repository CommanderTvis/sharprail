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
    public static WebApplication Create(string root, IPAddress address, int port, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host session token is required.", nameof(token));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port,
            endpoint => endpoint.Protocols = HttpProtocols.Http2));
        builder.Services.AddSingleton<IWorkspaceHost>(new WorkspaceHost(root));
        builder.Services.AddSingleton<IProjectServices>(new ProjectServices(root));
        builder.Services.AddCodeFirstGrpc(options => options.MaxReceiveMessageSize = 16 * 1024 * 1024);
        var app = builder.Build();
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
        app.MapGrpcService<WorkspaceRpc>();
        app.MapGrpcService<ProjectRpc>();
        return app;
    }
}
