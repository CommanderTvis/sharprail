using Avalonia.Controls;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Panels;

public static class Dialogs
{
    public static async Task<string[]?> Prompt(Window owner, string title, params (string Label, string Value)[] fields)
    {
        var window = Create(title, 520, 150 + fields.Length * 75);
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
        accept.IsDefault = true; buttons.Children.Add(accept);
        window.Opened += (_, _) => inputs.First().Focus();
        return await window.ShowDialog<string[]?>(owner);
    }

    public static async Task<string?> HostPath(Window owner, string initial, string? pickerError)
    {
        var window = Create("Open project by path", 520, pickerError is null ? 230 : 290);
        window.FindControl<StackPanel>("DialogBody")!.Spacing = 12;
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        text.Text = "Enter the absolute path of a folder on the computer running SharpRail."; text.IsVisible = true;
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
        accept.Name = "OpenProjectPathSubmit"; accept.IsDefault = true; buttons.Children.Add(accept);
        window.Opened += (_, _) => input.Focus();
        return await window.ShowDialog<string?>(owner);
    }

    public static async Task<bool> Confirm(Window owner, string title, string explanation, string confirmLabel = "Remove worktree", string? confirmName = null)
    {
        var window = Create(title, 520, 240);
        window.FindControl<StackPanel>("DialogBody")!.Spacing = 18;
        window.FindControl<StackPanel>("DialogFields")!.IsVisible = false;
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        text.Text = explanation; text.IsVisible = true;
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        var cancel = Ui.Button("Cancel", () => window.Close(false));
        actions.Children.Add(cancel);
        var confirm = Ui.Button(confirmLabel, () => window.Close(true));
        confirm.Name = confirmName;
        actions.Children.Add(confirm);
        window.Opened += (_, _) => cancel.Focus();
        return await window.ShowDialog<bool>(owner);
    }

    public enum SaveChoice { Cancel, Save, DontSave }

    public static async Task<SaveChoice> AskToSave(Window owner, string title, bool several)
    {
        var window = Create(title, 520, 240);
        window.FindControl<StackPanel>("DialogBody")!.Spacing = 18;
        window.FindControl<StackPanel>("DialogFields")!.IsVisible = false;
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        text.Text = "Your changes will be lost if you don't save them."; text.IsVisible = true;
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        actions.Children.Add(Ui.Button("Don't Save", () => window.Close(SaveChoice.DontSave)));
        actions.Children.Add(Ui.Button("Cancel", () => window.Close(SaveChoice.Cancel)));
        var save = Ui.Button(several ? "Save All" : "Save", () => window.Close(SaveChoice.Save));
        save.IsDefault = true; actions.Children.Add(save);
        window.Opened += (_, _) => save.Focus();
        return await window.ShowDialog<SaveChoice>(owner);
    }

    public static Window Create(string title, double width, double height)
    {
        var window = new DialogWindow { Title = title, Width = width, Height = height };
        window.FindControl<TextBlock>("DialogHeading")!.Text = title;
        return window;
    }
}
