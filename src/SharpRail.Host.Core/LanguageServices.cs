using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;

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
                    () => DirectoryPath is not null && Current.Settings.LspEnabled && Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE") != "1", () => Current.Settings.LanguageServers, WorkspaceLanguage, SaveWorkspaceLanguage)
                { DeathObserved = PublishLanguageDeath, GlobalIntelliJProvider = () => Current.Settings.JavaKotlinProvider == "intellij" };
                WorkspaceRemoved += workspace => _ = languages.CloseWorkspaceAsync(workspace);
                return languages;
            }
        }
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
    public ValueTask<IntelliJTermsResult> IntelliJTermsAsync(IntelliJTermsRequest request, CancellationToken cancellationToken = default)
        => state is null ? ValueTask.FromResult(new IntelliJTermsResult("unavailable", "This host has no language service storage."))
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
                "definition" => "Read-only semantic navigation across languages. Prefer this to grep for code relationships. Positions are zero-based UTF-16; alternatively use an exact qualified query from symbols for hover/definition/typeDefinition/implementation/references. Ambiguous names require an explicit position. symbolBody reads one outline range. scope=dependencies includes external server results. For returned jar:/jrt: paths, pass the exact path and its source as server with operation=source for bounded source text; offset then counts UTF-16 characters. No arbitrary URI reads or commands. Unsupported providers/schemes report source unavailable. Installing/initializing means retry later; partial coverage is not a complete index. Sessions stay warm: do not restart between queries. Use search for non-code or incomplete semantic results. Pass absolute workspace; follow pagination.",
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
                  "line":{"type":"integer","minimum":0},"column":{"type":"integer","minimum":0},
                  "query":{"type":"string","description":"Workspace symbol search text; symbols outline filter; or exact qualified outline name for symbolBody/hover/definition/typeDefinition/implementation/references. Omit for the symbol containing line/column."},
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