using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Tereditor.Host.Abstractions;

namespace Tereditor.Host.Core.Language;

/// <summary>One file or directory an agent renames: its URIs, its absolute old path and whether it is a directory.</summary>
internal sealed record RenamePair(string OldUri, string NewUri, string OldFull, bool Directory);

/// <summary>What a server said to workspace/willRenameFiles: whether it takes part for these pairs and the edit it asked for.</summary>
internal sealed record WillRename(bool Took, JsonNode? Edit);

/// <summary>Read-only LSP queries beyond plain navigation: call hierarchy, the capability dump, raw requests, the workspace diagnostic report, and the file-rename hooks.</summary>
internal sealed partial class LanguageSession
{
    private const int MaxRawJson = 64 * 1024;

    // The queries a raw request may send, with the capability that must be declared. Nothing here changes a file or runs a command.
    private static readonly Dictionary<string, string> RawMethods = new(StringComparer.Ordinal)
    {
        ["textDocument/hover"] = "hoverProvider",
        ["textDocument/definition"] = "definitionProvider",
        ["textDocument/declaration"] = "declarationProvider",
        ["textDocument/typeDefinition"] = "typeDefinitionProvider",
        ["textDocument/implementation"] = "implementationProvider",
        ["textDocument/references"] = "referencesProvider",
        ["textDocument/documentSymbol"] = "documentSymbolProvider",
        ["textDocument/documentHighlight"] = "documentHighlightProvider",
        ["textDocument/documentLink"] = "documentLinkProvider",
        ["textDocument/foldingRange"] = "foldingRangeProvider",
        ["textDocument/selectionRange"] = "selectionRangeProvider",
        ["textDocument/signatureHelp"] = "signatureHelpProvider",
        ["textDocument/completion"] = "completionProvider",
        ["textDocument/inlayHint"] = "inlayHintProvider",
        ["textDocument/codeLens"] = "codeLensProvider",
        ["textDocument/documentColor"] = "colorProvider",
        ["textDocument/semanticTokens/full"] = "semanticTokensProvider",
        ["textDocument/semanticTokens/range"] = "semanticTokensProvider",
        ["textDocument/prepareCallHierarchy"] = "callHierarchyProvider",
        ["textDocument/prepareTypeHierarchy"] = "typeHierarchyProvider",
        ["textDocument/prepareRename"] = "renameProvider",
        ["textDocument/linkedEditingRange"] = "linkedEditingRangeProvider",
        ["textDocument/moniker"] = "monikerProvider",
        ["textDocument/diagnostic"] = "diagnosticProvider",
        ["workspace/symbol"] = "workspaceSymbolProvider",
        ["workspace/diagnostic"] = "diagnosticProvider",
        ["callHierarchy/incomingCalls"] = "callHierarchyProvider",
        ["callHierarchy/outgoingCalls"] = "callHierarchyProvider",
        ["typeHierarchy/supertypes"] = "typeHierarchyProvider",
        ["typeHierarchy/subtypes"] = "typeHierarchyProvider",
        ["completionItem/resolve"] = "completionProvider",
        ["inlayHint/resolve"] = "inlayHintProvider",
        ["codeLens/resolve"] = "codeLensProvider",
        ["documentLink/resolve"] = "documentLinkProvider",
        ["workspaceSymbol/resolve"] = "workspaceSymbolProvider"
    };

    // Raw methods whose parameters carry a cursor position next to the document.
    private static readonly HashSet<string> PositionMethods =
    [
        "textDocument/hover", "textDocument/definition", "textDocument/declaration", "textDocument/typeDefinition", "textDocument/implementation", "textDocument/references",
        "textDocument/documentHighlight", "textDocument/signatureHelp", "textDocument/completion", "textDocument/prepareCallHierarchy", "textDocument/prepareTypeHierarchy",
        "textDocument/prepareRename", "textDocument/linkedEditingRange", "textDocument/moniker"
    ];

    // What a raw request refuses by name, and the tool that does it with a preview and the write approval.
    private static readonly (string Match, string Why)[] RawRefusals =
    [
        ("workspace/executeCommand", "runs a server command that can change files. Code actions run their commands only inside lsp_code_actions operation=apply."),
        ("workspace/applyEdit", "applies an edit. Edits are written only by lsp_code_actions, lsp_rename and lsp_format."),
        ("textDocument/rename", "writes a rename. Use lsp_rename (preview first, then apply)."),
        ("textDocument/formatting", "writes formatting. Use lsp_format (preview first, then apply)."),
        ("textDocument/rangeFormatting", "writes formatting. Use lsp_format (preview first, then apply)."),
        ("textDocument/onTypeFormatting", "writes formatting. Use lsp_format (preview first, then apply)."),
        ("textDocument/codeAction", "lists actions. Use lsp_code_actions operation=list."),
        ("codeAction/resolve", "computes an action's edit. Use lsp_code_actions operation=preview."),
        ("textDocument/willSaveWaitUntil", "asks for edits to apply on save. Use lsp_format."),
        ("workspace/willRenameFiles", "asks for the edits of a file move. Use lsp_rename with path and newPath."),
        ("workspace/willCreateFiles", "asks for the edits of a file creation, which no tool applies."),
        ("workspace/willDeleteFiles", "asks for the edits of a file deletion, which no tool applies."),
        ("textDocument/didOpen", "is a notification that changes the server's document state."),
        ("textDocument/didChange", "is a notification that changes the server's document state."),
        ("textDocument/didClose", "is a notification that changes the server's document state."),
        ("textDocument/didSave", "is a notification that changes the server's document state."),
                ("workspace/did", "is a notification that changes the server's state."),
        ("initialize", "is lifecycle. Use lsp_status operation=retry or restart."),
        ("shutdown", "is lifecycle. Use lsp_status operation=stop."),
        ("exit", "is lifecycle. Use lsp_status operation=stop."),
        ("$/", "is protocol plumbing, not a query.")
    ];

    /// <summary>Why a raw request for <paramref name="method"/> is refused before any server is asked, or null when it is a permitted query.</summary>
    internal static string? RawRefusal(string method)
    {
        if (method.Length == 0) return "Name the LSP method to send, such as textDocument/documentHighlight.";
        foreach (var (match, why) in RawRefusals)
            if (method == match || match is "workspace/did" or "$/" && method.StartsWith(match, StringComparison.Ordinal))
                return $"{method} {why}";
        if (RawMethods.ContainsKey(method)) return null;
        return $"{method} is not a read-only query tereditor forwards. Allowed: {string.Join(", ", RawMethods.Keys.Order(StringComparer.Ordinal))}.";
    }

    private bool Declares(string provider) => capabilities[provider] is { } value && value.ToJsonString() != "false";

    private LanguageResult CapabilitiesResult(LanguageRequest request)
    {
        var document = new JsonObject
        {
            ["serverCapabilities"] = capabilities.DeepClone(),
            ["registeredDynamically"] = new JsonArray(registeredCapabilities.Values.Distinct().Order(StringComparer.Ordinal).Select(method => (JsonNode?)JsonValue.Create(method)).ToArray())
        };
        return new("ready", $"{server.Id}: the capabilities it declared in initialize, and the methods it registered later.", server.Id, request.Version) { Json = document.ToJsonString(), Freshness = "current" };
    }

    /// <summary>The call tree of the symbol at the position: its callers or callees, to <see cref="LanguageRequest.Depth"/> levels, with the per-node and total caps.</summary>
    private async Task<LanguageResult> CallsAsync(LanguageResult result, LanguageRequest request, JsonObject position, CancellationToken token)
    {
        var incoming = request.Operation == "incomingCalls";
        var depth = Math.Clamp(request.Depth, 1, 10);
        var maxChildren = Math.Clamp(request.MaxChildren, 1, 100);
        var maxNodes = Math.Clamp(request.MaxNodes, 1, 500);
        if (await connection.RequestAsync("textDocument/prepareCallHierarchy", position, token) is not JsonArray { Count: > 0 } roots)
            return result with { State = "notFound", Detail = $"No call hierarchy item at line {request.Line}, column {request.Column} (zero-based). Name the function or method as query, or point at its name.", Freshness = "current" };
        var nodes = new List<LanguageItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var trimmed = false; var capped = false; var listed = 0;
        foreach (var root in roots)
        {
            if (root is null) continue;
            seen.Add(Key(root));
            nodes.Add(CallItem(root, 0, ""));
            await Walk(root, 1);
            if (capped) break;
        }
        var name = roots[0]?["name"]?.ToString() ?? request.Query;
        var detail = $"{(incoming ? "Callers" : "Callees")} of {name}, {depth} level{(depth == 1 ? "" : "s")} deep, at most {maxChildren} per node and {maxNodes} nodes; depth 0 is the symbol itself, via names the node it was reached from."
            + (trimmed ? " Some nodes had more entries than maxChildren; raise it to see them." : "") + (capped ? " Stopped at maxNodes; raise it or lower depth." : "")
            + (listed == 0 ? $" Nothing {(incoming ? "calls" : "is called by")} it." : "");
        var page = nodes.Skip(request.Offset).Take(request.Limit).ToArray();
        return result with
        {
            Detail = detail,
            Locations = page,
            Total = nodes.Count,
            Truncated = trimmed || capped || request.Offset + page.Length < nodes.Count,
            Freshness = IsIndexing || projectModel.Truncated || trimmed || capped ? "partial" : "current"
        };

        async Task Walk(JsonNode parent, int level)
        {
            token.ThrowIfCancellationRequested();
            if (await connection.RequestAsync(incoming ? "callHierarchy/incomingCalls" : "callHierarchy/outgoingCalls", new JsonObject { ["item"] = parent.DeepClone() }, token) is not JsonArray calls) return;
            var children = 0;
            foreach (var call in calls)
            {
                if (call?[incoming ? "from" : "to"] is not { } node) continue;
                if (children >= maxChildren) { trimmed = true; break; }
                if (listed >= maxNodes) { capped = true; return; }
                children++; listed++;
                var repeated = !seen.Add(Key(node));
                var item = CallItem(node, level, parent["name"]?.ToString() ?? "");
                nodes.Add(repeated ? item with { Text = item.Text + " (listed above)" } : item);
                if (!repeated && level < depth)
                {
                    await Walk(node, level + 1);
                    if (capped) return;
                }
            }
        }

        static string Key(JsonNode node) => node["uri"] + "|" + node["selectionRange"]?["start"]?["line"] + ":" + node["selectionRange"]?["start"]?["character"] + "|" + node["name"];

        LanguageItem CallItem(JsonNode node, int level, string via)
        {
            var uri = node["uri"]?.ToString() ?? "";
            var path = Relative(uri) ?? (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri);
            var range = node["selectionRange"] ?? node["range"] ?? new JsonObject { ["start"] = Position(0, 0), ["end"] = Position(0, 0) };
            return Item(path, range, node["name"]?.ToString() ?? "", node["kind"]?.ToString() ?? "", server.Id) with { Depth = level, Via = via, Container = node["detail"]?.ToString() ?? "" };
        }
    }

    /// <summary>A permitted query, sent as given; the document comes from <paramref name="uri"/> (null for a workspace-level query).</summary>
    private async Task<LanguageResult> RawAsync(LanguageResult result, LanguageRequest request, string? uri, CancellationToken token)
    {
        var method = request.Method;
        if (RawRefusal(method) is { } refusal) return result with { State = "refused", Detail = refusal, Freshness = "current" };
        if (!Declares(RawMethods[method]) && !registeredCapabilities.Values.Contains(method))
            return result with { State = "unsupported", Detail = $"{server.Id} does not declare {RawMethods[method]}, so it does not support {method}. lsp_status operation=capabilities lists what it declares." };
        JsonObject parameters;
        try { parameters = request.Payload is null ? [] : JsonNode.Parse(request.Payload) as JsonObject ?? throw new ArgumentException("payload must be a JSON object."); }
        catch (System.Text.Json.JsonException) { throw new ArgumentException("payload must be valid JSON."); }
        if (parameters.ContainsKey("textDocument") || parameters.ContainsKey("uri"))
            throw new ArgumentException("payload must not carry textDocument or uri; pass path and the host fills them in.");
        if (uri is not null && method.StartsWith("textDocument/", StringComparison.Ordinal))
        {
            parameters["textDocument"] = new JsonObject { ["uri"] = uri };
            if (PositionMethods.Contains(method) && !parameters.ContainsKey("position")) parameters["position"] = Position(request.Line, request.Column);
            if (method == "textDocument/references" && !parameters.ContainsKey("context")) parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
        }
        else if (uri is null && method.StartsWith("textDocument/", StringComparison.Ordinal))
            throw new ArgumentException($"{method} needs a document: pass path.");
        if (method == "workspace/symbol" && !parameters.ContainsKey("query")) parameters["query"] = request.Query;
        if (method == "workspace/diagnostic" && !parameters.ContainsKey("previousResultIds")) parameters["previousResultIds"] = new JsonArray();
        var response = await connection.RequestAsync(method, parameters, token);
        return result with { Json = response?.ToJsonString() ?? "null", Detail = $"{method} answered by {server.Id} (read-only).", Freshness = IsIndexing ? "partial" : "current" };
    }

    /// <summary>The server's workspace/diagnostic report as diagnostics per workspace-relative file, or null when it declares no workspace diagnostics or is still loading.</summary>
    internal async Task<Dictionary<string, LanguageItem[]>?> WorkspaceReportAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (connection.Exited || NeedsConfiguration || IsIndexing || capabilities["diagnosticProvider"] is not JsonObject provider || provider["workspaceDiagnostics"]?.GetValue<bool>() != true) return null;
            var report = await connection.RequestAsync("workspace/diagnostic", new JsonObject { ["identifier"] = provider["identifier"]?.ToString(), ["previousResultIds"] = new JsonArray() }, token, TimeSpan.FromSeconds(60));
            var files = new Dictionary<string, LanguageItem[]>(StringComparer.Ordinal);
            foreach (var file in report?["items"] as JsonArray ?? [])
                if (file?["uri"]?.ToString() is { } reportUri && file["kind"]?.ToString() == "full" && Relative(reportUri) is { } path && !documents.ContainsKey(reportUri))
                {
                    var items = Diagnostics(file["items"], path);
                    diagnostics[reportUri] = new(file["version"]?.GetValue<int>(), items);
                    files[path] = items;
                }
            return files;
        }
        finally { gate.Release(); }
    }

    /// <summary>Which of <paramref name="pairs"/> a file-operation registration (the static capability's filters) covers.</summary>
    private RenamePair[] Covered(string operation, IReadOnlyList<RenamePair> pairs)
    {
        if (capabilities["workspace"]?["fileOperations"]?[operation] is not JsonObject declared)
            return registeredCapabilities.Values.Contains($"workspace/{operation}Files") ? [.. pairs] : [];
        if (declared["filters"] is not JsonArray { Count: > 0 } filters) return [.. pairs];
        return [.. pairs.Where(pair => filters.Any(filter => Matches(filter, pair)))];
    }

    private static bool Matches(JsonNode? filter, RenamePair pair)
    {
        if ((filter?["scheme"]?.ToString() ?? "file") != "file") return false;
        var kind = filter?["pattern"]?["matches"]?.ToString();
        if (kind == "file" && pair.Directory || kind == "folder" && !pair.Directory) return false;
        if (filter?["pattern"]?["glob"]?.ToString() is not { } glob) return false;
        var options = filter["pattern"]?["options"]?["ignoreCase"]?.GetValue<bool>() == true ? RegexOptions.IgnoreCase : RegexOptions.None;
        return new Regex(Glob(glob).ToString(), options).IsMatch(pair.OldFull.Replace('\\', '/'));
    }

    /// <summary>Asks the server for the edits a move needs; <c>Took</c> is false when it declared no interest in these paths or has no such method.</summary>
    internal async Task<WillRename> WillRenameAsync(IReadOnlyList<RenamePair> pairs, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var covered = Covered("willRename", pairs);
            if (connection.Exited || covered.Length == 0) return new(false, null);
            var files = new JsonArray(covered.Select(pair => (JsonNode?)new JsonObject { ["oldUri"] = pair.OldUri, ["newUri"] = pair.NewUri }).ToArray());
            try { return new(true, await connection.RequestAsync("workspace/willRenameFiles", new JsonObject { ["files"] = files }, token, TimeSpan.FromSeconds(30))); }
            catch (LanguageRequestException error) when (error.Code == LanguageRequestException.MethodNotFound) { return new(false, null); }
        }
        finally { gate.Release(); }
    }

    /// <summary>The paths of this session's open documents that an editor holds unsaved, at or under <paramref name="full"/>.</summary>
    internal async Task<string[]> UnsavedUnderAsync(string full, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            var prefix = Path.TrimEndingDirectorySeparator(full) + Path.DirectorySeparatorChar;
            return [.. documents.Where(pair => pair.Value.Dirty && Uri.TryCreate(pair.Key, UriKind.Absolute, out var parsed) && parsed.IsFile &&
                (parsed.LocalPath == full || parsed.LocalPath.StartsWith(prefix, StringComparison.Ordinal))).Select(pair => Relative(pair.Key) ?? pair.Key)];
        }
        finally { gate.Release(); }
    }

    /// <summary>What this session's open documents look like to an edit being planned, by URI.</summary>
    internal async Task<Dictionary<string, WorkspaceEdits.Open>> OpenSnapshotAsync(CancellationToken token)
    {
        await gate.WaitAsync(token);
        try { return documents.ToDictionary(pair => pair.Key, pair => new WorkspaceEdits.Open(pair.Value.Version, pair.Value.Dirty)); }
        finally { gate.Release(); }
    }

    /// <summary>Tells the server a move happened: closes documents that were at the old paths, refreshes edited open ones, and sends didRenameFiles where it asked for it.</summary>
    internal async Task AfterRenameAsync(IReadOnlyList<RenamePair> pairs, IEnumerable<string> editedFulls, CancellationToken token)
    {
        await gate.WaitAsync(token);
        try
        {
            if (connection.Exited) return;
            foreach (var pair in pairs)
            {
                var prefix = Path.TrimEndingDirectorySeparator(pair.OldFull) + Path.DirectorySeparatorChar;
                foreach (var uri in documents.Keys.ToArray())
                {
                    if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) continue;
                    if (parsed.LocalPath != pair.OldFull && !(pair.Directory && parsed.LocalPath.StartsWith(prefix, StringComparison.Ordinal))) continue;
                    await connection.NotifyAsync("textDocument/didClose", TextDocument(uri), token);
                    documents.Remove(uri); diagnostics.TryRemove(uri, out _);
                }
            }
            foreach (var full in editedFulls)
                if (documents.TryGetValue(UriOf(full), out var open)) await RefreshOpenAsync(UriOf(full), open, token);
            var told = Covered("didRename", pairs);
            if (told.Length > 0)
                await connection.NotifyAsync("workspace/didRenameFiles", new JsonObject { ["files"] = new JsonArray(told.Select(pair => (JsonNode?)new JsonObject { ["oldUri"] = pair.OldUri, ["newUri"] = pair.NewUri }).ToArray()) }, token);
            HasDrafts = documents.Values.Any(value => value.Dirty); HasEditors = documents.Values.Any(value => value.Owner.Length > 0);
        }
        finally { gate.Release(); }
    }
}