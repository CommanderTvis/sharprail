using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.UI.Kit;

using Path = Avalonia.Controls.Shapes.Path;

namespace SharpRail.Plugins.Agent.UI;

/// <summary>
/// SharpRail's instructions as one context entry: a row with the PROJECT chip, the size the host measured and the
/// appended file's path and a count of the sections, that opens a popup of the sections an agent's sessions get, grouped by source
/// (SharpRail, then each enabled plugin). Each section is a checkbox with its title and size, and a one-line summary that
/// discloses its text. A checkbox only turns a section on or off in the project's <c>.sharprail/instructions.toml</c>;
/// no text is editable here.
/// </summary>
public sealed partial class InstructionSectionsView : Border
{
    private const string Closed = "M 5,3 L 9,7 L 5,11";
    private const string Open = "M 3,5 L 7,9 L 11,5";

    private readonly Func<Task<InstructionSectionList>> load;
    private readonly Func<string, bool, Task<InstructionSectionList>> toggle;
    private readonly Action<string> failed;
    private readonly string agentName;
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private readonly StackPanel body;
    private int generation;
    private readonly Dictionary<string, RowParts> rowParts = new(StringComparer.Ordinal);
    // The text of each section as last listed, for a disclosure that opens after the listing arrived.
    private readonly Dictionary<string, string> latest = new(StringComparer.Ordinal);
    private string shownLayout = "";

    /// <summary>Lets the Avalonia runtime loader create the view; it shows no sections.</summary>
    public InstructionSectionsView() : this("Agent", static () => Task.FromResult(new InstructionSectionList()), static (_, _) => Task.FromResult(new InstructionSectionList()), static _ => { })
    {
    }

    /// <summary>Creates the entry, collapsed; it loads its sections at once, for the count, and again on <see cref="Refresh"/>.</summary>
    /// <param name="agentName">The agent's name as the text shows it, such as <c>Claude Code</c>.</param>
    /// <param name="load">Lists the sections. It runs off the UI thread.</param>
    /// <param name="toggle">Turns a section on or off and returns the new listing. It runs off the UI thread.</param>
    /// <param name="failed">Reports an error message to the user.</param>
    public InstructionSectionsView(string agentName, Func<Task<InstructionSectionList>> load, Func<string, bool, Task<InstructionSectionList>> toggle, Action<string> failed)
    {
        AvaloniaXamlLoader.Load(this);
        this.agentName = agentName;
        this.load = load;
        this.toggle = toggle;
        this.failed = failed;
        body = this.FindControl<StackPanel>("InstructionSectionsBody")!;
        Text("InstructionSectionsIntro").Text = $"Untick a section to leave it out of new {agentName} sessions in this project.";
        this.FindControl<ContentControl>("InstructionSectionsIcon")!.Content = Ui.Icon("fileText", Ui.Hint, 14);
        this.FindControl<ContentControl>("InstructionSectionsScope")!.Content = ScopedSetting.ScopeChip("project", "InstructionSectionsScopeChip");
        ToolTip.SetTip(this.FindControl<ContentControl>("InstructionSectionsScope")!, "The choices live in the project and apply to everyone who commits and pulls them.");
        // The sections are chosen in a popup over the panel (a flyout, so it works on every platform), never inline in the table.
        var row = this.FindControl<Button>("InstructionSectionsToggle")!;
        ((Panel)body.Parent!).Children.Remove(body);
        var flyout = new Flyout
        {
            Placement = PlacementMode.BottomEdgeAlignedLeft,
            Content = new Border
            {
                Background = Ui.Elevated,
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                MaxHeight = 520,
                Child = new ScrollViewer { Content = body }
            }
        };
        flyout.FlyoutPresenterClasses.Add("chip-card");
        flyout.Opened += (_, _) => { IsOpen = true; _ = Run(load); };
        flyout.Closed += (_, _) => IsOpen = false;
        row.Flyout = flyout;
        ToolTip.SetTip(row, "Choose which of SharpRail's sections new sessions get");
        // The collapsed row counts its sections, so it reads as a list to open; the listing is read off the UI thread.
        _ = Run(load);
    }

    /// <summary>Whether the sections popup is open.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The popup's content: the grouped section checkboxes and the notes about the project's file.</summary>
    public Control SectionsPanel => body;

    /// <summary>Fills the entry's size column and the line under its name, typically the appended file's path.</summary>
    public void SetEntry(Control? size, Control? source)
    {
        this.FindControl<ContentControl>("InstructionSectionsSize")!.Content = size;
        var line = this.FindControl<ContentControl>("InstructionSectionsSource")!;
        line.Content = source;
        line.IsVisible = source is not null;
    }

    /// <summary>Reads the sections again, for example after the project's file changed; callable from any thread.</summary>
    public void Refresh() => Dispatcher.UIThread.Post(() =>
    {
        _ = Run(load);
    });

    private TextBlock Text(string name) => this.FindControl<TextBlock>(name)!;

    private async Task Run(Func<Task<InstructionSectionList>> request)
    {
        var mine = ++generation;
        try
        {
            var list = await Task.Run(request);
            if (mine == generation) Show(list);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            failed(error.Message is { Length: > 0 } message ? message : "The request failed.");
            // A refused change leaves the checkboxes as they were on disk.
            if (mine == generation) _ = Run(load);
        }
    }

    private void Show(InstructionSectionList list)
    {
        if (!Dispatcher.UIThread.CheckAccess()) { Dispatcher.UIThread.Post(() => Show(list)); return; }
        this.FindControl<SelectableTextBlock>("InstructionSectionsPath")!.Text = list.Path.Length > 0
            ? "Turned-off sections are listed in " + list.Path + "; commit it to share the choice with your team."
            : "";
        var ignored = Text("InstructionSectionsIgnoredEdits");
        ignored.IsVisible = list.IgnoredEditsPath is not null;
        ignored.Text = $"Your earlier hand edits to the {agentName} instructions are no longer used. A copy is kept at {list.IgnoredEditsPath}; delete it to hide this note.";
        var diagnostics = Text("InstructionSectionsDiagnostics");
        diagnostics.IsVisible = list.Diagnostics.Count > 0;
        diagnostics.Text = string.Join("\n", list.Diagnostics);
        var unknown = Text("InstructionSectionsUnknown");
        unknown.IsVisible = list.UnknownDisabled.Count > 0;
        unknown.Text = "Also turned off, but not offered here (a plugin that is off or gone): " + string.Join(", ", list.UnknownDisabled);
        var count = Text("InstructionSectionsCount");
        var off = list.Sections.Count(section => !section.Enabled);
        count.Text = $"{list.Sections.Count} section{(list.Sections.Count == 1 ? "" : "s")}" + (off > 0 ? $" · {off} off" : "") + " · Choose…";
        var groups = this.FindControl<StackPanel>("InstructionSectionsGroups")!;
        var writable = list.Diagnostics.Count == 0;
        // Each section keeps its row, keyed by id, and a refresh updates it in place: an open disclosure, its text and a
        // reader mid-scroll stay put while a settings change only flips the checkbox or size that changed. Only a change
        // of the grouping (a section added or removed, a source heading) lays the list out again.
        var layout = string.Join('\n', list.Sections.GroupBy(section => section.Source).Select(group => group.First().SourceName + ":" + string.Join(',', group.Select(section => section.Id))));
        var live = list.Sections.Select(section => section.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in rowParts.Keys.Where(id => !live.Contains(id)).ToArray()) rowParts.Remove(gone);
        if (layout != shownLayout)
        {
            shownLayout = layout;
            groups.Children.Clear();
            foreach (var group in list.Sections.GroupBy(section => section.Source))
            {
                var header = Ui.Text(group.First().SourceName, Ui.Hint, 11);
                header.Margin = new Thickness(0, 4, 0, 0);
                groups.Children.Add(header);
                foreach (var section in group)
                {
                    if (!rowParts.TryGetValue(section.Id, out var parts)) rowParts[section.Id] = parts = Row(section);
                    groups.Children.Add(parts.Root);
                }
            }
            if (list.Sections.Count == 0) groups.Children.Add(Ui.Text("No sections: SharpRail adds no instructions here.", Ui.Muted, 12));
        }
        latest.Clear();
        foreach (var section in list.Sections) { latest[section.Id] = section.Text; rowParts[section.Id].Update(section, writable); }
    }

    // A section's row: its controls and what they last showed, so a refresh changes only what differs.
    private sealed class RowParts(StackPanel root, CheckBox check, TextBlock title, TextBlock size, Run description, SelectableTextBlock text, ToggleButton disclosure)
    {
        private string shown = "";
        private string shownText = "";
        public StackPanel Root => root;
        public bool Updating { get; private set; }
        public bool Enabled { get; private set; }

        public void Update(InstructionSection section, bool writable)
        {
            Updating = true;
            try
            {
                var key = section.Title + "\0" + section.Description + "\0" + section.Enabled;
                if (key != shown)
                {
                    shown = key;
                    title.Text = section.Title;
                    description.Text = section.Description.Length > 0 ? section.Description : "Show text";
                    text.Foreground = section.Enabled ? Ui.TextBrush : Ui.Muted;
                }
                if (section.Text != shownText)
                {
                    shownText = section.Text;
                    var tokens = Math.Max(1, (section.Text.Length + 3) / 4);
                    size.Text = "~" + tokens + " " + TokenAmount.Unit(tokens, false);
                    ToolTip.SetTip(size, section.Text.Length + " characters, about four to a token");
                    // An open section shows its text at once; a closed one fills it on opening.
                    text.Text = disclosure.IsChecked == true ? section.Text : null;
                }
                Enabled = section.Enabled;
                // Also after a refused change: the box goes back to what is on disk, and is usable again.
                if (check.IsChecked != section.Enabled) check.IsChecked = section.Enabled;
                check.IsEnabled = writable;
                ToolTip.SetTip(check, writable ? section.Id : "Fix the project's instructions.toml first.");
            }
            finally { Updating = false; }
        }
    }

    // A checkbox with the title and size, then the summary as a disclosure that opens the section's text below it.
    private RowParts Row(InstructionSection section)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var title = Ui.Text(section.Title, Ui.TextBrush, 13);
        label.Children.Add(title);
        var size = Ui.Text("", Ui.Hint, 11);
        size.VerticalAlignment = VerticalAlignment.Center;
        label.Children.Add(size);
        var check = new CheckBox { Name = "InstructionSectionCheckbox", Tag = section.Id, IsChecked = section.Enabled, Content = label };
        check.Classes.Add("section");
        var open = expanded.Contains(section.Id);
        var chevron = new Path { Width = 10, Height = 10, Stretch = Stretch.Uniform, Stroke = Ui.Muted, StrokeThickness = 1.5, Data = Geometry.Parse(open ? Open : Closed) };
        var summary = new TextBlock { FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
        var description = new Run();
        summary.Inlines!.Add(description);
        summary.Inlines.Add(new Run(" "));
        summary.Inlines.Add(new InlineUIContainer(chevron) { BaselineAlignment = BaselineAlignment.Center });
        var disclosure = new ToggleButton { Name = "InstructionSectionDisclosure", Tag = section.Id, Content = summary, IsChecked = open, Margin = new Thickness(28, 0, 0, 0) };
        disclosure.Classes.Add("disclosure");
        var text = new SelectableTextBlock
        {
            Name = "InstructionSectionText",
            Tag = section.Id,
            FontFamily = Ui.CodeFont,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 2, 0, 4),
            IsVisible = open,
        };
        var row = new StackPanel { Name = "InstructionSectionRow", Tag = section.Id };
        row.Children.Add(check);
        row.Children.Add(disclosure);
        row.Children.Add(text);
        var parts = new RowParts(row, check, title, size, description, text, disclosure);
        check.IsCheckedChanged += (_, _) =>
        {
            if (parts.Updating || check.IsChecked == parts.Enabled) return;
            check.IsEnabled = false;
            var enabled = check.IsChecked == true;
            _ = Run(() => toggle(section.Id, enabled));
        };
        disclosure.IsCheckedChanged += (_, _) =>
        {
            var nowOpen = disclosure.IsChecked == true;
            if (nowOpen) { expanded.Add(section.Id); text.Text ??= latest.GetValueOrDefault(section.Id); }
            else expanded.Remove(section.Id);
            text.IsVisible = nowOpen;
            chevron.Data = Geometry.Parse(nowOpen ? Open : Closed);
        };
        return parts;
    }
}