using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

/// <summary>The MCP tools that change code through a language server: code actions, rename and format. See Refactoring.SPEC.md.</summary>
internal static class LanguageRefactoringTools
{
    private static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static IReadOnlyList<McpServer.McpTool> Create(HostStateStore state, string callingWorkspace) =>
    [
        Tool(state, callingWorkspace, "lsp_code_actions", "codeActions", ["list", "preview", "apply"], "list",
            "Language-server quick fixes and refactorings (add a missing using, remove unused code, extract, organize imports). Prefer them over hand-editing for semantic changes. operation=list (default) returns actions with an id for a file and range or a diagnostic: {\"path\":\"src/Foo.cs\",\"line\":12,\"column\":8} (zero-based UTF-16; omit line and column for the whole file), or {\"path\":\"src/Foo.cs\",\"query\":\"CS0246\"} to pick a diagnostic by code or message. kinds limits them, e.g. quickfix or source.organizeImports. carries says edit, command or resolve (the edit is computed on preview). operation=preview with an id shows the files and edits and writes nothing. operation=apply with an id writes them to disk and reports the changed files and the diagnostics before and after. An id is refused when the file changed since the listing; list again. Edits outside the workspace, onto an editor's unsaved buffer or from a server without the capability are refused, never emulated. Starts the server on demand. Pass the absolute workspace, also in subagents.", Properties.Actions),
        Tool(state, callingWorkspace, "lsp_rename", "rename", ["preview", "apply"], "preview",
            "Semantic rename through the language server: updates every reference, including other files. Prefer it over search and replace. Name the symbol, {\"query\":\"OldName\",\"newName\":\"NewName\"}, or give path with zero-based line and column. operation=preview (default) lists the files and edits and writes nothing, so check that the file list is what you expect, then repeat with operation=apply to write to disk. The result lists the changed files and the diagnostics before and after. A server without rename reports unsupported. To move or rename a file or directory pass path and newPath instead: every running server that asked for the move (workspace/willRenameFiles) updates imports and references, the path is moved on disk, and the edits are rolled back if the move fails; preview first, refused for paths outside the workspace, unsaved editor buffers, an existing destination or more than 1000 files. Pass the absolute workspace, also in subagents.", Properties.Rename),
        Tool(state, callingWorkspace, "lsp_format", "format", ["preview", "apply"], "preview",
            "Formats one file with the language server's formatter. operation=preview (default) shows the edits and writes nothing, operation=apply writes the file. A server without a formatter reports unsupported. Pass the absolute workspace, also in subagents.", Properties.Format)
    ];

    private static McpServer.McpTool Tool(HostStateStore state, string callingWorkspace, string name, string tool, string[] operations, string defaultOperation, string description, string[] allowed)
    {
        var schema = JsonNode.Parse("""
            {"type":"object","properties":{
              "workspace":{"type":"string","description":"Absolute workspace directory; always pass this after changing worktrees and in subagents."},
              "operation":{"type":"string"},
              "path":{"type":"string","description":"Workspace-relative file."},
              "line":{"type":"integer","minimum":0,"description":"Zero-based line."},
              "column":{"type":"integer","minimum":0,"description":"Zero-based UTF-16 column."},
              "endLine":{"type":"integer","minimum":0,"description":"Zero-based end line of the range; defaults to line."},
              "endColumn":{"type":"integer","minimum":0,"description":"Zero-based end column of the range; defaults to column."},
              "query":{"type":"string","description":"lsp_code_actions: a diagnostic code or message text in path. lsp_rename: the symbol name, as in lsp_navigate."},
              "kinds":{"type":"string","description":"LSP code action kinds to list, space separated, such as quickfix or refactor.extract or source.organizeImports."},
              "id":{"type":"string","description":"The action id from operation=list; required by preview and apply."},
              "newName":{"type":"string","description":"The new name."},
              "newPath":{"type":"string","description":"lsp_rename: the new workspace-relative path of path, to rename or move a file or directory (no line, column, query or newName)."},
              "limit":{"type":"integer","minimum":1,"maximum":200,"default":50,"description":"Maximum ambiguous candidates listed."}
            },"additionalProperties":false}
            """)!.AsObject();
        var properties = schema["properties"]!.AsObject();
        foreach (var key in properties.Select(pair => pair.Key).Except(allowed).ToArray()) properties.Remove(key);
        properties["operation"]!["enum"] = new JsonArray(operations.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
        properties["operation"]!["default"] = defaultOperation;
        return new(name, name.Replace('_', ' '), description, schema, async (arguments, token) =>
        {
            var workspace = LanguageMcpTools.ResolveWorkspace(arguments, callingWorkspace);
            var operation = arguments["operation"]?.GetValue<string>() ?? defaultOperation;
            if (!operations.Contains(operation)) throw new ArgumentException($"Use operation {string.Join(", ", operations)}.");
            var path = arguments["path"]?.GetValue<string>() ?? "";
            var id = arguments["id"]?.GetValue<string>() ?? "";
            if (tool == "codeActions" && operation != "list" && id.Length == 0) throw new ArgumentException("Pass the id of an action from operation=list.");
            if (tool == "codeActions" && operation == "list" && path.Length == 0) throw new ArgumentException("Pass the file as path.");
            if (tool == "format" && path.Length == 0) throw new ArgumentException("Pass the file as path.");
            var query = arguments["query"]?.GetValue<string>() ?? "";
            var newPath = arguments["newPath"]?.GetValue<string>() ?? "";
            if (tool == "rename" && newPath.Length > 0 && (path.Length == 0 || query.Length > 0 || arguments["newName"] is not null || arguments["line"] is not null || arguments["column"] is not null))
                throw new ArgumentException("To move a file or directory pass only path and newPath.");
            if (tool == "rename" && newPath.Length == 0 && path.Length == 0 && query.Length == 0) throw new ArgumentException("Name the symbol as query, or pass path with line and column.");
            if (tool == "rename" && newPath.Length == 0 && (arguments["newName"]?.GetValue<string>() ?? "").Length == 0) throw new ArgumentException("Pass newName.");
            var request = new LanguageRequest(workspace, path, Line: arguments["line"]?.GetValue<int>() ?? 0, Column: arguments["column"]?.GetValue<int>() ?? 0,
                Query: query, Limit: arguments["limit"]?.GetValue<int>() ?? 50)
            {
                EndLine = arguments["endLine"]?.GetValue<int>() ?? -1,
                EndColumn = arguments["endColumn"]?.GetValue<int>() ?? -1,
                Kinds = arguments["kinds"]?.GetValue<string>() ?? "",
                Id = id,
                NewName = arguments["newName"]?.GetValue<string>() ?? "",
                NewPath = newPath
            };
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
            bounded.CancelAfter(LanguageMcpTools.ToolDeadline);
            var elapsed = System.Diagnostics.Stopwatch.StartNew();
            LanguageResult result;
            try { result = await state.Languages.RefactorForAgentAsync(tool, operation, request, bounded.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { result = state.Languages.DeadlineExceeded(Path.TrimEndingDirectorySeparator(workspace), elapsed.Elapsed, LanguageMcpTools.ToolDeadline); }
            return (Format(result, name, operation), result.State is "error" or "conflict" or "failed");
        });
    }

    private static class Properties
    {
        internal static readonly string[] Actions = ["workspace", "operation", "path", "line", "column", "endLine", "endColumn", "query", "kinds", "id", "limit"];
        internal static readonly string[] Rename = ["workspace", "operation", "path", "line", "column", "query", "newName", "newPath", "limit"];
        internal static readonly string[] Format = ["workspace", "operation", "path"];
    }

    internal static string Format(LanguageResult result, string tool, string operation)
    {
        var response = new JsonObject { ["state"] = result.State, ["detail"] = Clip(result.Detail, 2_000) };
        if (result.Server.Length > 0) response["server"] = result.Server;
        if (result.Hint.Length > 0) response["hint"] = Clip(result.Hint, 400);
        response["freshness"] = result.Freshness;
        if (result.State == "initializing" && result.Servers.Count > 0)
            response["starting"] = new JsonArray(result.Servers.Select(server => (JsonNode?)new JsonObject { ["id"] = server.Id, ["state"] = server.State, ["detail"] = Clip(server.Detail, 300) }).ToArray());
        var remaining = 28_000;
        if (result.State == "ambiguous")
            response["candidates"] = Take(result.Locations.Select(item => new JsonObject { ["path"] = item.Path, ["line"] = item.Line, ["column"] = item.Column, ["text"] = Clip(item.Text, 300) }));
        if (operation == "list" && result.State == "ready")
        {
            response["actions"] = Take(result.AgentActions.Select(action => new JsonObject
            {
                ["id"] = action.Id,
                ["title"] = Clip(action.Title, 300),
                ["kind"] = action.Kind,
                ["preferred"] = action.Preferred,
                ["carries"] = action.Carries,
                ["files"] = new JsonArray(action.Files.Select(file => (JsonNode?)JsonValue.Create(file)).ToArray())
            }));
            response["total"] = result.Total;
            response["next"] = "Call lsp_code_actions with operation=preview or apply and an action id.";
        }
        if (result.Changes.Count > 0)
        {
            response["applied"] = result.Applied;
            response["totals"] = new JsonObject { ["files"] = result.Changes.Count, ["edits"] = result.Changes.Sum(change => change.EditCount) };
            response["changes"] = Take(result.Changes.Select(change =>
            {
                var item = new JsonObject { ["path"] = change.Path, ["change"] = change.Change, ["edits"] = change.EditCount };
                if (change.NewPath.Length > 0) item["newPath"] = change.NewPath;
                if (!result.Applied && change.Edits is { Count: > 0 })
                    item["preview"] = new JsonArray(change.Edits.Select(edit => (JsonNode?)new JsonObject
                    {
                        ["line"] = edit.Line,
                        ["column"] = edit.Column,
                        ["endLine"] = edit.EndLine,
                        ["endColumn"] = edit.EndColumn,
                        ["oldText"] = edit.Body,
                        ["newText"] = edit.Text
                    }).ToArray());
                return item;
            }));
            if (!result.Applied) response["next"] = $"Nothing was written. Repeat the call with operation=apply{(tool == "lsp_code_actions" ? " and the same id" : "")} to write these changes.";
        }
        if (result.Delta is { } delta)
        {
            response["diagnostics"] = new JsonObject
            {
                ["freshness"] = delta.Freshness,
                ["added"] = Take(delta.Added.Select(Diagnostic)),
                ["removed"] = Take(delta.Removed.Select(Diagnostic)),
                ["unchanged"] = delta.Unchanged
            };
        }
        return response.ToJsonString(Json);

        JsonArray Take(IEnumerable<JsonObject> items)
        {
            var values = new JsonArray();
            foreach (var item in items)
            {
                var length = item.ToJsonString(Json).Length + 1;
                if (length > remaining) { response["truncated"] = true; break; }
                remaining -= length;
                values.Add((JsonNode)item);
            }
            return values;
        }
    }

    private static JsonObject Diagnostic(LanguageItem item) => new()
    {
        ["path"] = item.Path,
        ["line"] = item.Line,
        ["column"] = item.Column,
        ["severity"] = item.Kind,
        ["text"] = Clip(item.Text, 500)
    };

    private static string Clip(string value, int maximum) => value.Length <= maximum ? value : value[..maximum] + "… [truncated]";
}