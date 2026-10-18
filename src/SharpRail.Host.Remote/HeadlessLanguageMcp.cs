using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;

namespace SharpRail.Host.Remote;

/// <summary>Explicit host-authenticated language and enabled-plugin MCP access without a terminal or agent process.</summary>
public static class HeadlessLanguageMcp
{
    /// <summary>Maps the production language tools and the enabled plugins' tools to POST /mcp for the host's fixed workspace.</summary>
    public static void Map(WebApplication app, HostStateStore state, PluginRuntime plugins, string workspace, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host token is required.", nameof(token));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token));
        var languageTools = LanguageMcpTools.Create(state, Path.GetFullPath(workspace));
        app.MapPost("/mcp", async (HttpContext context) =>
        {
            var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return Results.Unauthorized();
            JsonNode? message;
            try { message = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { return Results.Text("""{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}""", "application/json"); }
            // Read per request, so a plugin turned off stops serving its tools at once.
            var (status, response) = await McpServer.HandleAsync(message, [.. plugins.McpTools(null, Path.GetFullPath(workspace)), .. languageTools], context.RequestAborted);
            return response is null ? Results.StatusCode(status) : Results.Text(response.ToJsonString(), "application/json", statusCode: status);
        });
    }
}