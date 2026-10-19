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
    internal const string Guidance = "Air Context (`jbcontext`) provides semantic code discovery: it finds code by meaning and behavior, even when you do not know the symbol names or exact wording. You MUST call `context_search` first, before grep or reading files, for unfamiliar code, feature locations and cross-cutting behavior whenever this workspace has it enabled; do not wait for the user to ask. When you brief a subagent, its brief MUST say the same, that it passes `workspace` to every `context_*` call, and that its final report MUST state how many `context_search` calls it made; reject a report with none for code-discovery work. Check `context_status` when readiness is unknown. If disabled, unavailable, or not ready, say so in your report and continue with LSP and targeted text search rather than blocking. Use descriptive queries, inspect the returned locations, then MUST use LSP for exact symbols and relationships. Use text search for literal strings and configuration, and supplement incomplete semantic results as needed. `context_status` reports login/index readiness; `context_index` refreshes an already-enabled workspace. Use these SharpRail tools instead of running the global jbcontext installer or changing agent hooks/configuration. Only the user can enable cloud indexing or sign in. Unsaved code is not guaranteed indexed; use the editor/LSP for current drafts.";

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
            enabled = [.. context.ReadState(AirContextContract.WorkspacesState, new ContextWorkspaces([])).Paths.Where(ValidPath)];
            var active = service = new ContextService(workspace => { lock (gate) return enabled.Contains(workspace); }, async (workspace, log, token) =>
            {
                var store = Consent();
                await store.RequireAsync(token);
                if (Override?.Runtime is { } fixture) return await fixture(workspace, log, token);
                var terms = await store.ReviewAsync(token);
                var installed = await store.ExecuteAsync(new("download", terms.ArtifactId, terms.TermsSha256), token);
                if (installed.State != "downloaded") throw new IOException(installed.Detail);
                return new ContextRuntime(installed.Installation, workspace, log, store.AuthorizeExecutionAsync);
            });

            context.ToolGuidance(Guidance);
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
                        if (first && Enabled(agent.Terminal.WorkspaceId)) _ = active.AgentStartedAsync(agent.Terminal.WorkspaceId);
                        break;
                    case TerminalClosed closed:
                        lock (gate) launches.Remove(closed.Terminal.WorkspaceId + "\0" + closed.Terminal.TabKey);
                        break;
                }
            });
            context.OnWorkspace(change =>
            {
                if (change is not WorkspaceRemoved removed) return;
                SetEnabled(removed.Id, false);
                _ = active.ForgetWorkspaceAsync(removed.Id);
            });
        }

        internal async ValueTask DisposeAsync()
        {
            if (service is { } active) await active.DisposeAsync();
        }

        private static bool ValidPath(string value) => value.Length > 0 && !value.Contains('\0') && Path.IsPathFullyQualified(value);

        private bool Enabled(string workspace)
        {
            lock (gate) return enabled.Contains(workspace);
        }

        private void SetEnabled(string workspace, bool value)
        {
            lock (gate)
            {
                if (value ? !enabled.Add(workspace) : !enabled.Remove(workspace)) return;
                context.WriteState(AirContextContract.WorkspacesState, new ContextWorkspaces([.. enabled.Order(StringComparer.Ordinal)]));
            }
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
            if (call.Operation is "enable" or "disable" or "stop") context.Publish(AirContextContract.Changed, outcome);
            // The login is shared: every enabled workspace's pane learns of a change, in this window or another client.
            else if (call.Operation is "logout" or "check")
            {
                string[] others;
                lock (gate) others = [.. enabled];
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
                    SetEnabled(request.Workspace, enable);
                    if (!enable) await active.CloseWorkspaceAsync(request.Workspace);
                    return await active.ExecuteAsync(request with { Operation = "status" }, token);
                }
                return await active.ExecuteAsync(request, token);
            }
            catch (ContextConsentRequiredException) { return new("requiresAcceptance", "Review and accept the current Context agreements in the Air Context pane first."); }
            catch (Exception error) when (error is IOException or HttpRequestException or PlatformNotSupportedException or UnauthorizedAccessException)
            { return new("error", ContextOutput.Sanitize(error.Message), Enabled(request.Workspace)); }
        }

        private async ValueTask<PluginToolResult> RunToolAsync(string? requested, PluginToolContext call, string operation, string query, string path, int limit, CancellationToken token)
        {
            var workspace = Path.TrimEndingDirectorySeparator(Path.GetFullPath(call.WorkspaceId ?? call.Cwd));
            try
            {
                requested ??= workspace;
                if (!Path.IsPathFullyQualified(requested) || Path.TrimEndingDirectorySeparator(Path.GetFullPath(requested)) != workspace)
                    throw new UnauthorizedAccessException("Context tools are bound to their calling SharpRail workspace.");
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