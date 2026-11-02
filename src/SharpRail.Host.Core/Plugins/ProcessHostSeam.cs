using System.ComponentModel;
using System.Diagnostics;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>
/// What the Processes plugin may reach in the host (H27): the process table, per-process usage, the language service's running servers and
/// its stop operation, and the owned-process registry with its on/off switch. Every member reads the process table or waits.
/// </summary>
internal sealed class ProcessHostSeam(HostStateStore state) : IProcessHost
{
    private OwnedProcesses? Owned => state.Terminals?.Owned;

    public IReadOnlyList<(int Pid, int ParentPid, string Command)> Table() => [.. ProcessTable.Read().Select(row => (row.Pid, row.Ppid, row.Arguments))];

    public (TimeSpan Cpu, long MemoryBytes)? Usage(int pid)
    {
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