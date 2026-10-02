using Avalonia.Controls;


namespace SharpRail.UI.Panels;

public static class Dialogs
{
    public static async Task<string?> CreateProject(Window owner, string initialParent, Func<Task<string?>>? pickFolder,
        Func<string, string, Task<string>> createProject)
    {
        var window = Create("Create project", 520);
        var explanation = window.FindControl<TextBlock>("DialogExplanation")!;
        explanation.Text = pickFolder is null
            ? "Create a new project folder on the computer running SharpRail, then open its Project Home."
            : "Create a new project folder, then open its Project Home.";
        explanation.IsVisible = true;
        var fields = new NewProjectFields();
        window.FindControl<StackPanel>("DialogFields")!.Children.Add(fields);
        var parent = fields.FindControl<TextBox>("CreateProjectParent")!;
        var name = fields.FindControl<TextBox>("CreateProjectName")!;
        var browse = fields.FindControl<Button>("CreateProjectBrowse")!;
        var error = fields.FindControl<TextBlock>("CreateProjectError")!;
        error.Foreground = Ui.Danger;
        parent.Text = initialParent;
        browse.IsVisible = pickFolder is not null;
        browse.Click += async (_, _) =>
        {
            try { if (pickFolder is not null && await pickFolder() is { } picked) parent.Text = picked; }
            catch (Exception failure) when (failure is not OperationCanceledException) { error.Text = failure.Message; error.IsVisible = true; }
        };
        var buttons = window.FindControl<StackPanel>("DialogActions")!;
        buttons.Children.Add(Ui.Button("Cancel", () => window.Close(null)));
        var create = Primary(Ui.Button("Create project", () => { }));
        create.Name = "CreateProjectAccept"; create.IsDefault = true;
        buttons.Children.Add(create);
        var busy = false;
        void Refresh()
        {
            parent.IsEnabled = name.IsEnabled = browse.IsEnabled = !busy;
            create.IsEnabled = !busy && !string.IsNullOrWhiteSpace(parent.Text) && !string.IsNullOrWhiteSpace(name.Text);
            ((TextBlock)create.Content!).Text = busy ? "Creating…" : "Create project";
        }
        parent.TextChanged += (_, _) => Refresh();
        name.TextChanged += (_, _) => Refresh();
        create.Click += async (_, _) =>
        {
            if (!create.IsEnabled || busy) return;
            var into = parent.Text!.Trim();
            var folder = name.Text!.Trim();
            busy = true; error.IsVisible = false; Refresh();
            try { window.Close(await createProject(into, folder)); }
            catch (OperationCanceledException) { window.Close(null); }
            catch (Exception failure)
            {
                error.Text = failure is Grpc.Core.RpcException rpc ? rpc.Status.Detail : failure.Message;
                error.IsVisible = true;
                busy = false; Refresh();
            }
        };
        Refresh();
        window.Opened += (_, _) => (parent.Text?.Length > 0 ? name : parent).Focus();
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task<string[]?> Prompt(Window owner, string title, params (string Label, string Value)[] fields)
    {
        var window = Create(title, 520);
        var panel = window.FindControl<StackPanel>("DialogFields")!;
        var inputs = new List<TextBox>();
        foreach (var field in fields)
        {
            panel.Children.Add(Ui.Text(field.Label, size: 12));
            var input = new TextBox { Text = field.Value, PlaceholderText = field.Label };
            inputs.Add(input); panel.Children.Add(input);
        }
        var buttons = window.FindControl<StackPanel>("DialogActions")!;
        buttons.Children.Add(Ui.Button("Cancel", () => window.Close(null)));
        var accept = Ui.Button("Continue", () => window.Close(inputs.Select(input => input.Text?.Trim() ?? "").ToArray()));
        accept.IsDefault = true; buttons.Children.Add(Primary(accept));
        window.Opened += (_, _) => inputs.First().Focus();
        return await window.ShowDialog<string[]?>(owner);
    }

    public static async Task<string?> HostPath(Window owner, string initial, string? pickerError, bool remote, bool directory = true)
    {
        var window = Create(directory ? "Open project by path" : "Choose a document by path", 520);
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        // On the desktop the host is this computer, so the copy does not call it the host.
        var kind = directory ? "folder" : "file";
        text.Text = remote ? $"Enter the absolute path of a {kind} on the computer running SharpRail." : $"Enter the absolute path of a {kind}.";
        text.IsVisible = true;
        var panel = window.FindControl<StackPanel>("DialogFields")!;
        if (pickerError is not null)
        {
            var failure = Ui.Text("The folder picker failed: " + pickerError, Ui.Danger, 12);
            failure.Name = "OpenProjectPickerError"; failure.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            panel.Children.Add(failure);
        }
        var input = new TextBox { Name = "OpenProjectPathInput", Text = initial, PlaceholderText = directory ? "Directory path" : "File path" };
        panel.Children.Add(input);
        var buttons = window.FindControl<StackPanel>("DialogActions")!;
        buttons.Children.Add(Ui.Button("Cancel", () => window.Close(null)));
        var accept = Ui.Button(directory ? "Open project" : "Choose document", () => window.Close(input.Text?.Trim() is { Length: > 0 } path ? path : null));
        accept.Name = "OpenProjectPathSubmit"; accept.IsDefault = true; buttons.Children.Add(Primary(accept));
        window.Opened += (_, _) => input.Focus();
        return await window.ShowDialog<string?>(owner);
    }

    /// <summary>
    /// Takes a file or folder name. A name listed in the destination shows its collision as it is typed and
    /// cannot be confirmed; the dialog stays open while <paramref name="submit"/> runs and shows its failure.
    /// </summary>
    public static async Task PathName(Window owner, string title, string initial, string confirmLabel,
        Func<string, bool> exists, Func<string, Task> submit)
    {
        var window = Create(title, 520);
        var panel = window.FindControl<StackPanel>("DialogFields")!;
        var input = new TextBox { Name = "PathNameInput", Text = initial, PlaceholderText = "Name" };
        var error = Ui.Text("", Ui.Danger, 12);
        error.Name = "PathNameError"; error.IsVisible = false; error.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        panel.Children.Add(input); panel.Children.Add(error);
        var buttons = window.FindControl<StackPanel>("DialogActions")!;
        buttons.Children.Add(Ui.Button("Cancel", () => window.Close()));
        var busy = false;
        var confirm = Primary(Ui.Button(confirmLabel, () => { }));
        confirm.Name = "PathNameConfirm"; confirm.IsDefault = true;
        confirm.Click += (_, _) => _ = Submit();
        buttons.Children.Add(confirm);
        void Validate()
        {
            var name = input.Text ?? "";
            var problem = name.Trim().Length == 0 || name == initial ? "" : PathNameProblem(name) ?? (exists(name) ? $"{name} already exists" : null);
            error.Text = problem ?? ""; error.IsVisible = !string.IsNullOrEmpty(problem);
            confirm.IsEnabled = problem is null && !busy;
        }
        async Task Submit()
        {
            Validate();
            if (!confirm.IsEnabled) return;
            busy = true; confirm.IsEnabled = false;
            try { await submit(input.Text!); window.Close(); }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                busy = false; Validate();
                error.Text = failure.Message; error.IsVisible = true;
            }
        }
        input.TextChanged += (_, _) => Validate();
        Validate();
        window.Opened += (_, _) =>
        {
            input.Focus();
            // A rename selects the stem, so typing replaces the name and keeps the extension.
            var dot = initial.LastIndexOf('.');
            input.SelectionStart = 0;
            input.SelectionEnd = dot > 0 ? dot : initial.Length;
        };
        await window.ShowDialog(owner);
    }

    // The host's containment is the real gate; this only refuses what can never be a name in this folder.
    private static string? PathNameProblem(string name)
    {
        if (name != name.Trim()) return "A name cannot start or end with a space.";
        if (Path.IsPathRooted(name) || name.Split('/', '\\').Any(segment => segment is "" or "." or ".."))
            return "Enter a name inside this folder.";
        return null;
    }

    public static async Task<bool> Confirm(Window owner, string title, string explanation, string confirmLabel = "Remove worktree", string? confirmName = null,
        CancellationToken dismiss = default)
    {
        var window = Create(title, 520);
        window.FindControl<StackPanel>("DialogFields")!.IsVisible = false;
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        text.Text = explanation; text.IsVisible = true;
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        var cancel = Ui.Button("Cancel", () => window.Close(false));
        actions.Children.Add(cancel);
        var confirm = Primary(Ui.Button(confirmLabel, () => window.Close(true)));
        confirm.Name = confirmName;
        actions.Children.Add(confirm);
        window.Opened += (_, _) => cancel.Focus();
        using var dismissal = dismiss.Register(() => window.Close(false));
        return await window.ShowDialog<bool>(owner);
    }

    /// <summary>A single-button notice for a failure with no recovery inside it; unlike <see cref="Confirm"/> nothing is decided.</summary>
    public static async Task Notice(Window owner, string title, string description, string dismissLabel = "OK")
    {
        var window = Create(title, 384);
        window.Tag = "NoticeDialog";
        window.FindControl<StackPanel>("DialogFields")!.IsVisible = false;
        var heading = window.FindControl<TextBlock>("DialogHeading")!;
        var header = (StackPanel)heading.Parent!;
        header.Children.Remove(heading);
        header.Children.Insert(0, new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Children = { Ui.Icon("alertWarning", Ui.Danger), heading } });
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        text.Text = description; text.IsVisible = true;
        var dismiss = Primary(Ui.Button(dismissLabel, () => window.Close()));
        dismiss.Name = "NoticeDismiss"; dismiss.IsDefault = true;
        window.FindControl<StackPanel>("DialogActions")!.Children.Add(dismiss);
        window.Opened += (_, _) => dismiss.Focus();
        await window.ShowDialog(owner);
    }

    public enum SaveChoice { Cancel, Save, DontSave }

    public static async Task<SaveChoice> AskToSave(Window owner, string title, bool several)
    {
        var window = Create(title, 520);
        window.FindControl<StackPanel>("DialogFields")!.IsVisible = false;
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        text.Text = "Your changes will be lost if you don't save them."; text.IsVisible = true;
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        actions.Children.Add(Ui.Button("Don't Save", () => window.Close(SaveChoice.DontSave)));
        actions.Children.Add(Ui.Button("Cancel", () => window.Close(SaveChoice.Cancel)));
        var save = Ui.Button(several ? "Save All" : "Save", () => window.Close(SaveChoice.Save));
        save.IsDefault = true; actions.Children.Add(Primary(save));
        window.Opened += (_, _) => save.Focus();
        return await window.ShowDialog<SaveChoice>(owner);
    }

    /// <summary>Marks the dialog's confirming action, which takes the reference's solid primary button style.</summary>
    /// <summary>The folder a clone of <paramref name="url"/> is named after: its last path segment without <c>.git</c>.</summary>
    public static string RepositoryName(string url)
    {
        var tail = url.Trim().TrimEnd('/', '\\').Split('/', '\\', ':')[^1];
        return tail.EndsWith(".git", StringComparison.Ordinal) ? tail[..^4] : tail;
    }

    /// <summary>
    /// Clone repository: a URL, the parent folder (picked, or typed on a remote host), an optional folder name defaulting
    /// to the repository's and an optional depth, with the target shown before anything runs. The dialog stays open,
    /// showing git's reason, while a clone fails; it returns the clone's path once one succeeds.
    /// </summary>
    public static async Task<string?> CloneProject(Window owner, string initialParent, Func<Task<string?>>? pickFolder,
        Func<string, string, string, int?, Task<string>> clone)
    {
        var window = Create("Clone repository", 560);
        window.Tag = "CloneProjectDialog";
        var explanation = window.FindControl<TextBlock>("DialogExplanation")!;
        explanation.Text = "Runs git clone into a folder you choose, then opens the clone as a project.";
        explanation.IsVisible = true;
        var panel = window.FindControl<StackPanel>("DialogFields")!;
        var url = new TextBox { Name = "CloneProjectUrl", PlaceholderText = "Repository URL" };
        var parent = new TextBox { Name = "CloneProjectParent", Text = initialParent, PlaceholderText = "Folder to clone into" };
        var name = new TextBox { Name = "CloneProjectName", PlaceholderText = "from the URL" };
        var depth = new TextBox { Name = "CloneProjectDepth", PlaceholderText = "full history" };
        var target = Ui.Text("", Ui.Muted, 12);
        target.Name = "CloneProjectTarget"; target.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        var error = Ui.Text("", Ui.Danger, 12);
        error.Name = "CloneProjectError"; error.TextWrapping = Avalonia.Media.TextWrapping.Wrap; error.IsVisible = false;
        panel.Children.Add(url);
        if (pickFolder is null) panel.Children.Add(parent);
        else
        {
            var row = new DockPanel();
            var browse = Ui.Button("Choose…", () => { });
            browse.Name = "CloneProjectBrowse"; browse.Margin = new Avalonia.Thickness(8, 0, 0, 0);
            browse.Click += async (_, _) =>
            {
                try { if (await pickFolder() is { } picked) parent.Text = picked; }
                catch (Exception failure) when (failure is not OperationCanceledException) { error.Text = failure.Message; error.IsVisible = true; }
            };
            DockPanel.SetDock(browse, Dock.Right);
            row.Children.Add(browse); row.Children.Add(parent);
            panel.Children.Add(row);
        }
        panel.Children.Add(Labelled("Folder name", name, "optional"));
        panel.Children.Add(Labelled("Depth", depth, "optional · 1 for a shallow clone"));
        panel.Children.Add(target);
        panel.Children.Add(error);
        var buttons = window.FindControl<StackPanel>("DialogActions")!;
        buttons.Children.Add(Ui.Button("Cancel", () => window.Close(null)));
        var create = Primary(Ui.Button("Clone", () => { }));
        create.Name = "CloneProjectCreate"; create.IsDefault = true;
        buttons.Children.Add(create);
        var busy = false;
        (string Url, string Parent, string Folder, int? Depth, bool Ready) Read()
        {
            var source = url.Text?.Trim() ?? "";
            var folder = name.Text?.Trim() is { Length: > 0 } typed ? typed : RepositoryName(source);
            var into = parent.Text?.Trim() ?? "";
            var text = depth.Text?.Trim() ?? "";
            int? commits = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
            var depthValid = text.Length == 0 || commits >= 1;
            return (source, into, folder, depthValid ? commits : null, source.Length > 0 && into.Length > 0 && folder.Length > 0 && depthValid);
        }
        void Refresh()
        {
            var input = Read();
            name.PlaceholderText = RepositoryName(input.Url) is { Length: > 0 } derived ? derived : "from the URL";
            target.Text = input.Parent.Length > 0 && input.Folder.Length > 0 ? System.IO.Path.Combine(input.Parent, input.Folder) : "";
            target.IsVisible = target.Text.Length > 0;
            create.IsEnabled = input.Ready && !busy;
            if (create.Content is TextBlock label) label.Text = busy ? "Cloning…" : "Clone";
            else create.Content = busy ? "Cloning…" : "Clone";
        }
        foreach (var box in new[] { url, parent, name, depth }) box.TextChanged += (_, _) => Refresh();
        create.Click += async (_, _) =>
        {
            var input = Read();
            if (!input.Ready || busy) return;
            busy = true; error.IsVisible = false; Refresh();
            try { window.Close(await clone(input.Url, input.Parent, input.Folder, input.Depth)); }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                error.Text = failure is Grpc.Core.RpcException rpc ? rpc.Status.Detail : failure.Message;
                error.IsVisible = true;
                busy = false; Refresh();
            }
        };
        Refresh();
        window.Opened += (_, _) => url.Focus();
        return await window.ShowDialog<string?>(owner);
    }

    private static Control Labelled(string label, TextBox input, string hint)
    {
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("96,*") };
        var caption = Ui.Text(label, Ui.Muted, 12);
        caption.VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        Ui.Place(row, caption);
        Ui.Place(row, input, 0, 1);
        var stack = new StackPanel { Spacing = 2 };
        stack.Children.Add(row);
        var note = Ui.Text(hint, Ui.Muted, 11);
        note.Margin = new Avalonia.Thickness(96, 0, 0, 0);
        stack.Children.Add(note);
        return stack;
    }

    public static Button Primary(Button button)
    {
        button.Classes.Add("primary");
        return button;
    }

    public static Window Create(string title, double width) => DialogWindow.Create(title, width);
}