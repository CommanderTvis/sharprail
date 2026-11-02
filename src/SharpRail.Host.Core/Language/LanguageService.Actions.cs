using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

public sealed partial class LanguageService
{
    /// <summary>Runs a plugin-defined action (<c>Query</c> names it, <c>Configuration</c> carries its JSON arguments) on the server's running session; it never starts a server (H21).</summary>
    private async Task<LanguageResult> RunActionAsync(string workspace, LanguageServer? server, LanguageRequest request, CancellationToken token)
    {
        if (server is null || server.Spec.ActionAsync is null) throw new ArgumentException("Select a language server that offers actions.");
        if (request.Query.Length == 0) throw new ArgumentException("Name the action to run.");
        JsonNode? arguments;
        try { arguments = request.Configuration is null ? null : JsonNode.Parse(request.Configuration); }
        catch (JsonException) { throw new ArgumentException("Action arguments must be JSON."); }
        if (!entries.TryGetValue(workspace + "\0" + server.Id, out var entry) || !entry.Session.IsValueCreated || !entry.Session.Value.IsCompletedSuccessfully || entry.Session.Value.Result.Exited)
            return new("unavailable", $"{Name(server)} is not running. Start it, then retry.", server.Id) { Freshness = "unavailable" };
        try
        {
            var result = await entry.Session.Value.Result.RunActionAsync(request.Query, arguments, token);
            return new("ready", result?.ToJsonString() ?? "", server.Id) { Freshness = "current" };
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is IOException or TimeoutException or OperationCanceledException or ArgumentException or InvalidOperationException)
        {
            return new("error", error.Message, server.Id) { Freshness = "unavailable" };
        }
    }
}