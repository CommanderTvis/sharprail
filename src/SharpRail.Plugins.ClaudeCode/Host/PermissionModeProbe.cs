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
        return new(modes.Count > 0 ? modes : ClaudePermissionModes.Fallback) { FromCli = modes.Count > 0, ConfiguredDefault = UserSetting("permissions", "defaultMode"), ConfiguredAdvisor = UserSetting(null, "advisorModel") };
    }

    private static async Task<IReadOnlyList<string>> ProbeAsync(string binary)
    {
        var run = await Bounded.RunAsync([binary, "--help"], Timeout);
        return run.Ok ? ClaudePermissionModes.Parse(run.Out) : [];
    }

    /// <summary>A string setting of the user's settings (in <paramref name="section"/> when given), the only file read: project files vary by directory.</summary>
    public static string? UserSetting(string? section, string key)
    {
        try
        {
            var path = Path.Combine(ClaudeEnvironment.ClaudeHome(), "settings.json");
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path), new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            var scope = root;
            if (section is not null && (!root.TryGetProperty(section, out scope) || scope.ValueKind != JsonValueKind.Object)) return null;
            return scope.TryGetProperty(key, out var setting) && setting.ValueKind == JsonValueKind.String && setting.GetString() is { Length: > 0 } value ? value : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}