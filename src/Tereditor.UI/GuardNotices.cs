using Tereditor.Host.Abstractions;

namespace Tereditor.UI;

/// <summary>
/// Shows the memory guard's banner inside each terminal tab, from the shared state, so every client (desktop, remote, browser, Android)
/// shows the same one; dismissing it in any client dismisses it in all of them.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private void ApplyGuardNotices(HostState host)
    {
        foreach (var terminal in documentContent.Values.OfType<Terminal.TerminalView>()) ApplyGuardNotice(terminal, host);
    }

    private void ApplyGuardNotice(Terminal.TerminalView terminal, HostState host)
    {
        var notice = host.GuardNotices.FirstOrDefault(notice => TerminalTab.SessionFor(notice.Terminal.WorkspaceId, notice.Terminal.TabKey) == terminal.SessionId);
        if (notice is null) { terminal.ShowGuardNotice(null, [], null); return; }
        terminal.ShowGuardNotice(notice.Text, notice.Lines, () => _ = ShareAsync(HostStateChange.DismissGuardNotice(notice.Terminal, notice.Sequence)));
    }

    // Notices compare by tab and sequence: their line lists are fresh arrays in every remote snapshot.
    private static bool SameGuardNotices(HostState previous, HostState next) =>
        previous.GuardNotices.Select(notice => (notice.Terminal, notice.Sequence)).SequenceEqual(next.GuardNotices.Select(notice => (notice.Terminal, notice.Sequence)));
}