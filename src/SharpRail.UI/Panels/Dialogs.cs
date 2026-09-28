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

    public static async Task<bool> Confirm(Window owner, string title, string explanation)
    {
        var window = Create(title, 520, 240);
        window.FindControl<StackPanel>("DialogBody")!.Spacing = 18;
        window.FindControl<StackPanel>("DialogFields")!.IsVisible = false;
        var text = window.FindControl<TextBlock>("DialogExplanation")!;
        text.Text = explanation; text.IsVisible = true;
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        actions.Children.Add(Ui.Button("Cancel", () => window.Close(false)));
        actions.Children.Add(Ui.Button("Remove worktree", () => window.Close(true)));
        return await window.ShowDialog<bool>(owner);
    }

    public static Window Create(string title, double width, double height)
    {
        var window = new DialogWindow { Title = title, Width = width, Height = height };
        window.FindControl<TextBlock>("DialogHeading")!.Text = title;
        return window;
    }
}
