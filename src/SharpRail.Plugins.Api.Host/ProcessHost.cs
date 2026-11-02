using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Api.Host;

/// <summary>
/// The host's process facts and the owned-process registry, lent to the plugin that manages agent processes (H27). Every member reads
/// the process table, signals or waits, so none may run on a UI thread. Rows are tuples so the surface stays small.
/// </summary>
public interface IProcessHost
{
    /// <summary>Every live process of the machine: pid, parent pid and command line. It launches the process lister: call it once per snapshot.</summary>
    /// <returns>The rows; empty when the table cannot be read.</returns>
    IReadOnlyList<(int Pid, int ParentPid, string Command)> Table();

    /// <summary>One process's total processor time and memory (macOS physical footprint, elsewhere working set); null when it is gone.</summary>
    /// <param name="pid">The process id.</param>
    /// <returns>The usage, or null.</returns>
    (TimeSpan Cpu, long MemoryBytes)? Usage(int pid);

    /// <summary>The language-server processes this host runs: workspace root, server id and pid.</summary>
    /// <returns>The running servers.</returns>
    IReadOnlyList<(string Workspace, string ServerId, int Pid)> LanguageServers();

    /// <summary>The name people see for a language server id, such as <c>C# (Roslyn)</c>; the id itself when unknown.</summary>
    /// <param name="serverId">The server id.</param>
    /// <returns>The display name.</returns>
    string LanguageServerName(string serverId);

    /// <summary>Stops one language server through the language service's own stop operation; it restarts on next use.</summary>
    /// <param name="workspace">The workspace root.</param>
    /// <param name="serverId">The server id.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the server has stopped.</returns>
    ValueTask StopLanguageServerAsync(string workspace, string serverId, CancellationToken cancellationToken);

    /// <summary>The processes agents reported as their own: reporting terminal, pid and description.</summary>
    /// <returns>The reported processes.</returns>
    IReadOnlyList<(TerminalRef Terminal, int Pid, string Description)> OwnedProcesses();

    /// <summary>Records a live process as the terminal's, so it is stopped with the terminal and on quit.</summary>
    /// <param name="terminal">The reporting terminal.</param>
    /// <param name="pid">The process id.</param>
    /// <param name="description">A few words for the user.</param>
    /// <returns>The confirmation text.</returns>
    /// <exception cref="ArgumentException">The process may not be owned.</exception>
    string ReportOwned(TerminalRef terminal, int pid, string? description);

    /// <summary>Forgets the terminal's record of a pid without touching the process.</summary>
    /// <param name="terminal">The reporting terminal.</param>
    /// <param name="pid">The process id.</param>
    /// <returns>False when the terminal had no such record.</returns>
    bool ReleaseOwned(TerminalRef terminal, int pid);

    /// <summary>Ends a process gracefully, then by force after the grace the registry uses on terminal close; the command line guards against a reused pid.</summary>
    /// <param name="pid">The process id.</param>
    /// <param name="command">The command line the process was listed with.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    /// <returns>A task that completes when the process is gone or the escalation ended.</returns>
    ValueTask StopProcessAsync(int pid, string command, CancellationToken cancellationToken);

    /// <summary>
    /// Switches agent-owned process management on: reported processes are stopped with their terminal and on quit, and records of dead
    /// hosts are swept now. While it is off nothing can be reported and nothing is stopped.
    /// </summary>
    /// <param name="enabled">Whether management is on; turning it off forgets the records without stopping the processes.</param>
    void ManageOwned(bool enabled);
}