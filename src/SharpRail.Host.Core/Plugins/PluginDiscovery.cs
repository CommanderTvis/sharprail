using System.Text.Json;

using SharpRail.Plugins.Api;

namespace SharpRail.Host.Core.Plugins;

/// <summary>One directory under a plugin root: a manifest, or the reason it was refused.</summary>
public sealed record DiscoveredPlugin(string Directory, PluginManifest? Manifest, string? Refused);

/// <summary>Manifest intake and the read-only scan of plugin roots. Nothing here watches or remembers.</summary>
public static class PluginDiscovery
{
    /// <summary>Reads a manifest strictly; an external one must be named after its directory.</summary>
    public static (PluginManifest? Manifest, string? Refused) ReadManifest(string json, string? directoryName)
    {
        PluginManifest? manifest;
        try { manifest = JsonSerializer.Deserialize<PluginManifest>(json, PluginJson.Options); }
        catch (JsonException error) { return (null, "invalid manifest: " + PluginErrors.Describe(error)); }
        if (manifest is null) return (null, "invalid manifest: $: expected an object");
        if (!PluginIdentity.IsPluginId(manifest.Id)) return (null, $"invalid manifest: $.id: \"{manifest.Id}\" is not a plugin id");
        if (directoryName is not null && manifest.Id != directoryName)
            return (null, $"plugin id \"{manifest.Id}\" does not match its directory \"{directoryName}\"");
        if (manifest.ApiGeneration != PluginApi.Generation)
            return (null, $"plugin {manifest.Id} declares apiGeneration {manifest.ApiGeneration}, host expects {PluginApi.Generation}");
        return (manifest, null);
    }

    /// <summary>
    /// Lists every directory under each root. A missing root is skipped; a symbolic link is refused rather
    /// than followed; an unreadable or invalid manifest is a refused entry rather than a silent skip.
    /// </summary>
    public static IReadOnlyList<DiscoveredPlugin> Discover(IEnumerable<string> roots)
    {
        var found = new List<DiscoveredPlugin>();
        foreach (var root in roots.Distinct())
        {
            FileSystemInfo[] entries;
            try { entries = new DirectoryInfo(root).GetFileSystemInfos(); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { continue; }
            foreach (var entry in entries.OrderBy(entry => entry.Name, StringComparer.Ordinal))
            {
                var directory = entry.FullName;
                if (entry.LinkTarget is not null)
                {
                    found.Add(new(directory, null, $"plugin directory must not be a symbolic link: {directory}"));
                    continue;
                }
                if (entry is not DirectoryInfo) continue;
                string json;
                try { json = File.ReadAllText(Path.Combine(directory, PluginManifest.FileName)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    found.Add(new(directory, null, $"cannot read {PluginManifest.FileName}: {error.Message}"));
                    continue;
                }
                var (manifest, refused) = ReadManifest(json, entry.Name);
                found.Add(new(directory, manifest, refused));
            }
        }
        return found;
    }
}

internal static class PluginErrors
{
    // System.Text.Json appends the position to its messages; the path leads instead.
    internal static string Describe(JsonException error)
    {
        var message = error.Message;
        var cut = message.IndexOf(" Path: ", StringComparison.Ordinal);
        if (cut >= 0) message = message[..cut];
        return $"{error.Path ?? "$"}: {message}";
    }
}