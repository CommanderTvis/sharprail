using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

internal sealed partial class LanguageSession
{
    private const int MaxCodeActions = 50;

    private ConcurrentQueue<JsonNode>? commandEdits;

    // Edits a server sends with workspace/applyEdit while its command runs belong to the action being applied.
    private JsonNode? CaptureEdit(JsonNode? parameters)
    {
        if (commandEdits is not { } queue || parameters?["edit"] is not { } edit) return null;
        queue.Enqueue(edit);
        return new JsonObject { ["applied"] = true };
    }

    private bool Provides(string provider, string method)
        => capabilities[provider] is { } value && value.ToJsonString() != "false" || registeredCapabilities.Values.Contains(method);

    private bool ResolvesActions => capabilities["codeActionProvider"] is JsonObject provider && provider["resolveProvider"]?.GetValue<bool>() == true;
}