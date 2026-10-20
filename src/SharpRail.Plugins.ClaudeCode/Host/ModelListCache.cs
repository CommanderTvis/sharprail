using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>
/// The model choices a terminal read off Claude Code's <c>/model</c> picker, kept on the host that runs the CLI so the
/// next picker opens from here instead of walking the live picker again. One small JSON file under the plugin's state
/// directory; a missing, corrupt or newer-format file is ignored, and a write goes through a temporary file.
/// </summary>
internal sealed class ModelListCache(string path, Func<DateTimeOffset>? clock = null, Func<string, string>? fingerprint = null)
{
    /// <summary>How long a stored list is served without reading again; an older one is still served, then refreshed.</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);

    private const int Format = 1;

    private readonly Lock gate = new();
    private readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Func<string, string> keyOf = fingerprint ?? Fingerprint;
    private Stored? loaded;
    private bool read;

    private sealed record Stored(string Key, IReadOnlyList<string> Models, DateTimeOffset FetchedAt);

    public static string DefaultPath() => Path.Combine(SystemPrompt.StateDirectory(), ClaudeCodeContract.Id, "model-list.json");

    /// <summary>The stored list when it was read for the CLI and account that run now; otherwise an empty one.</summary>
    public ModelListResult Get(string claudeCommand)
    {
        var key = keyOf(claudeCommand);
        lock (gate)
        {
            if (Current() is not { } stored || stored.Key != key) return new([]);
            return new(stored.Models) { FetchedAt = stored.FetchedAt.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture), Stale = now() - stored.FetchedAt >= FreshFor };
        }
    }

    public Ack Put(string claudeCommand, IReadOnlyList<string> models)
    {
        // A list nothing was read into must not displace the last good one.
        if (models.Count == 0 || models.Any(string.IsNullOrWhiteSpace)) return new(false);
        var stored = new Stored(keyOf(claudeCommand), [.. models], now());
        lock (gate)
        {
            loaded = stored;
            read = true;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                File.WriteAllText(temporary, new JsonObject
                {
                    ["format"] = Format,
                    ["key"] = stored.Key,
                    ["fetchedAt"] = stored.FetchedAt.UtcDateTime.ToString("o", System.Globalization.CultureInfo.InvariantCulture),
                    ["models"] = new JsonArray([.. stored.Models.Select(model => (JsonNode?)JsonValue.Create(model))])
                }.ToJsonString());
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new(false); }
        }
        return new();
    }

    private Stored? Current()
    {
        if (read) return loaded;
        read = true;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is JsonObject { } file
                && file["format"]?.GetValue<int>() == Format
                && file["key"]?.GetValue<string>() is { Length: > 0 } key
                && DateTimeOffset.TryParse(file["fetchedAt"]?.GetValue<string>(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var fetchedAt)
                && file["models"] is JsonArray { Count: > 0 } models)
            {
                var names = models.Select(model => model?.GetValue<string>()).ToList();
                if (names.All(name => !string.IsNullOrWhiteSpace(name))) loaded = new(key, [.. names.Select(name => name!)], fetchedAt);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException) { }
        return loaded;
    }

    /// <summary>
    /// What changes the list without spawning anything: the binary (path, size, mtime stand in for its version), the
    /// configuration directory, the endpoint and model overrides in the host's environment, the provider switches, and
    /// the signed-in account and organisation Claude Code records in its state file.
    /// </summary>
    public static string Fingerprint(string claudeCommand)
    {
        var binary = ClaudeCommands.ClaudeBinary(claudeCommand);
        var parts = new List<string> { "binary=" + binary };
        if (Resolve(binary) is { } file && new FileInfo(file) is { Exists: true } info) parts.Add($"file={info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}");
        parts.Add("config=" + ClaudeEnvironment.ClaudeHome());
        foreach (var name in new[] { "ANTHROPIC_BASE_URL", "ANTHROPIC_MODEL", "ANTHROPIC_DEFAULT_OPUS_MODEL", "ANTHROPIC_DEFAULT_SONNET_MODEL", "ANTHROPIC_DEFAULT_HAIKU_MODEL",
            "ANTHROPIC_SMALL_FAST_MODEL", "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY" })
            parts.Add(name + "=" + Environment.GetEnvironmentVariable(name));
        if (Json.ReadObject(ClaudeEnvironment.ClaudeStatePath()) is { } state && state["oauthAccount"] is JsonObject account)
            parts.Add($"account={Json.String(account["accountUuid"])}|{Json.String(account["organizationUuid"])}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
    }

    private static string? Resolve(string binary)
    {
        if (Path.IsPathRooted(binary) || binary.Contains(Path.DirectorySeparatorChar)) return binary;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { binary + ".exe", binary + ".cmd", binary } : [binary])
                if (File.Exists(Path.Combine(directory, name))) return Path.Combine(directory, name);
        return null;
    }
}