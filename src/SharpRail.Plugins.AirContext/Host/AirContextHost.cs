using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.AirContext.Host;

/// <summary>
/// The Air Context plugin's host half: the JetBrains Context CLI's consent, provisioning and per-workspace indexing and
/// search, served to agents as <c>context_search</c>, <c>context_status</c> and <c>context_index</c>. Everything it does
/// belongs to one activation: disabling the plugin stops its processes and indexing and removes its tools and guidance.
/// </summary>
public sealed class AirContextHost : PluginHostModule
{
    /// <summary>Stand-ins the checks put in place of the vendor's terms server, download and CLI; production leaves them unset.</summary>
    internal sealed class Fixtures
    {
        /// <summary>Replaces the consent store, from the plugin's storage directory.</summary>
        internal Func<string, ContextConsent>? Consent { get; set; }

        /// <summary>Replaces the CLI runtime, called once consent is verified.</summary>
        internal Func<string, Action<string>, CancellationToken, Task<IContextRuntime>>? Runtime { get; set; }
    }

    /// <summary>Read when an activation first needs a consent store or runtime, so it can be set after the plugin started.</summary>
    internal static Fixtures? Override { get; set; }

    public override PluginContract Contract => AirContextContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var activation = new Activation(context);
        activation.Register();
        return ValueTask.FromResult<PluginDisposer?>(activation.DisposeAsync);
    }

    /// <summary>The guidance moved verbatim from the Claude Code and Codex instructions; it exists only while the plugin is enabled.</summary>
    internal const string Guidance = "Air Context (`jbcontext`) provides semantic code discovery: it finds code by meaning and behavior, even when you do not know the symbol names or exact wording. You MUST call `context_search` first, before grep or reading files, for unfamiliar code, feature locations and cross-cutting behavior whenever this workspace's project has it enabled; do not wait for the user to ask. When you brief a subagent, its brief MUST say the same, that it passes `workspace` to every `context_*` call, and that its final report MUST state how many `context_search` calls it made; reject a report with none for code-discovery work. Check `context_status` when readiness is unknown. If disabled, unavailable, or not ready, say so in your report and continue with LSP and targeted text search rather than blocking. Use descriptive queries, inspect the returned locations, then MUST use LSP for exact symbols and relationships. Use text search for literal strings and configuration, and supplement incomplete semantic results as needed. `context_status` reports login/index readiness; `context_index` refreshes an already-enabled workspace. Use these SharpRail tools instead of running the global jbcontext installer or changing agent hooks/configuration. Only the user can enable cloud indexing or sign in. Unsaved code is not guaranteed indexed; use the editor/LSP for current drafts.";

    /// <summary>The workspace a Context tool call targets: its own, or a sibling worktree of the same project that a subagent works in.</summary>
    internal static async ValueTask<string> ResolveWorkspaceAsync(string calling, string? requested, Func<string, ValueTask<HostWorkspace?>> find, Func<string?, ValueTask<IReadOnlyList<HostWorkspace>>> list)
    {
        if (requested is null) return calling;
        if (!Path.IsPathFullyQualified(requested)) throw new UnauthorizedAccessException("Pass the workspace as an absolute path.");
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested));
        if (target == calling) return calling;
        var origin = await find(calling);
        var found = await find(target);
        if (found is not null && origin is not null && found.ProjectId == origin.ProjectId) return Path.TrimEndingDirectorySeparator(Path.GetFullPath(found.Path));
        var known = await list(origin?.ProjectId);
        throw new UnauthorizedAccessException($"{target} is not a known SharpRail workspace of this project. Known workspaces: {(known.Count == 0 ? "none" : string.Join(", ", known.Select(item => item.Path)))}.");
    }

    private sealed class Activation(IPluginHostContext context)
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(5) };
        private readonly Lock gate = new();
        private readonly string? storage = context.StateDirectory;
        private readonly HashSet<string> launches = [];
        private HashSet<string> enabled = [];
        private ContextConsent? consent;
        private ContextService? service;

        internal void Register()
        {
            // Consent is per project. Records from when it was per workspace seed the set and are promoted to their
            // project as soon as the host resolves them, here in the background or at the next lookup.
            var stored = context.ReadState<ContextProjects?>(AirContextContract.ProjectsState, null);
            enabled = [.. (stored?.Projects ?? context.ReadState(AirContextContract.WorkspacesState, new ContextWorkspaces([])).Paths).Where(ValidPath)];
            if (stored is null && enabled.Count > 0) Save();
            var active = service = new ContextService(EnabledAsync, async (workspace, log, token) =>
            {
                var store = Consent();
                await store.RequireAsync(token);
                if (Override?.Runtime is { } fixture) return await fixture(workspace, log, token);
                var terms = await store.ReviewAsync(token);
                var installed = await store.ExecuteAsync(new("download", terms.ArtifactId, terms.TermsSha256), token);
                if (installed.State != "downloaded") throw new IOException(installed.Detail);
                return new ContextRuntime(installed.Installation, workspace, log, store.AuthorizeExecutionAsync);
            });

            context.Prompt(new PromptContribution { Id = "tool-guidance", Title = "Air Context search", Description = "When to search the indexed project with Air Context's context tools.", Text = Guidance });
            context.Method(AirContextContract.Consent, async (request, _, token) => await ConsentAsync(request, token));
            context.Method(AirContextContract.Workspace, async (call, _, token) => await WorkspaceAsync(call, token));
            context.Tool(new PluginTool<ContextSearchParams>("context_search", "context search",
                "Semantic code discovery in this enabled workspace. Use a descriptive query when you do not know where behavior lives; follow returned file/line references with targeted reads and LSP navigation. Prefer LSP for known symbols. Indexing sends source chunks to JetBrains; only the user can enable it. If disabled, signed out or indexing, report that state and use other tools. Results may lag working-tree changes; LSP/editor buffers remain authoritative for unsaved code.",
                (arguments, call, token) => RunToolAsync(arguments.Workspace, call, "search", arguments.Query, arguments.Path ?? "", arguments.Limit ?? 20, token)));
            context.Tool(new PluginTool<ContextStatusParams>("context_status", "context status",
                "Read this workspace's SharpRail-managed Context state. Never enables indexing, accepts terms or signs in. Use refresh=true for vendor-reported index status.",
                (arguments, call, token) => RunToolAsync(arguments.Workspace, call, arguments.Refresh == true ? "refresh" : "status", "", "", 20, token)));
            context.Tool(new PluginTool<ContextIndexParams>("context_index", "context index",
                "Refresh semantic indexing for this already-enabled workspace. Returns immediately; poll context_status. Source chunks are sent to JetBrains. Cannot enable a workspace or accept terms.",
                (arguments, call, token) => RunToolAsync(arguments.Workspace, call, "index", "", "", 20, token)));

            context.OnTerminal(change =>
            {
                switch (change)
                {
                    // First recognition of a SharpRail UI-launched agent starts background indexing for an enabled
                    // workspace; model and status updates of the same launch do not repeat it.
                    case TerminalAgentChanged { Record: var record } agent:
                        var launched = record?.LaunchedByUi == true && record.Kind is "codex" or "claude";
                        var key = agent.Terminal.WorkspaceId + "\0" + agent.Terminal.TabKey;
                        var first = false;
                        lock (gate)
                        {
                            if (launched) first = launches.Add(key);
                            else launches.Remove(key);
                        }
                        if (first) _ = active.AgentStartedAsync(agent.Terminal.WorkspaceId);
                        break;
                    case TerminalClosed closed:
                        lock (gate) launches.Remove(closed.Terminal.WorkspaceId + "\0" + closed.Terminal.TabKey);
                        break;
                }
            });
            context.OnWorkspace(change =>
            {
                // The project's consent outlives any one of its workspaces.
                if (change is WorkspaceRemoved removed) _ = active.ForgetWorkspaceAsync(removed.Id);
                // A new workspace of an enabled project starts indexing in the background, as the Default workspace
                // does when an agent launches there, so agents working in it can search without a UI launch.
                else if (change is WorkspaceCreated created) _ = Task.Run(() => CreatedAsync(active, created.Workspace.Path));
            });
            string[] seeded;
            lock (gate) seeded = [.. enabled];
            _ = Task.Run(async () =>
            {
                foreach (var path in seeded)
                {
                    try { await EnabledAsync(path, default); }
                    catch (Exception error) when (error is IOException or InvalidOperationException) { }
                }
            });
        }

        internal async ValueTask DisposeAsync()
        {
            if (service is { } active) await active.DisposeAsync();
        }

        private async Task CreatedAsync(ContextService active, string path)
        {
            var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            try
            {
                if (!Directory.Exists(workspace) || !await EnabledAsync(workspace, default)) return;
                await active.AgentStartedAsync(workspace);
                context.Publish(AirContextContract.Changed, (await ExecuteAsync(new(workspace, "status"), default)).For(workspace));
            }
            catch (Exception error) when (error is IOException or InvalidOperationException or ArgumentException or UnauthorizedAccessException) { }
        }

        private static bool ValidPath(string value) => value.Length > 0 && !value.Contains('\0') && Path.IsPathFullyQualified(value);

        /// <summary>The project a workspace belongs to, or the workspace itself when no open project knows it.</summary>
        private async ValueTask<string> ProjectAsync(string workspace, CancellationToken token) =>
            await context.WorkspaceAsync(workspace, token) is { } known ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(known.ProjectId)) : workspace;

        /// <summary>Whether the workspace's project is enabled; a per-workspace record from before is promoted to its project here.</summary>
        private async ValueTask<bool> EnabledAsync(string workspace, CancellationToken token)
        {
            lock (gate) if (enabled.Count == 0) return false;
            var project = await ProjectAsync(workspace, token);
            lock (gate) if (enabled.Contains(project)) return true;
            var covered = await CoveredAsync(workspace, project, token);
            lock (gate)
            {
                if (enabled.Contains(project)) return true;
                if (!covered.Aggregate(false, (any, path) => enabled.Remove(path) | any)) return false;
                enabled.Add(project);
                Save();
                return true;
            }
        }

        private void Save()
        {
            lock (gate) context.WriteState(AirContextContract.ProjectsState, new ContextProjects([.. enabled.Order(StringComparer.Ordinal)]));
        }

        /// <summary>The workspaces the project's consent covers: every known one, or just this one outside an open project.</summary>
        private async ValueTask<string[]> CoveredAsync(string workspace, string project, CancellationToken token)
        {
            var known = await context.WorkspacesAsync(project, token);
            return [.. known.Select(item => Path.TrimEndingDirectorySeparator(Path.GetFullPath(item.Path))).Append(workspace).Distinct()];
        }

        private ContextConsent Consent()
        {
            lock (gate)
            {
                if (consent is not null) return consent;
                var directory = storage ?? throw new IOException("This host has no persistent Context storage.");
                if (Override?.Consent is { } fixture) return consent = fixture(directory);
                var os = OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "unsupported";
                var arch = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
                {
                    System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
                    System.Runtime.InteropServices.Architecture.X64 => "x64",
                    _ => "unsupported"
                };
                return consent = new(directory, Http, ContextDistribution.Bundle(os, arch));
            }
        }

        private async ValueTask<ContextConsentResult> ConsentAsync(ContextConsentRequest request, CancellationToken token)
        {
            try
            {
                return storage is null ? new("unavailable", "This host has no persistent Context storage.") : await Consent().ExecuteAsync(request, token);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return new("error", error.Message);
            }
        }

        private async ValueTask<ContextOutcome> WorkspaceAsync(ContextCall call, CancellationToken token)
        {
            if (call.Operation is not ("status" or "check" or "enable" or "disable" or "login" or "logout" or "index" or "stop" or "logs"))
                throw new ArgumentException("Unknown Context operation.");
            var result = await ExecuteAsync(new(call.WorkspaceId, call.Operation), token);
            var outcome = result.For(Path.TrimEndingDirectorySeparator(Path.GetFullPath(call.WorkspaceId)));
            if (call.Operation is "enable" or "disable")
            {
                // The consent is the project's: every pane of it, in this window or another client, follows.
                var workspace = outcome.WorkspaceId;
                foreach (var covered in await CoveredAsync(workspace, await ProjectAsync(workspace, token), token))
                    context.Publish(AirContextContract.Changed, covered == workspace ? outcome : (await ExecuteAsync(new(covered, "status"), token)).For(covered));
            }
            else if (call.Operation == "stop") context.Publish(AirContextContract.Changed, outcome);
            // The login is shared: every enabled workspace's pane learns of a change, in this window or another client.
            else if (call.Operation is "logout" or "check")
            {
                string[] projects;
                lock (gate) projects = [.. enabled];
                var others = new List<string>();
                foreach (var project in projects)
                    others.AddRange(Directory.Exists(project) ? await CoveredAsync(project, project, token) : []);
                foreach (var other in others.Append(outcome.WorkspaceId).Distinct())
                    context.Publish(AirContextContract.Changed, (await ExecuteAsync(new(other, "status"), token)).For(other));
            }
            return outcome;
        }

        /// <summary>One operation, from the user interface or an agent's tool; enable and disable are the user's alone.</summary>
        private async Task<ContextResult> ExecuteAsync(ContextRequest request, CancellationToken token)
        {
            if (storage is null || service is not { } active) return new("unavailable", "This host has no persistent Context storage.");
            if (!Path.IsPathFullyQualified(request.Workspace) || !Directory.Exists(request.Workspace)) throw new ArgumentException("Context requires an existing absolute workspace.");
            request = request with { Workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Workspace)) };
            try
            {
                if (request.Operation is "enable" or "disable")
                {
                    var enable = request.Operation == "enable";
                    if (enable) await Consent().RequireAsync(token);
                    var project = await ProjectAsync(request.Workspace, token);
                    var covered = enable ? [] : await CoveredAsync(request.Workspace, project, token);
                    lock (gate)
                    {
                        // Disabling also drops not-yet-promoted records of the project's workspaces.
                        var changed = enable ? enabled.Add(project) : covered.Aggregate(enabled.Remove(project), (any, path) => enabled.Remove(path) | any);
                        if (changed) Save();
                    }
                    foreach (var path in covered) await active.CloseWorkspaceAsync(path);
                    return await active.ExecuteAsync(request with { Operation = "status" }, token);
                }
                return await active.ExecuteAsync(request, token);
            }
            catch (ContextConsentRequiredException) { return new("requiresAcceptance", "Review and accept the current Context agreements in the Air Context pane first."); }
            catch (Exception error) when (error is IOException or HttpRequestException or PlatformNotSupportedException or UnauthorizedAccessException)
            { return new("error", ContextOutput.Sanitize(error.Message), await EnabledAsync(request.Workspace, CancellationToken.None)); }
        }

        private async ValueTask<PluginToolResult> RunToolAsync(string? requested, PluginToolContext call, string operation, string query, string path, int limit, CancellationToken token)
        {
            var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(call.WorkspaceId ?? call.Cwd));
            try
            {
                workspace = await AirContextHost.ResolveWorkspaceAsync(workspace, requested, id => context.WorkspaceAsync(id, token), project => context.WorkspacesAsync(project, token));
                var result = await ExecuteAsync(new(workspace, operation, query, path, limit), token);
                var response = new JsonObject { ["state"] = result.State, ["detail"] = result.Detail, ["enabled"] = result.Enabled };
                if (result.Data.Length > 0)
                {
                    try { response["results"] = JsonNode.Parse(result.Data); }
                    catch (JsonException) { response["results"] = result.Data; }
                }
                return new(response.ToJsonString()) { IsError = result.State is "error" or "requiresAcceptance" or "requiresLogin" or "unavailable" };
            }
            catch (Exception error) when (error is ArgumentException or UnauthorizedAccessException)
            {
                return new(error.Message) { IsError = true };
            }
        }
    }
}