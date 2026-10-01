using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;

namespace SharpRail.Host.Remote;

// Serves the spec tools to agents in a host's terminals over MCP. Agents speak HTTP/1.1, so the route is a
// separate loopback server that lives as long as the host; the per-terminal token in the path is the only
// identity, and an unknown one is 404.
internal static class McpRoute
{
    internal static void Attach(WebApplication host)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) return;
        WebApplication? server = null;
        PtyTerminalService? served = null;
        string? endpoint = null;
        host.Lifetime.ApplicationStarted.Register(() =>
        {
            if (host.Services.GetService<ITerminalService>() is not PtyTerminalService terminals) return;
            server = Create(terminals);
            server.StartAsync().GetAwaiter().GetResult();
            endpoint = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
            (served = terminals).McpEndpoint = endpoint;
        });
        host.Lifetime.ApplicationStopping.Register(() =>
        {
            if (served is not null && served.McpEndpoint == endpoint) served.McpEndpoint = null;
            server?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        });
    }

    private static WebApplication Create(PtyTerminalService terminals)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.MapPost("/mcp/{token}", async (HttpContext context, string token) =>
        {
            if (terminals.McpWorkspace(token) is not { } workspace) return Results.NotFound();
            JsonNode? message;
            try { message = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { return Results.Text("""{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}""", "application/json"); }
            var (status, body) = await McpServer.HandleAsync(message, workspace, context.RequestAborted);
            return body is null ? Results.StatusCode(status) : Results.Text(body.ToJsonString(), "application/json", statusCode: status);
        });
        // Every tool is request/response, so there is no SSE stream to open and no session to end.
        app.MapMethods("/mcp/{token}", ["GET", "DELETE"], () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        return app;
    }
}
