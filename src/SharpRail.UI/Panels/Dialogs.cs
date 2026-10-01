using Avalonia.Controls;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Panels;

public static class Dialogs
{
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

    public static async Task<string?> HostPath(Window owner, string initial, string? pickerError, bool remote)
    {
        var window = Create("Open project by path", 520);
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        // On the desktop the host is this computer, so the copy does not call it the host.
        text.Text = remote ? "Enter the absolute path of a folder on the computer running SharpRail." : "Enter the absolute path of a folder.";
        text.IsVisible = true;
        var panel = window.FindControl<StackPanel>("DialogFields")!;
        if (pickerError is not null)
        {
            var failure = Ui.Text("The folder picker failed: " + pickerError, Ui.Danger, 12);
            failure.Name = "OpenProjectPickerError"; failure.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
            panel.Children.Add(failure);
        }
        var input = new TextBox { Name = "OpenProjectPathInput", Text = initial, PlaceholderText = "Directory path" };
        panel.Children.Add(input);
        var buttons = window.FindControl<StackPanel>("DialogActions")!;
        buttons.Children.Add(Ui.Button("Cancel", () => window.Close(null)));
        var accept = Ui.Button("Open project", () => window.Close(input.Text?.Trim() is { Length: > 0 } path ? path : null));
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
    public static Button Primary(Button button)
    {
        button.Classes.Add("primary");
        return button;
    }

    public static Window Create(string title, double width)
    {
        // Standard dialogs use the reference's 28rem card; the window adds room for the card's shadow.
        var card = width <= 520 ? Math.Min(width, 448) : width;
        // The height fits the content; callers that need a fixed size (the Mermaid viewer) set it themselves.
        var window = new DialogWindow { Title = title, Width = (card + 48) * InterfaceZoom.Current };
        window.FindControl<TextBlock>("DialogHeading")!.Text = title;
        return window;
    }
}