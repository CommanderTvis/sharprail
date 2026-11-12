using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Blueprint;

public sealed record BlueprintNote(string Control, string Message);
public sealed record BlueprintRead(BlueprintDoc Doc, IReadOnlyList<BlueprintNote> Notes);

/// <summary>Reads the fork's streaming !control format, renders it and derives its serialized line spans.</summary>
public static partial class BlueprintFormat
{
    private static readonly string[] AxisSeparators = [" — ", " – ", " -- ", " - ", ": "];
    public static string Slug(string text)
    {
        var slug = SlugCharacters().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? "unnamed" : slug;
    }
    public static string Humanize(string id)
    {
        var words = id.Replace('-', ' ').Trim();
        return words.Length == 0 ? "" : char.ToUpperInvariant(words[0]) + words[1..];
    }
    private static (string Label, string Axis) SplitOption(string body)
    {
        foreach (var separator in AxisSeparators)
        {
            var at = body.IndexOf(separator, StringComparison.Ordinal);
            if (at > 0) return (body[..at].Trim(), body[(at + separator.Length)..].Trim());
        }
        return (body.Trim(), "");
    }
    private static string UniqueId(string candidate, HashSet<string> taken)
    {
        if (taken.Add(candidate)) return candidate;
        for (var number = 2; ; number++)
            if (taken.Add(candidate + "-" + number)) return candidate + "-" + number;
    }
    private static bool DroppedPartial(string line, bool inControl)
    {
        if (inControl && (Option().IsMatch(line) || PartialCheckbox().IsMatch(line))) return true;
        var trimmed = line.TrimStart();
        return trimmed.StartsWith('!') || trimmed.Length > 0 && "!control".StartsWith(trimmed, StringComparison.Ordinal);
    }
    private sealed class OpenControl(BlueprintControl control, bool inferKind)
    {
        public BlueprintControl Control = control;
        public readonly HashSet<string> OptionIds = [];
        public readonly List<string> Selected = [];
        public bool InferKind = inferKind;
    }

    public static BlueprintDoc Parse(string text) => Read(text).Doc;
    public static BlueprintRead Read(string text)
    {
        var match = Frontmatter().Match(text);
        var frontmatter = match.Success ? match.Value : "";
        var body = text[frontmatter.Length..];
        var lines = body.Split('\n');
        var partialIndex = text.EndsWith('\n') ? -1 : lines.Length - 1;
        var blocks = new List<BlueprintBlock>();
        var takenIds = new HashSet<string>();
        var notes = new List<BlueprintNote>();
        var prose = new List<string>();
        OpenControl? open = null;
        void FlushProse()
        {
            var passage = LeadingBlank().Replace(string.Join('\n', prose), "", 1).TrimEnd();
            prose.Clear();
            if (passage.Length > 0) blocks.Add(new BlueprintProse("prose-" + blocks.Count, passage));
        }
        void FlushControl()
        {
            if (open is null) return;
            var control = open.Control;
            var selected = control.Kind == BlueprintControlKind.Select ? open.Selected.Take(1).ToArray()
                : open.Selected.Count > 0 ? open.Selected.ToArray() : control.SelectedIds;
            if (control.Kind == BlueprintControlKind.Select && selected.Count == 0 && control.Options.Count > 0)
                selected = [control.Options[0].Id];
            control = control with { SelectedIds = selected, Pending = control.Options.Count == 0 };
            if (control.Pending) notes.Add(new(control.Id, "no option lines — shown as an unfinished control, not a question"));
            blocks.Add(new BlueprintControlBlock(control.Id, control));
            open = null;
        }
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index];
            var syntax = line.TrimEnd('\r');
            if (index == partialIndex && DroppedPartial(syntax, open is not null)) continue;
            var marker = Marker().Match(syntax);
            if (marker.Success)
            {
                FlushControl();
                FlushProse();
                var named = marker.Groups[1].Value.ToLowerInvariant();
                var knownKind = named is "select" or "multi";
                var rawId = marker.Groups[knownKind ? 2 : 1].Value;
                var wanted = rawId.Length > 0 ? Slug(rawId) : "control-" + (blocks.Count + 1);
                var id = UniqueId(wanted, takenIds);
                if (!knownKind && marker.Groups[2].Length > 0)
                    notes.Add(new(id, $"\"{named}\" is not a kind, so it was read as the id and \"{marker.Groups[2].Value}\" was dropped — write \"!control select {Slug(marker.Groups[2].Value)}\" or \"!control multi {Slug(marker.Groups[2].Value)}\""));
                if (rawId.Length == 0)
                    notes.Add(new(id, $"no id on the marker, so it answers to \"{id}\" by position — a rewrite that adds a control above it moves the reader's choice to a different question"));
                if (id != wanted)
                    notes.Add(new(id, $"\"{wanted}\" is already taken, so this one answers to \"{id}\" — the reader's choice on the first is not the one they see here"));
                open = new(new(id, knownKind && named == "multi" ? BlueprintControlKind.Multi : BlueprintControlKind.Select,
                    Humanize(id), [], [], true, false), !knownKind);
                continue;
            }
            if (open is not null)
            {
                var option = Option().Match(syntax);
                if (option.Success)
                {
                    var (label, axis) = SplitOption(option.Groups[3].Value);
                    if (label.Length == 0) continue;
                    var checkbox = option.Groups[2].Success;
                    if (open.InferKind)
                    {
                        open.Control = open.Control with { Kind = checkbox ? BlueprintControlKind.Multi : BlueprintControlKind.Select };
                        open.InferKind = false;
                    }
                    var id = UniqueId(Slug(label), open.OptionIds);
                    open.Control = open.Control with { Options = [.. open.Control.Options, new BlueprintOption(id, label, axis)] };
                    if (axis.Length == 0)
                        notes.Add(new(open.Control.Id, $"\"{label}\" has no reason after it — the reader picks along a property, so write \"{label} — why you would pick it\""));
                    if (checkbox ? option.Groups[2].Value.Equals("x", StringComparison.OrdinalIgnoreCase) : option.Groups[1].Value == "=") open.Selected.Add(id);
                    continue;
                }
                FlushControl();
                if (line.Trim().Length == 0) continue;
            }
            prose.Add(line);
        }
        FlushControl();
        FlushProse();
        return new(new(blocks, frontmatter), notes);
    }

    private static string RenderBlock(BlueprintBlock block) => block switch
    {
        BlueprintProse prose => prose.Text,
        BlueprintControlBlock { Control: var control } => string.Join('\n',
            new[] { $"!control {control.Kind.ToString().ToLowerInvariant()} {control.Id}" }.Concat(control.Options.Select(option =>
            {
                var selected = control.SelectedIds.Contains(option.Id);
                var marker = control.Kind == BlueprintControlKind.Multi ? selected ? "[x]" : "[ ]" : selected ? "=" : "-";
                return marker + " " + option.Label + (option.Axis.Length > 0 ? " — " + option.Axis : "");
            }))),
        _ => throw new ArgumentOutOfRangeException(nameof(block))
    };
    public static string Serialize(BlueprintDoc doc) => doc.Frontmatter + string.Join("\n\n", doc.Blocks.Select(RenderBlock)) + "\n";
    public static IReadOnlyDictionary<string, BlueprintBlockLines> BlockLines(BlueprintDoc doc)
    {
        var spans = new Dictionary<string, BlueprintBlockLines>();
        var line = doc.Frontmatter.Length > 0 ? (doc.Frontmatter.EndsWith('\n') ? doc.Frontmatter[..^1] : doc.Frontmatter).Split('\n').Length + 1 : 1;
        foreach (var block in doc.Blocks)
        {
            var height = RenderBlock(block).Split('\n').Length;
            spans[block.Id] = new(line, line + height - 1);
            line += height + 1;
        }
        return spans;
    }
    public static IReadOnlyList<BlueprintControl> Controls(BlueprintDoc doc) => doc.Blocks.OfType<BlueprintControlBlock>().Select(block => block.Control).ToArray();
    public static string SelectedLabels(BlueprintControl control)
    {
        var labels = control.Options.Where(option => control.SelectedIds.Contains(option.Id)).Select(option => option.Label).ToArray();
        return labels.Length > 0 ? string.Join(", ", labels) : "nothing";
    }

    [GeneratedRegex(@"^\s*!control\b[ \t]*(\S*)[ \t]*(\S*)[ \t]*$")] private static partial Regex Marker();
    [GeneratedRegex(@"^\s*(?:([=-])|\[([ xX])\])[ \t]+(.*)$")] private static partial Regex Option();
    [GeneratedRegex(@"^\s*\[[ xX]?$")] private static partial Regex PartialCheckbox();
    [GeneratedRegex(@"[^a-z0-9]+")] private static partial Regex SlugCharacters();
    [GeneratedRegex(@"^---\r?\n[\s\S]*?\r?\n---\r?\n?")] private static partial Regex Frontmatter();
    [GeneratedRegex(@"^\s*\n")] private static partial Regex LeadingBlank();
}