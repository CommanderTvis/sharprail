using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

namespace SharpRail.UI;

/// <summary>Tells the user about each process an agent reports with <c>owned_process_report</c>, which SharpRail stops with the terminal or on quit.</summary>
public sealed partial class WorkbenchWindow
{
    private void NotifyOwnedProcesses(HostState previous, HostState next)
    {
        foreach (var owned in next.OwnedProcesses.Where(process => !previous.OwnedProcesses.Contains(process)))
        {
            var tab = next.TerminalTitles.FirstOrDefault(title => title.Terminal == owned.Terminal)?.Title ?? "An agent terminal";
            var what = owned.Description.Length > 0 ? $"{owned.Description} (pid {owned.Pid})" : $"pid {owned.Pid}";
            Toasts.Push(ToastVariant.Info, $"{tab} reported {what}. SharpRail stops it when that terminal closes or SharpRail quits.", "Background process reported");
        }
    }

    /// <summary>The text of the quit confirmation's line about reported processes; empty when there are none.</summary>
    internal static string OwnedProcessesNote(int count) =>
        count == 0 ? "" : count == 1 ? " Quitting also stops 1 background process an agent reported." : $" Quitting also stops {count} background processes agents reported.";
}