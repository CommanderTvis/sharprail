using System.Globalization;
using System.Text.Json;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

/// <summary>
/// How often agents used each language server through the lsp tools, per workspace and server. Kept in memory and written to the host's language
/// storage, bounded to <see cref="Capacity"/> workspace/server pairs (the least recently used are dropped), so the pane can show whether tools are used at all.
/// </summary>
internal sealed class LanguageUsageStore(string directory)
{
    internal const int Capacity = 128;
    private readonly object gate = new();
    private Dictionary<string, LanguageUsageSummary>? counts;
    private string Path => System.IO.Path.Combine(directory, "usage.json");

    private sealed record Entry(string Workspace, string Server, LanguageUsageSummary Usage);

    internal LanguageUsageSummary? Get(string workspace, string server)
    {
        lock (gate) return Load().GetValueOrDefault(workspace + "\0" + server);
    }

    internal void Record(string workspace, string server, bool success)
    {
        lock (gate)
        {
            var all = Load();
            var key = workspace + "\0" + server;
            var before = all.GetValueOrDefault(key) ?? new(0, 0, 0, "");
            all[key] = new(before.Calls + 1, before.Successes + (success ? 1 : 0), before.Failures + (success ? 0 : 1), DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            foreach (var old in all.OrderBy(pair => pair.Value.LastUsed, StringComparer.Ordinal).Take(Math.Max(0, all.Count - Capacity)).ToArray()) all.Remove(old.Key);
            Save(all);
        }
    }

    internal void Remove(string workspace)
    {
        lock (gate)
        {
            var all = Load();
            var gone = all.Keys.Where(key => key.StartsWith(workspace + "\0", StringComparison.Ordinal)).ToArray();
            foreach (var key in gone) all.Remove(key);
            if (gone.Length > 0) Save(all);
        }
    }

    private Dictionary<string, LanguageUsageSummary> Load()
    {
        if (counts is not null) return counts;
        counts = [];
        try
        {
            if (File.Exists(Path))
                foreach (var entry in JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(Path)) ?? [])
                    if (entry.Usage is not null) counts[entry.Workspace + "\0" + entry.Server] = entry.Usage;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException) { }
        return counts;
    }

    private void Save(Dictionary<string, LanguageUsageSummary> all)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var temporary = Path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(all.Select(pair => { var parts = pair.Key.Split('\0'); return new Entry(parts[0], parts[1], pair.Value); })));
            File.Move(temporary, Path, true);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}