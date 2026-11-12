using System.Text;

using Tereditor.Host.Abstractions;

namespace Tereditor.Host.Core;

public sealed partial class ProjectServices
{
    private const string TereditorName = ".tereditor";
    private const string TereditorEntry = "/.tereditor/";
    // One process may serve several sessions of the same repository; appends to its exclude file take turns.
    private static readonly SemaphoreSlim ExcludeWrites = new(1, 1);

    public async ValueTask<TereditorDirectoryStatus> GetTereditorDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var current = root;
        if (!Directory.Exists(Path.Combine(current, TereditorName)) || await GitRepository.CommonDirectoryAsync(current, cancellationToken) is null)
            return new(false, false, false);
        var tracked = await GitRepository.IsTrackedAsync(current, TereditorName, cancellationToken);
        return new(true, tracked, !tracked && await GitRepository.IsIgnoredAsync(current, TereditorName, cancellationToken));
    }

    public async ValueTask<TereditorDirectoryStatus> ExcludeTereditorDirectoryAsync(CancellationToken cancellationToken = default)
    {
        var current = root;
        var status = await GetTereditorDirectoryAsync(cancellationToken);
        if (!status.Exists) return status with { Detail = "This workspace has no .tereditor directory." };
        if (status.Tracked) return status with { Detail = "Git tracks .tereditor, so excluding it has no effect." };
        if (status.Ignored) return status with { Detail = ".tereditor is already ignored." };
        var common = await GitRepository.CommonDirectoryAsync(current, cancellationToken)
            ?? throw new InvalidOperationException("This folder is not a Git repository.");
        await ExcludeWrites.WaitAsync(cancellationToken);
        try { await Task.Run(() => AppendExclude(Path.Combine(common, "info", "exclude")), cancellationToken); }
        finally { ExcludeWrites.Release(); }
        return new(true, false, await GitRepository.IsIgnoredAsync(current, TereditorName, cancellationToken), true);
    }

    // Keeps the file's existing text and line endings, adds a final newline only where one is missing, and swaps the
    // result in whole so a reader never sees a half-written exclude file.
    private static void AppendExclude(string file)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var existing = File.Exists(file) ? File.ReadAllText(file) : "";
        if (existing.Split('\n').Any(line => line.Trim() is TereditorEntry or "/.tereditor" or ".tereditor/" or ".tereditor")) return;
        var newline = existing.Contains("\r\n") ? "\r\n" : "\n";
        var text = new StringBuilder(existing);
        if (existing.Length > 0 && !existing.EndsWith('\n')) text.Append(newline);
        text.Append(TereditorEntry).Append(newline);
        var temporary = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            File.WriteAllText(temporary, text.ToString(), new UTF8Encoding(false));
            File.Move(temporary, file, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}