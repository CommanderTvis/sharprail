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

public sealed record NewWorkspaceChoice(bool InProjectFolder, string Base, string Path, string Branch, string Project, string? Name = null);

public sealed class NewWorkspaceDialog
{
    private readonly TextBlock description;
    private readonly TextBlock currentBranch;
    private readonly TextBox name;
    private readonly TextBlock hint;
    private readonly ToggleButton worktreeTarget;
    private readonly ToggleButton folderTarget;
    private readonly Button branchPicker;
    private readonly TextBlock branchLabel;
    private readonly TextBox search;
    private readonly StackPanel options;
    private readonly Button create;
    private readonly HashSet<string> collapsedRemotes;
    private readonly Action saveCollapsed;
    private BranchCatalog catalog;
    private string selected;
    private bool picked;
    private bool inFolder;
    private TextBlock? projectLabel;
    private readonly bool hasGit;

    public Window Window { get; }
    /// <summary>The project the workspace will be created in; picking another one loads its branches through <see cref="LoadProject"/>.</summary>
    public string Project { get; private set; }
    /// <summary>Returns a project's branch catalogue, or null when it cannot be used; the dialog then keeps its project.</summary>
    public Func<string, Task<BranchCatalog?>>? LoadProject { get; set; }

    public NewWorkspaceDialog(IReadOnlyList<string> projects, string project, BranchCatalog catalog, HashSet<string> collapsedRemotes, Action saveCollapsed, bool hasGit = true)
    {
        this.catalog = catalog;
        Project = project;
        this.hasGit = hasGit; inFolder = !hasGit;
        this.collapsedRemotes = collapsedRemotes; this.saveCollapsed = saveCollapsed;
        selected = catalog.DefaultBase;
        Window = Dialogs.Create("Start work", 560);
        Window.Tag = "NewWorkspaceDialog";
        description = Window.FindControl<TextBlock>("DialogExplanation")!;
        var fields = Window.FindControl<StackPanel>("DialogFields")!;

        // The target sits directly under the constant title; only one line of prose follows the mode.
        var targets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        worktreeTarget = Target("WsTargetWorktree", "New worktree", "gitBranch", false);
        worktreeTarget.IsEnabled = hasGit;
        worktreeTarget.IsVisible = hasGit;
        folderTarget = Target("WsTargetDefault", "Project folder", "homeFill", true);
        targets.Children.Add(worktreeTarget); targets.Children.Add(folderTarget);
        fields.Children.Add(targets);
        ((Panel)description.Parent!).Children.Remove(description);
        description.IsVisible = true;
        fields.Children.Add(description);

        fields.Children.Add(ProjectPicker(projects));

        branchLabel = Ui.Text("", Ui.TextBrush);
        var pickerContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        pickerContent.Children.Add(Ui.Icon("gitBranch", null, 14));
        pickerContent.Children.Add(Ui.Text("From"));
        pickerContent.Children.Add(branchLabel);
        pickerContent.Children.Add(Ui.Icon("arrowDown", null, 12));
        branchPicker = new Button
        {
            Name = "WsBranchPicker",
            Content = pickerContent,
            Padding = new Thickness(12, 4),
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new(4)
        };
        AutomationProperties.SetName(branchPicker, "Base branch");
        options = new StackPanel { Name = "BranchOptions", Spacing = 2 };
        search = new TextBox { Name = "BranchSearch", PlaceholderText = "Search branches…", Width = 300 };
        search.TextChanged += (_, _) => RenderOptions();
        search.AddHandler(InputElement.KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { branchPicker.Flyout?.Hide(); branchPicker.Focus(); e.Handled = true; return; }
            if (e.Key != Key.Enter) return;
            if (options.Children.OfType<Button>().FirstOrDefault(button => button.Name == "BranchOption") is { } first) Pick((string)first.Tag!);
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
        var list = new StackPanel { Spacing = 8, Margin = new Thickness(4) };
        list.Children.Add(search);
        list.Children.Add(new ScrollViewer { Content = options, MaxHeight = 260, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled });
        var flyout = new Flyout { Content = list, Placement = PlacementMode.BottomEdgeAlignedLeft };
        branchPicker.Flyout = flyout;
        flyout.Opened += (_, _) => { search.Text = ""; RenderOptions(); search.Focus(); };
        fields.Children.Add(branchPicker);
        currentBranch = Ui.Text("", Ui.TextBrush);
        currentBranch.Name = "WsCurrentBranch";
        fields.Children.Add(currentBranch);

        // Prefilled with the host's next free name so it is visible before creation; only an edit is sent.
        name = new TextBox { Name = "WsName", Text = SuggestedName, Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        AutomationProperties.SetName(name, "Name");
        fields.Children.Add(name);

        hint = Ui.Text("Press Enter to create.", Ui.Hint, 12);
        hint.Name = "WsEnterHint";
        fields.Children.Add(hint);

        var actions = Window.FindControl<StackPanel>("DialogActions")!;
        actions.Children.Add(Ui.Button("Cancel", () => Window.Close(null)));
        create = Ui.Button("Create", () =>
        {
            var typed = name.Text?.Trim() ?? "";
            var edited = typed.Length > 0 && typed != SuggestedName ? typed : null;
            Window.Close(new NewWorkspaceChoice(inFolder, selected, this.catalog.SuggestedPath,
                edited is null ? this.catalog.SuggestedBranch : BranchForName(edited), Project, edited));
        });
        create.Name = "WsCreate"; create.IsDefault = true; Dialogs.Primary(create);
        actions.Children.Add(create);
        Window.Opened += (_, _) => create.Focus();
        Render();
    }

    private string SuggestedName => Path.GetFileName(catalog.SuggestedPath);

    private string BranchForName(string displayName)
    {
        var slug = System.Text.RegularExpressions.Regex.Replace(displayName.ToLowerInvariant(), "[^a-z0-9]+", "-").TrimStart('-');
        slug = slug[..Math.Min(slug.Length, 60)].TrimEnd('-');
        if (slug.Length == 0) slug = "workspace";
        var branch = slug;
        for (var suffix = 2; catalog.Local.Any(existing => existing == branch || existing.StartsWith(branch + "/", StringComparison.Ordinal)); suffix++)
            branch = slug + "-" + suffix;
        return branch;
    }

    public Task<NewWorkspaceChoice?> ShowAsync(Window owner) => Window.ShowDialog<NewWorkspaceChoice?>(owner);

    /// <summary>One open project is a plain row; several make it a picker, so a workspace can start in any of them.</summary>
    private Control ProjectPicker(IReadOnlyList<string> projects)
    {
        var row = Ui.Row("folderFill", new DirectoryInfo(Project).Name, Ui.TextBrush);
        row.Name = "WsProjectPicker";
        projectLabel = row.Children.OfType<TextBlock>().Single();
        if (projects.Count < 2) return row;
        row.Children.Add(Ui.Icon("arrowDown", null, 12));
        var picker = new Button
        {
            Name = "WsProjectTrigger",
            Content = row,
            Padding = new Thickness(8, 4),
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new(4),
            HorizontalAlignment = HorizontalAlignment.Left,
            ContextMenu = new ContextMenu { Placement = PlacementMode.BottomEdgeAlignedLeft }
        };
        AutomationProperties.SetName(picker, "Project");
        foreach (var path in projects)
        {
            var item = Ui.Menu(new DirectoryInfo(path).Name, () => _ = PickProjectAsync(path));
            item.Name = "WsProjectOption"; item.Tag = path; item.ToggleType = MenuItemToggleType.Radio; item.IsChecked = path == Project;
            ToolTip.SetTip(item, path);
            picker.ContextMenu.Items.Add(item);
        }
        picker.Click += (_, _) =>
        {
            foreach (var item in picker.ContextMenu.Items.OfType<MenuItem>()) item.IsChecked = Equals(item.Tag, Project);
            picker.ContextMenu.Open(picker);
        };
        return picker;
    }

    private async Task PickProjectAsync(string path)
    {
        if (path == Project || LoadProject is null) return;
        create.IsEnabled = false;
        try
        {
            if (await LoadProject(path) is not { } loaded) return;
            Project = path; catalog = loaded; picked = false; selected = loaded.DefaultBase;
            projectLabel!.Text = new DirectoryInfo(path).Name;
            Render();
        }
        finally { create.IsEnabled = true; }
    }

    /// <summary>Replaces the list with a fresher catalogue of <paramref name="project"/>, unless the dialog has moved to another one.</summary>
    public void Update(BranchCatalog fresh, string project)
    {
        if (project != Project) return;
        if (fresh.DefaultBase == catalog.DefaultBase && fresh.Local.SequenceEqual(catalog.Local) && fresh.Remote.SequenceEqual(catalog.Remote) &&
            fresh.SuggestedPath == catalog.SuggestedPath && fresh.Current == catalog.Current) return;
        var untouched = name.Text == SuggestedName;
        catalog = fresh;
        if (untouched) name.Text = SuggestedName;
        if (!picked) selected = fresh.DefaultBase;
        Render();
    }

    private ToggleButton Target(string name, string label, string icon, bool folder)
    {
        var target = new ToggleButton
        {
            Name = name,
            Content = Ui.Row(icon, label),
            Padding = new Thickness(12, 4),
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
        description.Text = inFolder
            ? hasGit ? "No isolation: work lands in your project folder's current checkout."
                : "Your project folder itself. It is not a git repository, so there is nothing to isolate."
            : "A separate git worktree on its own new branch.";
        branchPicker.IsVisible = name.IsVisible = hint.IsVisible = !inFolder;
        currentBranch.IsVisible = inFolder && catalog.Current.Length > 0;
        currentBranch.Text = "On " + catalog.Current;
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
                var collapsed = collapsedRemotes.Contains(group.Key);
                options.Children.Add(RemoteToggle(group.Key, collapsed));
                if (!collapsed) foreach (var branch in group) options.Children.Add(Option(branch.Ref, branch.Name));
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

    // A remote's rows can be collapsed; the choice is remembered per remote name across pickers.
    private Button RemoteToggle(string remote, bool collapsed)
    {
        var toggle = new Button
        {
            Name = "RemoteGroupToggle",
            Tag = remote,
            Content = Ui.Row(collapsed ? "arrowRight" : "arrowDown", remote, Ui.Hint),
            Background = Avalonia.Media.Brushes.Transparent,
            BorderThickness = new(0),
            Padding = new Thickness(8, 4, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left
        };
        AutomationProperties.SetName(toggle, (collapsed ? "Expand " : "Collapse ") + remote);
        toggle.Click += (_, _) =>
        {
            if (!collapsedRemotes.Add(remote)) collapsedRemotes.Remove(remote);
            saveCollapsed();
            RenderOptions();
            options.Children.OfType<Button>().FirstOrDefault(button => button.Name == "RemoteGroupToggle" && Equals(button.Tag, remote))?.Focus();
        };
        return toggle;
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