using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal sealed partial class LanguageSession
{
    private IReadOnlyList<LanguageProject> directoryMarks = [];
    private DateTime directoryMarksRead;

    internal async Task<IReadOnlyList<LanguageProject>> DirectoryMarksAsync(bool refresh, CancellationToken token)
    {
        if (server.Spec.SourceRootsAsync is not { } roots || Exited) return [];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await gate.WaitAsync(deadline.Token);
            try
            {
                if (!refresh && DateTime.UtcNow - directoryMarksRead < TimeSpan.FromSeconds(10)) return directoryMarks;
                directoryMarksRead = DateTime.UtcNow;
                var reporter = "Reported by " + server.Implementation.Split('·')[0].Trim() + ".";
                var marks = new List<LanguageProject>();
                foreach (var reported in (await roots(this, deadline.Token)).Take(500))
                {
                    if (!Path.IsPathFullyQualified(reported)) continue;
                    var path = Path.GetFullPath(reported);
                    if (!LanguageProjectModel.Contains(workspace, path) || !Directory.Exists(path)) continue;
                    var relative = Path.GetRelativePath(workspace, path).Replace('\\', '/');
                    marks.Add(new(relative == "." ? "" : relative, "", server.Id, "Source root", "reported", reporter));
                }
                return directoryMarks = marks.DistinctBy(mark => mark.Root).OrderBy(mark => mark.Root, StringComparer.Ordinal).ToArray();
            }
            finally { gate.Release(); }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return directoryMarks; }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException)
        {
            log.Add("directory metadata", error.Message);
            return directoryMarks = [];
        }
    }
}