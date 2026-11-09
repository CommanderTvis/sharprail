using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Processes.Host;

/// <summary>
/// The host half: the process snapshot and stop methods, and the agent-facing owned-process management that this plugin owns
/// (the report and release tools, the prompt section and the registry's switch). Disabled, none of it exists.
/// </summary>
public sealed class ProcessesHost : PluginHostModule
{
    public override PluginContract Contract => ProcessesContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        if (context.Processes is not { } processes) return ValueTask.FromResult<PluginDisposer?>(null);
        var sampler = new ProcessSampler(new HostSource(context, processes)) { StopAgentChildren = () => context.Settings<ProcessesSettings>().StopAgentChildren };
        // The embedded adapter runs a handler on the caller's thread, and sampling reads the process table.
        context.Method(ProcessesContract.Snapshot, async (_, _, token) =>
        {
            await processes.RefreshAsync(token);
            return await Task.Run(sampler.Sample, token);
        });
        context.Method(ProcessesContract.Stop, async (parameters, _, token) => new ProcessStopResult(await sampler.StopAsync(parameters.Id, token)));

        // A browser tab starts no process an agent could own, so the tools and their prompt would only mislead.
        if (!OperatingSystem.IsBrowser())
        {
            context.Tool(new PluginTool<OwnedProcessReportParams>(ProcessesPlugin.ReportTool, "Report a long-running process you started",
                "Report a long-running background process you started (a dev server, a watcher, a run_in_background command) so SharpRail stops it when this terminal closes or SharpRail quits, instead of leaving it running. Report only processes you started and want gone then; call owned_process_release if you stop it yourself. Refuses pid 1, SharpRail, any terminal's shell, a dead pid and another user's process.",
                async (arguments, call, token) =>
                {
                    if (call.Terminal is not { } terminal) return new PluginToolResult("This session has no terminal tab to own a process.") { IsError = true };
                    try { return new PluginToolResult(await Task.Run(() => processes.ReportOwned(terminal, arguments.Pid, arguments.Description), token)); }
                    catch (ArgumentException error) { return new PluginToolResult(error.Message) { IsError = true }; }
                }));
            context.Tool(new PluginTool<OwnedProcessReleaseParams>(ProcessesPlugin.ReleaseTool, "Withdraw a reported process",
                "Tell SharpRail a process you reported with owned_process_report is no longer yours to be stopped, usually because you stopped it. It does not stop the process.",
                (arguments, call, _) => ValueTask.FromResult(call.Terminal is not { } terminal
                    ? new PluginToolResult("This session has no terminal tab to own a process.") { IsError = true }
                    : new PluginToolResult(processes.ReleaseOwned(terminal, arguments.Pid) ? $"Released pid {arguments.Pid}." : $"This terminal had not reported pid {arguments.Pid}; nothing changed."))));
            context.Prompt(new PromptContribution
            {
                Id = "owned-processes",
                Title = "Owned processes",
                Description = "Report long-running background processes with owned_process_report, release them with owned_process_release.",
                Placement = PromptPlacement.Section,
                Text = """
                    Report every long-running background process you start (dev servers, watchers, `run_in_background` commands) with `owned_process_report` (its `pid`, and a short `description`), so SharpRail stops it when the terminal closes or SharpRail quits.
                    Call `owned_process_release` when you stop it yourself. Report only processes you started.
                    """
            });
        }

        processes.ManageOwned(true);
        // Disabling forgets the records without stopping the processes: the user opted out of having them managed.
        return ValueTask.FromResult<PluginDisposer?>(() =>
        {
            processes.ManageOwned(false);
            return ValueTask.CompletedTask;
        });
    }

    private sealed class HostSource(IPluginHostContext context, IProcessHost processes) : IProcessSource
    {
        public ProcessInputs Read()
        {
            var shells = context.Terminals();
            var perWorkspace = new Dictionary<string, int>(StringComparer.Ordinal);
            var terminals = shells.Select(shell =>
            {
                var index = perWorkspace[shell.Terminal.WorkspaceId] = perWorkspace.GetValueOrDefault(shell.Terminal.WorkspaceId) + 1;
                return new TerminalInfo(shell.Terminal, shell.Pid, context.AgentRecord(shell.Terminal)?.Kind, $"tab {index}", context.TerminalTitle(shell.Terminal), MemoryText(shell.Terminal));
            }).Concat(context.ListAgentSessions().Select(session => new TerminalInfo(new(session.WorkspaceId, "chat:" + session.Id), session.Pid, session.Agent, "chat", session.Title))).ToArray();
            return new(terminals, processes.Table(), processes.LanguageServers(), processes.OwnedProcesses(), processes.LanguageServerName)
            {
                Workers = OperatingSystem.IsBrowser(),
                GuardEvents = [.. processes.MemoryGuardEvents().Select(item => (item.At, item.Text))]
            };
        }

        // "memory 2.1 of 8 GB", with the guard's state when it is not Ok: how the terminal's tree stands against its limit.
        private string? MemoryText(TerminalRef terminal)
        {
            if (processes.TerminalMemory(terminal) is not { } memory) return null;
            static string Gb(long bytes) => (bytes / (double)(1L << 30)).ToString(bytes >= 10L << 30 ? "0" : "0.#", System.Globalization.CultureInfo.InvariantCulture);
            return $"memory {Gb(memory.Bytes)} of {Gb(memory.Limit)} GB" + (memory.State == "Ok" ? "" : " (" + memory.State.ToLowerInvariant() + ")");
        }

        public (TimeSpan Cpu, long MemoryBytes)? Usage(int pid) => processes.Usage(pid);

        public string WorkspaceLabel(string workspace)
        {
            var trimmed = workspace.TrimEnd('/', '\\');
            var name = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
            return name.Length > 0 ? name : workspace;
        }

        public ValueTask StopLanguageServerAsync(string workspace, string serverId, CancellationToken cancellationToken) => processes.StopLanguageServerAsync(workspace, serverId, cancellationToken);

        public ValueTask StopProcessAsync(int pid, string command, CancellationToken cancellationToken) => processes.StopProcessAsync(pid, command, cancellationToken);
    }
}