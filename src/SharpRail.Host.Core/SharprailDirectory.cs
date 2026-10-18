using System.Text;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    private const string SharprailName = ".sharprail";
    private const string SharprailEntry = "/.sharprail/";
    // One process may serve several sessions of the same repository; appends to its exclude file take turns.
    private static readonly SemaphoreSlim ExcludeWrites = new(1, 1);

    public async ValueTask<SharprailDirectoryStatus> GetSharprailDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var current = root;
        if (!Directory.Exists(Path.Combine(current, SharprailName)) || await GitRepository.CommonDirectoryAsync(current, cancellationToken) is null)
            return new(false, false, false);
        var tracked = await GitRepository.IsTrackedAsync(current, SharprailName, cancellationToken);
        return new(true, tracked, !tracked && await GitRepository.IsIgnoredAsync(current, SharprailName, cancellationToken));
    }

    public async ValueTask<SharprailDirectoryStatus> ExcludeSharprailDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var current = root;
        var status = await GetSharprailDirectoryAsync(cancellationToken);
        if (!status.Exists) return status with { Detail = "This workspace has no .sharprail directory." };
        if (status.Tracked) return status with { Detail = "Git tracks .sharprail, so excluding it has no effect." };
        if (status.Ignored) return status with { Detail = ".sharprail is already ignored." };
        var common = await GitRepository.CommonDirectoryAsync(current, cancellationToken)
            ?? throw new InvalidOperationException("This folder is not a Git repository.");
        await ExcludeWrites.WaitAsync(cancellationToken);
        try { await Task.Run(() => AppendExclude(Path.Combine(common, "info", "exclude")), cancellationToken); }
        finally { ExcludeWrites.Release(); }
        return new(true, false, await GitRepository.IsIgnoredAsync(current, SharprailName, cancellationToken), true);
    }

    // Keeps the file's existing text and line endings, adds a final newline only where one is missing, and swaps the
    // result in whole so a reader never sees a half-written exclude file.
    private static void AppendExclude(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var existing = File.Exists(file) ? File.ReadAllText(file) : "";
        if (existing.Split('\n').Any(line => line.Trim() is SharprailEntry or "/.sharprail" or ".sharprail/" or ".sharprail")) return;
        var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
        var text = new StringBuilder(existing);
        if (existing.Length > 0 && !existing.EndsWith('\n')) text.Append(newline);
        text.Append(SharprailEntry).Append(newline);
        var temporary = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}