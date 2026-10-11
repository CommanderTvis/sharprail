using Ghostty.Avalonia;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Terminal;

internal static class TerminalFileLinks
{
    internal static async Task<IReadOnlyList<TerminalLink>> Resolve(ITerminalService terminals, TerminalLaunch launch, string text, CancellationToken token)
        => (await terminals.FindFileLinksAsync(launch.SessionId, text, token)).Where(link => !link.IsDirectory)
            .Select(link => new TerminalLink(link.Start, link.Length, link.Path, link.Line, link.Column, link.IsDirectory)).ToArray();
}