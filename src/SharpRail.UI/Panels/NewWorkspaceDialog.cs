using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Panels;

public sealed record NewWorkspaceChoice(bool InProjectFolder, string Base, string Path, string Branch);

public sealed class NewWorkspaceDialog
{
    private readonly TextBlock heading;
    private readonly TextBlock description;
    private readonly ToggleButton worktreeTarget;
    private readonly ToggleButton folderTarget;
    private readonly Button branchPicker;
    private readonly TextBlock branchLabel;
    private readonly TextBox search;
    private readonly StackPanel options;
    private readonly Button create;
    private BranchCatalog catalog;
    private string selected;
    private bool picked;
    private bool inFolder;

    public Window Window { get; }

    public NewWorkspaceDialog(string projectName, BranchCatalog catalog)
    {
        this.catalog = catalog;
        selected = catalog.DefaultBase;
        Window = Dialogs.Create("Create workspace", 560);
        Window.Tag = "NewWorkspaceDialog";
        heading = Window.FindControl<TextBlock>("DialogHeading")!;
        description = Window.FindControl<TextBlock>("DialogExplanation")!;
        description.IsVisible = true;
        var fields = Window.FindControl<StackPanel>("DialogFields")!;

        var targets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        worktreeTarget = Target("WsTargetWorktree", "New worktree", "gitBranch", false);
        folderTarget = Target("WsTargetDefault", "Project folder", "homeFill", true);
        targets.Children.Add(worktreeTarget); targets.Children.Add(folderTarget);
        fields.Children.Add(targets);

        var project = Ui.Row("folderFill", projectName, Ui.TextBrush);
        project.Name = "WsProjectPicker";
        fields.Children.Add(project);

        branchLabel = Ui.Text("", Ui.TextBrush);
        var pickerContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        pickerContent.Children.Add(Ui.Icon("gitBranch", null, 14));
        pickerContent.Children.Add(Ui.Text("From"));
        pickerContent.Children.Add(branchLabel);
        pickerContent.Children.Add(Ui.Icon("arrowDown", null, 12));
        branchPicker = new Button
        {
            Name = "WsBranchPicker",
            Content = pickerContent,
            Padding = new Thickness(10, 5),
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new(4)
        };
        AutomationProperties.SetName(branchPicker, "Base branch");
        options = new StackPanel { Name = "BranchOptions", Spacing = 1 };
        search = new TextBox { Name = "BranchSearch", PlaceholderText = "Search branches…", Width = 300 };
        search.TextChanged += (_, _) => RenderOptions();
        search.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { branchPicker.Flyout?.Hide(); branchPicker.Focus(); e.Handled = true; return; }
            if (e.Key != Key.Enter) return;
            if (options.Children.OfType<Button>().FirstOrDefault() is { } first) Pick((string)first.Tag!);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(4) };
        list.Children.Add(search);
        list.Children.Add(new ScrollViewer { Content = options, MaxHeight = 260, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var flyout = new Flyout { Content = list, Placement = PlacementMode.BottomEdgeAlignedLeft };
        branchPicker.Flyout = flyout;
        flyout.Opened += (_, _) => { search.Text = ""; RenderOptions(); search.Focus(); };
        fields.Children.Add(branchPicker);

        var hint = Ui.Text("Press Enter to create.", Ui.Hint, 12);
        hint.Name = "WsEnterHint";
        fields.Children.Add(hint);

        var actions = Window.FindControl<StackPanel>("DialogActions")!;
        actions.Children.Add(Ui.Button("Cancel", () => Window.Close(null)));
        create = Ui.Button("Create", () => Window.Close(new NewWorkspaceChoice(inFolder, selected, this.catalog.SuggestedPath, this.catalog.SuggestedBranch)));
        create.Name = "WsCreate"; create.IsDefault = true; Dialogs.Primary(create);
        actions.Children.Add(create);
        Window.Opened += (_, _) => create.Focus();
        Render();
    }

    public Task<NewWorkspaceChoice?> ShowAsync(Window owner) => Window.ShowDialog<NewWorkspaceChoice?>(owner);

    public void Update(BranchCatalog fresh)
    {
        if (fresh.DefaultBase == catalog.DefaultBase && fresh.Local.SequenceEqual(catalog.Local) && fresh.Remote.SequenceEqual(catalog.Remote) &&
            fresh.SuggestedPath == catalog.SuggestedPath) return;
        catalog = fresh;
        if (!picked) selected = fresh.DefaultBase;
        Render();
    }

    private ToggleButton Target(string name, string label, string icon, bool folder)
    {
        var target = new ToggleButton
        {
            Name = name,
            Content = Ui.Row(icon, label),
            Padding = new Thickness(10, 5),
            CornerRadius = new(4)
        };
        target.Click += (_, _) => { inFolder = folder; Render(); };
        return target;
    }

    private void Pick(string reference)
    {
        selected = reference; picked = true;
        branchPicker.Flyout?.Hide();
        Render();
    }

    private void Render()
    {
        worktreeTarget.IsChecked = !inFolder;
        folderTarget.IsChecked = inFolder;
        heading.Text = inFolder ? "Work in project folder" : "Create workspace";
        description.Text = inFolder
            ? "Work directly in your project folder, with no isolation: changes land in your current checkout."
            : "A separate checkout on its own new branch. Files, changes, and terminals stay scoped to it.";
        branchPicker.IsVisible = !inFolder;
        branchLabel.Text = selected;
        ((TextBlock)create.Content!).Text = inFolder ? "Start" : "Create";
        RenderOptions();
    }

    private void RenderOptions()
    {
        options.Children.Clear();
        var filter = search.Text?.Trim() ?? "";
        bool Matches(string reference) => reference.Contains(filter, StringComparison.OrdinalIgnoreCase);
        var local = catalog.Local.Where(Matches).ToArray();
        if (local.Length > 0)
        {
            options.Children.Add(Heading("Local"));
            foreach (var branch in local) options.Children.Add(Option(branch, branch));
        }
        var remote = catalog.Remote.Where(branch => Matches(branch.Ref)).ToArray();
        if (remote.Length > 0)
        {
            options.Children.Add(Heading("Remote"));
            foreach (var group in remote.GroupBy(branch => branch.Remote))
            {
                var name = Heading(group.Key); name.Margin = new Thickness(8, 4, 0, 0);
                options.Children.Add(name);
                foreach (var branch in group) options.Children.Add(Option(branch.Ref, branch.Name));
            }
        }
        if (options.Children.Count == 0)
        {
            var empty = Ui.Text("No branches found.", Ui.Hint, 12);
            empty.Name = "BranchEmpty";
            options.Children.Add(empty);
        }
    }

    private static TextBlock Heading(string text)
    {
        var heading = Ui.Text(text, Ui.Hint, 11);
        heading.Name = "BranchGroup"; heading.Margin = new Thickness(0, 4, 0, 0);
        return heading;
    }

    private Button Option(string reference, string label)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        Ui.Place(row, Ui.Text(label, reference == selected ? Ui.Accent : Ui.TextBrush));
        if (reference == catalog.DefaultBase)
        {
            var badge = Ui.Text("default", Ui.Hint, 11);
            badge.Name = "BranchDefault"; badge.Margin = new Thickness(8, 0, 0, 0);
            Ui.Place(row, badge, 0, 1);
        }
        var option = new Button
        {
            Name = "BranchOption",
            Tag = reference,
            Content = row,
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new(0),
            Padding = new Thickness(8, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(option, reference);
        option.Click += (_, _) => Pick(reference);
        return option;
    }
}