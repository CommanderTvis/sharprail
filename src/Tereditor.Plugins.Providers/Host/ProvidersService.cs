using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Tereditor.Plugins.Providers.Host;

/// <summary>A provider the user added by hand: not in the agent's documentation, so tereditor remembers its name and variable.</summary>
public sealed record CustomProvider(ProviderAgent Agent, string Id, string Name, string? EnvVar);

/// <summary>Where the services read and write: the two credential files, the remembered custom providers and the process environment.</summary>
internal sealed record ProviderFiles(Func<string> PiAuth, Func<string> OmpEnv, Func<IReadOnlyList<CustomProvider>> LoadCustom, Action<IReadOnlyList<CustomProvider>> SaveCustom, Func<string, string?> Environment);

/// <summary>
/// Reads and edits the credential files of Pi (<c>auth.json</c>, https://github.com/badlogic/pi-mono docs/providers.md) and omp
/// (the <c>.env</c> of its tereditor profile, https://omp.sh/docs/env). A report never carries a key; a key is written once
/// and never read back or logged. Files are rewritten whole and atomically, with the owner as their only reader.
/// </summary>
internal sealed class ProvidersService(ProviderFiles files)
{
    private readonly Lock gate = new();

    /// <summary>The default files: Pi's agent directory (<c>PI_CODING_AGENT_DIR</c> or <c>~/.pi/agent</c>) and the tereditor profile of omp.</summary>
    public static ProviderFiles DefaultFiles(Func<IReadOnlyList<CustomProvider>> load, Action<IReadOnlyList<CustomProvider>> save, string? ompHome = null) => new(
        () => Path.Combine(Environment.GetEnvironmentVariable("PI_CODING_AGENT_DIR") is { Length: > 0 } agent ? agent
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent"), "auth.json"),
        () => Path.Combine(ompHome ?? (Environment.GetEnvironmentVariable("TEREDITOR_OMP_HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
            ".omp", "profiles", "tereditor", "agent", ".env"),
        load, save, Environment.GetEnvironmentVariable);

    public ProvidersReport List(ProviderAgent agent)
    {
        lock (gate) return Build(agent);
    }

    public ProvidersReport SetKey(SetProviderKeyParams p)
    {
        if (!ProviderCatalog.IsProviderId(p.ProviderId)) throw new ArgumentException("A provider id uses lower case letters, digits, dots, dashes and underscores.");
        var key = p.Key.Trim();
        if (key.Length == 0) throw new ArgumentException("A key cannot be empty.");
        if (key.Any(c => c is '\n' or '\r' or '\0')) throw new ArgumentException("A key is one line.");
        lock (gate)
        {
            var known = ProviderCatalog.For(p.Agent).FirstOrDefault(provider => provider.Id == p.ProviderId);
            var custom = files.LoadCustom().FirstOrDefault(provider => provider.Agent == p.Agent && provider.Id == p.ProviderId);
            var variable = known?.EnvVar ?? p.EnvVar ?? custom?.EnvVar;
            if (known is null)
            {
                if (variable is not null && !ProviderCatalog.IsEnvVar(variable)) throw new ArgumentException("A variable name uses upper case letters, digits and underscores.");
                if (p.Agent == ProviderAgent.Omp && variable is null) throw new ArgumentException("omp reads a key from an environment variable: name the variable for this provider.");
                Remember(new CustomProvider(p.Agent, p.ProviderId, p.Name?.Trim() is { Length: > 0 } name ? name : p.ProviderId, variable));
            }
            if (p.Agent == ProviderAgent.Pi) WritePiKey(p.ProviderId, key);
            else WriteOmpKey(variable!, key);
            return Build(p.Agent);
        }
    }

    public ProvidersReport RemoveKey(RemoveProviderKeyParams p)
    {
        lock (gate)
        {
            var custom = files.LoadCustom().FirstOrDefault(provider => provider.Agent == p.Agent && provider.Id == p.ProviderId);
            if (p.Agent == ProviderAgent.Pi) RemovePiKey(p.ProviderId);
            else if ((ProviderCatalog.Omp.FirstOrDefault(provider => provider.Id == p.ProviderId)?.EnvVar ?? custom?.EnvVar) is { } variable) RemoveOmpKey(variable);
            if (custom is not null) files.SaveCustom([.. files.LoadCustom().Where(provider => provider != custom)]);
            return Build(p.Agent);
        }
    }

    private void Remember(CustomProvider provider)
    {
        var all = files.LoadCustom().Where(existing => !(existing.Agent == provider.Agent && existing.Id == provider.Id));
        files.SaveCustom([.. all, provider]);
    }

    private ProvidersReport Build(ProviderAgent agent)
    {
        var stored = agent == ProviderAgent.Pi ? ReadPi() : ReadOmp();
        var rows = new List<ProviderEntry>();
        foreach (var provider in ProviderCatalog.For(agent))
            rows.Add(Row(agent, provider.Id, provider.Name, provider.EnvVar, false, stored));
        foreach (var provider in files.LoadCustom().Where(provider => provider.Agent == agent && rows.All(row => row.Id != provider.Id)))
            rows.Add(Row(agent, provider.Id, provider.Name, provider.EnvVar, true, stored));
        // A Pi entry the catalog and the custom list do not know (a provider defined in models.json) is still shown, so it can be removed.
        if (agent == ProviderAgent.Pi)
            foreach (var (id, kind) in stored.Where(pair => rows.All(row => row.Id != pair.Key)))
                rows.Add(Row(agent, id, id, null, true, stored));
        var location = agent == ProviderAgent.Pi ? files.PiAuth() : files.OmpEnv();
        return new ProvidersReport(agent, location, rows)
        {
            Note = agent == ProviderAgent.Omp ? "omp reads these keys in launches that use tereditor's tools (its tereditor profile)." : null
        };
    }

    private ProviderEntry Row(ProviderAgent agent, string id, string name, string? variable, bool custom, Dictionary<string, string> stored)
    {
        var source = ProviderSource.None;
        string? note = id == ProviderCatalog.AnthropicId ? ProviderCatalog.AnthropicNote : null;
        if (agent == ProviderAgent.Pi && stored.TryGetValue(id, out var kind))
        {
            source = kind == "api_key" ? ProviderSource.Key : ProviderSource.SignIn;
            if (source == ProviderSource.SignIn && id == ProviderCatalog.AnthropicId) note = "A Claude subscription sign-in is stored. It is not supported here: sign out in Pi (/logout) and add an API key instead.";
        }
        else if (agent == ProviderAgent.Omp && variable is not null && stored.ContainsKey(variable)) source = ProviderSource.Key;
        if (source == ProviderSource.None && variable is not null && files.Environment(variable) is { Length: > 0 }) source = ProviderSource.Environment;
        return new ProviderEntry(id, name, variable, source) { Custom = custom, Note = note };
    }

    // The ids of Pi's auth file with the kind of each credential; the secret values are never kept.
    private Dictionary<string, string> ReadPi()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = files.PiAuth();
        if (!File.Exists(path)) return result;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject root) return result;
            foreach (var (id, node) in root)
                if (node is JsonObject credential) result[id] = credential["type"]?.GetValue<string>() ?? "api_key";
        }
        catch (Exception error) when (error is JsonException or IOException or UnauthorizedAccessException or InvalidOperationException) { }
        return result;
    }

    private void WritePiKey(string id, string key)
    {
        var path = files.PiAuth();
        var root = ReadObject(path);
        root[id] = new JsonObject { ["type"] = "api_key", ["key"] = key };
        WritePrivate(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private void RemovePiKey(string id)
    {
        var path = files.PiAuth();
        if (!File.Exists(path)) return;
        var root = ReadObject(path);
        if (!root.Remove(id)) return;
        WritePrivate(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static JsonObject ReadObject(string path)
    {
        if (!File.Exists(path)) return [];
        try { return JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidOperationException("Pi's auth.json is not a JSON object; fix or remove it first."); }
        catch (JsonException) { throw new InvalidOperationException("Pi's auth.json is not valid JSON; fix or remove it first."); }
    }

    // omp's .env: only the variables named here are touched, every other line is kept as it is.
    private Dictionary<string, string> ReadOmp()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var path = files.OmpEnv();
        if (!File.Exists(path)) return result;
        try
        {
            foreach (var line in File.ReadAllLines(path))
                if (Assignment(line) is { } name && line[(line.IndexOf('=') + 1)..].Trim().Length > 0) result[name] = "set";
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        return result;
    }

    private static string? Assignment(string line)
    {
        var trimmed = line.TrimStart();
        if (trimmed.StartsWith('#')) return null;
        if (trimmed.StartsWith("export ", StringComparison.Ordinal)) trimmed = trimmed[7..].TrimStart();
        var equals = trimmed.IndexOf('=');
        return equals > 0 && ProviderCatalog.IsEnvVar(trimmed[..equals].Trim()) ? trimmed[..equals].Trim() : null;
    }

    private void WriteOmpKey(string variable, string key)
    {
        var path = files.OmpEnv();
        var lines = File.Exists(path) ? File.ReadAllLines(path).ToList() : [];
        var entry = variable + "=" + Quote(key);
        var at = lines.FindIndex(line => Assignment(line) == variable);
        if (at >= 0) lines[at] = entry;
        else lines.Add(entry);
        WritePrivate(path, string.Join('\n', lines) + "\n");
    }

    private void RemoveOmpKey(string variable)
    {
        var path = files.OmpEnv();
        if (!File.Exists(path)) return;
        var lines = File.ReadAllLines(path).Where(line => Assignment(line) != variable).ToList();
        WritePrivate(path, lines.Count == 0 ? "" : string.Join('\n', lines) + "\n");
    }

    private static string Quote(string value) =>
        value.Any(c => char.IsWhiteSpace(c) || c is '#' or '"' or '\'' or '\\' or '$' or '`') ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"" : value;

    // Written to a sibling file that only the owner can read, then moved over the real one.
    private static void WritePrivate(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        try
        {
            // The file exists, empty and readable by its owner only, before the secret is written into it.
            File.WriteAllBytes(temporary, []);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

[JsonSerializable(typeof(List<CustomProvider>))]
internal sealed partial class ProvidersHostJson : JsonSerializerContext;