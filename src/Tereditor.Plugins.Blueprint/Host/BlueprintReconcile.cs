namespace SharpRail.Plugins.Blueprint;

/// <summary>Applies reader edits and carries locked choices through an author's rewrite.</summary>
public static class BlueprintReconcile
{
    private static BlueprintDoc MapControl(BlueprintDoc doc, string id, Func<BlueprintControl, BlueprintControl> change) =>
        doc with
        {
            Blocks = doc.Blocks.Select(block => block is BlueprintControlBlock control && control.Control.Id == id
            ? new BlueprintControlBlock(block.Id, change(control.Control)) : block).ToArray()
        };

    public static BlueprintDoc ApplySelection(BlueprintDoc doc, string controlId, string optionId) => MapControl(doc, controlId, control =>
    {
        if (!control.Options.Any(option => option.Id == optionId)) return control;
        IReadOnlyList<string> selected = control.Kind == BlueprintControlKind.Select ? [optionId]
            : control.Options.Where(option => option.Id == optionId ? !control.SelectedIds.Contains(optionId) : control.SelectedIds.Contains(option.Id)).Select(option => option.Id).ToArray();
        return control with { SelectedIds = selected, Locked = true };
    });

    public static BlueprintDoc ApplyTextEdit(BlueprintDoc doc, BlueprintEditTarget target, string after)
    {
        if (target is BlueprintFrontmatterTarget) return doc with { Frontmatter = after };
        if (target is BlueprintProseTarget prose)
            return doc with
            {
                Blocks = doc.Blocks.Select(block => block is BlueprintProse passage && block.Id == prose.BlockId
                ? passage with { Text = after } : block).ToArray()
            };
        var optionTarget = (BlueprintOptionTarget)target;
        return MapControl(doc, optionTarget.ControlId, control =>
        {
            var taken = control.Options.Select(option => option.Id).ToHashSet();
            var options = control.Options.Select(option =>
            {
                if (option.Id != optionTarget.OptionId) return option;
                if (target is BlueprintOptionAxisTarget) return option with { Axis = after };
                taken.Remove(option.Id);
                var id = BlueprintFormat.Slug(after);
                for (var number = 2; taken.Contains(id); number++) id = BlueprintFormat.Slug(after) + "-" + number;
                return option with { Id = id, Label = after };
            }).ToArray();
            var renamed = options.Where((option, at) => option.Id != control.Options[at].Id).FirstOrDefault();
            var selected = renamed is not null && control.SelectedIds.Contains(optionTarget.OptionId)
                ? control.SelectedIds.Select(id => id == optionTarget.OptionId ? renamed.Id : id).ToArray() : control.SelectedIds;
            return control with { Options = options, SelectedIds = selected };
        });
    }

    public static string? TextAt(BlueprintDoc doc, BlueprintEditTarget target)
    {
        if (target is BlueprintFrontmatterTarget) return doc.Frontmatter;
        if (target is BlueprintProseTarget prose) return doc.Blocks.OfType<BlueprintProse>().FirstOrDefault(block => block.Id == prose.BlockId)?.Text;
        var optionTarget = (BlueprintOptionTarget)target;
        var option = BlueprintFormat.Controls(doc).FirstOrDefault(control => control.Id == optionTarget.ControlId)?.Options.FirstOrDefault(option => option.Id == optionTarget.OptionId);
        return target is BlueprintOptionAxisTarget ? option?.Axis : option?.Label;
    }

    public static BlueprintDoc CarryOverLocks(BlueprintDoc previous, BlueprintDoc next)
    {
        var before = BlueprintFormat.Controls(previous).ToDictionary(control => control.Id);
        return next with
        {
            Blocks = next.Blocks.Select(block =>
            {
                if (block is not BlueprintControlBlock control || !before.TryGetValue(control.Control.Id, out var prior) || !prior.Locked || prior.SelectedIds.Count == 0) return block;
                var kept = prior.Options.Where(option => prior.SelectedIds.Contains(option.Id)).ToArray();
                if (kept.Length == 0) return block;
                var missing = kept.Where(option => !control.Control.Options.Any(candidate => candidate.Id == option.Id));
                return new BlueprintControlBlock(block.Id, control.Control with
                {
                    Kind = prior.Kind,
                    Options = [.. missing, .. control.Control.Options],
                    SelectedIds = kept.Select(option => option.Id).ToArray(),
                    Locked = true
                });
            }).ToArray()
        };
    }

    public static IReadOnlyList<BlueprintChange> Diff(BlueprintDoc baseline, BlueprintDoc next)
    {
        var before = BlueprintFormat.Controls(baseline).ToDictionary(control => control.Id);
        var after = BlueprintFormat.Controls(next).ToDictionary(control => control.Id);
        var changes = new List<BlueprintChange>();
        static string Options(BlueprintControl control) => string.Concat(control.Options.Select(option => option.Id + " " + option.Axis));
        foreach (var (id, control) in after)
        {
            if (!before.TryGetValue(id, out var prior)) { changes.Add(new BlueprintControlAdded(id, control.Title)); continue; }
            if (string.Join(',', prior.SelectedIds) != string.Join(',', control.SelectedIds))
            {
                changes.Add(new BlueprintControlReselected(id, control.Title, BlueprintFormat.SelectedLabels(prior), BlueprintFormat.SelectedLabels(control)));
                continue;
            }
            if (Options(prior) != Options(control)) changes.Add(new BlueprintControlOptionsChanged(id, control.Title));
        }
        foreach (var (id, control) in before)
            if (!after.ContainsKey(id)) changes.Add(new BlueprintControlRemoved(id, control.Title));
        var priorProse = baseline.Blocks.OfType<BlueprintProse>().Select(prose => BlueprintFormat.Slug(prose.Text)).ToHashSet();
        var rewritten = next.Blocks.OfType<BlueprintProse>().Count(prose => !priorProse.Contains(BlueprintFormat.Slug(prose.Text)));
        if (rewritten > 0) changes.Add(new BlueprintProseChanged(rewritten));
        return changes;
    }

    public static string DescribeEdit(BlueprintDoc doc, BlueprintEdit edit)
    {
        if (edit.Target is BlueprintProseTarget) return "a passage now reads: " + edit.After;
        if (edit.Target is BlueprintFrontmatterTarget) return "the frontmatter now reads:\n" + edit.After;
        var target = (BlueprintOptionTarget)edit.Target;
        var control = BlueprintFormat.Controls(doc).FirstOrDefault(control => control.Id == target.ControlId);
        var what = target is BlueprintOptionAxisTarget ? "the reason for" : "the name of";
        return $"{what} an option under {control?.Title ?? target.ControlId} is now \"{edit.After}\" (was \"{edit.Before}\")";
    }
}