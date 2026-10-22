using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core;

public sealed partial class HostStateStore
{
    private LanguageService? languages;
    internal bool AllowsLanguageFile(string workspace, string path) => languages?.AllowsRead(workspace, path) == true;
    internal LanguageService Languages
    {
        get
        {
            lock (gate)
            {
                if (languages is not null) return languages;
                languages = new(Path.Combine(DirectoryPath ?? Path.Combine(Path.GetTempPath(), "sharprail-languages"), "language"),
                    () => DirectoryPath is not null && Current.Settings.LspEnabled && Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE") != "1", () => Current.Settings.LanguageServers, WorkspaceLanguage, SaveWorkspaceLanguage, LanguageServerSpecs)
                { DeathObserved = PublishLanguageDeath, GlobalProvider = () => Current.Settings.JavaKotlinProvider };
                WorkspaceRemoved += workspace => _ = languages.CloseWorkspaceAsync(workspace);
                return languages;
            }
        }
    }

    /// <summary>The language servers enabled plugins provide (H21), set by the plugin runtime when it starts.</summary>
    internal Func<IReadOnlyList<LanguageServerSpec>>? LanguageServerSource { get; set; }

    /// <summary>The servers in the catalog now: the plugin runtime's, or, for a host that runs none, the builtin plugins'.</summary>
    internal IReadOnlyList<LanguageServerSpec> LanguageServerSpecs() => LanguageServerSource?.Invoke() ?? [.. LanguageServers.Catalog.Select(server => server.Spec)];

    internal IReadOnlyList<LanguageServer> LanguageCatalog => LanguageServers.Wrap(LanguageServerSpecs());

    /// <summary>Called when plugins activate or deactivate: stops sessions of servers that left the catalog.</summary>
    internal void LanguageServersChanged()
    {
        LanguageService? service;
        lock (gate) service = languages;
        if (service is not null) _ = service.CatalogChangedAsync();
    }

    /// <summary>Ends this host's language processes on shutdown; listeners do not own them.</summary>
    public async ValueTask StopLanguagesAsync()
    {
        StopLanguageFileWatchers();
        if (languages is { } service) await service.DisposeAsync();
    }
}

public sealed partial class ProjectServices
{
    public ValueTask<LanguageTermsResult> LanguageTermsAsync(LanguageTermsRequest request, CancellationToken cancellationToken = default)
        => state is null ? ValueTask.FromResult(new LanguageTermsResult("unavailable", "This host has no language service storage."))
            : state.Languages.TermsAsync(request, cancellationToken);

    public ValueTask<LanguageResult> LanguageAsync(LanguageRequest request, CancellationToken cancellationToken = default)
        => state is null ? ValueTask.FromResult(new LanguageResult("unavailable", "This host has no language service storage."))
            : request.Operation is "javaKotlinProvider" or "setJavaKotlinProvider"
                ? state.JavaKotlinProviderAsync(request, cancellationToken)
            : request.Operation is "projectLanguageFile" or "saveProjectLanguageFile"
                ? state.ProjectLanguageFileAsync(request, cancellationToken)
            : request.Operation is "projectAutostart" or "setProjectAutostart" or "prepareAgent"
                ? state.ProjectLanguageAutostartAsync(request, cancellationToken)
                : state.Languages.ExecuteAsync(request, cancellationToken);
}

public static class LanguageMcpTools
{
    /// <summary>The deadline of every language tool call; settable for checks.</summary>
    internal static TimeSpan ToolDeadline { get; set; } = LanguageLimits.ToolDeadline;

    public static IReadOnlyList<McpServer.McpTool> Create(HostStateStore state, string callingWorkspace)
    {
        return [Tool("lsp_diagnostics", "diagnostics"), Tool("lsp_navigate", "definition"), Tool("lsp_status", "status")];

        McpServer.McpTool Tool(string name, string operation) => new(name, name.Replace('_', ' '),
            operation switch
            {
                "diagnostics" => "Inspect edited paths or scope=workspace. Starts the language servers that own the files on demand and waits up to 60 s; coverage says checked, pending or skipped per file. Prefer edited-file scans. Partial/pending/unversioned coverage does not mean clean. Follow page nextOffset. Pass the absolute workspace, including in subagents.",
                "definition" => "Read-only semantic navigation (Roslyn, clangd, TypeScript, Python, JVM...). Use it instead of grep for code: it finds implementations, overloads, partial classes and generated code that text search misses. Name symbols, not positions: {\"operation\":\"references\",\"query\":\"LanguageService\"} (also definition, implementation, typeDefinition, hover) resolves the name through workspace symbols and runs at its declaration; a simple name, Type/Member or Namespace.Type.Member all work. If several symbols share the name the result is ambiguous and lists candidates: repeat with one candidate's path, line and column, or a more qualified query. With path, query is matched against that file's outline: {\"operation\":\"definition\",\"path\":\"src/Foo.cs\",\"query\":\"Foo/Bar\"}. Outline: {\"operation\":\"symbols\",\"path\":\"src/Foo.cs\"}. One body: {\"operation\":\"symbolBody\",\"path\":\"src/Foo.cs\",\"query\":\"Bar\"}. Find a file: {\"operation\":\"workspaceSymbols\",\"query\":\"Foo\"}. Explicit positions: {\"operation\":\"references\",\"path\":\"src/Foo.cs\",\"line\":12,\"column\":22}; line and column are zero-based UTF-16, while preview prefixes are one-based line numbers. scope=dependencies includes library results; read a returned jar:/jrt: path with operation=source and its source server. initializing means wait and retry; sessions stay warm. Pass the absolute workspace (also in subagents) and follow pagination.",
                _ => "Manage workspace language servers. servers lists states; catalog identifies implementations. retry starts a server; restart applies saved settings. configurationSchema lists version-specific named settings; configuration reads saved and active settings; configure saves a complete object without starting. Read before replacing settings. Missing libraries: use provider initializationOptions/settings or clangd includePaths/compilerFlags/compilationDatabasePath. Arguments replace server arguments; null keeps defaults; ${workspaceFolder} expands on host. Settings persist and are visible in the LSP pane. stop ends a process; logs reads bounded evidence. Reuse warm sessions, poll status during import/indexing. Terms acceptance remains user-only. Pass absolute workspace, including in subagents."
            },
            Schema(JsonNode.Parse("""
                {"type":"object","properties":{
                  "workspace":{"type":"string","description":"Absolute workspace directory; always pass this after changing worktrees and in subagents."},
                  "path":{"type":"string","description":"Workspace-relative file, or exact dependency URI returned by navigation (requires source server). Diagnostics path checks one file and excludes paths/scope."},
                  "paths":{"type":"array","items":{"type":"string"},"minItems":1,"description":"Explicit workspace-relative files to inspect, such as every file edited by this agent. Diagnostics only."},
                  "scope":{"type":"string","enum":["files","workspace","file","module","all","dependencies"],"description":"Diagnostics: files requires paths; workspace scans source files using running servers. Navigation: workspace (default references), file, module (only with reliable provider ownership), or dependencies (all server results including external dependencies; all is an alias). Scope filters semantic results before pagination; it never substitutes text search."},
                  "operation":{"type":"string","enum":["diagnostics","hover","definition","typeDefinition","implementation","references","source","symbolBody","symbols","workspaceSymbols","status","retry","restart","stop","configurationSchema","configuration","configure","logs","servers","catalog"]},
                  "server":{"type":"string","description":"Source server id for dependency URI reads/navigation, or catalog id for path-free lifecycle operations. Start each needed language explicitly before a mixed-language diagnostic scan."},
                  "configuration":{"type":"object","description":"Complete workspace configuration to save (configure/restart only); omitted fields reset to defaults. Read operation=configuration first to preserve existing values. No source files are changed.","properties":{
                    "useForJavaAndKotlin":{"type":"boolean","description":"Legacy workspace selection, retained for migration only. The host-global Java/Kotlin provider choice in Settings is authoritative."},
                    "installationPath":{"type":"string","description":"IntelliJ only: absolute user-managed installation directory on the host."},
                    "projectJdk":{"type":"string","description":"IntelliJ only: absolute project JDK on host, separate from bundled server runtime."},
                    "buildTool":{"type":"string","enum":["","maven","gradle","bazel","jps"]},
                    "autoStart":{"type":"boolean","description":"Start this server in newly created workspaces inheriting these settings; Stop clears it; Start sets it. A supplied restart configuration preserves this choice."},
                    "arguments":{"type":["array","null"],"items":{"type":"string"},"description":"Replacement server command arguments, not compiler flags. null preserves the launcher defaults."},
                    "initializationOptions":{"type":["object","null"],"additionalProperties":true},
                    "settings":{"type":["object","null"],"additionalProperties":true,"description":"Server-specific workspace configuration sections, e.g. python.analysis.extraPaths or java.project.referencedLibraries."},
                    "includePaths":{"type":["array","null"],"items":{"type":"string"},"description":"clangd fallback include directories, relative to this workspace or absolute."},
                    "compilerFlags":{"type":["array","null"],"items":{"type":"string"},"description":"clangd fallback compiler flags, used only without a compile command for the file. Prefer a compilation database for mixed languages or per-file flags."},
                    "compilationDatabasePath":{"type":"string","description":"clangd compilation database directory, workspace-relative or absolute."}
                  },"additionalProperties":false},
                  "line":{"type":"integer","minimum":0,"description":"Zero-based line (preview prefixes are one-based). Optional when query names the symbol."},"column":{"type":"integer","minimum":0,"description":"Zero-based UTF-16 column."},
                  "query":{"type":"string","description":"Symbol name. Without path, definition/references/implementation/typeDefinition/hover look it up across the workspace (simple, Type/Member or Namespace.Type.Member). With path, a name from that file's outline (symbols lists them; a simple name works when unique). workspaceSymbols: search text; symbols: outline filter. Omit to use line/column."},
                  "offset":{"type":"integer","minimum":0,"description":"Result offset (UTF-16 character offset for source); with a path, navigation pages one server. For diagnostics applies separately to sorted diagnostics and file coverage."},
                  "limit":{"type":"integer","minimum":1,"maximum":200,"default":50,"description":"Maximum items per result list. Refine query/path if a workspace-wide symbol result exceeds the output budget."}
                },"additionalProperties":false}
                """)!.AsObject(), operation), async (arguments, token) =>
            {
                var workspace = arguments["workspace"]?.GetValue<string>() ?? callingWorkspace;
                workspace = Path.GetFullPath(workspace);
                var calling = Path.TrimEndingDirectorySeparator(Path.GetFullPath(callingWorkspace));
                if (workspace != calling)
                {
                    var origin = ProjectServices.KnownCheckout(calling);
                    var target = ProjectServices.KnownCheckout(workspace);
                    if (origin is null || target is null || origin.Value.ProjectRoot != target.Value.ProjectRoot)
                        throw new ArgumentException("Choose a workspace of this terminal's project.");
                }
                var offset = arguments["offset"]?.GetValue<int>() ?? 0;
                var limit = arguments["limit"]?.GetValue<int>() ?? 50;
                if (offset < 0 || limit is < 1 or > 200) throw new ArgumentException("Use offset >= 0 and limit between 1 and 200.");
                var selectedOperation = arguments["operation"]?.GetValue<string>() ?? operation;
                var path = arguments["path"]?.GetValue<string>() ?? "";
                var paths = arguments["paths"]?.AsArray().Select(item => item!.GetValue<string>()).ToArray();
                var scope = arguments["scope"]?.GetValue<string>() ?? "";
                if (selectedOperation == "diagnostics")
                {
                    if (path.Length > 0)
                    {
                        if (paths is not null || scope.Length > 0) throw new ArgumentException("Use path, paths or scope workspace, not a combination.");
                        paths = [path]; path = "";
                    }
                    if (scope.Length == 0) scope = paths is null ? "workspace" : "files";
                }
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
                bounded.CancelAfter(ToolDeadline);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                LanguageResult result;
                try
                {
                    result = await state.Languages.ExecuteForAgentAsync(new(workspace, path,
                    selectedOperation, arguments["line"]?.GetValue<int>() ?? 0,
                    arguments["column"]?.GetValue<int>() ?? 0, Query: arguments["query"]?.GetValue<string>() ?? "", Server: arguments["server"]?.GetValue<string>() ?? "", Scope: scope, Paths: paths, Offset: offset, Limit: limit, Configuration: arguments["configuration"]?.ToJsonString()), bounded.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { result = state.Languages.DeadlineExceeded(Path.TrimEndingDirectorySeparator(workspace), elapsed.Elapsed, ToolDeadline); }
                return (LanguageMcpResponse.Format(result, selectedOperation, offset, limit, path.Length > 0), result.State is "error" or "conflict");
            });
        static JsonObject Schema(JsonObject schema, string operation)
        {
            var properties = schema["properties"]!.AsObject();
            var allowed = operation switch
            {
                "diagnostics" => new[] { "workspace", "path", "paths", "scope", "operation", "offset", "limit" },
                "definition" => ["workspace", "path", "scope", "operation", "server", "line", "column", "query", "offset", "limit"],
                _ => ["workspace", "path", "operation", "server", "configuration", "offset", "limit"]
            };
            foreach (var key in properties.Select(pair => pair.Key).Except(allowed).ToArray()) properties.Remove(key);
            var operations = operation switch
            {
                "diagnostics" => new[] { "diagnostics" },
                "definition" => ["hover", "definition", "typeDefinition", "implementation", "references", "symbols", "symbolBody", "workspaceSymbols", "source"],
                _ => ["status", "servers", "catalog", "configurationSchema", "configuration", "configure", "retry", "restart", "stop", "logs", "projects"]
            };
            properties["operation"]!["enum"] = new JsonArray(operations.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            properties["operation"]!["default"] = operation;
            if (properties["scope"] is { } scope)
                scope["enum"] = new JsonArray((operation == "diagnostics" ? new[] { "files", "workspace" } : ["file", "module", "workspace", "dependencies", "all"]).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            return schema;
        }

    }
}