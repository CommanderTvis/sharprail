using SharpRail.Plugins.AirContext;

namespace SharpRail.Plugins.AirContext.Host;

/// <summary>A workspace-scoped Context operation. Enable, disable and login are user-interface controls only.</summary>
internal sealed record ContextRequest(string Workspace, string Operation = "status", string Query = "", string Path = "", int Limit = 20);

/// <summary>Context lifecycle or bounded search output. No credentials are exposed.</summary>
internal sealed record ContextResult(string State, string Detail = "", bool Enabled = false, string Data = "", string Command = "")
{
    internal ContextOutcome For(string workspace) => new(workspace, State, Detail, Enabled, Data, Command);
}

/// <summary>A bounded in-memory log; the oldest output is dropped first.</summary>
internal sealed class ContextLog
{
    private const int Capacity = 32 * 1024;
    private readonly object gate = new();
    private readonly Queue<string> chunks = new();
    private int length;

    internal void Add(string source, string message)
    {
        var text = $"[{DateTime.UtcNow:HH:mm:ss} UTC] {source}: {message.TrimEnd()}\n";
        lock (gate)
        {
            if (text.Length > Capacity) text = text[^Capacity..];
            chunks.Enqueue(text); length += text.Length;
            while (length > Capacity) length -= chunks.Dequeue().Length;
        }
    }

    internal string Read()
    {
        lock (gate) return length == 0 ? "No logs yet. Viewing logs does not start the CLI." : string.Concat(chunks);
    }
}

internal static class Containment
{
    /// <summary>
    /// The one containment rule for a workspace-relative path: inside the root, never under <c>.git</c>, and no
    /// symbolic link on the way. The leaf may be missing.
    /// </summary>
    internal static string Contain(string currentRoot, string path)
    {
        var full = Path.GetFullPath(Path.Combine(currentRoot, path));
        var relative = Path.GetRelativePath(currentRoot, full);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new UnauthorizedAccessException("The path is outside this workspace.");
        var segments = relative.Split(Path.DirectorySeparatorChar);
        if (OperatingSystem.IsWindows() && segments.Any(segment => segment.Contains(':') || segment.EndsWith(' ') || segment is not ("." or "..") && segment.EndsWith('.')))
            throw new UnauthorizedAccessException("Alternate streams and ambiguous Windows paths are not supported.");
        if (segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase)))
            throw new UnauthorizedAccessException("Git's own directory cannot be read.");
        var check = currentRoot;
        foreach (var segment in segments)
        {
            check = Path.Combine(check, segment);
            if (new FileInfo(check).LinkTarget is not null) throw new UnauthorizedAccessException("Symbolic link previews are not supported.");
        }
        return full;
    }
}