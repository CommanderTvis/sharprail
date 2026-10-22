using System.Collections.Concurrent;
using System.Text.Json;

using SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>
/// The <c>--permission-mode</c> choices of the CLI this host runs, read once per executable from <c>claude --help</c>
/// and kept for the process; a CLI whose help names none gets SharpRail's fallback list.
/// </summary>
internal sealed class PermissionModeProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentDictionary<string, Lazy<Task<IReadOnlyList<string>>>> probes = new(StringComparer.Ordinal);

    public async Task<PermissionModesResult> GetAsync(string claudeCommand)
    {
        var binary = ClaudeCommands.ClaudeBinary(claudeCommand);
        var probe = probes.GetOrAdd(binary, key => new(() => Task.Run(() => ProbeAsync(key))));
        var modes = await probe.Value;
        // A CLI that could not be read (not installed yet, timed out) is asked again next time rather than remembered.
        if (modes.Count == 0) probes.TryRemove(new(binary, probe));
        return new(modes.Count > 0 ? modes : ClaudePermissionModes.Fallback) { FromCli = modes.Count > 0, ConfiguredDefault = ConfiguredDefault() };
    }

    private static async Task<IReadOnlyList<string>> ProbeAsync(string binary)
    {
        var run = await Bounded.RunAsync([binary, "--help"], Timeout);
        return run.Ok ? ClaudePermissionModes.Parse(run.Out) : [];
    }

    /// <summary><c>permissions.defaultMode</c> of the user's settings, the only file read: project files vary by directory.</summary>
    public static string? ConfiguredDefault()
    {
        try
        {
            var path = Path.Combine(ClaudeEnvironment.ClaudeHome(), "settings.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path), new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("permissions", out var permissions) && permissions.ValueKind == JsonValueKind.Object &&
                permissions.TryGetProperty("defaultMode", out var mode) && mode.ValueKind == JsonValueKind.String && mode.GetString() is { Length: > 0 } value
                ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}