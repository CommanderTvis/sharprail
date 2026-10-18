using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Core;

namespace SharpRail.Host.Remote;

/// <summary>Explicit host-authenticated language/Context MCP access without a terminal or agent process.</summary>
public static class HeadlessLanguageMcp
{
    /// <summary>Maps the production language and Context tools to POST /mcp for the host's fixed workspace.</summary>
    public static void Map(WebApplication app, HostStateStore state, string workspace, string token)
    {
        if (string.IsNullOrWhiteSpace(token)) throw new ArgumentException("A host token is required.", nameof(token));
        var expected = SHA256.HashData(Encoding.UTF8.GetBytes("Bearer " + token));
        var tools = LanguageMcpTools.Create(state, Path.GetFullPath(workspace)).Concat(ContextMcpTools.Create(state, Path.GetFullPath(workspace))).ToArray();
        app.MapPost("/mcp", async (HttpContext context) =>
        {
            var supplied = SHA256.HashData(Encoding.UTF8.GetBytes(context.Request.Headers.Authorization.ToString()));
            if (!CryptographicOperations.FixedTimeEquals(expected, supplied)) return Results.Unauthorized();
            JsonNode? message;
            try { message = await JsonNode.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted); }
            catch (JsonException) { return Results.Text("""{"jsonrpc":"2.0","id":null,"error":{"code":-32700,"message":"Parse error"}}""", "application/json"); }
            var (status, response) = await McpServer.HandleAsync(message, tools, context.RequestAborted);
            return response is null ? Results.StatusCode(status) : Results.Text(response.ToJsonString(), "application/json", statusCode: status);
        });
    }
}