using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Agent.UI;

/// <summary>
/// The sections of SharpRail's instructions an agent's sessions in a workspace get, grouped by source (SharpRail, then each
/// enabled plugin), each with a checkbox, title, description, size estimate and a read-only preview. A checkbox only turns
/// a section on or off in the project's <c>.sharprail/instructions.toml</c>; no text is editable here.
/// </summary>
public sealed partial class InstructionSectionsView : Border
{
    private readonly Func<Task<InstructionSectionList>> load;
    private readonly Func<string, bool, Task<InstructionSectionList>> toggle;
    private readonly Action<string> failed;
    private readonly string agentName;
    private readonly HashSet<string> expanded = new(StringComparer.Ordinal);
    private int generation;

    /// <summary>Creates the view; it loads its sections when first attached and again on <see cref="Refresh"/>.</summary>
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
        Text("InstructionSectionsIntro").Text = $"Sections SharpRail adds to new {agentName} sessions in this project. Untick a section to leave it out.";
        AttachedToVisualTree += (_, _) => Refresh();
    }

    /// <summary>Reads the sections again, for example after the project's file changed; callable from any thread.</summary>
    public void Refresh() => Dispatcher.UIThread.Post(() => _ = Run(load));

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
            var header = Ui.Text(group.First().SourceName, Ui.Hint, 12);
            header.Margin = new Thickness(0, 8, 0, 0);
            groups.Children.Add(header);
            foreach (var section in group) groups.Children.Add(Row(section, writable));
        }
        if (list.Sections.Count == 0) groups.Children.Add(Ui.Text("No sections: SharpRail adds no instructions here.", Ui.Muted, 12));
    }

    private Control Row(InstructionSection section, bool writable)
    {
        var label = new StackPanel { Spacing = 1 };
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(Ui.Text(section.Title, Ui.TextBrush, 13));
        var size = Ui.Text("~" + Math.Max(1, (section.Text.Length + 3) / 4) + " tokens", Ui.Hint, 11);
        size.VerticalAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(size, section.Text.Length + " characters, about four to a token");
        title.Children.Add(size);
        label.Children.Add(title);
        if (section.Description.Length > 0)
        {
            var description = Ui.Text(section.Description, Ui.Muted, 12);
            description.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            label.Children.Add(description);
        }
        var id = Ui.Text(section.Id, Ui.Hint, 11);
        id.FontFamily = Ui.CodeFont;
        label.Children.Add(id);
        var check = new CheckBox { Name = "InstructionSectionCheckbox", Tag = section.Id, IsChecked = section.Enabled, Content = label, IsEnabled = writable };
        if (!writable) ToolTip.SetTip(check, "Fix the project's instructions.toml first.");
        check.IsCheckedChanged += (_, _) =>
        {
            if (check.IsChecked == section.Enabled) return;
            check.IsEnabled = false;
            var enabled = check.IsChecked == true;
            _ = Run(() => toggle(section.Id, enabled));
        };
        var preview = new Expander
        {
            Name = "InstructionSectionPreview",
            Tag = section.Id,
            Header = Ui.Text("Preview", Ui.Muted, 12),
            IsExpanded = expanded.Contains(section.Id),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Margin = new Thickness(28, 0, 0, 4),
        };
        void Fill()
        {
            if (preview.Content is not null) return;
            preview.Content = new SelectableTextBlock
            {
                Name = "InstructionSectionText",
                Text = section.Text,
                FontFamily = Ui.CodeFont,
                FontSize = 12,
                Foreground = section.Enabled ? Ui.TextBrush : Ui.Muted,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
        }
        if (preview.IsExpanded) Fill();
        preview.Expanded += (_, _) => { expanded.Add(section.Id); Fill(); };
        preview.Collapsed += (_, _) => expanded.Remove(section.Id);
        var row = new StackPanel { Name = "InstructionSectionRow", Tag = section.Id };
        row.Children.Add(check);
        row.Children.Add(preview);
        return row;
    }
}