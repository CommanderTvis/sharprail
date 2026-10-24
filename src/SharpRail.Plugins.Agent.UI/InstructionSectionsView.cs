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
/// appended file's path, that expands to the sections an agent's sessions in a workspace get, grouped by source
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
    private bool loaded;
    private int generation;

    /// <summary>Creates the entry, collapsed; it loads its sections when first expanded and again on <see cref="Refresh"/>.</summary>
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
        Chevron();
        this.FindControl<Button>("InstructionSectionsToggle")!.Click += (_, _) => IsExpanded = !IsExpanded;
    }

    /// <summary>Whether the sections are shown; expanding loads them the first time.</summary>
    public bool IsExpanded
    {
        get => body.IsVisible;
        set
        {
            if (body.IsVisible == value) return;
            body.IsVisible = value;
            Chevron();
            if (value && !loaded) Refresh();
            ExpandedChanged?.Invoke(value);
        }
    }

    /// <summary>Raised when the user expands or collapses the entry, so a host can keep it open across re-renders.</summary>
    public event Action<bool>? ExpandedChanged;

    /// <summary>Fills the entry's size column and the line under its name, typically the appended file's path.</summary>
    public void SetEntry(Control? size, Control? source)
    {
        this.FindControl<ContentControl>("InstructionSectionsSize")!.Content = size;
        var line = this.FindControl<ContentControl>("InstructionSectionsSource")!;
        line.Content = source;
        line.IsVisible = source is not null;
    }

    /// <summary>Reads the sections again, for example after the project's file changed; callable from any thread. Does nothing until first expanded.</summary>
    public void Refresh() => Dispatcher.UIThread.Post(() =>
    {
        if (!loaded && !IsExpanded) return;
        loaded = true;
        _ = Run(load);
    });

    private void Chevron() => this.FindControl<Path>("InstructionSectionsChevron")!.Data = Geometry.Parse(IsExpanded ? Open : Closed);

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
        var groups = this.FindControl<StackPanel>("InstructionSectionsGroups")!;
        groups.Children.Clear();
        var writable = list.Diagnostics.Count == 0;
        foreach (var group in list.Sections.GroupBy(section => section.Source))
        {
            var header = Ui.Text(group.First().SourceName, Ui.Hint, 11);
            header.Margin = new Thickness(0, 4, 0, 0);
            groups.Children.Add(header);
            foreach (var section in group) groups.Children.Add(Row(section, writable));
        }
        if (list.Sections.Count == 0) groups.Children.Add(Ui.Text("No sections: SharpRail adds no instructions here.", Ui.Muted, 12));
    }

    // A checkbox with the title and size, then the summary as a disclosure that opens the section's text below it.
    private Control Row(InstructionSection section, bool writable)
    {
        var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        label.Children.Add(Ui.Text(section.Title, Ui.TextBrush, 13));
        var tokens = Math.Max(1, (section.Text.Length + 3) / 4);
        var size = Ui.Text("~" + tokens + " " + TokenAmount.Unit(tokens, false), Ui.Hint, 11);
        size.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(size, section.Text.Length + " characters, about four to a token");
        label.Children.Add(size);
        var check = new CheckBox { Name = "InstructionSectionCheckbox", Tag = section.Id, IsChecked = section.Enabled, Content = label, IsEnabled = writable };
        check.Classes.Add("section");
        ToolTip.SetTip(check, writable ? section.Id : "Fix the project's instructions.toml first.");
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked == section.Enabled) return;
            check.IsEnabled = false;
            var enabled = check.IsChecked == true;
            _ = Run(() => toggle(section.Id, enabled));
        };
        var open = expanded.Contains(section.Id);
        var chevron = new Path { Width = 10, Height = 10, Stretch = Stretch.Uniform, Stroke = Ui.Muted, StrokeThickness = 1.5, Data = Geometry.Parse(open ? Open : Closed) };
        var summary = new TextBlock { FontSize = 12, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
        summary.Inlines!.Add(new Run(section.Description.Length > 0 ? section.Description : "Show text"));
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
            Foreground = section.Enabled ? Ui.TextBrush : Ui.Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(28, 2, 0, 4),
            IsVisible = open,
        };
        // The text is filled on first opening, so a closed section costs no layout of its body.
        if (open) text.Text = section.Text;
        disclosure.IsCheckedChanged += (_, _) =>
        {
            var nowOpen = disclosure.IsChecked == true;
            if (nowOpen) { expanded.Add(section.Id); text.Text ??= section.Text; }
            else expanded.Remove(section.Id);
            text.IsVisible = nowOpen;
            chevron.Data = Geometry.Parse(nowOpen ? Open : Closed);
        };
        var row = new StackPanel { Name = "InstructionSectionRow", Tag = section.Id };
        row.Children.Add(check);
        row.Children.Add(disclosure);
        row.Children.Add(text);
        return row;
    }
}