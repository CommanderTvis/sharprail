using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Remote;

// The host's loopback HTTP/1.1 server, which processes on the host machine reach: the spec tools and plugin
// tools over MCP at /mcp/{token}, where the per-terminal token is the only identity and an unknown one is 404,
// and plugin routes at /plugin/{id}/{subpath}. It starts on first use and lives as long as the host.
public sealed class LoopbackServer(PtyTerminalService? terminals) : IAsyncDisposable
{
    private readonly Lock gate = new();
    private WebApplication? server;
    private string? baseUrl;
    private bool disposed;

    /// <summary>The runtime serving plugin routes and adding plugin tools to each terminal's MCP table.</summary>
    public PluginRuntime? Plugins { get; set; }

    /// <summary>The server's base URL without a trailing slash, starting it on first use.</summary>
    public string BaseUrl
    {
        get
        {
            lock (gate)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (baseUrl is not null) return baseUrl;
                var started = Create();
                started.StartAsync().GetAwaiter().GetResult();
                server = started;
                baseUrl = started.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single().TrimEnd('/');
                if (terminals is not null) terminals.McpEndpoint = baseUrl;
                return baseUrl;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        WebApplication? running;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            if (terminals is not null && baseUrl is not null && terminals.McpEndpoint == baseUrl) terminals.McpEndpoint = null;
            running = server;
        }
        if (running is not null) await running.DisposeAsync();
    }

    private WebApplication Create()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, endpoint => endpoint.Protocols = HttpProtocols.Http1));
        var app = builder.Build();
        app.MapPost("/mcp/{token}", async (HttpContext context, string token) =>
        {
            if (terminals?.McpOwner(token) is not { } owner) return Results.NotFound();
            JsonNode? message;
            try { message = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { return Results.Text("""{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}""", "application/json"); }
            var tools = Plugins?.McpTools(owner.Terminal, owner.Workspace) ?? [];
            var (status, body) = await McpServer.HandleAsync(message, owner.Workspace, tools, context.RequestAborted);
            return body is null ? Results.StatusCode(status) : Results.Text(body.ToJsonString(), "application/json", statusCode: status);
        });
        // Every tool is request/response, so there is no SSE stream to open and no session to end.
        app.MapMethods("/mcp/{token}", ["GET", "DELETE"], () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));
        app.Map("/plugin/{id}", (HttpContext context, string id) => ServeAsync(context, id, ""));
        app.Map("/plugin/{id}/{**subpath}", (HttpContext context, string id, string? subpath) => ServeAsync(context, id, subpath ?? ""));
        return app;
    }

    private async Task ServeAsync(HttpContext context, string id, string subpath)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body, context.RequestAborted);
        var request = new PluginHttpRequest(context.Request.Method, subpath, context.Request.QueryString.Value ?? "",
            context.Request.Headers.ToDictionary(header => header.Key, header => header.Value.ToString(), StringComparer.OrdinalIgnoreCase), body.ToArray());
        PluginHttpResponse? response;
        try { response = Plugins is { } plugins ? await plugins.ServeRouteAsync(id, request, context.RequestAborted) : null; }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            await context.Response.WriteAsync(error.Message, context.RequestAborted);
            return;
        }
        if (response is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        context.Response.StatusCode = response.Status;
        context.Response.ContentType = response.ContentType;
        foreach (var (name, value) in response.Headers) context.Response.Headers[name] = value;
        await context.Response.Body.WriteAsync(response.Body, context.RequestAborted);
    }
}
