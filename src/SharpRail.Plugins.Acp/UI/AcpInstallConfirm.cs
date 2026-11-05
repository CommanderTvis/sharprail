using Avalonia;
using Avalonia.Controls;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Acp.UI;

/// <summary>The install confirmation: shown over the window like every other confirmation, so it appears where the user clicked Install, never somewhere down the list.</summary>
internal static class AcpInstallConfirm
{
    public static string Title(AcpInstallPlan plan) => $"Install {plan.Name} {plan.Version}?";

    /// <summary>What the dialog says in plain words: what the install does, then the not-recommended line when the agent has one.</summary>
    public static string Explanation(AcpInstallPlan plan) => plan.Note + (plan.NotRecommended is null ? "" : "\n\n" + AcpInstallPlan.NotRecommendedLine);

    /// <summary>The facts the user is agreeing to, in order: source, command, checksum, license, repository. Absent ones are left out.</summary>
    public static IReadOnlyList<(string Label, string Value)> Facts(AcpInstallPlan plan)
    {
        var facts = new List<(string, string)> { ("Source", plan.Source), ("Runs", plan.CommandLine) };
        if (plan.Sha256 is { } sha) facts.Add(("SHA-256", sha));
        if (plan.License is { } license) facts.Add(("License", license));
        if (plan.Repository is { } repository) facts.Add(("Repository", repository));
        return facts;
    }

    /// <summary>True when the user confirmed. Escape, Cancel and a click outside answer no.</summary>
    public static async Task<bool> ShowAsync(Window owner, AcpInstallPlan plan)
    {
        var dialog = DialogWindow.Create(Title(plan), 520);
        dialog.Tag = "AcpInstallDialog";
        dialog.FindControl<TextBlock>("DialogExplanation")!.Text = Explanation(plan);
        var fields = dialog.FindControl<StackPanel>("DialogFields")!;
        foreach (var (label, value) in Facts(plan))
        {
            var line = new SelectableTextBlock { Text = $"{label}: {value}", FontFamily = Ui.CodeFont, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Foreground = Ui.TextBrush };
            fields.Children.Add(line);
        }
        var confirmed = false;
        var actions = dialog.FindControl<StackPanel>("DialogActions")!;
        var cancel = Ui.Button("Cancel", () => dialog.Close());
        cancel.Name = "AcpConfirmCancel";
        actions.Children.Add(cancel);
        var install = new Button { Name = "AcpConfirmInstall", Content = Ui.Text("Confirm and install", Ui.TextBrush), Padding = new Thickness(10, 5), Background = Ui.Elevated, BorderBrush = Ui.BorderBrush, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), IsDefault = true };
        install.Click += (_, _) => { confirmed = true; dialog.Close(); };
        actions.Children.Add(install);
        dialog.Opened += (_, _) => cancel.Focus();
        await dialog.ShowDialog(owner);
        return confirmed;
    }
}