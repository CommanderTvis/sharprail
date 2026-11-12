using Avalonia;
using Avalonia.Controls;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Acp.UI;

/// <summary>The outcome of checking the Add custom agent form: the entry to add, or what is wrong with the form.</summary>
internal sealed record AcpCustomFormResult(AcpAgentEntry? Entry, IReadOnlyList<string> Errors);

/// <summary>The Add custom agent form: its validation and the popup that shows it.</summary>
internal static class AcpCustomForm
{
    /// <summary>
    /// Checks the fields. A name and a command are needed, the name must not be one already in the list (adding would replace that agent), every
    /// environment line must be NAME=value, and a working directory must be a full path or blank.
    /// </summary>
    public static AcpCustomFormResult Validate(string? name, string? command, string? arguments, string? environment, string? directory, IReadOnlyList<AcpAgentEntry> existing)
    {
        var errors = new List<string>();
        var title = name?.Trim() ?? "";
        var program = command?.Trim() ?? "";
        if (title.Length == 0) errors.Add("Enter a name.");
        if (program.Length == 0) errors.Add("Enter the command that starts the agent.");
        if (program.Contains('\n')) errors.Add("The command is one line. Put its arguments in the arguments field.");
        if (title.Length > 0 && existing.Any(agent => agent.Id == AcpEntryText.IdFor(title) || string.Equals(agent.Name, title, StringComparison.OrdinalIgnoreCase)))
            errors.Add("An agent with this name is already in the list. Choose another name.");
        var lines = (environment ?? "").Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < lines.Length; index++)
            if (lines[index].IndexOf('=') <= 0) errors.Add($"Environment line {index + 1} is not NAME=value.");
        var cwd = directory?.Trim() ?? "";
        if (cwd.Length > 0 && !Path.IsPathRooted(cwd)) errors.Add("The working directory is a full path, or blank to use the workspace.");
        if (errors.Count > 0) return new(null, errors);
        return new(new AcpAgentEntry(AcpEntryText.IdFor(title), title, program)
        {
            Args = AcpEntryText.SplitArguments(arguments ?? ""),
            Env = AcpEntryText.ParseEnvironment(environment ?? ""),
            Cwd = cwd
        }, []);
    }

    /// <summary>The popup, in the overlay over <paramref name="owner"/>. Null when cancelled; Add stays closed until the form is valid.</summary>
    public static async Task<AcpAgentEntry?> ShowAsync(Window owner, IReadOnlyList<AcpAgentEntry> existing)
    {
        var dialog = DialogWindow.Create("Add custom agent", 520);
        dialog.Tag = "AcpAddDialog";
        dialog.FindControl<TextBlock>("DialogExplanation")!.Text = "A custom agent is a program on this computer that speaks the Agent Client Protocol. It runs with your user's access, so add only programs you trust.";
        TextBox Box(string name, string placeholder, bool multiline = false)
        {
            var box = new TextBox { Name = name, PlaceholderText = placeholder, FontFamily = Ui.CodeFont, FontSize = 12, AcceptsReturn = multiline, MinHeight = multiline ? 60 : 0 };
            Avalonia.Automation.AutomationProperties.SetName(box, placeholder);
            return box;
        }
        var name = Box("AcpAddName", "Name");
        var command = Box("AcpAddCommand", "Command, such as /usr/local/bin/my-agent");
        var arguments = Box("AcpAddArguments", "Arguments, separated by spaces");
        var environment = Box("AcpAddEnvironment", "Environment, one NAME=value per line", multiline: true);
        var directory = Box("AcpAddDirectory", "Working directory (blank: the workspace)");
        var problem = new TextBlock { Name = "AcpAddError", Foreground = Ui.Danger, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, IsVisible = false };
        var fields = dialog.FindControl<StackPanel>("DialogFields")!;
        foreach (var control in new Control[] { name, command, arguments, environment, directory, problem }) fields.Children.Add(control);
        AcpAgentEntry? added = null;
        void Add()
        {
            var result = Validate(name.Text, command.Text, arguments.Text, environment.Text, directory.Text, existing);
            if (result.Entry is null)
            {
                problem.Text = string.Join('\n', result.Errors);
                problem.IsVisible = true;
                return;
            }
            added = result.Entry;
            dialog.Close();
        }
        var actions = dialog.FindControl<StackPanel>("DialogActions")!;
        var cancel = Ui.Button("Cancel", () => dialog.Close());
        cancel.Name = "AcpAddCancel";
        actions.Children.Add(cancel);
        var add = new Button { Name = "AcpAddConfirm", Content = Ui.Text("Add", Ui.TextBrush), Padding = new Thickness(10, 5), Background = Ui.Elevated, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), IsDefault = true };
        add.Click += (_, _) => Add();
        actions.Children.Add(add);
        dialog.Opened += (_, _) => name.Focus();
        await dialog.ShowDialog(owner);
        return added;
    }
}