using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;
using SharpRail.Plugins.UI.Kit.Markdown;

namespace SharpRail.Plugins.Blueprint.UI;

internal sealed partial class BlueprintPane : UserControl
{
    private readonly IPluginUIContext context;
    private readonly BlueprintStore store;
    private readonly string workspace;
    private readonly StackPanel document;
    private readonly Dictionary<string, (BlueprintBlock Block, Control Control)> blocks = [];
    private readonly Dictionary<string, MarkdownPreview> previews = [];
    private BlueprintProperties? frontmatter;
    private string? dismissed;
    private BlueprintState? state;

    public BlueprintPane(IPluginUIContext context, BlueprintStore store, string workspace)
    {
        AvaloniaXamlLoader.Load(this);
        this.context = context; this.store = store; this.workspace = workspace;
        Name = "Blueprint";
        document = this.FindControl<StackPanel>("Document")!;
        this.FindControl<Border>("Header")!.BorderBrush = Ui.BorderBrush;
        this.FindControl<Border>("Edits")!.Background = Ui.InfoWash;
        this.FindControl<Border>("Changes")!.Background = Ui.WarningWash;
        this.FindControl<Button>("Raw")!.Click += async (_, _) => await context.Editors.OpenAsync(workspace, BlueprintContract.File, new() { Raw = true });
        this.FindControl<Button>("Confirm")!.Click += (_, _) => Run(() => context.RequestAsync(BlueprintContract.ConfirmEdits, new(workspace)));
        this.FindControl<Button>("Discard")!.Click += (_, _) => Run(() => context.RequestAsync(BlueprintContract.DiscardEdits, new(workspace)));
        this.FindControl<Button>("Dismiss")!.Click += (_, _) => { dismissed = Signature(); RenderChanges(); };
        this.FindControl<Button>("OutlineToggle")!.Click += (_, _) =>
        {
            var outline = this.FindControl<ScrollViewer>("Outline")!;
            outline.IsVisible = !outline.IsVisible;
        };
        AttachedToVisualTree += (_, _) => { store.Changed += Changed; store.Follow(workspace); Render(); };
        DetachedFromVisualTree += (_, _) => store.Changed -= Changed;
        Render();
    }

    private async void Run(Func<ValueTask<BlueprintAck>> operation)
    {
        try { await operation(); }
        catch (Exception error) { context.Notify(PluginNotificationKind.Error, "Could not update Blueprint", error.Message); }
    }

    private void Edit(BlueprintEditTarget target, string text) => Run(() => context.RequestAsync(BlueprintContract.Edit, new(workspace, target, text)));
    private void Changed(string id) { if (id == workspace) Render(); }
    private string Signature() => JsonSerializer.Serialize(state?.Changes, PluginJson.Options);

    private MarkdownPreview Preview(string text, string blockId)
    {
        var settings = context.Host().AppSettings;
        var preview = new MarkdownPreview(text, BlueprintContract.File, new(
            async (path, ct) => await context.ReadFileAsync(workspace, path, ct), id => store.SpecPath(workspace, id),
            (path, anchor) => _ = context.Editors.OpenAsync(workspace, path), 14,
            settings.MarkdownLineWidth, settings.MarkdownLineWidthBounded))
        { Margin = new Avalonia.Thickness(-24, -16), VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        preview.SelectionChanged += selection =>
        {
            if (state?.Lines.GetValueOrDefault(blockId) is not { } span) return;
            var lines = selection.Split('\n');
            context.Editors.ReportSelection(new("blueprint:" + workspace, workspace, BlueprintContract.File, EditorKind.File, false),
                selection.Length == 0 ? null : new(span.StartLine, 1, span.EndLine, lines[^1].Length + 1, selection));
        };
        previews[blockId] = preview;
        return preview;
    }

    private void Render()
    {
        state = store.Get(workspace);
        var brief = this.FindControl<TextBlock>("Brief")!;
        brief.Text = state?.Brief ?? "Blueprint"; brief.Foreground = Ui.TextBrush;
        var awaiting = state?.Phase == BlueprintPhase.Awaiting;
        this.FindControl<TextBlock>("Phase")!.IsVisible = awaiting;
        this.FindControl<Button>("OutlineToggle")!.IsVisible = state is { Phase: BlueprintPhase.Ready };
        this.FindControl<Border>("Edits")!.IsVisible = state?.PendingEdits.Count > 0;
        var edits = state?.PendingEdits.Count ?? 0;
        this.FindControl<TextBlock>("EditCount")!.Text = $"{edits} {(edits == 1 ? "edit" : "edits")} not in the file yet.";
        RenderChanges();
        if (state is null || awaiting)
        {
            document.Children.Clear(); blocks.Clear(); previews.Clear(); frontmatter = null;
            var message = !store.Loaded(workspace) ? "Loading…" : state is null
                ? "This workspace has no specification open. The host keeps one only while it is running."
                : "The spec shows up here as soon as the agent writes it. All of it is yours to change.";
            document.Children.Add(Ui.Text(message, Ui.Muted));
            if (awaiting && state?.Author is BlueprintTerminalAuthor)
                document.Children.Add(Ui.Text("Claude Code asks to trust the folder on first run. It is the new, empty folder for this spec — answer Yes, I trust this folder in the terminal.", Ui.Muted));
            return;
        }
        var controls = new List<Control>();
        if (state.Doc.Frontmatter.Length > 0)
        {
            frontmatter ??= new BlueprintProperties(state.Doc.Frontmatter, next => Edit(new BlueprintFrontmatterTarget(), next));
            frontmatter.Update(state.Doc.Frontmatter);
            controls.Add(frontmatter);
        }
        foreach (var block in state.Doc.Blocks)
        {
            if (blocks.TryGetValue(block.Id, out var existing) && block is BlueprintProse prose && existing.Control is BlueprintEditable editable)
            { if (existing.Block != block) editable.Update(prose.Text); blocks[block.Id] = (block, editable); }
            else if (!blocks.TryGetValue(block.Id, out existing) || JsonSerializer.Serialize(existing.Block, PluginJson.Options) != JsonSerializer.Serialize(block, PluginJson.Options))
            {
                Control control = block switch
                {
                    BlueprintProse passage => new BlueprintEditable(passage.Text, true, "BlueprintProse", next => Edit(new BlueprintProseTarget(block.Id), next), text => Preview(text, block.Id), "Write a passage…"),
                    BlueprintControlBlock choice => new BlueprintControlView(choice.Control, ChangedControl(choice.Control.Id),
                        option => Run(() => context.RequestAsync(BlueprintContract.Select, new(workspace, choice.Control.Id, option))), Edit),
                    _ => throw new InvalidOperationException("Unknown Blueprint block.")
                };
                blocks[block.Id] = (block, control);
            }
            controls.Add(blocks[block.Id].Control);
            if (block is BlueprintControlBlock decision && blocks[block.Id].Control is BlueprintControlView view)
                view.SetChanged(ChangedControl(decision.Control.Id));
        }
        foreach (var id in blocks.Keys.Except(state.Doc.Blocks.Select(block => block.Id)).ToArray()) { blocks.Remove(id); previews.Remove(id); }
        foreach (var old in document.Children.Except(controls).ToArray()) document.Children.Remove(old);
        for (var index = 0; index < controls.Count; index++)
        {
            if (index < document.Children.Count && document.Children[index] == controls[index]) continue;
            document.Children.Remove(controls[index]); document.Children.Insert(index, controls[index]);
        }
        var headings = this.FindControl<StackPanel>("HeadingList")!;
        foreach (var preview in previews.Values) preview.RefreshSpecLinks();
        headings.Children.Clear();
        foreach (var block in state.Doc.Blocks.OfType<BlueprintProse>())
            if (previews.TryGetValue(block.Id, out var preview))
                foreach (var heading in preview.Headings)
                {
                    var button = Ui.Button(heading.Text, () => preview.ScrollToAnchor(heading.Id));
                    button.Margin = new Avalonia.Thickness((heading.Level - 1) * 8, 0, 0, 0);
                    headings.Children.Add(button);
                }
    }

    private bool ChangedControl(string id) => state?.Changes.Any(change => ControlId(change) == id) == true;
    private static string? ControlId(BlueprintChange change) => change switch
    {
        BlueprintControlAdded added => added.ControlId,
        BlueprintControlRemoved removed => removed.ControlId,
        BlueprintControlReselected selected => selected.ControlId,
        BlueprintControlOptionsChanged options => options.ControlId,
        _ => null
    };

    private void RenderChanges()
    {
        this.FindControl<Border>("Changes")!.IsVisible = state?.Changes.Count > 0 && dismissed != Signature();
        var count = state?.Changes.Count ?? 0;
        this.FindControl<TextBlock>("ChangeCount")!.Text = $"{count} {(count == 1 ? "thing" : "things")} moved in the agent's last rewrite.";
        var list = this.FindControl<WrapPanel>("ChangeList")!;
        list.Children.Clear();
        foreach (var change in state?.Changes ?? [])
        {
            var text = change switch
            {
                BlueprintControlAdded added => added.Title + " · new",
                BlueprintControlRemoved removed => removed.Title + " · dropped",
                BlueprintControlReselected selected => $"{selected.Title} · {selected.From} → {selected.To}",
                BlueprintControlOptionsChanged options => options.Title + " · new options",
                BlueprintProseChanged prose => $"{prose.Count} {(prose.Count == 1 ? "passage" : "passages")} rewritten",
                _ => ""
            };
            if (change is BlueprintProseChanged or BlueprintControlRemoved) list.Children.Add(Ui.Text(text, Ui.Muted, 12));
            else list.Children.Add(Ui.Button(text, () => { if (ControlId(change) is { } id && blocks.GetValueOrDefault(id).Control is { } control) control.BringIntoView(); }));
        }
    }
}