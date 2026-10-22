using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

/// <summary>Host-owned language processes, shared by editor windows and agents, with worktree-local document state.</summary>
public sealed partial class LanguageService : IAsyncDisposable
{
    private readonly object startGate = new();
    /// <summary>The host-wide provider choice: <c>standard</c>, or the id of a server that replaces others (H21); absent, each workspace chooses.</summary>
    internal Func<string>? GlobalProvider { get; init; }
    private readonly ConcurrentDictionary<string, Entry> entries = new();
    private readonly ConcurrentDictionary<string, LanguageLog> logs = new();
    private readonly ConcurrentDictionary<string, bool> stopped = new();
    private readonly ConcurrentDictionary<(string Workspace, string Path), bool> exposed = new();
    private readonly ConcurrentDictionary<string, LanguageRequest[]> drafts = new();
    internal bool AllowsRead(string workspace, string path) => exposed.ContainsKey((workspace, path));
    private readonly LanguageServers servers;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Timer sampler;
    private readonly Func<bool> enabled;
    private readonly Func<string, string, string> configuration;
    private readonly Action<string, string, string>? saveConfiguration;
    private int disposed;
    private readonly LanguageExitStore exits;
    private readonly LanguageUsageStore usage;
    /// <summary>Called with workspace, server id and each exit record the host writes, for requested stops as well as deaths.</summary>
    internal Action<string, string, LanguageExit>? ExitObserved { get; set; }
    /// <summary>Called with workspace, server id, time and reason when a server dies without anyone asking it to; the host publishes it to clients.</summary>
    internal Action<string, string, string, string>? DeathObserved { get; init; }
    private sealed class Entry
    {
        internal Lazy<Task<LanguageSession>> Session = null!;
        /// <summary>Who started this server: human, config, workspace creation or agent. Informational only; no origin changes how long the server runs.</summary>
        internal string StartedBy = LanguageOrigins.Human;
        internal string Configuration = "";
        internal string State = "installing";
        internal int ExpiryRecorded;
        internal string Detail = "Installing or locating the language server…";
        internal readonly CancellationTokenSource Stop;
        internal readonly CancellationToken Token;
        internal int Stopped;
        internal Entry(CancellationToken token) { Stop = CancellationTokenSource.CreateLinkedTokenSource(token); Token = Stop.Token; }
    }

    public LanguageService(string directory, Func<bool>? enabled = null, Func<string>? configuration = null, Func<string, string, string>? workspaceConfiguration = null, Action<string, string, string>? saveConfiguration = null,
        Func<IReadOnlyList<LanguageServerSpec>>? catalog = null)
    {
        servers = new(directory, configuration, catalog: catalog);
        exits = new(directory);
        usage = new(directory);
        this.configuration = workspaceConfiguration ?? ((_, _) => "{}");
        this.saveConfiguration = saveConfiguration;
        this.enabled = enabled ?? (() => Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE") != "1");
        sampler = new Timer(_ => SampleMemory(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    /// <summary>The user's host-wide tool paths from host state (JSON of executable name to path), honoured by probes and launches.</summary>
    internal Func<string> ToolPaths { set => servers.ToolPaths = value; }

    public async ValueTask<LanguageResult> ExecuteAsync(LanguageRequest request, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) != 0) return new("stopped", "The host is shutting down.");
        if (request.Operation == "catalog") return new("ready", servers.Storage)
        {
            Servers = servers.Servers.Select(server => new LanguageServerState(server.Id, "", "available", server.Implementation, server.Version) { Presentation = Present(server) }).ToArray()
        };
        if (request.Operation == "resolution")
        {
            var chosen = servers.Find(request.Server) ?? throw new ArgumentException("Unknown language server.");
            if (!Path.IsPathFullyQualified(request.Workspace) || !Directory.Exists(request.Workspace)) throw new ArgumentException("Language requests require an existing absolute workspace directory.");
            var resolution = await servers.ResolveAsync(chosen, Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace)), token);
            return new(resolution.State, resolution.Message, chosen.Id) { Configuration = resolution.ToJson() };
        }
        if (!enabled() && request.Operation is not ("configurationSchema" or "configuration" or "configure" or "servers" or "logs"))
        {
            if (request.Operation == "close")
                foreach (var draftKey in drafts.Keys.Where(key => key.StartsWith(Path.TrimEndingDirectorySeparator(request.Workspace) + "\0", StringComparison.Ordinal)))
                    drafts.AddOrUpdate(draftKey, [], (_, saved) => saved.Where(item => item.DocumentId != request.DocumentId || item.Path != request.Path).ToArray());
            return new("disabled", "Language services are disabled in Settings > LSP or by this host's configuration.", Version: request.Version) { Freshness = "unavailable" };
        }
        if (request.Text is { Length: > 1_048_576 }) return new("unavailable", "Language analysis is limited to 1 Mi characters per document.", Version: request.Version);
        if (!Path.IsPathFullyQualified(request.Workspace) || !Directory.Exists(request.Workspace))
            throw new ArgumentException("Language requests require an existing absolute workspace directory.");
        var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace));
        request = request with { Workspace = workspace };
        if (request.Operation is not ("servers" or "inspect" or "diagnostics" or "hover" or "definition" or "typeDefinition" or "implementation" or "references" or "source" or "symbolBody" or "symbols" or "workspaceSymbols" or "close" or "status" or "retry" or "restart" or "stop" or "configurationSchema" or "configuration" or "configure" or "logs" or "projects"))
            throw new ArgumentException("Unknown language operation.");
        if (request.Offset < 0 || request.Limit is < 1 or > 200) throw new ArgumentException("Offset must be nonnegative and limit must be between 1 and 200.");
        if (request.Line < 0 || request.Column < 0) throw new ArgumentException("Positions must be zero-based and nonnegative.");
        if (request.Text is not null && string.IsNullOrWhiteSpace(request.DocumentId)) throw new ArgumentException("An editor buffer requires a document identity.");
        if (request.Paths is { Count: > 0 } || request.Operation == "diagnostics" && request.Scope.Length > 0)
        {
            if (request.Operation != "diagnostics" || request.Path.Length > 0 || request.Server.Length > 0 || request.Text is not null)
                throw new ArgumentException("Diagnostic scopes cannot be combined with a path, server, buffer or other operation.");
            return await DiagnoseFilesAsync(request, token);
        }
        if (request.Scope.Length > 0 && (request.Operation is not ("references" or "implementation" or "definition" or "typeDefinition" or "workspaceSymbols") ||
            request.Scope is not ("file" or "module" or "workspace" or "all" or "dependencies") || request.Path.Length == 0 && request.Scope is "file" or "module"))
            throw new ArgumentException("Navigation scopes are file, module (when supported), workspace or dependencies (all server results); file/module need a path.");
        if (request.Configuration is not null && request.Operation is not ("configure" or "restart")) throw new ArgumentException("Configuration is accepted only by configure or restart.");
        if (request.Operation == "projects") return await ProjectRootsAsync(request, token);
        var provider = Replacing(workspace);
        if (Competing(provider).Any(id => entries.ContainsKey(workspace + "\0" + id) || drafts.ContainsKey(workspace + "\0" + id)))
            await SwitchProviderAsync(workspace, provider, CancellationToken.None);
        if (request.Operation == "servers") return ListServers(workspace, token);
        if (LanguageSession.IsVirtual(request.Path))
        {
            if (request.Text is not null || request.Dirty || request.Operation is not ("source" or "hover" or "definition" or "typeDefinition" or "implementation" or "references" or "symbols" or "symbolBody"))
                throw new ArgumentException("Dependency URIs support read-only navigation and source retrieval.");
            if (!entries.TryGetValue(workspace + "\0" + request.Server, out var owner) ||
                !owner.Session.IsValueCreated || !owner.Session.Value.IsCompletedSuccessfully || !owner.Session.Value.Result.OwnsVirtual(request.Path))
                return new("unavailable", "Use the exact URI and source server returned by navigation in this workspace's current session.") { Freshness = "unavailable" };
            try { return await owner.Session.Value.Result.ExecuteAsync(request, token); }
            catch (IOException error) { return new("unavailable", error.Message, request.Server) { Freshness = "unavailable" }; }
        }
        LanguageServer? selectedServer = null;
        if (request.Server.Length > 0)
        {
            if (request.Operation is not ("retry" or "restart" or "stop" or "status" or "configurationSchema" or "configuration" or "configure" or "logs") || request.Path.Length > 0 || request.Text is not null)
                throw new ArgumentException("Select a server only for path-free lifecycle operations.");
            selectedServer = servers.Find(request.Server)
                ?? throw new ArgumentException("Unknown language server.");
        }
        if (request.Operation == "configurationSchema")
            return selectedServer is null ? throw new ArgumentException("Select a server for its configuration schema.") : LanguageConfigurationCatalog.Read(selectedServer);
        if (request.Operation == "logs")
        {
            if (selectedServer is null) throw new ArgumentException("Select a server to view its logs.");
            return logs.TryGetValue(workspace + "\0" + selectedServer.Id, out var log) ? log.Read(selectedServer.Id)
                : new("ready", "No logs yet. Viewing logs does not start the server.", selectedServer.Id);
        }
        if (request.Operation is "configuration" or "configure" or "restart")
        {
            if (selectedServer is null) throw new ArgumentException("Select a server for workspace configuration or restart.");
            if (request.Operation == "configure" && request.Configuration is null) throw new ArgumentException("Supply the complete configuration object to save.");
            if (request.Configuration is { } supplied)
            {
                var parsed = WorkspaceLanguageConfiguration.Parse(supplied, selectedServer.Id);
                if (saveConfiguration is null) throw new InvalidOperationException("Workspace configuration storage is unavailable.");
                var priorOptions = WorkspaceLanguageConfiguration.Parse(configuration(workspace, selectedServer.Id), selectedServer.Id);
                saveConfiguration(workspace, selectedServer.Id, WorkspaceLanguageConfiguration.Serialize(parsed));
                if (GlobalProvider is null && selectedServer.Spec.Replaces.Count > 0 && priorOptions.UseForJavaAndKotlin != parsed.UseForJavaAndKotlin)
                    await SwitchProviderAsync(workspace, Replacing(workspace), CancellationToken.None);
            }
            if (request.Operation != "restart")
                return new("ready", "Workspace settings; configure saves without starting. Restart applies saved settings.", selectedServer.Id)
                {
                    Configuration = configuration(workspace, selectedServer.Id),
                    ActiveConfiguration = entries.GetValueOrDefault(workspace + "\0" + selectedServer.Id)?.Configuration ?? ""
                };
            request = request with { Operation = "retry" };
        }
        var mapped = "";
        if (request.Path.Length == 0 && selectedServer is null)
        {
            // A positional operation named by a symbol runs at that symbol's declaration; other symbol queries search the workspace.
            if (LanguageUsage.ResolvesByName(request)) return await ByNameAsync(request, token);
            if (LanguageUsage.MapsToWorkspaceSearch(request))
            {
                mapped = $"No path was given, so this searched workspace symbols for \"{request.Query}\" across the servers for this workspace's languages. ";
                request = request with { Operation = "workspaceSymbols" };
            }
            if (request.Operation is not ("diagnostics" or "status" or "workspaceSymbols")) throw new ArgumentException(LanguageUsage.PathRequired(request.Operation, workspace));
            var results = new List<LanguageResult>();
            if (request.Operation == "diagnostics")
            {
                foreach (var (sessionKey, running) in entries.ToArray())
                {
                    token.ThrowIfCancellationRequested();
                    if (!sessionKey.StartsWith(workspace + "\0", StringComparison.Ordinal)) continue;
                    if (running.State == "expired")
                    {
                        var expiredId = sessionKey[(sessionKey.IndexOf('\0') + 1)..];
                        results.Add(new("expired", running.Detail, expiredId) { Freshness = "unavailable" });
                        continue;
                    }
                    if (!running.Session.IsValueCreated || !running.Session.Value.IsCompletedSuccessfully) continue;
                    try
                    {
                        var inspection = await running.Session.Value.Result.WorkspaceDiagnosticsAsync(token);
                        if (inspection.State == "expired") RecordExpiry(running, sessionKey, workspace);
                        results.Add(inspection);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                    catch (Exception error) { results.Add(new("error", error.Message) { Freshness = "unavailable" }); }
                }
            }
            else foreach (var path in Discover(workspace))
            {
                token.ThrowIfCancellationRequested();
                results.Add(await ExecuteAsync(request with { Path = path }, token));
            }
            var state = LanguageUsage.CombinedState(request.Operation, results.Select(result => result.State).ToArray());
            return new(state,
                mapped + (results.Count == 0 ? "No language server is configured for any file in this workspace." : string.Join("\n", results.Select(result => result.Server + ": " + result.Detail).Distinct())), Version: request.Version)
            {
                Diagnostics = results.SelectMany(result => result.Diagnostics).Distinct().ToArray(),
                Total = results.Sum(result => result.Total),
                Truncated = results.Any(result => result.Truncated),
                Locations = results.SelectMany(result => result.Locations).Distinct().ToArray(),
                Freshness = results.Count == 0 ? "unavailable" : results.All(result => result.Freshness == "current") ? "current" : "partial",
                Coverage = request.Operation == "diagnostics" ? "server-reported workspace diagnostics; servers may inspect only open files" : "Server-reported loaded project graphs; no full-index completeness guarantee."
            };
        }
        var external = AllowsRead(workspace, request.Path);
        var full = external ? request.Path : ProjectServices.Contain(workspace, request.Path);
        if (external && (request.Dirty || request.Text is not null)) throw new UnauthorizedAccessException("Language-server dependency files are read-only.");
        request = request with { Path = external ? full : Path.GetRelativePath(workspace, full) };
        var server = selectedServer ?? Detect(workspace, full);
        if (server is null) return new("unsupported", "No language server is configured for this file type.", Version: request.Version);
        if (request.Operation != "stop" && request.Operation != "close")
        {
            if (server.Spec.Replaces.Count > 0 && Replacing(workspace)?.Id != server.Id)
                return new("disabled", $"Select {Name(server)} for {Languages(server)} in the host settings before starting it.", server.Id, request.Version) { Freshness = "unavailable" };
            if (Replacing(workspace) is { } replacing && replacing.Spec.Replaces.Contains(server.Id))
                return new("disabled", $"{Name(replacing)} is selected for {Languages(replacing)} on this host. Change the host provider choice to use existing providers.", server.Id, request.Version) { Freshness = "unavailable" };
        }
        var key = workspace + "\0" + server.Id;
        if (saveConfiguration is not null && (request.Operation == "stop" || request.Operation == "retry" && request.Configuration is null))
        {
            var saved = WorkspaceLanguageConfiguration.Parse(configuration(workspace, server.Id), server.Id);
            saveConfiguration(workspace, server.Id, WorkspaceLanguageConfiguration.Serialize(saved with { AutoStart = request.Operation == "retry" }));
        }
        if (request.Operation == "stop")
        {
            stopped[key] = true;
            if (logs.TryGetValue(key, out var stoppingLog)) stoppingLog.Add("lifecycle", "Stopped by request.");
            if (entries.TryRemove(key, out var stopping))
            {
                try { await PreserveDraftsAsync(key, stopping, token); }
                finally { await StopAsync(stopping, "a stop was requested"); }
            }
            return new("stopped", "Language server stopped. Use Retry to restart.", server.Id, request.Version);
        }
        if (request.Operation == "close")
            drafts.AddOrUpdate(key, [], (_, saved) => saved.Where(item => item.DocumentId != request.DocumentId || item.Path != request.Path).ToArray());
        if (request.Operation == "retry") stopped.TryRemove(key, out _);
        else if (stopped.ContainsKey(key)) return new("stopped", "Language server stopped. Use Retry to restart.", server.Id, request.Version);
        if (request.Operation == "close" && !entries.ContainsKey(key)) return new("closed", Version: request.Version);
        if (request.Operation == "retry" && entries.TryRemove(key, out var previous))
        {
            try { await PreserveDraftsAsync(key, previous, token); }
            finally { await StopAsync(previous, "a start or restart was requested"); }
        }
        // Only the dictionary winner starts a process.
        var options = WorkspaceLanguageConfiguration.Parse(configuration(workspace, server.Id), server.Id);
        var candidate = new Entry(lifetime.Token) { Configuration = WorkspaceLanguageConfiguration.Serialize(options), StartedBy = LanguageOrigins.Normalize(request.Origin) };
        candidate.Session = new(() => Task.Run(async () =>
        {
            var log = logs.GetOrAdd(key, _ => new());
            log.Add("lifecycle", "Starting " + server.Id + ".");
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(candidate.Token);
                deadline.CancelAfter(TimeSpan.FromMinutes(15));
                // A workspace just created is still receiving its cloned restore and build outputs; starting without them costs a full restore.
                candidate.Detail = "Waiting for the workspace's cloned build outputs…";
                await WorkspaceClone.WhenClonedAsync(workspace, LanguageLimits.CloneWait, deadline.Token);
                var sessionDirectory = servers.SessionDirectory(server, workspace);
                var start = await servers.ProvisionAsync(server, workspace, deadline.Token, options, sessionDirectory);
                if (options.Arguments is not null)
                {
                    foreach (var argument in options.Arguments)
                        if (server.Spec.ReservedArguments.Any(reserved => argument == reserved || argument.StartsWith(reserved + "=", StringComparison.Ordinal)))
                            throw new IOException($"{argument} is managed by the {server.Id} provider; remove it from the configured arguments.");
                    // A runtime-hosted server (node, java, pwsh, Rscript) keeps the launcher arguments that name its program; configured ones are appended.
                    if (!LanguageServers.HostsRuntime(server)) start.ArgumentList.Clear();
                    foreach (var argument in options.Arguments) start.ArgumentList.Add(WorkspaceLanguageConfiguration.Expand(argument, workspace));
                    servers.ConfigureCaches(start, server, workspace);
                }
                candidate.State = "initializing"; candidate.Detail = "Starting and indexing the workspace…";
                var session = await LanguageSession.StartAsync(start, workspace, server, deadline.Token, options, log, servers.DirectoryPath, exit => RecordExit(workspace, server.Id, exit, candidate.StartedBy), sessionDirectory);
                try { if (drafts.TryGetValue(key, out var saved)) await session.RestoreDraftsAsync(saved, deadline.Token); }
                catch
                {
                    var expired = await session.ObserveExpiryAsync();
                    session.Terminate("restoring unsaved editor buffers failed", failure: true);
                    await session.DisposeAsync();
                    if (expired) throw new LanguageServerExpiredException(server.Spec.ExpiredDetail);
                    throw;
                }
                log.Add("lifecycle", "Initialized; serving this workspace.");
                return session;
            }
            catch (Exception error)
            {
                if (error is LanguageTermsRequiredException or LanguageServerExpiredException) { candidate.State = error is LanguageServerExpiredException ? "expired" : "requiresAcceptance"; candidate.Detail = error.Message; }
                if (error is LanguageServerExpiredException) RecordExpiry(candidate, key, workspace);
                log.Add("startup", error.Message);
                throw;
            }
        }, candidate.Token));
        Entry entry;
        lock (startGate)
        {
            if (!enabled())
            {
                candidate.Stop.Dispose();
                return new("disabled", "Language services are disabled on this host.", Version: request.Version);
            }
            entry = entries.GetOrAdd(key, candidate);
        }
        if (!ReferenceEquals(candidate, entry)) candidate.Stop.Dispose();
        var sessionTask = entry.Session.Value;
        if (!sessionTask.IsCompleted && request.Operation != "close")
        {
            var state = entry.State;
            return new(state, entry.Detail, server.Id, request.Version)
            {
                Freshness = state is "expired" or "requiresAcceptance" ? "unavailable" : "pending"
            };
        }
        try
        {
            var session = await sessionTask.WaitAsync(token);
            var result = await session.ExecuteAsync(request, token);
            if (result.State == "expired") RecordExpiry(entry, key, workspace);
            foreach (var location in result.Locations)
                if (Path.IsPathFullyQualified(location.Path)) exposed[(workspace, location.Path)] = true;
            return result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error) { return new(error is LanguageServerExpiredException ? "expired" : error is LanguageTermsRequiredException ? "requiresAcceptance" : "error", error.Message + " Use Retry after resolving the problem.", server.Id, request.Version) { Freshness = "unavailable" }; }
    }

    private void RecordExit(string workspace, string server, LanguageExit exit, string startedBy)
    {
        var use = usage.Get(workspace, server);
        exit = exit with { StartedBy = startedBy, Details = $"Started by: {startedBy}\n" + (use is null ? "Agent calls: none\n" : $"Agent calls: {use.Calls} ({use.Successes} ok, {use.Failures} failed), last {use.LastUsed}\n") + exit.Details };
        exits.Set(workspace, server, exit);
        ExitObserved?.Invoke(workspace, server, exit);
        if (exit.Unexpected && Volatile.Read(ref disposed) == 0) DeathObserved?.Invoke(workspace, server, exit.Time, exit.Reason);
    }

    private void RecordExpiry(Entry entry, string key, string workspace)
    {
        var server = LanguageServers.Known(key[(key.IndexOf('\0') + 1)..]);
        entry.State = "expired"; entry.Detail = server?.Spec.ExpiredDetail ?? entry.Detail;
        if (server is null || Interlocked.Exchange(ref entry.ExpiryRecorded, 1) != 0) return;
        try
        {
            var active = WorkspaceLanguageConfiguration.Parse(entry.Configuration, server.Id);
            server.Spec.OnExpired(servers.InstallationFor(server, workspace, active.InstallationPath));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            entry.ExpiryRecorded = 0;
            logs.GetOrAdd(key, _ => new()).Add("cleanup", error.Message);
        }
    }

    private LanguageResult ListServers(string workspace, CancellationToken token)
    {
        var paths = new Dictionary<string, string>();
        foreach (var path in Discover(workspace))
        {
            token.ThrowIfCancellationRequested();
            paths[Detect(workspace, path)!.Id] = path;
        }

        var rows = new List<LanguageServerState>();
        foreach (var catalogServer in servers.Servers.OrderByDescending(server => paths.ContainsKey(server.Id)).ThenBy(server => server.Id, StringComparer.Ordinal))
        {
            var id = catalogServer.Id;
            var path = paths.GetValueOrDefault(id) ?? "";
            var key = workspace + "\0" + id;
            var state = stopped.ContainsKey(key) ? "stopped" : "idle";
            var detail = state == "stopped" ? "Stopped. Start to resume analysis." : "Starts on first use; Start provisions it now.";
            if (entries.TryGetValue(key, out var entry))
            {
                state = entry.State; detail = entry.Detail;
                if (entry.Session.IsValueCreated)
                {
                    var task = entry.Session.Value;
                    if (task.IsCompletedSuccessfully)
                    {
                        if (task.Result.Expired) RecordExpiry(entry, key, workspace);
                        state = task.Result.Expired ? "expired" : task.Result.Exited ? "error" : task.Result.NeedsConfiguration ? "actionRequired" : task.Result.IsIndexing ? "initializing" : "ready";
                        detail = task.Result.Expired ? catalogServer.Spec.ExpiredDetail : task.Result.Exited ? (task.Result.ExitRecord is { } died ? (died.Unexpected ? "Stopped unexpectedly: " : "Stopped: ") + died.Reason : "The server exited; its exit status is still being read.") + " Use Start / Retry to restart." : task.Result.Status;
                    }
                    else if (task.IsFaulted) { state = task.Exception!.GetBaseException() is LanguageServerExpiredException ? "expired" : task.Exception!.GetBaseException() is LanguageTermsRequiredException ? "requiresAcceptance" : "error"; detail = task.Exception!.GetBaseException().Message; }
                    else if (task.IsCanceled) { state = "stopped"; detail = "Startup was cancelled."; }
                }
            }
            if (Replacing(workspace) is { } replacing && replacing.Spec.Replaces.Contains(id)) { state = "disabled"; detail = $"{Name(replacing)} is selected for {Languages(replacing)}. Existing provider settings are preserved."; }
            if (!enabled()) { state = "disabled"; detail = "Language services are disabled on this host. Workspace settings are preserved."; }
            var exit = exits.Get(workspace, id);
            if (entries.TryGetValue(key, out var running) && running.Session.IsValueCreated && running.Session.Value is { IsCompletedSuccessfully: true } finished) exit = finished.Result.ExitRecord is { } live ? exit is not null && exit.Time == live.Time ? exit : live : exit;
            if (exit is not null && state is "idle" or "stopped")
                detail = (exit.Unexpected ? "Stopped unexpectedly: " : "Last run: ") + exit.Reason + " " + detail;
            rows.Add(new(id, path, state, detail, catalogServer.Version,
                WorkspaceLanguageConfiguration.Serialize(WorkspaceLanguageConfiguration.Parse(configuration(workspace, id), id)),
                entries.GetValueOrDefault(key)?.Configuration ?? "", configuration(workspace, id) != "{}")
            {
                Exit = exit,
                Usage = usage.Get(workspace, id),
                StartedBy = entries.TryGetValue(key, out var started) ? started.StartedBy : exit?.StartedBy ?? "",
                Presentation = Present(catalogServer),
                Dependencies = servers.ProbeDependencies(catalogServer, workspace)
            });
        }
        return new(enabled() ? "ready" : "disabled", enabled() ? "Available language servers; detected languages are listed first." : "Language services are disabled. Workspace settings remain available.") { Servers = rows };
    }

    /// <summary>What the providing plugin asks the user interface to show (H21).</summary>
    internal static LanguageServerPresentation Present(LanguageServer server) => new(Name(server), server.Spec.Icon?.Data, server.Spec.Icon?.Format ?? "",
        [.. Enum.GetValues<LanguageServerOptionGroups>().Where(group => group != LanguageServerOptionGroups.None && server.Spec.OptionGroups.HasFlag(group))
            .Select(group => JsonNamingPolicy.CamelCase.ConvertName(group.ToString()))],
        server.Spec.Replaces, server.Spec.TermsAsync is not null, server.Spec.TermsNote, server.Spec.ReplacesLabel);

    private static string Name(LanguageServer server) => server.Spec.Name.Length > 0 ? server.Spec.Name : server.Id;

    private string Languages(LanguageServer replacing) => replacing.Spec.ReplacesLabel.Length > 0 ? replacing.Spec.ReplacesLabel
        : string.Join(" and ", replacing.Spec.Replaces.Select(id => servers.Find(id) is { } replaced ? Name(replaced) : id));

    /// <summary>The server chosen to replace others in this workspace: the host-wide choice when there is one, else the workspace's (H21).</summary>
    private LanguageServer? Replacing(string workspace)
    {
        var replacing = servers.Current.Replacing;
        if (GlobalProvider?.Invoke() is { } global)
        {
            foreach (var server in replacing)
                if (server.Id == global) return server;
            return null;
        }
        foreach (var server in replacing)
            if (WorkspaceLanguageConfiguration.Parse(configuration(workspace, server.Id), server.Id).UseForJavaAndKotlin) return server;
        return null;
    }

    /// <summary>The servers that must not run beside the chosen replacement, or every replacing server when none is chosen.</summary>
    private IEnumerable<string> Competing(LanguageServer? chosen) => chosen is not null
        ? chosen.Spec.Replaces.Concat(servers.Current.Replacing.Where(server => server != chosen).Select(server => server.Id))
        : servers.Current.Replacing.Select(server => server.Id);

    private LanguageServer? Detect(string workspace, string path)
    {
        var server = servers.Detect(path);
        return server is not null && Replacing(workspace) is { } replacing && replacing.Spec.Replaces.Contains(server.Id) ? replacing : server;
    }

    /// <summary>Applies a host-wide provider choice to every workspace with sessions or drafts.</summary>
    internal async Task SwitchProviderAsync(CancellationToken token)
    {
        var workspaces = entries.Keys.Concat(drafts.Keys).Select(key => key.Split('\0')[0]).Distinct().ToArray();
        foreach (var workspace in workspaces) await SwitchProviderAsync(workspace, Replacing(workspace), token);
    }

    private async Task SwitchProviderAsync(string workspace, LanguageServer? chosen, CancellationToken token)
    {
        foreach (var id in Competing(chosen).ToArray())
        {
            var key = workspace + "\0" + id;
            if (entries.TryRemove(key, out var entry))
            {
                try { await PreserveDraftsAsync(key, entry, token); }
                finally { await StopAsync(entry, "the language provider choice changed"); }
            }
            if (!drafts.TryRemove(key, out var saved)) continue;
            foreach (var draft in saved)
            {
                var target = workspace + "\0" + (chosen?.Id ?? servers.Detect(draft.Path)!.Id);
                drafts.AddOrUpdate(target, [draft], (_, existing) => existing.Where(item => item.DocumentId != draft.DocumentId || item.Path != draft.Path).Append(draft).ToArray());
            }
        }
    }

    /// <summary>Stops the sessions of servers no enabled plugin provides any more (H21); their drafts and settings are kept.</summary>
    internal async Task CatalogChangedAsync()
    {
        var provided = servers.Servers.Select(server => server.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var key in entries.Keys.Where(key => !provided.Contains(key[(key.IndexOf('\0') + 1)..])).ToArray())
            if (entries.TryRemove(key, out var entry))
            {
                try { await PreserveDraftsAsync(key, entry, lifetime.Token); }
                catch (OperationCanceledException) { }
                finally { await StopAsync(entry, "its plugin was disabled"); }
            }
    }

    /// <summary>The ids of the servers that own at least one file in the workspace; scans the tree, so call it off the UI thread.</summary>
    internal IReadOnlyList<string> DetectedServers(string workspace)
        => [.. Discover(workspace).Select(path => Detect(workspace, path)).OfType<LanguageServer>().Select(server => server.Id).Distinct()];

    private IEnumerable<string> Discover(string workspace)
    {
        var seen = new HashSet<string>();
        foreach (var (file, server) in Census(workspace))
            if (seen.Add(server.Id)) yield return file;
    }

    /// <summary>
    /// The agent instructions' view of the workspace's languages (H20): the same bounded census as detection, counting every
    /// file per server (with a chosen replacement standing in for the servers it replaces). Scans the tree, so call it off the UI thread.
    /// </summary>
    internal ProjectLanguageProfile LanguageProfile(string workspace, CancellationToken token = default)
        => ProjectLanguages.Profile(workspace, Census(workspace).Select(item => (item.Path, (Detect(workspace, item.Path) ?? item.Server).Id)), token);

    /// <summary>Every file of the bounded tree walk with the server that owns it, as workspace-relative paths.</summary>
    private IEnumerable<(string Path, LanguageServer Server)> Census(string workspace)
    {
        var queue = new Queue<string>(); queue.Enqueue(workspace);
        var visited = 0;
        while (queue.Count > 0 && visited++ < 5000)
        {
            var directory = queue.Dequeue();
            foreach (var file in Directory.EnumerateFiles(directory))
                if (servers.Detect(file) is { } server) yield return (Path.GetRelativePath(workspace, file), server);
            foreach (var child in Directory.EnumerateDirectories(directory))
                if (Path.GetFileName(child) is not (".git" or ".tools" or ".bench" or "node_modules" or "bin" or "obj" or "target" or ".venv" or "vendor" or ".gradle" or ".dart_tool")
                    && (File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) queue.Enqueue(child);
        }
    }

    internal async Task SuspendAsync()
    {
        KeyValuePair<string, Entry>[] sessions;
        lock (startGate)
        {
            sessions = entries.ToArray();
            foreach (var pair in sessions) entries.TryRemove(pair);
        }
        await Task.WhenAll(sessions.Select(async pair =>
        {
            try { await PreserveDraftsAsync(pair.Key, pair.Value, CancellationToken.None); }
            finally { await StopAsync(pair.Value, "the workspace's language services were suspended"); }
        }));
    }

    /// <summary>Samples each running server's resident memory for its exit record; servers run for the lifetime of their workspace and are never stopped for being unused.</summary>
    internal void SampleMemory()
    {
        foreach (var entry in entries.Values)
            if (entry.Session.IsValueCreated && entry.Session.Value is { IsCompletedSuccessfully: true } running) running.Result.SampleMemory();
    }

    public async ValueTask CloseWorkspaceAsync(string workspace)
    {
        projectRoots.TryRemove(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)), out _);
        foreach (var key in logs.Keys) if (key.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + "\0", StringComparison.Ordinal)) logs.TryRemove(key, out _);
        foreach (var key in exposed.Keys) if (key.Workspace == workspace) exposed.TryRemove(key, out _);
        foreach (var key in drafts.Keys) if (key.StartsWith(workspace + "\0", StringComparison.Ordinal)) drafts.TryRemove(key, out _);
        foreach (var (key, entry) in entries)
            if (key.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)) + "\0", StringComparison.Ordinal) && entries.TryRemove(new KeyValuePair<string, Entry>(key, entry)))
                await StopAsync(entry, "workspace removed");
        exits.Remove(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)));
        usage.Remove(Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace)));
    }

    private async Task PreserveDraftsAsync(string key, Entry entry, CancellationToken token)
    {
        if (entry.Session.IsValueCreated && entry.Session.Value is { IsCompletedSuccessfully: true } task)
            drafts[key] = await (await task).DraftsAsync(token);
    }

    private static async Task StopAsync(Entry entry, string reason)
    {
        if (Interlocked.Exchange(ref entry.Stopped, 1) != 0) return;
        entry.Stop.Cancel();
        try
        {
            if (entry.Session.IsValueCreated)
            {
                var session = await entry.Session.Value;
                session.Terminate(reason);
                await session.DisposeAsync();
            }
        }
        catch (Exception) { }
        entry.Stop.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel(); await sampler.DisposeAsync();
        foreach (var entry in entries.Values) await StopAsync(entry, "the host is shutting down");
        entries.Clear(); logs.Clear(); lifetime.Dispose();
    }
}