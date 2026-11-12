using Ghostty.Avalonia;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Terminal;

internal static class TerminalFileLinks
{
    internal static async Task<IReadOnlyList<TerminalLink>> Resolve(ITerminalService terminals, TerminalLaunch launch, string text, CancellationToken token)
    {
        // Directories link only where the text names them absolutely (a path, `~/`, a file URL): bare folder names are
        // too common in prose to underline.
        var files = (await terminals.FindFileLinksAsync(launch.SessionId, text, token)).Where(link => !link.IsDirectory || Absolute(text, link.Start))
            .Select(link => new TerminalLink(link.Start, link.Length, link.Path, link.Line, link.Column, link.IsDirectory)).ToArray();
        if (launch.ResolveCommitLinks is null) return files;
        IReadOnlyList<TerminalLink> commits;
        try { commits = await launch.ResolveCommitLinks(text, token); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return files; }
        return files.Concat(commits.Where(commit => !files.Any(file => commit.Start < file.Start + file.Length && file.Start < commit.Start + commit.Length))).ToArray();
    }

    private static bool Absolute(string text, int start) => text[start] is '/' or '~' ||
        text.AsSpan(start).StartsWith("file:", StringComparison.OrdinalIgnoreCase) ||
        start + 2 < text.Length && char.IsAsciiLetter(text[start]) && text[start + 1] == ':' && text[start + 2] is '/' or '\\';
}