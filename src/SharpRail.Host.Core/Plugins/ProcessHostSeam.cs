using System.ComponentModel;
using System.Diagnostics;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>What the page lends the Processes plugin where there is no process table: the usage of the Web Workers its language servers run in.</summary>
public static class ProcessLinks
{
    /// <summary>The processor time (time spent handling messages) and memory of a worker by its id, or null when it is gone.</summary>
    public static Func<int, (TimeSpan Cpu, long MemoryBytes)?>? TabUsage { get; set; }

    /// <summary>Reads every worker's usage so <see cref="TabUsage"/> can answer without leaving the thread.</summary>
    public static Func<CancellationToken, Task>? TabRefresh { get; set; }
}

/// <summary>
/// What the Processes plugin may reach in the host (H27): the process table, per-process usage, the language service's running servers and
/// its stop operation, and the owned-process registry with its on/off switch. Every member reads the process table or waits.
/// </summary>
internal sealed class ProcessHostSeam(HostStateStore state) : IProcessHost
{
    private OwnedProcesses? Owned => state.Terminals?.Owned;

    // A browser tab has no process table: its language servers are Web Workers, listed with the usage the page reads from them.
    public IReadOnlyList<(int Pid, int ParentPid, string Command, TimeSpan? Elapsed)> Table() => OperatingSystem.IsBrowser() ? [] : [.. ProcessTable.Read().Select(row => (row.Pid, row.Ppid, row.Arguments, row.Elapsed))];

    public ValueTask RefreshAsync(CancellationToken cancellationToken) => ProcessLinks.TabRefresh is { } refresh ? new(refresh(cancellationToken)) : default;

    public (TimeSpan Cpu, long MemoryBytes)? Usage(int pid)
    {
        if (OperatingSystem.IsBrowser()) return ProcessLinks.TabUsage?.Invoke(pid);
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.HasExited ? null : (process.TotalProcessorTime, LanguageMemory.Footprint(process));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception or NotSupportedException) { return null; }
    }

    public IReadOnlyList<(string Workspace, string ServerId, int Pid)> LanguageServers() => state.Languages.RunningServers();

    public string LanguageServerName(string serverId) => state.Languages.ServerName(serverId);

    public async ValueTask StopLanguageServerAsync(string workspace, string serverId, CancellationToken cancellationToken) =>
        await state.Languages.ExecuteAsync(new LanguageRequest(workspace, Operation: "stop", Server: serverId), cancellationToken);

    public IReadOnlyList<(TerminalRef Terminal, int Pid, string Description)> OwnedProcesses() =>
        [.. (Owned?.List() ?? []).Select(info => (info.Terminal, info.Pid, info.Description))];

    public string ReportOwned(TerminalRef terminal, int pid, string? description)
    {
        if (Owned is not { } owned || state.Terminals is not { } terminals) throw new ArgumentException("This host has no terminals to own a process.");
        var shells = terminals.List().Where(process => process.Pid is not null).Select(process => process.Pid!.Value).ToArray();
        return owned.Report(terminal, pid, description, shells);
    }

    public bool ReleaseOwned(TerminalRef terminal, int pid) => Owned?.Release(terminal, pid) == true;

    public async ValueTask StopProcessAsync(int pid, string command, CancellationToken cancellationToken)
    {
        if (Owned is { } owned) await owned.StopPidAsync(pid, command);
        else await ProcessTable.EndAsync(new Dictionary<int, string> { [pid] = command }, TimeSpan.FromSeconds(3), Posix.Sigterm);
    }

    public void ManageOwned(bool enabled) => Owned?.SetEnabled(enabled);
}