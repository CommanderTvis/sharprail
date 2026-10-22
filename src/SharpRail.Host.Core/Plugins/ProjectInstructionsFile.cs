using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Tomlyn;

namespace SharpRail.Host.Core.Plugins;

/// <summary>
/// The project's instruction-section choices, <c>.sharprail/instructions.toml</c> under the project root
/// (<see cref="Version"/> 1): only a disable-list of section identifiers, so a project never stores instruction text and a
/// change to the enabled plugins can never leave it invalid. Unknown identifiers are kept and ignored; sections not listed
/// are on. Reading never throws: a broken file disables nothing and is reported.
/// </summary>
internal static class ProjectInstructionsFile
{
    internal const int Version = 1;
    internal const int MaximumIds = 512;

    /// <summary>The file as last read: the disabled identifiers in file order, unknown top-level keys, and problems.</summary>
    internal sealed record Snapshot(string Path, bool Exists, string Stamp, IReadOnlyList<string> Disabled, JsonObject Extra, IReadOnlyList<string> Diagnostics)
    {
        /// <summary>Whether the file can be rewritten without losing what the user wrote: it is absent or parsed cleanly.</summary>
        internal bool Writable => !Exists || Diagnostics.Count == 0;

        internal static Snapshot Empty(string path) => new(path, false, "", [], new JsonObject(), []);
    }

    internal static string PathOf(string root) => System.IO.Path.Combine(root, ".sharprail", "instructions.toml");

    internal static string StampOf(string path)
    {
        var info = new FileInfo(path);
        return info.Exists ? info.Length + "-" + info.LastWriteTimeUtc.Ticks : "";
    }

    /// <summary>A section identifier: <c>core.</c> or <c>plugin.</c>, then lower-case letters, digits, <c>.</c>, <c>-</c> and <c>_</c>.</summary>
    internal static bool ValidId(string id) =>
        id.Length is > 5 and <= 160 && (id.StartsWith("core.", StringComparison.Ordinal) || id.StartsWith("plugin.", StringComparison.Ordinal))
        && id.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-' or '_') && !id.EndsWith('.');

    internal static Snapshot Read(string root)
    {
        var path = PathOf(root);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return Snapshot.Empty(path);
            var stamp = StampOf(path);
            if (info.Length > ProjectToml.MaximumBytes) return Failed(path, stamp, $"{path} is larger than {ProjectToml.MaximumBytes / 1024} KiB; it is ignored.");
            return Parse(File.ReadAllText(path, Encoding.UTF8), path, stamp);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Failed(path, "unreadable", $"{path} could not be read: {error.Message}");
        }
    }

    private static Snapshot Failed(string path, string stamp, string message) => new(path, true, stamp, [], new JsonObject(), [message]);

    internal static Snapshot Parse(string text, string path, string stamp)
    {
        JsonObject document;
        try { document = ProjectToml.Parse(text); }
        catch (TomlException error) { return Failed(path, stamp, $"{path} is not valid TOML and is ignored: {error.Message}"); }
        if (ProjectToml.VersionOf(document) != Version) return Failed(path, stamp, $"{path} needs version = {Version} and is ignored.");
        var extra = new JsonObject();
        foreach (var (key, value) in document)
            if (key is not ("version" or "disabled")) extra[key] = value?.DeepClone();
        var diagnostics = new List<string>();
        var disabled = new List<string>();
        switch (document["disabled"])
        {
            case null: break;
            case JsonArray items:
                foreach (var item in items)
                {
                    if (item is JsonValue value && value.GetValueKind() == JsonValueKind.String && value.GetValue<string>() is var id && ValidId(id))
                    {
                        if (!disabled.Contains(id, StringComparer.Ordinal)) disabled.Add(id);
                    }
                    else diagnostics.Add($"disabled entry {item?.ToJsonString() ?? "null"} is not a section identifier such as \"core.visualize\"; it is ignored.");
                }
                break;
            default:
                return Failed(path, stamp, $"{path}: disabled must be an array of section identifiers; the file is ignored.");
        }
        if (disabled.Count > MaximumIds) return Failed(path, stamp, $"{path} lists more than {MaximumIds} sections; the file is ignored.");
        return new(path, true, stamp, disabled, extra, diagnostics);
    }

    /// <summary>The file's text for a disable-list: one identifier per line, sorted, after a comment saying what the file is; <paramref name="extra"/> keys follow.</summary>
    internal static string Render(IEnumerable<string> disabled, JsonObject? extra = null)
    {
        var text = new StringBuilder();
        text.Append("# Instruction sections SharpRail leaves out of agent sessions in this project.\n");
        text.Append("# Shared with the team: commit it. Change it in the agent's Context tab; unknown ids are kept.\n");
        text.Append("version = ").Append(Version).Append('\n');
        var ids = disabled.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        if (ids.Length == 0) text.Append("disabled = []\n");
        else
        {
            text.Append("disabled = [\n");
            foreach (var id in ids) text.Append("  ").Append(ProjectToml.Quote(id)).Append(",\n");
            text.Append("]\n");
        }
        if (extra is { Count: > 0 })
        {
            foreach (var (key, value) in extra)
                if (value is not (null or JsonObject)) text.Append(ProjectToml.KeyText(key)).Append(" = ").Append(ProjectToml.Inline(value)).Append('\n');
            foreach (var (key, value) in extra)
                if (value is JsonObject table) { text.Append('\n'); ProjectToml.Sections(text, [key], table); }
        }
        return text.ToString();
    }

    /// <summary>
    /// Turns <paramref name="id"/> on or off in the project's file and writes it atomically, keeping every other listed
    /// identifier, known or not. Turning on the last disabled section leaves a file with an empty list.
    /// </summary>
    /// <exception cref="ArgumentException">The identifier is malformed, or the file is broken and is not overwritten.</exception>
    internal static Snapshot Set(string root, string id, bool enabled)
    {
        if (!ValidId(id)) throw new ArgumentException($"\"{id}\" is not a section identifier.", nameof(id));
        var current = Read(root);
        if (!current.Writable) throw new ArgumentException($"Fix {current.Path} first: {string.Join(" ", current.Diagnostics)}");
        var next = current.Disabled.Where(item => item != id).ToList();
        if (!enabled) next.Add(id);
        if (current.Exists && next.Order(StringComparer.Ordinal).SequenceEqual(current.Disabled.Order(StringComparer.Ordinal))) return current;
        ProjectToml.WriteAtomic(current.Path, Render(next, current.Extra));
        return Read(root);
    }
}
