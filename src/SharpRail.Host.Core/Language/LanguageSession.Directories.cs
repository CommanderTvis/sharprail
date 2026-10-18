using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal sealed partial class LanguageSession
{
    private IReadOnlyList<LanguageProject> directoryMarks = [];
    private DateTime directoryMarksRead;

    internal async Task<IReadOnlyList<LanguageProject>> DirectoryMarksAsync(bool refresh, CancellationToken token)
    {
        const string command = "java.project.listSourcePaths";
        if (server.Id != "java" || Exited || capabilities["executeCommandProvider"]?["commands"] is not JsonArray commands ||
            !commands.Any(value => value?.ToString() == command)) return [];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await gate.WaitAsync(deadline.Token);
            try
            {
                if (!refresh && DateTime.UtcNow - directoryMarksRead < TimeSpan.FromSeconds(10)) return directoryMarks;
                directoryMarksRead = DateTime.UtcNow;
                var response = await connection.RequestAsync("workspace/executeCommand", new { command, arguments = Array.Empty<string>() }, deadline.Token);
                if (response?["status"]?.GetValue<bool>() != true || response["data"] is not JsonArray paths) return directoryMarks = [];
                var marks = new List<LanguageProject>();
                foreach (var item in paths.Take(500))
                {
                    var path = item?["path"]?.ToString() ?? "";
                    if (!Path.IsPathFullyQualified(path)) continue;
                    path = Path.GetFullPath(path);
                    if (!LanguageProjectModel.Contains(workspace, path) || !Directory.Exists(path)) continue;
                    var relative = Path.GetRelativePath(workspace, path).Replace('\\', '/');
                    marks.Add(new(relative == "." ? "" : relative, "", server.Id, "Source root", "reported", "Reported by Eclipse JDT Language Server."));
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