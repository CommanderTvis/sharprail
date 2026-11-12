using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

using Tereditor.Host.Abstractions;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Host.Core.Language;

/// <summary>What an agent does with code actions, rename and formatting: list, prepare (preview) and commit. Writing happens only in commit.</summary>
internal sealed partial class LanguageSession
{
    private const int MaxListings = 16;
    private const int MaxPlans = 16;

    private sealed record Listing(string Uri, string TextHash, JsonNode[] Actions);

    private readonly Dictionary<string, Listing> listings = [];
    private readonly Dictionary<string, EditPlan> plans = [];

    private static bool IsAgentRefactoring(string operation) => operation is "agentCodeActions" or "agentPrepare" or "agentCommit" or "agentRename" or "agentFormat";

    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private WorkspaceEdits.Open? OpenState(string uri) => documents.TryGetValue(uri, out var document) ? new(document.Version, document.Dirty) : null;

    private async Task<LanguageResult> AgentRefactoringAsync(LanguageResult result, LanguageRequest request, string uri, Document document, CancellationToken token)
    {
        try
        {
            return request.Operation switch
            {
                "agentCodeActions" => await AgentListAsync(result, request, uri, document, token),
                "agentPrepare" => await AgentPrepareAsync(result, request, uri, document, token),
                "agentRename" => await AgentRenameAsync(result, request, uri, document, token),
                "agentFormat" => await AgentFormatAsync(result, request, uri, document, token),
                _ => await AgentCommitAsync(result, request, token)
            };
        }
        catch (EditRefused refused) { return result with { State = refused.State, Detail = refused.Message }; }
        catch (IOException error) when (!connection.Exited) { return result with { State = "error", Detail = $"{server.Id} refused the request: {error.Message}", Freshness = "unavailable" }; }
    }

    private async Task<LanguageItem[]> CurrentDiagnosticsAsync(string uri, Document document, string path, CancellationToken token)
    {
        if (diagnostics.TryGetValue(uri, out var published) && (published.Version is null || published.Version == document.Version)) return published.Items;
        if (capabilities["diagnosticProvider"] is not JsonObject provider) return [];
        var report = await connection.RequestAsync("textDocument/diagnostic", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["identifier"] = provider["identifier"]?.ToString() }, token);
        if (report?["kind"]?.ToString() != "full") return [];
        var items = Diagnostics(report["items"], path);
        diagnostics[uri] = new(document.Version, items);
        return items;
    }

    private static bool Overlaps(LanguageItem item, int line, int column, int endLine, int endColumn)
        => (item.Line, item.Column).CompareTo((endLine, endColumn)) <= 0 && (line, column).CompareTo((item.EndLine, item.EndColumn)) <= 0;

    private async Task<LanguageResult> AgentListAsync(LanguageResult result, LanguageRequest request, string uri, Document document, CancellationToken token)
    {
        if (!Provides("codeActionProvider", "textDocument/codeAction"))
            return result with { State = "unsupported", Detail = $"{server.Id} does not support code actions." };
        var items = await CurrentDiagnosticsAsync(uri, document, request.Path, token);
        var (line, column, endLine, endColumn) = (request.Line, request.Column, request.EndLine < 0 ? request.Line : request.EndLine, request.EndColumn < 0 ? request.Column : request.EndColumn);
        LanguageItem[] context;
        if (request.Query.Length > 0)
        {
            var matches = items.Where(item => item.Raw.Length > 0 && (JsonNode.Parse(item.Raw)?["code"]?.ToString() == request.Query
                || item.Text.Contains(request.Query, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (matches.Length != 1)
                return result with
                {
                    State = matches.Length == 0 ? "notFound" : "ambiguous",
                    Detail = matches.Length == 0 ? $"{request.Path} has no diagnostic with code or message \"{request.Query}\". Run lsp_diagnostics for it."
                        : $"{matches.Length} diagnostics match \"{request.Query}\". Repeat with the line and column of one.",
                    Locations = matches.Take(request.Limit).ToArray(),
                    Total = matches.Length,
                    Freshness = "current"
                };
            context = matches;
            (line, column, endLine, endColumn) = (matches[0].Line, matches[0].Column, matches[0].EndLine, matches[0].EndColumn);
        }
        else if (line == 0 && column == 0 && request.EndLine < 0)
        {
            (endLine, endColumn) = (document.Text.Count(character => character == '\n'), document.Text.Length - (document.Text.LastIndexOf('\n') + 1));
            context = items;
        }
        else context = items.Where(item => Overlaps(item, line, column, endLine, endColumn)).ToArray();

        var kinds = request.Kinds.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries);
        var parameters = new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["range"] = new JsonObject { ["start"] = Position(line, column), ["end"] = Position(endLine, endColumn) },
            ["context"] = new JsonObject { ["diagnostics"] = new JsonArray(context.Where(item => item.Raw.Length > 0).Select(item => JsonNode.Parse(item.Raw)!).ToArray()), ["triggerKind"] = 1 }
        };
        if (kinds.Length > 0) parameters["context"]!["only"] = new JsonArray(kinds.Select(kind => (JsonNode?)JsonValue.Create(kind)).ToArray());
        var response = await connection.RequestAsync("textDocument/codeAction", parameters, token);

        var usable = new List<(JsonNode Action, bool Preferred, bool Quickfix)>();
        foreach (var entry in response as JsonArray ?? [])
        {
            if (entry is not JsonObject action || action["disabled"] is not null || (action["title"]?.ToString() ?? "").Length == 0) continue;
            if (action["command"] is JsonValue) action = new JsonObject { ["title"] = action["title"]!.DeepClone(), ["command"] = action.DeepClone() };
            var command = action["command"]?["command"]?.ToString();
            if (action["edit"] is null && !(command is null ? ResolvesActions : ((ILanguageServerSession)this).SupportsCommand(command))) continue;
            var kind = action["kind"]?.ToString() ?? "";
            if (kinds.Length > 0 && !kinds.Any(wanted => kind == wanted || kind.StartsWith(wanted + ".", StringComparison.Ordinal))) continue;
            usable.Add((action, action["isPreferred"]?.GetValue<bool>() == true, kind == "quickfix" || kind.StartsWith("quickfix.", StringComparison.Ordinal)));
        }
        var ordered = usable.OrderByDescending(item => item.Preferred).ThenByDescending(item => item.Quickfix).Take(MaxCodeActions).ToArray();
        listings[request.Id] = new(uri, Hash(document.Text), ordered.Select(item => item.Action).ToArray());
        while (listings.Count > MaxListings) listings.Remove(listings.Keys.First());
        return result with
        {
            AgentActions = ordered.Select((item, index) => new LanguageAction($"{request.Id}.{index}", item.Action["title"]!.ToString(), item.Action["kind"]?.ToString() ?? "", item.Preferred,
                item.Action["edit"] is not null ? "edit" : item.Action["command"] is not null ? "command" : "resolve",
                item.Action["edit"] is { } edit ? EditFiles(edit) : [])).ToArray(),
            Total = ordered.Length,
            Freshness = "current"
        };
    }

    private string[] EditFiles(JsonNode edit)
    {
        var uris = new List<string>();
        foreach (var change in edit["documentChanges"] as JsonArray ?? [])
        {
            if (change is null) continue;
            switch (change["kind"]?.ToString())
            {
                case null: uris.Add(change["textDocument"]?["uri"]?.ToString() ?? ""); break;
                case "rename": uris.Add(change["oldUri"]?.ToString() ?? ""); uris.Add(change["newUri"]?.ToString() ?? ""); break;
                default: uris.Add(change["uri"]?.ToString() ?? ""); break;
            }
        }
        foreach (var (uri, _) in edit["changes"] as JsonObject ?? []) uris.Add(uri);
        return [.. uris.Where(uri => uri.Length > 0).Select(uri => Relative(uri) ?? uri).Distinct()];
    }

    private async Task<LanguageResult> AgentPrepareAsync(LanguageResult result, LanguageRequest request, string uri, Document document, CancellationToken token)
    {
        var dot = request.Id.LastIndexOf('.');
        if (dot < 0 || !listings.TryGetValue(request.Id[..dot], out var listing) || !int.TryParse(request.Id[(dot + 1)..], out var index) || index < 0 || index >= listing.Actions.Length)
            return result with { State = "notFound", Detail = "Unknown or expired action id. List the actions again." };
        if (listing.Uri != uri || listing.TextHash != Hash(document.Text))
            return result with { State = "stale", Detail = "The file changed since the actions were listed. List them again." };
        var action = listing.Actions[index].DeepClone();
        if (action["edit"] is null && ResolvesActions && await connection.RequestAsync("codeAction/resolve", action, token) is JsonObject resolved) action = resolved;
        var edits = new List<JsonNode>();
        if (action["edit"] is { } edit) edits.Add(edit);
        if (action["command"] is JsonObject command && command["command"]?.ToString() is { Length: > 0 } name && ((ILanguageServerSession)this).SupportsCommand(name))
        {
            var captured = commandEdits = new();
            try
            {
                var arguments = new JsonObject { ["command"] = name };
                if (command["arguments"] is { } values) arguments["arguments"] = values.DeepClone();
                await connection.RequestAsync("workspace/executeCommand", arguments, token);
                edits.AddRange(captured);
            }
            finally { commandEdits = null; }
        }
        return Plan(result, request.Id, action["title"]?.ToString() ?? "", edits);
    }

    private LanguageResult Plan(LanguageResult result, string id, string title, List<JsonNode> edits)
    {
        if (edits.Count == 0) return result with { State = "unsupported", Detail = $"{server.Id} offered no text change for this." };
        var plan = WorkspaceEdits.Build(workspace, title, edits, Relative, OpenState);
        var changes = plan.Changes();
        if (changes.Count == 0) return result with { State = "notApplicable", Detail = "The edit changes nothing.", Freshness = "current" };
        plans[id] = plan;
        while (plans.Count > MaxPlans) plans.Remove(plans.Keys.First());
        return result with { Plan = id, Changes = changes, Detail = title, Freshness = "current" };
    }

    private async Task<LanguageResult> AgentRenameAsync(LanguageResult result, LanguageRequest request, string uri, Document document, CancellationToken token)
    {
        if (!Provides("renameProvider", "textDocument/rename")) return result with { State = "unsupported", Detail = $"{server.Id} does not support rename." };
        var name = request.NewName;
        if (name.Length is 0 or > 200 || name.Any(char.IsControl) || name != name.Trim())
            return result with { State = "notApplicable", Detail = "Give a new name of 1 to 200 characters without control characters or surrounding spaces." };
        var position = new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = Position(request.Line, request.Column) };
        var title = $"Rename to {name}";
        var note = "";
        if (capabilities["renameProvider"] is JsonObject provider && provider["prepareProvider"]?.GetValue<bool>() == true)
        {
            var prepared = await connection.RequestAsync("textDocument/prepareRename", position.DeepClone(), token);
            if (prepared is null || prepared["defaultBehavior"]?.GetValue<bool>() == false)
                return result with { State = "notApplicable", Detail = "The server cannot rename the element at this position." };
            var range = prepared["range"] ?? (prepared["start"] is not null ? prepared : null);
            if ((prepared["placeholder"]?.ToString() ?? (range is null ? null : SliceOf(document.Text, range))) is { } current) title = $"Rename {current} to {name}";
        }
        else note = " The server offers no prepareRename, so the position was not validated first.";
        position["newName"] = name;
        var edit = await connection.RequestAsync("textDocument/rename", position, token);
        if (edit is null) return result with { State = "notApplicable", Detail = "The server returned no edit for the rename." + note };
        var planned = Plan(result, request.Id, title, [edit]);
        return planned.State == "ready" ? planned with { Detail = planned.Detail + note } : planned;
    }

    private static string? SliceOf(string text, JsonNode range)
    {
        var lines = text.Split('\n');
        int Of(JsonNode point)
        {
            var line = point["line"]!.GetValue<int>();
            return line < lines.Length ? lines.Take(line).Sum(value => value.Length + 1) + Math.Min(point["character"]!.GetValue<int>(), lines[line].Length) : text.Length;
        }
        int start = Of(range["start"]!), end = Of(range["end"]!);
        return end >= start && end - start <= 200 ? text[start..end].TrimEnd('\r') : null;
    }

    private async Task<LanguageResult> AgentFormatAsync(LanguageResult result, LanguageRequest request, string uri, Document document, CancellationToken token)
    {
        if (!Provides("documentFormattingProvider", "textDocument/formatting")) return result with { State = "unsupported", Detail = $"{server.Id} does not support formatting." };
        var edits = await connection.RequestAsync("textDocument/formatting", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = uri },
            ["options"] = new JsonObject { ["tabSize"] = 4, ["insertSpaces"] = true }
        }, token);
        if (edits is not JsonArray { Count: > 0 } list) return result with { State = "notApplicable", Detail = "The file is already formatted.", Freshness = "current" };
        return Plan(result, request.Id, "Format " + request.Path, [new JsonObject { ["changes"] = new JsonObject { [uri] = list.DeepClone() } }]);
    }

    private async Task<LanguageResult> AgentCommitAsync(LanguageResult result, LanguageRequest request, CancellationToken token)
    {
        if (!plans.Remove(request.Id, out var plan)) return result with { State = "stale", Detail = "The prepared edit is gone. Prepare it again." };
        foreach (var file in plan.Files.Where(file => file.Changed))
            if (OpenState(UriOf(file.Full)) is { Dirty: true }) throw new EditRefused("conflict", $"{file.Rel} has unsaved changes in an editor. Save or close it first.");
        WorkspaceEdits.Commit(plan);
        foreach (var file in plan.Files.Where(file => file.Changed))
        {
            var uri = UriOf(file.Full);
            if (!documents.TryGetValue(uri, out var open)) continue;
            if (file.Text is null)
            {
                await connection.NotifyAsync("textDocument/didClose", TextDocument(uri), token);
                documents.Remove(uri); diagnostics.TryRemove(uri, out _);
            }
            else await RefreshOpenAsync(uri, open, token);
        }
        HasEditors = documents.Values.Any(value => value.Owner.Length > 0);
        return result with { Applied = true, Plan = request.Id, Changes = plan.Changes(), Detail = plan.Title, Freshness = "current" };
    }
}