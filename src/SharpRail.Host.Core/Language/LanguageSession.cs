using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal sealed partial class LanguageSession : IAsyncDisposable
{
    private readonly LanguageConnection connection;
    private readonly TaskCompletionSource projectInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LanguageProjectModel projectModel = null!;
    private bool projectLoading;
    private readonly ConcurrentDictionary<string, string> registeredCapabilities = new();
    private string projectAction = "";
    internal bool NeedsConfiguration => projectAction.Length > 0;
    private readonly TaskCompletionSource projectActionReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, string> indexing = new();
    internal bool IsIndexing => projectLoading || !indexing.IsEmpty || projectRestores.Values.Any(task => !task.IsCompleted);
    internal string Status => projectAction.Length > 0 ? projectAction : detail;
    internal string ProjectDetail => (projectAction.Length > 0 ? projectAction : detail) + ". " + projectModel.Description;
    private readonly ConcurrentDictionary<string, Task> projectRestores = new();
    private readonly LanguageLog log;
    private object[] ProjectFolders => projectModel.WorkspaceFolders.Select(path => (object)new { uri = UriOf(path + Path.DirectorySeparatorChar), name = Path.GetFileName(path) }).ToArray();
    private readonly string workspace;
    private readonly LanguageServer server;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, Document> documents = [];
    private readonly ConcurrentDictionary<string, Published> diagnostics = new();
    private readonly ConcurrentDictionary<string, int> changed = new();
    private readonly CancellationTokenSource lifetime = new();
    private JsonNode capabilities = new JsonObject();
    private FileSystemWatcher? watcher;
    private string detail = "Ready";
    private int nextDocumentVersion;
    private int disposed;
    private string? ownedCache;
    internal bool HasDrafts { get; private set; }
    internal bool Exited => connection.Exited;
    internal LanguageExit? ExitRecord => connection.ExitRecord;
    /// <summary>Names why SharpRail is about to end this server, so its exit record explains the death.</summary>
    internal void Terminate(string reason, bool failure = false) => connection.Terminate(reason, failure);
    internal void SampleMemory() => connection.SampleMemory();
    internal async Task<bool> ObserveExpiryAsync() => server.Id == "intellij" && await connection.ObserveExitAsync() == 7;
    internal bool Expired => server.Id == "intellij" && connection.ExitCode == 7;
    private sealed record Document(string Text, int Version, string Owner, long ClientVersion, bool Dirty);
    private sealed record Published(int? Version, LanguageItem[] Items);

    private LanguageSession(ProcessStartInfo start, string workspace, LanguageServer server, WorkspaceLanguageOptions options, LanguageLog log, Action<LanguageExit>? onExit)
    {
        this.workspace = workspace; this.server = server; this.log = log;
        var settings = WorkspaceLanguageConfiguration.Settings(options, workspace, server.Id);
        connection = new(start, workspace, log, server.Id, onExit)
        {
            Notification = OnNotification,
            ClientRequest = OnClientRequest,
            WorkspaceFolders = () => ProjectFolders,
            Configuration = section => section.Length == 0 ? settings : settings[section] ?? section.Split('.').Aggregate((JsonNode?)settings, (node, key) => node is JsonObject obj ? obj[key] : null)
        };
    }

    internal static async Task<LanguageSession> StartAsync(ProcessStartInfo start, string workspace, LanguageServer server, CancellationToken token, WorkspaceLanguageOptions? options = null, LanguageLog? log = null, string? storage = null, Action<LanguageExit>? onExit = null)
    {
        options ??= new();
        var model = LanguageProjectModel.Discover(workspace, server, token, options.CompilationDatabasePath);
        var initialization = WorkspaceLanguageConfiguration.Initialization(options, workspace, server.Id);
        model.ConfigureInitialization(initialization);
        if (server.Id == "php" && storage is not null)
        {
            if (!initialization.ContainsKey("storagePath")) initialization["storagePath"] = Path.Combine(storage, "workspaces", LanguageServers.Key(workspace), server.Id);
            if (!initialization.ContainsKey("globalStoragePath")) initialization["globalStoragePath"] = Path.Combine(storage, "cache", server.Id, server.Version);
        }
        var session = new LanguageSession(start, workspace, server, options, log ?? new(), onExit);
        if (server.Id == "intellij" && storage is not null && start.ArgumentList.IndexOf("--system-path") is var cacheIndex && cacheIndex >= 0 && cacheIndex + 1 < start.ArgumentList.Count)
        {
            var cache = start.ArgumentList[cacheIndex + 1];
            if (LanguageProjectModel.Contains(Path.Combine(storage, "workspaces", LanguageServers.Key(workspace), "intellij"), cache)) session.ownedCache = cache;
        }
        session.projectModel = model;
        if (server.Id is "intellij" or "kotlin")
        {
            var projectJdk = options.ProjectJdk.Length == 0 && options.InitializationOptions is { } supplied && supplied.TryGetProperty("defaultSdk", out _)
                ? (Path: initialization["defaultSdk"]?.ToString() ?? "", Source: "workspace initialization defaultSdk")
                : ProjectJava.Resolve(workspace, options.ProjectJdk);
            session.log.Add("project JDK", projectJdk.Source + (projectJdk.Path.Length > 0 ? ": " + projectJdk.Path : "") + ". This is separate from the server runtime and is not an import-failure diagnosis.");
        }
        session.projectLoading = server.Id == "csharp" && initialization["autoLoadProjects"]?.GetValue<int>() != 0 && session.projectModel.Manifests.Length > 0;
        try
        {
            var result = await session.connection.RequestAsync("initialize", new
            {
                processId = Environment.ProcessId,
                clientInfo = new { name = "SharpRail", version = "1.0" },
                rootUri = UriOf(workspace + Path.DirectorySeparatorChar),
                initializationOptions = initialization,
                workDoneToken = server.Id == "intellij" ? "sharprail-intellij-init" : null,
                workspaceFolders = session.ProjectFolders,
                capabilities = new
                {
                    general = new { positionEncodings = new[] { "utf-16" } },
                    window = new { workDoneProgress = true },
                    workspace = new { workspaceFolders = true, configuration = true, didChangeWatchedFiles = new { dynamicRegistration = true } },
                    textDocument = new
                    {
                        references = new { dynamicRegistration = true },
                        synchronization = new { didSave = true },
                        publishDiagnostics = new { versionSupport = true, relatedInformation = true },
                        diagnostic = new { dynamicRegistration = false, relatedDocumentSupport = false },
                        definition = new { linkSupport = true },
                        typeDefinition = new { linkSupport = true },
                        implementation = new { linkSupport = true },
                        documentSymbol = new { hierarchicalDocumentSymbolSupport = true },
                        semanticTokens = new
                        {
                            requests = new { full = true },
                            formats = new[] { "relative" },
                            tokenTypes = new[] { "namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "field", "delegate", "enumMember", "event", "function", "method", "macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator", "decorator" },
                            tokenModifiers = new[] { "declaration", "definition", "readonly", "static", "deprecated", "abstract", "async", "modification", "documentation", "defaultLibrary" },
                            augmentsSyntaxTokens = false,
                            overlappingTokenSupport = false,
                            multilineTokenSupport = false
                        }
                    }
                }
            }, token, server.Id is "intellij" or "ruby" ? TimeSpan.FromMinutes(15) : null);
            session.capabilities = result?["capabilities"]?.DeepClone() ?? new JsonObject();
            if (session.capabilities["positionEncoding"]?.ToString() is { } encoding && encoding != "utf-16")
                throw new IOException("The server selected an unsupported position encoding: " + encoding);
            await session.connection.NotifyAsync("initialized", new { }, token);
            await session.connection.NotifyAsync("workspace/didChangeConfiguration", new { settings = WorkspaceLanguageConfiguration.Settings(options, workspace, server.Id) }, token);
            if (session.projectLoading)
            {
                session.log.Add("project", "Loading project graph. " + session.projectModel.Description);
                var completed = await Task.WhenAny(session.projectInitialized.Task, session.projectActionReported.Task).WaitAsync(TimeSpan.FromMinutes(3), token);
                if (completed == session.projectActionReported.Task) throw new IOException("Project initialization needs configuration: " + session.projectAction);
                if (!session.projectRestores.IsEmpty) await Task.WhenAll(session.projectRestores.Values).WaitAsync(token);
                session.projectLoading = false;
                session.detail = "Project graph loaded";
                session.log.Add("project", session.projectModel.Description);
            }
            try
            {
                session.watcher = new(workspace) { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName };
                session.watcher.Changed += (_, e) => session.Changed(e.FullPath, 2);
                session.watcher.Created += (_, e) => session.Changed(e.FullPath, 1);
                session.watcher.Deleted += (_, e) => session.Changed(e.FullPath, 3);
                session.watcher.Renamed += (_, e) => { session.Changed(e.OldFullPath, 3); session.Changed(e.FullPath, 1); };
                session.watcher.Error += (_, _) => session.detail = "File watching lost events; Retry to reindex.";
                session.watcher.EnableRaisingEvents = true;
            }
            catch (Exception error) when (error is IOException or PlatformNotSupportedException)
            { session.detail = "File watcher unavailable: " + error.Message; }
            return session;
        }
        catch (Exception failure)
        {
            var expired = server.Id == "intellij" && await session.connection.ObserveExitAsync() == 7;
            session.Terminate("it did not finish starting: " + failure.Message, failure: true);
            await session.DisposeAsync();
            if (expired) throw new IntelliJExpiredException();
            throw;
        }
    }

    private void Changed(string path, int type)
    {
        var relative = Path.GetRelativePath(workspace, path);
        if (relative.Split(Path.DirectorySeparatorChar).Any(part => part is ".git" or ".tools" or "node_modules" or "bin" or "obj" or ".bench" or "target")) return;
        changed[UriOf(path)] = type;
    }

    internal async Task<LanguageRequest[]> DraftsAsync(CancellationToken token)
    {
        lifetime.Cancel();
        await gate.WaitAsync(token);
        try
        {
            return documents.Where(pair => pair.Value.Dirty).Select(pair => new LanguageRequest(workspace,
                Path.GetRelativePath(workspace, new Uri(pair.Key).LocalPath), Text: pair.Value.Text,
                DocumentId: pair.Value.Owner, Version: pair.Value.ClientVersion, Dirty: true)).ToArray();
        }
        finally { gate.Release(); }
    }

    internal async Task RestoreDraftsAsync(LanguageRequest[] drafts, CancellationToken token)
    {
        foreach (var draft in drafts)
        {
            var path = ProjectServices.Contain(workspace, draft.Path); var uri = UriOf(path);
            var document = new Document(draft.Text!, ++nextDocumentVersion, draft.DocumentId, draft.Version, true);
            documents[uri] = document;
            await connection.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = LanguageServers.LanguageId(path, server), version = document.Version, text = document.Text } }, token);
        }
        HasDrafts = documents.Values.Any(value => value.Dirty);
    }

    internal async Task<LanguageResult> WorkspaceDiagnosticsAsync(CancellationToken token)
    {
        string[] paths;
        await gate.WaitAsync(token);
        try { paths = documents.Keys.Select(Relative).OfType<string>().ToArray(); }
        finally { gate.Release(); }
        foreach (var path in paths) await ExecuteAsync(new(workspace, path, "diagnostics"), token);
        return await ExecuteAsync(new(workspace, Operation: "workspaceDiagnostics"), token);
    }

    internal async Task<LanguageResult> ExecuteAsync(LanguageRequest request, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        token = linked.Token;
        await gate.WaitAsync(token);
        try
        {
            if (connection.Exited)
            {
                diagnostics.Clear();
                if (Expired) return new("expired", IntelliJDistribution.Expired, server.Id, request.Version) { Freshness = "unavailable" };
                throw new IOException("The language server exited.");
            }
            if (request.Operation is "retry" or "status") return new(NeedsConfiguration ? "actionRequired" : IsIndexing ? "initializing" : "ready", ProjectDetail, server.Id, request.Version) { SemanticColoring = SemanticColoring, Coverage = projectModel.Description, Freshness = NeedsConfiguration || IsIndexing || projectModel.Truncated ? "partial" : "current" };
            if (request.Operation == "workspaceDiagnostics")
            {
                var coverage = "open documents";
                if (capabilities["diagnosticProvider"] is JsonObject provider && provider["workspaceDiagnostics"]?.GetValue<bool>() == true)
                {
                    var report = await connection.RequestAsync("workspace/diagnostic", new { identifier = provider["identifier"]?.ToString(), previousResultIds = Array.Empty<object>() }, token);
                    foreach (var file in report?["items"] as JsonArray ?? [])
                        if (file?["uri"]?.ToString() is { } reportUri && file["kind"]?.ToString() == "full" && Relative(reportUri) is { } reportPath)
                            diagnostics[reportUri] = new(file["version"]?.GetValue<int>(), Diagnostics(file["items"], reportPath));
                    coverage = "workspace";
                }
                return new("ready", detail + (HasDrafts ? "; includes an editor's unsaved buffer" : ""), server.Id)
                {
                    Diagnostics = diagnostics.Where(pair => !documents.TryGetValue(pair.Key, out var current) || pair.Value.Version is null || pair.Value.Version == current.Version).SelectMany(pair => pair.Value.Items).Distinct().ToArray(),
                    Freshness = "partial",
                    Coverage = coverage
                };
            }
            var virtualSource = IsVirtual(request.Path);
            var path = virtualSource ? request.Path : Path.IsPathFullyQualified(request.Path) ? request.Path : ProjectServices.Contain(workspace, request.Path);
            var uri = virtualSource ? path : UriOf(path);
            if (request.Operation == "close")
            {
                if (documents.TryGetValue(uri, out var closed) && closed.Owner == request.DocumentId)
                {
                    await connection.NotifyAsync("textDocument/didClose", new { textDocument = new { uri } }, token);
                    documents.Remove(uri); diagnostics.TryRemove(uri, out _);
                    HasDrafts = documents.Values.Any(value => value.Dirty);
                }
                return new("closed", Server: server.Id, Version: request.Version);
            }
            var changes = changed.ToArray();
            foreach (var change in changes) changed.TryRemove(change);
            if (changes.Length > 0)
                await connection.NotifyAsync("workspace/didChangeWatchedFiles", new { changes = changes.Select(change => new { uri = change.Key, type = change.Value }).ToArray() }, token);
            // An open document keeps the server's view of the file, so one edited on disk is resynchronized here: otherwise
            // files that depend on it report phantom errors (a member "does not exist") until it is diagnosed itself.
            foreach (var change in changes)
                if (change.Key != uri && change.Value != 3 && documents.TryGetValue(change.Key, out var open) && !open.Dirty)
                    await RefreshOpenAsync(change.Key, open, token);
            documents.TryGetValue(uri, out var document);
            if (document is not null && request.Text is not null && document.Dirty && document.Owner != request.DocumentId && document.Text != request.Text)
                return new("conflict", "Another editor owns a different unsaved buffer for this file.", server.Id, request.Version);
            if (document is not null && document.Owner == request.DocumentId && request.Text is not null && request.Version < document.ClientVersion)
                return new("stale", "A newer document version is already being analyzed.", server.Id, request.Version);
            var dependency = virtualSource ? await ReadVirtualAsync(uri, token) : null;
            var text = request.Text ?? (document is { Dirty: true } ? document.Text : dependency?.Text ?? await ReadAsync(path, token));
            if (request.Operation == "source") return SourceResult(request, text);
            var saved = document is { Dirty: true } && request.Text is not null && !request.Dirty;
            if (document is null)
            {
                document = new(text, ++nextDocumentVersion, request.Text is null ? "" : request.DocumentId, request.Version, request.Dirty);
                documents[uri] = document;
                await connection.NotifyAsync("textDocument/didOpen", new { textDocument = new { uri, languageId = dependency?.Language ?? LanguageServers.LanguageId(path, server), version = document.Version, text } }, token);
            }
            else if (document.Text != text)
            {
                var old = document.Text;
                document = new(text, ++nextDocumentVersion, request.Text is null ? document.Owner : request.DocumentId, request.Version, request.Dirty);
                documents[uri] = document; diagnostics.TryRemove(uri, out _);
                var sync = capabilities["textDocumentSync"];
                var kind = sync is JsonObject ? sync["change"]?.GetValue<int>() ?? 0 : sync?.GetValue<int>() ?? 0;
                if (kind == 0) throw new IOException("The server does not support document changes.");
                object[] contentChanges = kind == 2
                    ? [new { range = new { start = new { line = 0, character = 0 }, end = End(old) }, text }]
                    : [new { text }];
                await connection.NotifyAsync("textDocument/didChange", new { textDocument = new { uri, version = document.Version }, contentChanges }, token);
            }
            else if (request.Text is not null)
                documents[uri] = document = document with { Owner = request.DocumentId, ClientVersion = request.Version, Dirty = request.Dirty };
            HasDrafts = documents.Values.Any(value => value.Dirty);
            if (saved && capabilities["textDocumentSync"] is JsonObject synchronization && synchronization["save"] is { } save && save.ToJsonString() != "false")
                await connection.NotifyAsync("textDocument/didSave", new { textDocument = new { uri }, text }, token);

            var result = new LanguageResult("ready", NeedsConfiguration ? projectAction : detail, server.Id, request.Version) { SemanticColoring = SemanticColoring, Coverage = projectModel.Description };
            if (request.Operation is "inspect" or "diagnostics" or "fileDiagnostics")
            {
                var fresh = "pending";
                LanguageItem[] items = [];
                if (capabilities["diagnosticProvider"] is JsonObject provider)
                {
                    var report = await connection.RequestAsync("textDocument/diagnostic", new { textDocument = new { uri }, identifier = provider["identifier"]?.ToString() }, token);
                    if (report?["kind"]?.ToString() == "full")
                    {
                        items = Diagnostics(report["items"], request.Path); fresh = "current";
                        diagnostics[uri] = new(document.Version, items);
                    }
                    if (request.Operation == "diagnostics" && provider["workspaceDiagnostics"]?.GetValue<bool>() == true)
                    {
                        var workspaceReport = await connection.RequestAsync("workspace/diagnostic", new { identifier = provider["identifier"]?.ToString(), previousResultIds = Array.Empty<object>() }, token);
                        foreach (var file in workspaceReport?["items"] as JsonArray ?? [])
                            if (file?["uri"]?.ToString() is { } reportUri && file["kind"]?.ToString() == "full" && Relative(reportUri) is { } reportPath)
                                diagnostics[reportUri] = new(file["version"]?.GetValue<int>(), Diagnostics(file["items"], reportPath));
                        result = result with { Coverage = "workspace" };
                    }
                }
                else if (diagnostics.TryGetValue(uri, out var published))
                {
                    if (published.Version == document.Version) { items = published.Items; fresh = "current"; }
                    else if (published.Version is null) { items = published.Items; fresh = "unversioned"; }
                }
                result = result with
                {
                    Diagnostics = request.Operation == "diagnostics" ? diagnostics.Where(pair => !documents.TryGetValue(pair.Key, out var current) || pair.Value.Version is null || pair.Value.Version == current.Version).SelectMany(pair => pair.Value.Items).Distinct().ToArray() : items,
                    Freshness = request.Operation == "diagnostics" && result.Coverage != "workspace" ? "partial" : fresh,
                    Detail = request.Text is null && document.Dirty ? detail + "; includes an editor's unsaved buffer" : detail
                };
                if (request.Operation == "inspect" && capabilities["semanticTokensProvider"] is JsonObject semantic && semantic["full"] is { } full && full.ToJsonString() != "false")
                {
                    var tokens = await connection.RequestAsync("textDocument/semanticTokens/full", new { textDocument = new { uri } }, token);
                    result = result with { Tokens = Semantic(tokens?["data"], semantic["legend"]?["tokenTypes"], semantic["legend"]?["tokenModifiers"], request.Path) };
                }
                return result;
            }
            if (NeedsConfiguration) return result with { State = "actionRequired", Detail = ProjectDetail, Freshness = "partial", Coverage = projectModel.Description };
            if (projectRestores.Values.Any(task => task.IsFaulted)) return result with { State = "error", Detail = ProjectDetail, Freshness = "partial", Coverage = projectModel.Description };
            if (IsIndexing) return result with { State = "initializing", Detail = ProjectDetail, Freshness = "partial" };
            var method = request.Operation switch
            {
                "symbols" or "symbolBody" => "textDocument/documentSymbol",
                "workspaceSymbols" => "workspace/symbol",
                _ => "textDocument/" + request.Operation
            };
            var capability = request.Operation switch
            {
                "symbols" or "symbolBody" => "documentSymbolProvider",
                "workspaceSymbols" => "workspaceSymbolProvider",
                _ => request.Operation + "Provider"
            };
            if ((capabilities[capability] is null || capabilities[capability]!.ToJsonString() == "false") && !registeredCapabilities.Values.Contains(method))
                return result with { State = "unsupported", Detail = $"{server.Id} does not support {request.Operation}." };
            if (request.Query.Length > 0 && request.Operation is "definition" or "typeDefinition" or "implementation" or "references" or "hover")
            {
                if ((capabilities["documentSymbolProvider"] is null || capabilities["documentSymbolProvider"]!.ToJsonString() == "false") && !registeredCapabilities.Values.Contains("textDocument/documentSymbol"))
                    return result with { State = "unsupported", Detail = "Symbol-selected navigation requires a document outline. Use an explicit position." };
                var outline = await connection.RequestAsync("textDocument/documentSymbol", new { textDocument = new { uri } }, token);
                // An exact qualified name wins; otherwise a simple or partly qualified name matches the end of the outline name.
                var selections = SymbolSelections(outline).ToArray();
                var exact = selections.Any(item => item.Name == request.Query);
                bool Named(string name) => exact ? name == request.Query : LanguageUsage.EndsWithName(name, request.Query);
                var matches = selections.Where(item => Named(item.Name)).ToArray();
                if (matches.Length != 1 || matches[0].Range is null)
                    return result with
                    {
                        State = matches.Length == 0 ? "notFound" : matches.Length > 1 ? "ambiguous" : "unsupported",
                        Detail = matches.Length == 0 ? LanguageUsage.OutlineNotFound(request) : matches.Length > 1 ? LanguageUsage.OutlineAmbiguous(request, matches.Length) : "The outline gives this symbol no selection range; use its line and column (zero-based).",
                        Locations = Symbols(outline, request.Path).Where(item => Named(item.Text)).Skip(request.Offset).Take(request.Limit).ToArray(),
                        Total = matches.Length,
                        Truncated = request.Offset + request.Limit < matches.Length,
                        Freshness = "partial"
                    };
                var position = matches[0].Range!["start"]!;
                request = request with { Line = position["line"]!.GetValue<int>(), Column = position["character"]!.GetValue<int>() };
            }
            object parameters = request.Operation == "workspaceSymbols" ? new { query = request.Query }
                : request.Operation == "references" ? new { textDocument = new { uri }, position = new { line = request.Line, character = request.Column }, context = new { includeDeclaration = true } }
                : new { textDocument = new { uri }, position = new { line = request.Line, character = request.Column } };
            var locations = await connection.RequestAsync(method, parameters, token);
            return await NavigationResultAsync(result, locations, request, token);
        }
        catch (IOException) when (server.Id == "intellij")
        {
            if (!await ObserveExpiryAsync()) throw;
            diagnostics.Clear();
            return new("expired", IntelliJDistribution.Expired, server.Id, request.Version) { Freshness = "unavailable" };
        }
        finally { gate.Release(); }
    }

    private async Task RefreshOpenAsync(string uri, Document open, CancellationToken token)
    {
        string text;
        try { text = await ReadAsync(new Uri(uri).LocalPath, token); }
        catch (IOException) { return; }
        catch (UnauthorizedAccessException) { return; }
        if (text == open.Text) return;
        var sync = capabilities["textDocumentSync"];
        var kind = sync is JsonObject ? sync["change"]?.GetValue<int>() ?? 0 : sync?.GetValue<int>() ?? 0;
        if (kind == 0) return;
        var document = open with { Text = text, Version = ++nextDocumentVersion };
        documents[uri] = document; diagnostics.TryRemove(uri, out _);
        object[] contentChanges = kind == 2 ? [new { range = new { start = new { line = 0, character = 0 }, end = End(open.Text) }, text }] : [new { text }];
        await connection.NotifyAsync("textDocument/didChange", new { textDocument = new { uri, version = document.Version }, contentChanges }, token);
    }

    private static async Task<string> ReadAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("File is too large for language analysis.");
        var text = await File.ReadAllTextAsync(path, token);
        if (text.Length > 1_048_576) throw new IOException("Language analysis is limited to 1 Mi characters per document.");
        return text;
    }

    private object? OnClientRequest(string method, JsonNode? parameters)
    {
        if (method == "client/registerCapability")
            foreach (var registration in parameters?["registrations"] as JsonArray ?? [])
                if (registration?["id"] is { } id && registration["method"] is { } name) registeredCapabilities[id.ToString()] = name.ToString();
        if (method == "client/unregisterCapability")
            foreach (var registration in parameters?["unregisterations"] as JsonArray ?? [])
                if (registration?["id"] is { } id) registeredCapabilities.TryRemove(id.ToString(), out _);
        if (method == "window/showMessageRequest")
        {
            var message = parameters?["message"]?.ToString() ?? "Server requested an action";
            var choices = (parameters?["actions"] as JsonArray ?? []).Select(item => item?["title"]?.ToString() ?? "").Where(title => title.Length > 0);
            var description = message + "; choices: " + string.Join(", ", choices) + ". See server logs and workspace configuration.";
            log.Add("server message", description);
            var severity = parameters?["type"]?.GetValue<int>() ?? 3;
            // Warning dismissal prompts (for example scalafmt dialect notices) do not block indexing.
            if (choices.Any() && (severity == 1 || projectLoading && severity == 3))
            {
                projectAction = description;
                projectActionReported.TrySetResult();
            }
        }
        if (server.Id != "csharp" || method != "workspace/_roslyn_projectNeedsRestore") return null;
        var paths = (parameters?["projectFilePaths"] as JsonArray ?? []).Select(value => value!.GetValue<string>())
            .Where(path => Path.IsPathFullyQualified(path) && LanguageProjectModel.Contains(workspace, path)).Distinct().ToArray();
        if (paths.Length == 0) return null;
        var key = string.Join("\n", paths.Order(StringComparer.Ordinal));
        projectRestores.GetOrAdd(key, _ => Task.Run(async () =>
        {
            try
            {
                log.Add("project restore", key);
                await connection.RequestAsync("workspace/_roslyn_restore", new { projectFilePaths = paths }, lifetime.Token, TimeSpan.FromMinutes(3));
            }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            {
                detail = "Project restore failed: " + error.Message;
                log.Add("project restore", detail);
                throw;
            }
        }));
        return null;
    }

    private void OnNotification(string method, JsonNode? parameters)
    {
        if (method == "experimental/serverStatus" && server.Id == "rust")
        {
            if (parameters?["quiescent"]?.GetValue<bool>() == true) indexing.TryRemove("rust-project", out _);
            else indexing["rust-project"] = parameters?["message"]?.ToString() ?? "Loading Cargo project graph…";
        }
        if (method == "$/analyzerStatus" && server.Id == "dart")
        {
            if (parameters?["isAnalyzing"]?.GetValue<bool>() == true) indexing["dart-analysis"] = "Resolving Dart analysis contexts…";
            else indexing.TryRemove("dart-analysis", out _);
        }
        if (method == "workspace/projectInitializationComplete") projectInitialized.TrySetResult();
        if (method == "workspace/_roslyn_projectNeedsRestore") OnClientRequest(method, parameters);
        if (method == "textDocument/publishDiagnostics" && parameters?["uri"]?.ToString() is { } uri && Relative(uri) is { } path)
            diagnostics[uri] = new(parameters["version"]?.GetValue<int>(), Diagnostics(parameters["diagnostics"], path));
        if (method == "$/progress" && parameters?["value"] is { } progress)
        {
            var id = parameters["token"]?.ToJsonString() ?? "progress";
            if (progress["kind"]?.ToString() == "end") indexing.TryRemove(id, out _);
            else if (progress["kind"]?.ToString() is "begin" or "report") indexing[id] = progress["message"]?.ToString() ?? progress["title"]?.ToString() ?? "Indexing…";
            detail = indexing.IsEmpty ? "Ready" : string.Join("; ", indexing.Values);
        }
    }

    private LanguageItem[] Diagnostics(JsonNode? value, string path) => (value as JsonArray ?? [])
        .Where(item => item?["range"] is not null)
        .Select(item => Item(path, item!["range"]!, item["message"]?.ToString() ?? "", item["severity"]?.ToString() ?? "1", item["source"]?.ToString() ?? server.Id)).ToArray();

    private IEnumerable<LanguageItem> Locations(JsonNode? node, string fallback)
    {
        foreach (var value in node is JsonArray array ? array : node is null ? [] : new JsonArray(node.DeepClone()))
        {
            if (value is null) continue;
            var location = value["location"] ?? value;
            var uri = location["targetUri"]?.ToString() ?? location["uri"]?.ToString();
            var path = uri is null ? fallback : Relative(uri) ?? (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri);
            var range = location["targetSelectionRange"] ?? location["selectionRange"] ?? location["range"];
            if (path is not null && range is not null) yield return Item(path, range, value["name"]?.ToString() ?? "", value["kind"]?.ToString() ?? "", server.Id);
            foreach (var child in Locations(value["children"], fallback)) yield return child;
        }
    }

    private bool SemanticColoring => capabilities["semanticTokensProvider"] is JsonObject semantic && semantic["full"] is { } full && full.ToJsonString() != "false";

    private static LanguageItem[] Semantic(JsonNode? data, JsonNode? legend, JsonNode? modifierLegend, string path)
    {
        if (data is not JsonArray values || legend is not JsonArray types) return [];
        var result = new List<LanguageItem>(); var line = 0; var column = 0;
        for (var i = 0; i + 4 < values.Count; i += 5)
        {
            var delta = values[i]!.GetValue<int>(); line += delta;
            column = (delta == 0 ? column : 0) + values[i + 1]!.GetValue<int>();
            var type = values[i + 3]!.GetValue<int>(); var length = values[i + 2]!.GetValue<int>();
            if (line >= 0 && column >= 0 && length > 0 && type >= 0 && type < types.Count)
            {
                var bits = values[i + 4]!.GetValue<uint>();
                var modifiers = modifierLegend is JsonArray names ? string.Join(' ', Enumerable.Range(0, Math.Min(32, names.Count))
                    .Where(index => (bits & (1u << index)) != 0).Select(index => names[index]!.ToString())) : "";
                result.Add(new(path, line, column, line, column + length, Kind: types[type]!.ToString()) { Modifiers = modifiers });
            }
        }
        return result.ToArray();
    }

    private string? Relative(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || !parsed.IsFile) return null;
        var path = Path.GetRelativePath(workspace, parsed.LocalPath);
        return path == ".." || path.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ? null : path;
    }

    private static LanguageItem Item(string path, JsonNode range, string text, string kind, string source)
        => new(path, range["start"]!["line"]!.GetValue<int>(), range["start"]!["character"]!.GetValue<int>(),
            range["end"]!["line"]!.GetValue<int>(), range["end"]!["character"]!.GetValue<int>(), text, kind, source);
    private static object End(string text)
    {
        var line = text.Count(character => character == '\n');
        return new { line, character = text.Length - (text.LastIndexOf('\n') + 1) };
    }
    private static string UriOf(string path) => new Uri(path).AbsoluteUri;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        watcher?.Dispose(); lifetime.Cancel();
        await gate.WaitAsync();
        try
        {
            await connection.DisposeAsync(); lifetime.Dispose();
            if (ownedCache is { } cache)
                try { if (Directory.Exists(cache)) Directory.Delete(cache, true); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { log.Add("cleanup", error.Message); }
        }
        finally { gate.Release(); }
    }
}