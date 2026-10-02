using System.Text.RegularExpressions;

namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

internal sealed record ScopedPath(ClaudeConfigScope Scope, string Path);

/// <summary>Every file and directory Claude Code reads its configuration from, in precedence order where it matters.</summary>
internal static partial class ClaudePaths
{
    public static string ClaudeHome() => ClaudeEnvironment.ClaudeHome();

    private static string ManagedSettingsPath() =>
        OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode/managed-settings.json"
        : OperatingSystem.IsWindows() ? @"C:\Program Files\ClaudeCode\managed-settings.json"
        : "/etc/claude-code/managed-settings.json";

    private static string ManagedInstructionsPath() =>
        OperatingSystem.IsMacOS() ? "/Library/Application Support/ClaudeCode/CLAUDE.md"
        : OperatingSystem.IsWindows() ? @"C:\Program Files\ClaudeCode\CLAUDE.md"
        : "/etc/claude-code/CLAUDE.md";

    public static IReadOnlyList<ScopedPath> SettingsPaths(string root) =>
    [
        new(ClaudeConfigScope.Managed, ManagedSettingsPath()),
        new(ClaudeConfigScope.Local, Path.Combine(root, ".claude", "settings.local.json")),
        new(ClaudeConfigScope.Project, Path.Combine(root, ".claude", "settings.json")),
        new(ClaudeConfigScope.User, Path.Combine(ClaudeHome(), "settings.json"))
    ];

    // Claude Code reads AGENTS.md in a directory that has no CLAUDE.md.
    private static string DirectoryInstructionsPath(string directory)
    {
        var claude = Path.Combine(directory, "CLAUDE.md");
        var agents = Path.Combine(directory, "AGENTS.md");
        return !File.Exists(claude) && File.Exists(agents) ? agents : claude;
    }

    private static IEnumerable<string> DirectoriesFromRoot(string cwd)
    {
        var directories = new List<string>();
        for (var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cwd)); ; directory = Path.GetDirectoryName(directory)!)
        {
            directories.Insert(0, directory);
            if (Path.GetDirectoryName(directory) is null) return directories;
        }
    }

    /// <summary>The user's instructions, then every directory's from the filesystem root down to <paramref name="cwd"/>, then its <c>.claude/CLAUDE.md</c>.</summary>
    public static IReadOnlyList<ScopedPath> InstructionPaths(string cwd) =>
    [
        new(ClaudeConfigScope.Managed, ManagedInstructionsPath()),
        new(ClaudeConfigScope.User, Path.Combine(ClaudeHome(), "CLAUDE.md")),
        .. DirectoriesFromRoot(cwd).SelectMany(directory => new ScopedPath[]
        {
            new(ClaudeConfigScope.Project, DirectoryInstructionsPath(directory)),
            new(ClaudeConfigScope.Local, Path.Combine(directory, "CLAUDE.local.md"))
        }),
        new(ClaudeConfigScope.Project, Path.Combine(cwd, ".claude", "CLAUDE.md"))
    ];

    public static IReadOnlyList<ScopedPath> RulesDirectories(string root) =>
        [new(ClaudeConfigScope.User, Path.Combine(ClaudeHome(), "rules")), new(ClaudeConfigScope.Project, Path.Combine(root, ".claude", "rules"))];

    public static IReadOnlyList<ScopedPath> McpPaths(string root) => [new(ClaudeConfigScope.Project, Path.Combine(root, ".mcp.json"))];

    public static string ClaudeStatePath() => ClaudeEnvironment.ClaudeStatePath();

    public static IReadOnlyList<ScopedPath> SkillDirectories(string root) =>
        [new(ClaudeConfigScope.User, Path.Combine(ClaudeHome(), "skills")), new(ClaudeConfigScope.Project, Path.Combine(root, ".claude", "skills"))];

    public static IReadOnlyList<ScopedPath> AgentDirectories(string root) =>
        [new(ClaudeConfigScope.User, Path.Combine(ClaudeHome(), "agents")), new(ClaudeConfigScope.Project, Path.Combine(root, ".claude", "agents"))];

    [GeneratedRegex("[^a-zA-Z0-9]")]
    private static partial Regex NotAlphanumeric();

    public static string MemoryIndexPath(string root) =>
        Path.Combine(ClaudeHome(), "projects", NotAlphanumeric().Replace(root, "-"), "memory", "MEMORY.md");
}