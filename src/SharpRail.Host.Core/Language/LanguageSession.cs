using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

internal sealed partial class LanguageSession : IAsyncDisposable, ILanguageServerSession
{
    private readonly LanguageConnection connection;
    private readonly TaskCompletionSource projectInitialized = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private LanguageProjectModel projectModel = null!;
    private bool projectLoading;
    private readonly ConcurrentDictionary<string, string> registeredCapabilities = new();
    private string projectAction = "";
    private string unavailable = "";
    private string pluginState = "";
    private string ActionText => unavailable.Length > 0 ? unavailable : projectAction;
    internal bool NeedsConfiguration => ActionText.Length > 0;
    /// <summary>The state the providing plugin published through <see cref="ILanguageServerSession.SetState"/> (JSON), or empty.</summary>
    internal string PluginState => pluginState;
    private readonly TaskCompletionSource projectActionReported = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ConcurrentDictionary<string, string> indexing = new();
    internal bool IsIndexing => projectLoading || !indexing.IsEmpty || projectRestores.Values.Any(task => !task.IsCompleted) || Reloading || Settling;
    internal string Status => ActionText.Length > 0 ? ActionText : detail;
    /// <summary>What an answer held back while the project graph loads says, with what the server is doing.</summary>
    internal string LoadingDetail => "The project graph is still loading (" + (Reloading ? "reloading projects after watched project files changed" : detail) + "), so this answer could be incomplete; call again. " + projectModel.Description;
    internal string ProjectDetail => (ActionText.Length > 0 ? ActionText : detail) + ". " + projectModel.Description;
    private readonly ConcurrentDictionary<string, Task> projectRestores = new();
    private readonly LanguageLog log;
    private JsonArray ProjectFolders => [.. projectModel.WorkspaceFolders.Select(path => (JsonNode)new JsonObject { ["uri"] = UriOf(path + Path.DirectorySeparatorChar), ["name"] = Path.GetFileName(path) })];
    private readonly string workspace;
    private readonly LanguageServer server;
    private readonly SemaphoreSlim gate = new(1);
    private readonly Dictionary<string, Document> documents = [];
    private readonly ConcurrentDictionary<string, Published> diagnostics = new();
    private readonly CancellationTokenSource lifetime = new();
    private JsonNode capabilities = new JsonObject();
    private string detail = "Ready";
    private int nextDocumentVersion;
    private int disposed;
    private string? ownedCache;
    internal bool HasDrafts { get; private set; }
    /// <summary>Whether an editor buffer (a document with an owner) is open in this session; such a server is never stopped by the limits.</summary>
    internal bool HasEditors { get; private set; }
    /// <summary>The last sampled resident memory of the server process, in bytes; zero before the first sample.</summary>
    internal long MemoryBytes => connection.MemoryBytes;
    internal bool Exited => connection.Exited;
    internal int? ProcessId => connection.ProcessId;
    internal LanguageExit? ExitRecord => connection.ExitRecord;
    /// <summary>Names why SharpRail is about to end this server, so its exit record explains the death.</summary>
    internal void Terminate(string reason, bool failure = false) => connection.Terminate(reason, failure);
    internal void SampleMemory() => connection.SampleMemory();
    private sealed record Document(string Text, int Version, string Owner, long ClientVersion, bool Dirty);
    private sealed record Published(int? Version, LanguageItem[] Items);

    private LanguageSession(ProcessStartInfo start, string workspace, LanguageServer server, WorkspaceLanguageOptions options, LanguageLog log, Action<LanguageExit>? onExit, ILanguageProcess? link)
    {
        this.workspace = workspace; this.server = server; this.log = log;
        var settings = WorkspaceLanguageConfiguration.Settings(options, workspace, server.Id);
        connection = new(start, workspace, log, server.Id, onExit, link)
        {
            Notification = OnNotification,
            ClientRequest = OnClientRequest,
            AcceptsShowDocument = server.Spec.AcceptsShowDocument,
            WorkspaceFolders = () => ProjectFolders,
            ApplyEdit = CaptureEdit,
            Configuration = section => section.Length == 0 ? settings : settings[section] ?? section.Split('.').Aggregate((JsonNode?)settings, (node, key) => node is JsonObject obj ? obj[key] : null)
        };
    }

    /// <param name="sessionDirectory">The launch's own directory (<see cref="ILanguageServerInstallation.SessionDirectory"/>), removed when the session ends.</param>
    internal static async Task<LanguageSession> StartAsync(ProcessStartInfo start, string workspace, LanguageServer server, CancellationToken token, WorkspaceLanguageOptions? options = null, LanguageLog? log = null, string? storage = null, Action<LanguageExit>? onExit = null, string? sessionDirectory = null, ILanguageProcess? link = null)
    {
        options ??= new();
        log ??= new();
        var model = LanguageProjectModel.Discover(workspace, server, token, options);
        var initialization = WorkspaceLanguageConfiguration.Initialization(options, workspace, server.Id, model.Manifests, storage ?? "", log, sessionDirectory ?? "");
        var session = new LanguageSession(start, workspace, server, options, log, onExit, link) { ownedCache = sessionDirectory };
        session.projectModel = model;
        session.projectLoading = server.Spec.ProjectLoadedNotification.Length > 0 && server.Spec.LoadsProjects(initialization) && session.projectModel.Manifests.Length > 0;
        session.StartWatching();
        var initializing = CodeIntelligenceLog.Begin("host.initialize", ("workspace", workspace), ("server", server.Id));
        try
        {
            var result = await session.connection.RequestAsync("initialize", new JsonObject
            {
                ["processId"] = Environment.ProcessId,
                ["clientInfo"] = new JsonObject { ["name"] = "SharpRail", ["version"] = "1.0" },
                ["rootUri"] = UriOf(workspace + Path.DirectorySeparatorChar),
                ["initializationOptions"] = initialization.DeepClone(),
                ["workDoneToken"] = server.Spec.InitializeProgressToken,
                ["workspaceFolders"] = session.ProjectFolders,
                ["capabilities"] = new JsonObject
                {
                    ["general"] = new JsonObject { ["positionEncodings"] = new JsonArray("utf-16") },
                    ["window"] = server.Spec.AcceptsShowDocument
                        ? new JsonObject { ["workDoneProgress"] = true, ["showDocument"] = new JsonObject { ["support"] = true } }
                        : new JsonObject { ["workDoneProgress"] = true },
                    ["experimental"] = new JsonObject(server.Spec.ExperimentalClientCapabilities.Select(name => KeyValuePair.Create(name, (JsonNode?)true))),
                    ["workspace"] = new JsonObject
                    {
                        ["applyEdit"] = true,
                        ["workspaceEdit"] = new JsonObject { ["documentChanges"] = true },
                        ["executeCommand"] = new JsonObject { ["dynamicRegistration"] = false },
                        ["workspaceFolders"] = true,
                        ["configuration"] = true,
                        ["didChangeWatchedFiles"] = new JsonObject { ["dynamicRegistration"] = true, ["relativePatternSupport"] = true }
                    },
                    ["textDocument"] = new JsonObject
                    {
                        ["references"] = new JsonObject { ["dynamicRegistration"] = true },
                        ["codeAction"] = new JsonObject
                        {
                            ["dynamicRegistration"] = true,
                            ["isPreferredSupport"] = true,
                            ["disabledSupport"] = true,
                            ["dataSupport"] = true,
                            ["resolveSupport"] = new JsonObject { ["properties"] = new JsonArray("edit") },
                            ["codeActionLiteralSupport"] = new JsonObject { ["codeActionKind"] = new JsonObject { ["valueSet"] = new JsonArray("", "quickfix", "refactor", "refactor.extract", "refactor.inline", "refactor.rewrite", "source", "source.organizeImports", "source.fixAll") } }
                        },
                        ["synchronization"] = new JsonObject { ["didSave"] = true },
                        ["publishDiagnostics"] = new JsonObject { ["versionSupport"] = true, ["relatedInformation"] = true },
                        ["diagnostic"] = new JsonObject { ["dynamicRegistration"] = false, ["relatedDocumentSupport"] = false },
                        ["definition"] = new JsonObject { ["linkSupport"] = true },
                        ["typeDefinition"] = new JsonObject { ["linkSupport"] = true },
                        ["implementation"] = new JsonObject { ["linkSupport"] = true },
                        ["documentSymbol"] = new JsonObject { ["hierarchicalDocumentSymbolSupport"] = true },
                        ["semanticTokens"] = new JsonObject
                        {
                            ["requests"] = new JsonObject { ["full"] = true },
                            ["formats"] = new JsonArray("relative"),
                            ["tokenTypes"] = new JsonArray("namespace", "type", "class", "enum", "interface", "struct", "typeParameter", "parameter", "variable", "property", "field", "delegate", "enumMember", "event", "function", "method", "macro", "keyword", "modifier", "comment", "string", "number", "regexp", "operator", "decorator"),
                            ["tokenModifiers"] = new JsonArray("declaration", "definition", "readonly", "static", "deprecated", "abstract", "async", "modification", "documentation", "defaultLibrary"),
                            ["augmentsSyntaxTokens"] = false,
                            ["overlappingTokenSupport"] = false,
                            ["multilineTokenSupport"] = false
                        }
                    }
                }
            }, token, server.Spec.InitializeTimeout);
            initializing.Succeed("answered");
            session.capabilities = result?["capabilities"]?.DeepClone() ?? new JsonObject();
            if (session.capabilities["positionEncoding"]?.ToString() is { } encoding && encoding != "utf-16")
                throw new IOException("The server selected an unsupported position encoding: " + encoding);
            await session.connection.NotifyAsync("initialized", new JsonObject(), token);
            await session.connection.NotifyAsync("workspace/didChangeConfiguration", new JsonObject { ["settings"] = WorkspaceLanguageConfiguration.Settings(options, workspace, server.Id) }, token);
            if (session.projectLoading)
            {
                session.log.Add("project", "Loading project graph. " + session.projectModel.Description);
                var graph = CodeIntelligenceLog.Begin("host.project-graph", ("workspace", workspace), ("server", server.Id));
                try
                {
                    var completed = await Task.WhenAny(session.projectInitialized.Task, session.projectActionReported.Task).WaitAsync(TimeSpan.FromMinutes(3), token);
                    if (completed == session.projectActionReported.Task) throw new IOException("Project initialization needs configuration: " + session.projectAction);
                    if (!session.projectRestores.IsEmpty) await Task.WhenAll(session.projectRestores.Values).WaitAsync(token);
                    session.projectLoading = false;
                    session.detail = "Project graph loaded";
                    session.log.Add("project", session.projectModel.Description);
                    graph.Succeed("loaded");
                }
                catch (Exception graphFailure) { graph.Fail(graphFailure); throw; }
            }
            // Files that changed while the graph loaded are reported now that the server has registered its watchers.
            session.ServeWatched();
            return session;
        }
        catch (Exception failure)
        {
            initializing.Fail(failure);
            session.Terminate("it did not finish starting: " + failure.Message, failure: true);
            await session.DisposeAsync();
            throw;
        }
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
            await connection.NotifyAsync("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = LanguageServers.LanguageId(path, server), ["version"] = document.Version, ["text"] = document.Text } }, token);
        }
        HasDrafts = documents.Values.Any(value => value.Dirty); HasEditors = documents.Values.Any(value => value.Owner.Length > 0);
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
                throw new IOException("The language server exited.");
            }
            if (unavailable.Length > 0) return new("actionRequired", unavailable, server.Id, request.Version) { SemanticColoring = SemanticColoring, Freshness = "unavailable", Coverage = projectModel.Description };
            if (request.Operation is "retry" or "status") return new(NeedsConfiguration ? "actionRequired" : IsIndexing ? "initializing" : "ready", ProjectDetail, server.Id, request.Version) { SemanticColoring = SemanticColoring, Coverage = projectModel.Description, Freshness = NeedsConfiguration || IsIndexing || projectModel.Truncated ? "partial" : "current" };
            if (request.Operation == "workspaceDiagnostics")
            {
                var coverage = "open documents";
                if (capabilities["diagnosticProvider"] is JsonObject provider && provider["workspaceDiagnostics"]?.GetValue<bool>() == true)
                {
                    var report = await connection.RequestAsync("workspace/diagnostic", new JsonObject { ["identifier"] = provider["identifier"]?.ToString(), ["previousResultIds"] = new JsonArray() }, token);
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
                    await connection.NotifyAsync("textDocument/didClose", TextDocument(uri), token);
                    documents.Remove(uri); diagnostics.TryRemove(uri, out _);
                    HasDrafts = documents.Values.Any(value => value.Dirty); HasEditors = documents.Values.Any(value => value.Owner.Length > 0);
                }
                return new("closed", Server: server.Id, Version: request.Version);
            }
            await SendWatchedAsync(token);
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
                await connection.NotifyAsync("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri, ["languageId"] = dependency?.Language ?? LanguageServers.LanguageId(path, server), ["version"] = document.Version, ["text"] = text } }, token);
            }
            else if (document.Text != text)
            {
                var old = document.Text;
                document = new(text, ++nextDocumentVersion, request.Text is null ? document.Owner : request.DocumentId, request.Version, request.Dirty);
                documents[uri] = document; diagnostics.TryRemove(uri, out _);
                var sync = capabilities["textDocumentSync"];
                var kind = sync is JsonObject ? sync["change"]?.GetValue<int>() ?? 0 : sync?.GetValue<int>() ?? 0;
                if (kind == 0) throw new IOException("The server does not support document changes.");
                await connection.NotifyAsync("textDocument/didChange", DidChange(uri, document.Version, kind, old, text), token);
            }
            else if (request.Text is not null)
                documents[uri] = document = document with { Owner = request.DocumentId, ClientVersion = request.Version, Dirty = request.Dirty };
            HasDrafts = documents.Values.Any(value => value.Dirty); HasEditors = documents.Values.Any(value => value.Owner.Length > 0);
            if (saved && capabilities["textDocumentSync"] is JsonObject synchronization && synchronization["save"] is { } save && save.ToJsonString() != "false")
                await connection.NotifyAsync("textDocument/didSave", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["text"] = text }, token);

            var result = new LanguageResult("ready", NeedsConfiguration ? ActionText : detail, server.Id, request.Version) { SemanticColoring = SemanticColoring, Coverage = projectModel.Description };
            if (request.Operation is "inspect" or "diagnostics" or "fileDiagnostics")
            {
                var fresh = "pending";
                LanguageItem[] items = [];
                if (capabilities["diagnosticProvider"] is JsonObject provider)
                {
                    var report = await connection.RequestAsync("textDocument/diagnostic", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["identifier"] = provider["identifier"]?.ToString() }, token);
                    if (report?["kind"]?.ToString() == "full")
                    {
                        items = Diagnostics(report["items"], request.Path); fresh = "current";
                        diagnostics[uri] = new(document.Version, items);
                    }
                    if (request.Operation == "diagnostics" && provider["workspaceDiagnostics"]?.GetValue<bool>() == true)
                    {
                        var workspaceReport = await connection.RequestAsync("workspace/diagnostic", new JsonObject { ["identifier"] = provider["identifier"]?.ToString(), ["previousResultIds"] = new JsonArray() }, token);
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
                // An agent's file check while the project graph loads could name types that are merely not loaded yet: it stays pending.
                if (request.Operation == "fileDiagnostics" && IsIndexing)
                    result = result with { Diagnostics = [], Freshness = "pending", Detail = LoadingDetail };
                if (request.Operation == "inspect" && capabilities["semanticTokensProvider"] is JsonObject semantic && semantic["full"] is { } full && full.ToJsonString() != "false")
                {
                    var tokens = await connection.RequestAsync("textDocument/semanticTokens/full", TextDocument(uri), token);
                    result = result with { Tokens = Semantic(tokens?["data"], semantic["legend"]?["tokenTypes"], semantic["legend"]?["tokenModifiers"], request.Path) };
                }
                return result;
            }
            if (NeedsConfiguration) return result with { State = "actionRequired", Detail = ProjectDetail, Freshness = "partial", Coverage = projectModel.Description };
            if (projectRestores.Values.Any(task => task.IsFaulted)) return result with { State = "error", Detail = ProjectDetail, Freshness = "partial", Coverage = projectModel.Description };
            if (request.Operation is "codeActions" or "applyCodeAction") return await CodeActionsAsync(result, request, uri, document, token);
            // An outline is syntax and answers at once. References, implementations and symbol searches could silently miss what is not
            // loaded yet, so they wait; a definition, type definition or hover that found something is right, and only an empty one waits.
            var loading = IsIndexing;
            if (loading && request.Operation is "references" or "implementation" or "workspaceSymbols")
                return result with { State = "initializing", Detail = LoadingDetail, Freshness = "partial" };
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
                var outline = await connection.RequestAsync("textDocument/documentSymbol", TextDocument(uri), token);
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
            var parameters = request.Operation == "workspaceSymbols" ? new JsonObject { ["query"] = request.Query }
                : new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = uri }, ["position"] = Position(request.Line, request.Column) };
            if (request.Operation == "references") parameters["context"] = new JsonObject { ["includeDeclaration"] = true };
            var locations = await connection.RequestAsync(method, parameters, token);
            var answered = await NavigationResultAsync(result, locations, request, token);
            if (loading && request.Operation is "definition" or "typeDefinition" or "hover" && answered.State == "ready" && answered.Total == 0 && answered.Locations.Count == 0)
                return answered with { State = "initializing", Detail = LoadingDetail, Freshness = "partial" };
            return answered;
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
        await connection.NotifyAsync("textDocument/didChange", DidChange(uri, document.Version, kind, open.Text, text), token);
    }

    private static async Task<string> ReadAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new IOException("File is too large for language analysis.");
        var text = await File.ReadAllTextAsync(path, token);
        if (text.Length > 1_048_576) throw new IOException("Language analysis is limited to 1 Mi characters per document.");
        return text;
    }

    private JsonNode? OnClientRequest(string method, JsonNode? parameters)
    {
        if (method == "client/registerCapability") Register(parameters);
        if (method == "client/unregisterCapability") Unregister(parameters);
        if (method == "client/registerCapability")
            foreach (var registration in parameters?["registrations"] as JsonArray ?? [])
                if (registration?["id"] is { } id && registration["method"] is { } name) registeredCapabilities[id.ToString()] = name.ToString();
        if (method == "client/unregisterCapability")
            foreach (var registration in parameters?["unregisterations"] as JsonArray ?? [])
                if (registration?["id"] is { } id) registeredCapabilities.TryRemove(id.ToString(), out _);
        if (method == "window/showDocument")
        {
            server.Spec.OnMessage(this, method, parameters);
            return new JsonObject { ["success"] = true };
        }
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
        server.Spec.OnMessage(this, method, parameters);
        return null;
    }

    string ILanguageServerSession.Workspace => workspace;

    bool ILanguageServerSession.SupportsCommand(string command) =>
        capabilities["executeCommandProvider"]?["commands"] is JsonArray commands && commands.Any(value => value?.ToString() == command);

    Task<JsonNode?> ILanguageServerSession.RequestAsync(string method, object? parameters, CancellationToken token, TimeSpan? timeout) =>
        connection.RequestAsync(method, parameters as JsonNode ?? (parameters is null ? null : JsonSerializer.SerializeToNode(parameters, PluginJson.TypeInfo(parameters.GetType()))), token, timeout);

    void ILanguageServerSession.SetState(string? state) =>
        pluginState = state is { Length: > 16 * 1024 } ? throw new ArgumentException("Plugin state exceeds 16 KiB.") : state ?? "";

    void ILanguageServerSession.SetUnavailable(string? reason) => unavailable = reason ?? "";

    internal Task<JsonNode?> RunActionAsync(string name, JsonNode? arguments, CancellationToken token) =>
        server.Spec.ActionAsync is { } action ? action(this, name, arguments, token) : throw new ArgumentException("This language server has no actions.");

    void ILanguageServerSession.SetProgress(string key, string? message)
    {
        if (message is null) indexing.TryRemove(key, out _);
        else indexing[key] = message;
    }

    bool ILanguageServerSession.Contains(string path) => LanguageProjectModel.Contains(workspace, path);

    void ILanguageServerSession.Track(string key, string describe, Func<CancellationToken, Task> work) => projectRestores.GetOrAdd(key, _ => Task.Run(async () =>
    {
        try
        {
            log.Add(describe, key);
            await work(lifetime.Token);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException)
        {
            detail = char.ToUpperInvariant(describe[0]) + describe[1..] + " failed: " + error.Message;
            log.Add(describe, detail);
            throw;
        }
    }));

    private void OnNotification(string method, JsonNode? parameters)
    {
        server.Spec.OnMessage(this, method, parameters);
        ObserveLoading(method, parameters);
        if (method.Length > 0 && method == server.Spec.ProjectLoadedNotification) projectInitialized.TrySetResult();
        if (method == "textDocument/publishDiagnostics" && parameters?["uri"]?.ToString() is { } uri && Relative(uri) is { } path)
            diagnostics[uri] = new(parameters["version"]?.GetValue<int>(), Diagnostics(parameters["diagnostics"], path));
        if (method == "$/progress" && parameters?["value"] is { } progress)
        {
            var id = parameters["token"]?.ToJsonString() ?? "progress";
            if (progress["kind"]?.ToString() == "end") indexing.TryRemove(id, out _);
            // A server that opens a token per project and never ends some of them must not grow this bookkeeping, or the detail every poll carries, without bound.
            else if (progress["kind"]?.ToString() is "begin" or "report" && (indexing.Count < MaxProgressTokens || indexing.ContainsKey(id))) indexing[id] = progress["message"]?.ToString() ?? progress["title"]?.ToString() ?? "Indexing…";
            detail = ProgressDetail(indexing);
        }
    }

    /// <summary>The most progress tokens one session tracks at once.</summary>
    internal const int MaxProgressTokens = 256;
    private const int ShownProgressMessages = 3;

    /// <summary>One short line for the open progress tokens: the first few messages (in a stable order) and how many more there are.</summary>
    internal static string ProgressDetail(IReadOnlyDictionary<string, string> tokens)
    {
        if (tokens.Count == 0) return "Ready";
        var shown = tokens.Values.Order(StringComparer.Ordinal).Take(ShownProgressMessages).Select(message => message.Length > 120 ? message[..120] + "…" : message);
        return string.Join("; ", shown) + (tokens.Count > ShownProgressMessages ? $" (+{tokens.Count - ShownProgressMessages} more)" : "");
    }

    private LanguageItem[] Diagnostics(JsonNode? value, string path) => (value as JsonArray ?? [])
        .Where(item => item?["range"] is not null)
        .Select(item => Item(path, item!["range"]!, item["message"]?.ToString() ?? "", item["severity"]?.ToString() ?? "1", item["source"]?.ToString() ?? server.Id) with { Raw = item.ToJsonString() }).ToArray();

    private IEnumerable<LanguageItem> Locations(JsonNode? node, string fallback)
    {
        foreach (var value in node is JsonArray array ? array : node is null ? [] : new JsonArray(node.DeepClone()))
        {
            if (value is null) continue;
            var location = value["location"] ?? value;
            var uri = location["targetUri"]?.ToString() ?? location["uri"]?.ToString();
            var path = uri is null ? fallback : Relative(uri) ?? (Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : uri);
            var range = location["targetSelectionRange"] ?? location["selectionRange"] ?? location["range"];
            if (path is not null && range is not null) yield return Item(path, range, value["name"]?.ToString() ?? "", value["kind"]?.ToString() ?? "", server.Id) with { Container = value["containerName"]?.ToString() ?? "" };
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
    private static JsonObject End(string text) => Position(text.Count(character => character == '\n'), text.Length - (text.LastIndexOf('\n') + 1));
    private static JsonObject Position(int line, int character) => new() { ["line"] = line, ["character"] = character };
    private static JsonObject TextDocument(string uri) => new() { ["textDocument"] = new JsonObject { ["uri"] = uri } };

    private static JsonObject DidChange(string uri, int version, int kind, string old, string text) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = uri, ["version"] = version },
        ["contentChanges"] = new JsonArray(kind == 2
            ? new JsonObject { ["range"] = new JsonObject { ["start"] = Position(0, 0), ["end"] = End(old) }, ["text"] = text }
            : new JsonObject { ["text"] = text })
    };
    private static string UriOf(string path) => new Uri(path).AbsoluteUri;

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        watch?.Dispose(); flushTimer?.Dispose(); lifetime.Cancel();
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