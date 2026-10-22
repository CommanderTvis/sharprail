using SharpRail.Host.Core.Plugins;

namespace SharpRail.Host.Core;

/// <summary>
/// The project's instruction-section disable-list, <c>.sharprail/instructions.toml</c> in the project root (the Default
/// checkout, so every workspace of the project agrees). Read lazily off the UI thread and re-read when its stamp changes;
/// written only by an explicit toggle.
/// </summary>
public sealed partial class HostStateStore
{
    private readonly Dictionary<string, ProjectInstructionsFile.Snapshot> instructionFiles = [];
    private readonly Lock instructionGate = new();

    /// <summary>Raised with the project root after its file changed, through the host or on disk.</summary>
    internal event Action<string>? InstructionsChanged;

    /// <summary>The project root whose file governs <paramref name="workspace"/>: its registered project, else the path itself.</summary>
    internal string InstructionsRoot(string workspace)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(workspace);
        return ProjectOfWorkspace(trimmed) ?? ProjectOfWorkspace(workspace) ?? trimmed;
    }

    /// <summary>The workspace's project file; a stamp check keeps it current without a watcher.</summary>
    internal ProjectInstructionsFile.Snapshot ProjectInstructions(string workspace)
    {
        var root = InstructionsRoot(workspace);
        var path = ProjectInstructionsFile.PathOf(root);
        ProjectInstructionsFile.Snapshot? known;
        lock (instructionGate) instructionFiles.TryGetValue(root, out known);
        var stamp = ProjectInstructionsFile.StampOf(path);
        if (known is not null && known.Stamp == stamp && known.Stamp != "unreadable") return known;
        var next = ProjectInstructionsFile.Read(root);
        Remember(root, next, known);
        return next;
    }

    /// <summary>Turns one section on or off for the workspace's project.</summary>
    internal ProjectInstructionsFile.Snapshot SetInstructionSection(string workspace, string id, bool enabled)
    {
        var root = InstructionsRoot(workspace);
        ProjectInstructionsFile.Snapshot? known;
        lock (instructionGate) instructionFiles.TryGetValue(root, out known);
        var next = ProjectInstructionsFile.Set(root, id, enabled);
        Remember(root, next, known ?? ProjectInstructionsFile.Snapshot.Empty(next.Path));
        return next;
    }

    private void Remember(string root, ProjectInstructionsFile.Snapshot next, ProjectInstructionsFile.Snapshot? known)
    {
        lock (instructionGate) instructionFiles[root] = next;
        // The first read is not a change; a later one that differs is, whoever made it.
        if (known is not null && !known.Disabled.SequenceEqual(next.Disabled)) InstructionsChanged?.Invoke(root);
    }
}
