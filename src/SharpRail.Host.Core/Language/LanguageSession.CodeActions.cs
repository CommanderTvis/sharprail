using System.Collections.Concurrent;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

internal sealed partial class LanguageSession
{
    private const int MaxCodeActions = 50;

    /// <summary>The actions last offered for one document state; applying an id from any other state is refused as stale.</summary>
    private sealed record OfferedActions(string Uri, int DocumentVersion, long ClientVersion, JsonNode[] Actions);

    private OfferedActions? offered;
    private ConcurrentQueue<JsonNode>? commandEdits;

    // Edits a server sends with workspace/applyEdit while its command runs belong to the action being applied.
    private object? CaptureEdit(JsonNode? parameters)
    {
        if (commandEdits is not { } queue || parameters?["edit"] is not { } edit) return null;
        queue.Enqueue(edit);
        return new { applied = true };
    }

    private bool Provides(string provider, string method)
        => capabilities[provider] is { } value && value.ToJsonString() != "false" || registeredCapabilities.Values.Contains(method);

    private bool ResolvesActions => capabilities["codeActionProvider"] is JsonObject provider && provider["resolveProvider"]?.GetValue<bool>() == true;

    private async Task<LanguageResult> CodeActionsAsync(LanguageResult result, LanguageRequest request, string uri, Document document, CancellationToken token)
    {
        if (!Provides("codeActionProvider", "textDocument/codeAction"))
            return result with { State = "unsupported", Detail = $"{server.Id} does not support code actions." };
        if (request.Operation == "applyCodeAction") return await ApplyCodeActionAsync(result, request, uri, document, token);

        var caret = new { line = request.Line, character = request.Column };
        var context = diagnostics.TryGetValue(uri, out var published) && (published.Version is null || published.Version == document.Version)
            ? published.Items.Where(item => item.Raw.Length > 0 && Covers(item, request.Line, request.Column)).Select(item => JsonNode.Parse(item.Raw)!).ToArray() : [];
        var response = await connection.RequestAsync("textDocument/codeAction", new { textDocument = new { uri }, range = new { start = caret, end = caret }, context = new { diagnostics = context } }, token);

        var usable = new List<(JsonNode Action, bool Preferred, bool Quickfix)>();
        foreach (var entry in response as JsonArray ?? [])
        {
            if (entry is not JsonObject action || action["disabled"] is not null || (action["title"]?.ToString() ?? "").Length == 0) continue;
            // A bare Command (its "command" is the name) is the legacy form of an action that only runs a command.
            if (action["command"] is JsonValue) action = new JsonObject { ["title"] = action["title"]!.DeepClone(), ["command"] = action.DeepClone() };
            var command = action["command"]?["command"]?.ToString();
            if (action["edit"] is null && !(command is null ? ResolvesActions : ((ILanguageServerSession)this).SupportsCommand(command))) continue;
            var kind = action["kind"]?.ToString() ?? "";
            usable.Add((action, action["isPreferred"]?.GetValue<bool>() == true, kind == "quickfix" || kind.StartsWith("quickfix.", StringComparison.Ordinal)));
        }
        var ordered = usable.OrderByDescending(item => item.Preferred).ThenByDescending(item => item.Quickfix).Take(MaxCodeActions).ToArray();
        offered = new(uri, document.Version, request.Version, ordered.Select(item => item.Action).ToArray());
        return result with
        {
            Actions = ordered.Select((item, index) => new LanguageItem(request.Path, request.Line, request.Column, request.Line, request.Column,
                item.Action["title"]!.ToString(), item.Action["kind"]?.ToString() ?? "", index.ToString(), item.Preferred ? "preferred" : "")).ToArray(),
            Freshness = "current"
        };
    }

    private async Task<LanguageResult> ApplyCodeActionAsync(LanguageResult result, LanguageRequest request, string uri, Document document, CancellationToken token)
    {
        if (offered is not { } current || current.Uri != uri || current.DocumentVersion != document.Version || current.ClientVersion != request.Version
            || !int.TryParse(request.Query, out var id) || id < 0 || id >= current.Actions.Length)
            return result with { State = "stale", Detail = "The quick fixes belong to an older version of the document." };
        var action = current.Actions[id].DeepClone();
        if (action["edit"] is null && ResolvesActions && await connection.RequestAsync("codeAction/resolve", action, token) is JsonObject resolved) action = resolved;

        var edits = new List<LanguageItem>();
        if (action["edit"] is { } edit && Collect(edit, uri, document, edits) is { } refused) return result with { State = refused.State, Detail = refused.Detail };
        if (action["command"] is JsonObject command && command["command"]?.ToString() is { Length: > 0 } name && ((ILanguageServerSession)this).SupportsCommand(name))
        {
            var captured = commandEdits = new();
            try
            {
                var arguments = new JsonObject { ["command"] = name };
                if (command["arguments"] is { } values) arguments["arguments"] = values.DeepClone();
                await connection.RequestAsync("workspace/executeCommand", arguments, token);
                foreach (var change in captured)
                    if (Collect(change, uri, document, edits) is { } failed) return result with { State = failed.State, Detail = failed.Detail };
            }
            finally { commandEdits = null; }
        }
        return edits.Count == 0
            ? result with { State = "unsupported", Detail = $"{server.Id} offered no text change for this action." }
            : result with { Edits = edits, Freshness = "current" };
    }

    private static bool Covers(LanguageItem item, int line, int column)
        => (item.Line, item.Column).CompareTo((line, column)) <= 0 && (line, column).CompareTo((item.EndLine, item.EndColumn)) <= 0;

    // Gathers a WorkspaceEdit's text edits; file creation, renaming and deletion, edits outside the workspace and edits for another document version are refused whole.
    private (string State, string Detail)? Collect(JsonNode edit, string uri, Document document, List<LanguageItem> into)
    {
        var added = new List<LanguageItem>();
        (string State, string Detail)? Add(string target, JsonNode? list)
        {
            if (Relative(target) is not { } path) return ("unsupported", "The action edits a file outside the workspace.");
            foreach (var change in list as JsonArray ?? [])
            {
                if (change?["range"] is not { } range || change["newText"] is not JsonValue text) return ("unsupported", "The action uses an edit form the editor does not apply.");
                added.Add(Item(path, range, text.ToString(), "", server.Id));
            }
            return null;
        }
        if (edit["documentChanges"] is JsonArray changes)
            foreach (var change in changes)
            {
                if (change is null) continue;
                if (change["kind"] is not null) return ("unsupported", "The action creates, renames or deletes files, which the editor does not apply yet.");
                var target = change["textDocument"]?["uri"]?.ToString();
                if (target is null) return ("unsupported", "The action uses an edit form the editor does not apply.");
                if (target == uri && change["textDocument"]!["version"] is { } version && version.GetValue<int>() != document.Version)
                    return ("stale", "The quick fixes belong to an older version of the document.");
                if (Add(target, change["edits"]) is { } refusal) return refusal;
            }
        else if (edit["changes"] is JsonObject map)
            foreach (var (target, list) in map)
                if (Add(target, list) is { } refusal) return refusal;
        into.AddRange(added);
        return null;
    }
}