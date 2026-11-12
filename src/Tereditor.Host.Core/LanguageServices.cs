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
    /// <summary>Where language servers keep installations, caches and per-workspace state.</summary>
    internal string LanguageDirectory => Path.Combine(DirectoryPath ?? Path.Combine(Path.GetTempPath(), "sharprail-languages"), "language");

    /// <summary>Consent to terms by a host with no window (headless); null on the desktop, which asks in a window.</summary>
    internal LanguageConsent? LanguageConsent { get; private set; }

    /// <summary>Turns on headless consent: Java and Kotlin use the server with terms once it has a grant, and a grant lets its provisioning install the pinned archive.</summary>
    internal LanguageConsent EnableLanguageConsent(Func<string, string?> environment, string workspace) =>
        LanguageConsent = new(LanguageDirectory, () => LanguageCatalog, environment, (server, prepare, token) => Languages.ReadTermsAsync(server, workspace, prepare, token));

    internal LanguageService Languages
    {
        get
        {
            lock (gate)
            {
                if (languages is not null) return languages;
                languages = new(LanguageDirectory,
                    () => DirectoryPath is not null && Current.Settings.LspEnabled && Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE") != "1", () => Current.Settings.LanguageServers, WorkspaceLanguage, SaveWorkspaceLanguage, LanguageServerSpecs)
                { DeathObserved = PublishLanguageDeath, GlobalProvider = () => LanguageConsent?.Provider() ?? Current.Settings.JavaKotlinProvider, StandardWithAnyReplaced = () => LanguageConsent is not null, Consent = server => LanguageConsent?.GrantFor(server), ToolPaths = () => Current.Settings.LanguageTools, Limits = CurrentLanguageLimits };
                WorkspaceRemoved += workspace => _ = languages.CloseWorkspaceAsync(workspace);
                return languages;
            }
        }
    }

    /// <summary>The language-server limits from host settings; a value that no longer parses falls back to the defaults.</summary>
    private LanguageServerLimits CurrentLanguageLimits()
    {
        try { return LanguageServerLimits.Parse(Current.Settings.LanguageLimits); }
        catch (Exception error) when (error is ArgumentException or FormatException or InvalidOperationException or System.Text.Json.JsonException) { return LanguageServerLimits.Default; }
    }

    private ProjectLanguageCache? projectLanguages;

    /// <summary>
    /// The workspace's last known languages for the agent instructions (H20), or <see langword="null"/> before the first
    /// census. Never waits: a stale or missing entry starts a background census whose result serves the next session.
    /// </summary>
    internal ProjectLanguageProfile? ProjectLanguages(string workspace)
    {
        if (!Current.Settings.LspEnabled) return null;
        ProjectLanguageCache cache;
        lock (gate)
            cache = projectLanguages ??= new(Path.Combine(DirectoryPath ?? Path.Combine(Path.GetTempPath(), "sharprail-languages"), "language"),
                (path, token) => Languages.LanguageProfile(path, token));
        return cache.Get(Path.TrimEndingDirectorySeparator(workspace));
    }

    /// <summary>The census in flight for a workspace's agent instructions, for checks.</summary>
    internal Task ProjectLanguagesPending(string workspace) => projectLanguages?.Pending(Path.TrimEndingDirectorySeparator(workspace)) ?? Task.CompletedTask;

    /// <summary>The language servers enabled plugins provide (H21), set by the plugin runtime when it starts.</summary>
    internal Func<IReadOnlyList<LanguageServerSpec>>? LanguageServerSource { get; set; }

    /// <summary>The servers in the catalog now: the plugin runtime's, or, for a host that runs none, the builtin plugins'.</summary>
    internal IReadOnlyList<LanguageServerSpec> LanguageServerSpecs() => LanguageServerSource?.Invoke() ?? LanguageServers.BuiltinSpecs;

    internal IReadOnlyList<LanguageServer> LanguageCatalog => LanguageServers.Wrap(LanguageServerSpecs());

    /// <summary>Called when plugins activate or deactivate: stops sessions of servers that left the catalog.</summary>
    internal void LanguageServersChanged()
    {
        LanguageService? service;
        lock (gate) service = languages;
        if (service is not null) _ = service.CatalogChangedAsync();
    }

    /// <summary>Installs a language server and its pinned runtimes without starting a session; false when no enabled plugin provides the id.</summary>
    public Task<bool> WarmLanguageServerAsync(string serverId, string workspace, CancellationToken token) => Languages.WarmAsync(serverId, workspace, token);

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
            : request.Operation is "projectHidden" or "setProjectHidden"
                ? state.ProjectLanguageHiddenAsync(request, cancellationToken)
                : state.Languages.ExecuteAsync(request, cancellationToken);
}

public static class LanguageMcpTools
{
    /// <summary>The deadline of every language tool call; settable for checks.</summary>
    internal static TimeSpan ToolDeadline { get; set; } = LanguageLimits.ToolDeadline;

    /// <summary>The absolute workspace of a call: the terminal's own, or another checkout of the same project.</summary>
    internal static string ResolveWorkspace(JsonObject arguments, string callingWorkspace)
    {
        var workspace = Path.GetFullPath(arguments["workspace"]?.GetValue<string>() ?? callingWorkspace);
        var calling = Path.TrimEndingDirectorySeparator(Path.GetFullPath(callingWorkspace));
        if (workspace != calling)
        {
            var origin = ProjectServices.KnownCheckout(calling);
            var target = ProjectServices.KnownCheckout(workspace);
            if (origin is null || target is null || origin.Value.ProjectRoot != target.Value.ProjectRoot)
                throw new ArgumentException("Choose a workspace of this terminal's project.");
        }
        return workspace;
    }

    public static IReadOnlyList<McpServer.McpTool> Create(HostStateStore state, string callingWorkspace)
    {
        // A host whose plugins provide no language server (a browser tab) offers agents no language tools.
        if (state.LanguageCatalog.Count == 0) return [];
        return [Tool("lsp_diagnostics", "diagnostics"), Tool("lsp_navigate", "definition"), Tool("lsp_status", "status"), .. LanguageRefactoringTools.Create(state, callingWorkspace)];

        McpServer.McpTool Tool(string name, string operation) => new(name, name.Replace('_', ' '),
            operation switch
            {
                "diagnostics" => "Inspect edited paths (globs such as src/**/*.ts work, 20 files at most) or scope=workspace (one workspace/diagnostic request where a server offers it, else file by file). Starts the language servers that own the files on demand and waits up to 60 s; coverage says checked, pending or skipped per file and which LSP method produced it. Errors come first; each diagnostic carries severity (error, warning, information, hint) and code; summary counts errors, warnings, information and hints shown, failedFiles counts files that could not be checked; minSeverity=error|warning hides the rest and the note names what was hidden. A push-only server gets timeout seconds (5..300, default 12) per server to report late diagnostics. Prefer edited-file scans. Partial/pending/unversioned coverage does not mean clean. Follow page nextOffset. Pass the absolute workspace, including in subagents.",
                "definition" => "Read-only semantic navigation (Roslyn, clangd, TypeScript, Python, JVM...). Use it instead of grep for code: it finds implementations, overloads, partial classes and generated code that text search misses. Name symbols, not positions: {\"operation\":\"references\",\"query\":\"LanguageService\"} (also definition, implementation, typeDefinition, hover) resolves the name through workspace symbols and runs at its declaration; a simple name, Type/Member or Namespace.Type.Member all work. If several symbols share the name the result is ambiguous and lists candidates: repeat with one candidate's path, line and column, or a more qualified query. With path, query is matched against that file's outline: {\"operation\":\"definition\",\"path\":\"src/Foo.cs\",\"query\":\"Foo/Bar\"}. Outline: {\"operation\":\"symbols\",\"path\":\"src/Foo.cs\"}. One body: {\"operation\":\"symbolBody\",\"path\":\"src/Foo.cs\",\"query\":\"Bar\"}. Find a file: {\"operation\":\"workspaceSymbols\",\"query\":\"Foo\"}. Explicit positions: {\"operation\":\"references\",\"path\":\"src/Foo.cs\",\"line\":12,\"column\":22}; line and column are zero-based UTF-16, while preview prefixes are one-based line numbers. scope=dependencies includes library results, also for workspaceSymbols (library symbols the server indexes); read a returned jar:/jrt: path with operation=source and its source server. Call hierarchy by name: {\"operation\":\"incomingCalls\",\"query\":\"Foo/Bar\"} (callers) or outgoingCalls (callees), with depth (default 3), maxChildren (20) and maxNodes (100); depth 0 is the symbol. operation=request sends one read-only LSP query as method plus optional path, line/column or query and a JSON payload ({\"operation\":\"request\",\"method\":\"textDocument/documentHighlight\",\"path\":\"src/Foo.cs\",\"line\":3,\"column\":5}), or with server for workspace-level methods; anything that writes or executes is refused and names the tool to use. initializing means wait and retry; sessions stay warm. Pass the absolute workspace (also in subagents) and follow pagination.",
                _ => "Manage workspace language servers. servers lists states; catalog identifies implementations. capabilities dumps the capabilities a server declared (all running servers, one with server, or the owner of path). retry starts a server; restart applies saved settings. configurationSchema lists version-specific named settings; configuration reads saved and active settings; configure saves a complete object without starting. Read before replacing settings. Missing libraries: use provider initializationOptions/settings or clangd includePaths/compilerFlags/compilationDatabasePath. Arguments replace server arguments; null keeps defaults; ${workspaceFolder} expands on host. Settings persist and are visible in the LSP pane. stop ends a process; logs reads bounded evidence. Reuse warm sessions, poll status during import/indexing. Terms acceptance remains user-only. Pass absolute workspace, including in subagents."
            },
            Schema(JsonNode.Parse("""
                {"type":"object","properties":{
                  "workspace":{"type":"string","description":"Absolute workspace directory; always pass this after changing worktrees and in subagents."},
                  "path":{"type":"string","description":"Workspace-relative file, or exact dependency URI returned by navigation (requires source server). Diagnostics path checks one file and excludes paths/scope."},
                  "paths":{"type":"array","items":{"type":"string"},"minItems":1,"description":"Explicit workspace-relative files to inspect, such as every file edited by this agent, or globs (src/**/*.ts; at most 20 matches). Diagnostics only."},
                  "minSeverity":{"type":"string","enum":["error","warning","information","hint"],"description":"Diagnostics only: hide diagnostics less severe than this (error shows errors only)."},
                  "timeout":{"type":"integer","minimum":5,"maximum":300,"description":"Diagnostics only: seconds to wait per server for diagnostics it pushes late. Default 12."},
                  "depth":{"type":"integer","minimum":1,"maximum":10,"default":3,"description":"incomingCalls/outgoingCalls: call levels to follow."},
                  "maxChildren":{"type":"integer","minimum":1,"maximum":100,"default":20,"description":"incomingCalls/outgoingCalls: entries listed per node."},
                  "maxNodes":{"type":"integer","minimum":1,"maximum":500,"default":100,"description":"incomingCalls/outgoingCalls: entries listed in all."},
                  "method":{"type":"string","description":"request only: the LSP method, such as textDocument/documentHighlight or typeHierarchy/supertypes. Read-only queries only."},
                  "payload":{"type":"object","additionalProperties":true,"description":"request only: the method's JSON parameters. The host fills textDocument (from path) and position (from line and column); do not pass them."},
                  "scope":{"type":"string","enum":["files","workspace","file","module","all","dependencies"],"description":"Diagnostics: files requires paths; workspace scans source files using running servers. Navigation: workspace (default references), file, module (only with reliable provider ownership), or dependencies (all server results including external dependencies; all is an alias). Scope filters semantic results before pagination; it never substitutes text search."},
                  "operation":{"type":"string","enum":["diagnostics","hover","definition","typeDefinition","implementation","references","incomingCalls","outgoingCalls","request","source","symbolBody","symbols","workspaceSymbols","status","capabilities","retry","restart","stop","configurationSchema","configuration","configure","logs","servers","catalog"]},
                  "server":{"type":"string","description":"Source server id for dependency URI reads/navigation, or catalog id for path-free lifecycle operations. Start each needed language explicitly before a mixed-language diagnostic scan."},
                  "configuration":{"type":"object","description":"Complete workspace configuration to save (configure/restart only); omitted fields reset to defaults. Read operation=configuration first to preserve existing values. No source files are changed.","properties":{
                    "useForJavaAndKotlin":{"type":"boolean","description":"Legacy workspace selection, retained for migration only. The host-global Java/Kotlin provider choice in Settings is authoritative."},
                    "installationPath":{"type":"string","description":"IntelliJ only: absolute user-managed installation directory on the host."},
                    "projectJdk":{"type":"string","description":"IntelliJ only: absolute project JDK on host, separate from bundled server runtime."},
                    "buildTool":{"type":"string","enum":["","maven","gradle","bazel","jps"]},
                    "autoStart":{"type":"boolean","description":"Start this server when this workspace is opened as a project; created workspaces never inherit it and start servers on demand. Stop clears it; Start sets it. A supplied restart configuration preserves this choice."},
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
                var workspace = ResolveWorkspace(arguments, callingWorkspace);
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
                var timeout = arguments["timeout"]?.GetValue<int>() ?? 0;
                if (timeout != 0 && timeout is < 5 or > 300) throw new ArgumentException("timeout is between 5 and 300 seconds.");
                using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
                // A longer diagnostics wait extends the call's deadline by the same time.
                bounded.CancelAfter(selectedOperation == "diagnostics" && timeout > 0 ? ToolDeadline + TimeSpan.FromSeconds(timeout) : ToolDeadline);
                var elapsed = System.Diagnostics.Stopwatch.StartNew();
                LanguageResult result;
                try
                {
                    result = await state.Languages.ExecuteForAgentAsync(new(workspace, path,
                    selectedOperation == "request" ? "rawRequest" : selectedOperation, arguments["line"]?.GetValue<int>() ?? 0,
                    arguments["column"]?.GetValue<int>() ?? 0, Query: arguments["query"]?.GetValue<string>() ?? "", Server: arguments["server"]?.GetValue<string>() ?? "", Scope: scope, Paths: paths, Offset: offset, Limit: limit, Configuration: arguments["configuration"]?.ToJsonString())
                    {
                        Timeout = timeout,
                        MinSeverity = arguments["minSeverity"]?.GetValue<string>() ?? "",
                        Depth = arguments["depth"]?.GetValue<int>() ?? 3,
                        MaxChildren = arguments["maxChildren"]?.GetValue<int>() ?? 20,
                        MaxNodes = arguments["maxNodes"]?.GetValue<int>() ?? 100,
                        Method = arguments["method"]?.GetValue<string>() ?? "",
                        Payload = arguments["payload"]?.ToJsonString()
                    }, bounded.Token);
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { result = state.Languages.DeadlineExceeded(Path.TrimEndingDirectorySeparator(workspace), elapsed.Elapsed, ToolDeadline); }
                return (LanguageMcpResponse.Format(result, selectedOperation, offset, limit, path.Length > 0), result.State is "error" or "conflict" or "refused");
            });
        static JsonObject Schema(JsonObject schema, string operation)
        {
            var properties = schema["properties"]!.AsObject();
            var allowed = operation switch
            {
                "diagnostics" => new[] { "workspace", "path", "paths", "scope", "operation", "minSeverity", "timeout", "offset", "limit" },
                "definition" => ["workspace", "path", "scope", "operation", "server", "line", "column", "query", "depth", "maxChildren", "maxNodes", "method", "payload", "offset", "limit"],
                _ => ["workspace", "path", "operation", "server", "configuration", "offset", "limit"]
            };
            foreach (var key in properties.Select(pair => pair.Key).Except(allowed).ToArray()) properties.Remove(key);
            var operations = operation switch
            {
                "diagnostics" => new[] { "diagnostics" },
                "definition" => ["hover", "definition", "typeDefinition", "implementation", "references", "incomingCalls", "outgoingCalls", "symbols", "symbolBody", "workspaceSymbols", "request", "source"],
                _ => ["status", "capabilities", "servers", "catalog", "configurationSchema", "configuration", "configure", "retry", "restart", "stop", "logs", "projects"]
            };
            properties["operation"]!["enum"] = new JsonArray(operations.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            properties["operation"]!["default"] = operation;
            if (properties["scope"] is { } scope)
                scope["enum"] = new JsonArray((operation == "diagnostics" ? new[] { "files", "workspace" } : ["file", "module", "workspace", "dependencies", "all"]).Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
            return schema;
        }

    }
}