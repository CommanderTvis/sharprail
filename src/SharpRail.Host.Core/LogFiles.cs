namespace SharpRail.Host.Core;

/// <summary>Size cap for log files kept on disk: an oversized log moves to <c>.1</c> (replacing the previous one), so at most two files remain.</summary>
internal static class LogFiles
{
    internal const long DefaultLimit = 4 * 1024 * 1024;

    internal static void Rotate(string path, long limit = DefaultLimit)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists && file.Length > limit) File.Move(path, path + ".1", true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Rotates every <c>*.log</c> directly inside <paramref name="directory"/>, for tools that write their own logs there.</summary>
    internal static void RotateAll(string directory, long limit = DefaultLimit)
    {
        try { foreach (var path in Directory.EnumerateFiles(directory, "*.log")) Rotate(path, limit); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}