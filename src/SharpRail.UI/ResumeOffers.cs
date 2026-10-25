using SharpRail.Host.Abstractions;

namespace SharpRail.UI;

/// <summary>
/// Shows each revived agent tab's resume offer inside the tab. The host never resumes an agent on its own, since another
/// SharpRail may have the same session open; any client's answer settles the offer for every client.
/// </summary>
public sealed partial class WorkbenchWindow
{
    private void ApplyResumeOffers(HostState host)
    {
        foreach (var terminal in documentContent.Values.OfType<Terminal.TerminalView>()) ApplyResumeOffer(terminal, host);
    }

    private void ApplyResumeOffer(Terminal.TerminalView terminal, HostState host)
    {
        var offer = host.ResumeOffers.FirstOrDefault(offer => TerminalTab.SessionFor(offer.Terminal.WorkspaceId, offer.Terminal.TabKey) == terminal.SessionId);
        if (offer is null) { terminal.ShowResumeOffer(null, null, null); return; }
        var target = offer.Terminal;
        terminal.ShowResumeOffer(ResumeOfferText(offer),
            offer.OpenElsewhere ? null : () => _ = ShareAsync(HostStateChange.AnswerResume(target, true)),
            () => _ = ShareAsync(HostStateChange.AnswerResume(target, false)),
            offer.QuitConfirmation.Length > 0 ? () => _ = ConfirmQuitAsync(offer) : null);
    }

    // The confirmation shows on the client that clicked; the host does the ending and reports progress or failure on the offer.
    private async Task ConfirmQuitAsync(TerminalResumeOffer offer)
    {
        if (!await Panels.Dialogs.Confirm(this, "End that process?", offer.QuitConfirmation, "Quit and resume", "QuitSessionOwnerConfirm")) return;
        await ShareAsync(HostStateChange.QuitOwnerAndResume(offer.Terminal));
    }

    internal static string ResumeOfferText(TerminalResumeOffer offer)
    {
        var agent = offer.Agent switch { "claude" => "Claude", "codex" => "Codex", "omp" => "omp", var other => other };
        var session = offer.SessionId.Length > 0 ? $"{agent} session {offer.SessionId[..Math.Min(8, offer.SessionId.Length)]}" : $"A {agent} session";
        var text = offer.OpenElsewhere
            ? offer.OwnerText.Length > 0 ? $"{session} {offer.OwnerText}" : $"{session} is already open in another tab, SharpRail window or instance, so it is not resumed here."
            : $"{session} was running here.";
        return offer.Progress.Length > 0 ? $"{text} {offer.Progress}" : text;
    }
}