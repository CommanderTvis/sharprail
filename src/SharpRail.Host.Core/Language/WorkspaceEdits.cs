using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

/// <summary>An edit the host refuses whole; <see cref="State"/> is the result state the caller reports.</summary>
internal sealed class EditRefused(string state, string message) : Exception(message)
{
    internal string State { get; } = state;
}

/// <summary>The final state of one file after a workspace edit; <see cref="Original"/> is null when the file did not exist, <see cref="Text"/> null when it ends up deleted.</summary>
internal sealed class PlannedFile(string rel, string full, byte[]? original, bool bom, string? text)
{
    internal string Rel { get; } = rel;
    internal string Full { get; } = full;
    internal byte[]? Original { get; } = original;
    internal bool Bom { get; } = bom;
    internal string? Text { get; set; } = text;
    internal int Edits { get; set; }
    internal string? RenamedFrom { get; set; }
    internal string? RenamedTo { get; set; }
    internal List<LanguageItem> Preview { get; } = [];

    internal byte[] Bytes() => Text is null ? [] : [.. Bom ? Encoding.UTF8.GetPreamble() : [], .. Encoding.UTF8.GetBytes(Text)];
    internal bool Changed => !(Original is null && Text is null) && (Original is null || Text is null || !Original.AsSpan().SequenceEqual(Bytes()));
}

/// <summary>A workspace edit resolved against the files on disk, validated whole, ready to write. Nothing is written until <see cref="WorkspaceEdits.Commit"/>.</summary>
internal sealed class EditPlan(string title, IReadOnlyList<PlannedFile> files)
{
    internal string Title { get; } = title;
    internal IReadOnlyList<PlannedFile> Files { get; } = files;

    internal IReadOnlyList<LanguageChange> Changes()
    {
        var changes = new List<LanguageChange>();
        foreach (var file in Files.Where(file => file.Changed).OrderBy(file => file.Rel, StringComparer.Ordinal))
        {
            if (file.Original is not null && file.Text is null)
            {
                var target = file.RenamedTo is null ? null : Files.First(other => other.Rel == file.RenamedTo);
                changes.Add(target is null ? new(file.Rel, "deleted", 0) : new(file.Rel, "renamed", target.Edits, target.Rel, target.Preview));
            }
            else if (file.RenamedFrom is not null) continue;
            else changes.Add(new(file.Rel, file.Original is null ? "created" : "edited", file.Edits, "", file.Preview));
        }
        return changes;
    }
}

internal static class WorkspaceEdits
{
    internal const int MaxFiles = 500;
    internal const int MaxFileBytes = 1024 * 1024;
    internal const int MaxEditChars = 5 * 1024 * 1024;
    private const int MaxPreviewEdits = 20;

    /// <summary>What the session knows of an open document: the version it sent to the server and whether an editor holds unsaved changes.</summary>
    internal sealed record Open(int Version, bool Dirty);

    internal static EditPlan Build(string workspace, string title, IEnumerable<JsonNode> edits, Func<string, string?> relative, Func<string, Open?> open)
    {
        var files = new Dictionary<string, PlannedFile>(StringComparer.Ordinal);
        var chars = 0;

        PlannedFile File(string uri)
        {
            if (relative(uri) is not { } rel) throw new EditRefused("unsupported", "The edit touches a file outside the workspace or a virtual source.");
            string full;
            try { full = ProjectServices.Contain(workspace, rel, write: true); }
            catch (UnauthorizedAccessException error) { throw new EditRefused("unsupported", $"The edit touches {rel}: {error.Message}"); }
            if (files.TryGetValue(full, out var known)) return known;
            if (files.Count >= MaxFiles) throw new EditRefused("failed", $"The edit touches more than {MaxFiles} files.");
            if (open(uri) is { Dirty: true }) throw new EditRefused("conflict", $"{rel} has unsaved changes in an editor. Save or close it first.");
            byte[]? original = null; var bom = false; string? text = null;
            if (System.IO.File.Exists(full))
            {
                if (new FileInfo(full).Length > MaxFileBytes) throw new EditRefused("failed", $"{rel} is larger than 1 MiB.");
                original = System.IO.File.ReadAllBytes(full);
                bom = original.AsSpan().StartsWith(Encoding.UTF8.GetPreamble());
                try { text = new UTF8Encoding(false, true).GetString(original, bom ? 3 : 0, original.Length - (bom ? 3 : 0)); }
                catch (DecoderFallbackException) { throw new EditRefused("unsupported", $"{rel} is not valid UTF-8."); }
            }
            else if (Directory.Exists(full)) throw new EditRefused("unsupported", $"{rel} is a directory; the edit renames or deletes directories, which is not applied.");
            return files[full] = new(rel, full, original, bom, text);
        }

        foreach (var edit in edits)
        {
            if (edit["documentChanges"] is JsonArray changes)
            {
                foreach (var change in changes)
                {
                    if (change is null) continue;
                    switch (change["kind"]?.ToString())
                    {
                        case "create":
                            {
                                var file = File(change["uri"]?.ToString() ?? "");
                                if (file.Text is not null)
                                {
                                    if (change["options"]?["overwrite"]?.GetValue<bool>() != true && change["options"]?["ignoreIfExists"]?.GetValue<bool>() != true)
                                        throw new EditRefused("failed", $"The edit creates {file.Rel}, which exists.");
                                    if (change["options"]?["overwrite"]?.GetValue<bool>() == true) file.Text = "";
                                }
                                else file.Text = "";
                                break;
                            }
                        case "rename":
                            {
                                var from = File(change["oldUri"]?.ToString() ?? "");
                                var to = File(change["newUri"]?.ToString() ?? "");
                                if (from.Text is null) throw new EditRefused("failed", $"The edit renames {from.Rel}, which does not exist.");
                                if (to.Text is not null && change["options"]?["overwrite"]?.GetValue<bool>() != true)
                                {
                                    if (change["options"]?["ignoreIfExists"]?.GetValue<bool>() == true) break;
                                    throw new EditRefused("failed", $"The edit renames {from.Rel} onto {to.Rel}, which exists.");
                                }
                                to.Text = from.Text; to.RenamedFrom = from.Rel; from.Text = null; from.RenamedTo = to.Rel;
                                break;
                            }
                        case "delete":
                            {
                                var file = File(change["uri"]?.ToString() ?? "");
                                if (file.Text is null)
                                {
                                    if (change["options"]?["ignoreIfNotExists"]?.GetValue<bool>() == true) break;
                                    throw new EditRefused("failed", $"The edit deletes {file.Rel}, which does not exist.");
                                }
                                file.Text = null;
                                break;
                            }
                        case null:
                            {
                                var uri = change["textDocument"]?["uri"]?.ToString() ?? throw new EditRefused("unsupported", "The edit uses a form SharpRail does not apply.");
                                if (change["textDocument"]!["version"] is JsonValue version && open(uri) is { } current && version.GetValue<int>() != current.Version)
                                    throw new EditRefused("stale", "The edit belongs to an older version of the document.");
                                Apply(File(uri), change["edits"] as JsonArray ?? []);
                                break;
                            }
                        default: throw new EditRefused("unsupported", $"The edit has the unknown file operation \"{change["kind"]}\".");
                    }
                }
            }
            else if (edit["changes"] is JsonObject map)
                foreach (var (uri, list) in map) Apply(File(uri), list as JsonArray ?? []);
        }
        return new(title, [.. files.Values]);

        void Apply(PlannedFile file, JsonArray list)
        {
            if (file.Text is null) throw new EditRefused("failed", $"The edit changes {file.Rel}, which it deletes or does not create.");
            var text = file.Text;
            var starts = LineStarts(text);
            var crlf = Regex.Count(text, "\r\n") * 2 > Regex.Count(text, "\r\n|\r|\n");
            var parsed = new List<(int Start, int End, int Index, string New)>();
            foreach (var entry in list)
            {
                if (entry?["range"] is not { } range || entry["newText"] is not JsonValue replacement) throw new EditRefused("unsupported", "The edit uses a form SharpRail does not apply (snippet or unknown edit).");
                var value = replacement.ToString();
                chars += value.Length;
                if (chars > MaxEditChars) throw new EditRefused("failed", "The edit carries more than 5 MiB of text.");
                if (text.Length > 0) value = crlf ? Regex.Replace(value, "\r\n|\r|\n", "\r\n") : value.Replace("\r\n", "\n");
                parsed.Add((Offset(text, starts, range["start"]!), Offset(text, starts, range["end"]!), parsed.Count, value));
            }
            parsed.Sort((a, b) => a.Start != b.Start ? a.Start.CompareTo(b.Start) : a.Index.CompareTo(b.Index));
            for (var i = 0; i < parsed.Count; i++)
            {
                if (parsed[i].End < parsed[i].Start) throw new EditRefused("failed", $"An edit of {file.Rel} has its end before its start.");
                if (i > 0 && parsed[i].Start < parsed[i - 1].End) throw new EditRefused("failed", $"Edits of {file.Rel} overlap.");
                if (file.Preview.Count < MaxPreviewEdits)
                    file.Preview.Add(Describe(file.Rel, text, starts, parsed[i].Start, parsed[i].End, parsed[i].New));
            }
            var builder = new StringBuilder(text);
            for (var i = parsed.Count - 1; i >= 0; i--)
                builder.Remove(parsed[i].Start, parsed[i].End - parsed[i].Start).Insert(parsed[i].Start, parsed[i].New);
            file.Text = builder.ToString();
            file.Edits += parsed.Count;
        }
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n' || text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) starts.Add(i + 1);
        return starts;
    }

    private static int Offset(string text, List<int> starts, JsonNode position)
    {
        var line = position["line"]!.GetValue<int>();
        var character = position["character"]!.GetValue<int>();
        if (line < 0 || character < 0) throw new EditRefused("failed", "The edit has a negative position.");
        if (line >= starts.Count) return text.Length;
        var end = line + 1 < starts.Count ? starts[line + 1] : text.Length;
        while (end > starts[line] && text[end - 1] is '\n' or '\r') end--;
        return Math.Min(starts[line] + character, end);
    }

    private static LanguageItem Describe(string rel, string text, List<int> starts, int start, int end, string replacement)
    {
        (int Line, int Column) At(int offset)
        {
            var line = starts.BinarySearch(offset);
            if (line < 0) line = ~line - 1;
            return (line, offset - starts[line]);
        }
        var (startLine, startColumn) = At(start);
        var (endLine, endColumn) = At(end);
        return new(rel, startLine, startColumn, endLine, endColumn, Clip(replacement), Body: Clip(text[start..end]));
    }

    private static string Clip(string value) => value.Length <= 200 ? value : value[..200] + "… [truncated]";

    /// <summary>Writes the plan. Every file is checked against the bytes the plan was built from first; a failed write restores the files already written.</summary>
    internal static void Commit(EditPlan plan)
    {
        var changed = plan.Files.Where(file => file.Changed).ToArray();
        foreach (var file in changed)
        {
            var current = System.IO.File.Exists(file.Full) ? System.IO.File.ReadAllBytes(file.Full) : null;
            if (!(current is null && file.Original is null || current is not null && file.Original is not null && current.AsSpan().SequenceEqual(file.Original)))
                throw new EditRefused("stale", $"{file.Rel} changed on disk after the edit was prepared.");
        }
        var undo = new List<Action>();
        try
        {
            foreach (var file in changed.Where(file => file.Text is not null))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Full)!);
                var temporary = file.Full + ".sharprail-edit";
                System.IO.File.WriteAllBytes(temporary, file.Bytes());
                var original = file.Original; var full = file.Full;
                undo.Add(() => { if (original is null) System.IO.File.Delete(full); else System.IO.File.WriteAllBytes(full, original); });
                System.IO.File.Move(temporary, file.Full, true);
            }
            foreach (var file in changed.Where(file => file.Text is null))
            {
                var original = file.Original!; var full = file.Full;
                undo.Add(() => System.IO.File.WriteAllBytes(full, original));
                System.IO.File.Delete(file.Full);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            var failed = new List<string>();
            for (var i = undo.Count - 1; i >= 0; i--)
                try { undo[i](); }
                catch (Exception restore) when (restore is IOException or UnauthorizedAccessException) { failed.Add(restore.Message); }
            foreach (var file in changed) try { System.IO.File.Delete(file.Full + ".sharprail-edit"); } catch (Exception) { }
            throw new EditRefused("failed", $"Writing failed ({error.Message}); " + (failed.Count == 0 ? "the files written so far were restored." : "restoring failed for some files: " + string.Join("; ", failed)));
        }
    }
}