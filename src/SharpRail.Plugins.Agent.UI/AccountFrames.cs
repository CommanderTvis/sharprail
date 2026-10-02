using System.Globalization;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Agent.UI;

internal sealed partial class AccountRow : Grid
{
    internal AccountRow(string label, string value)
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBlock>("Label")!.Text = label;
        var shown = this.FindControl<TextBlock>("Value")!;
        shown.Text = value;
        ToolTip.SetTip(shown, value);
    }
}

internal sealed partial class AccountUsageWindow : StackPanel
{
    internal AccountUsageWindow(string id, string label, double percent, DateTimeOffset? resetsAt,
        string prefix, AccountSeverity severity)
    {
        AvaloniaXamlLoader.Load(this);
        Name = prefix + "UsageWindow";
        Tag = id;
        this.FindControl<TextBlock>("Label")!.Text = label;
        var used = Math.Clamp(percent, 0, 100);
        var shown = this.FindControl<TextBlock>("Percent")!;
        shown.Name = prefix + "UsagePercent";
        shown.Text = used.ToString("0.##", CultureInfo.InvariantCulture) + "% used";
        var bar = this.FindControl<ProgressBar>("Usage")!;
        bar.Value = used;
        bar.Foreground = severity switch
        {
            AccountSeverity.Critical => Ui.Danger,
            AccountSeverity.Warning => Ui.Warning,
            _ => Ui.Accent
        };
        AutomationProperties.SetName(bar, label + " usage");
        var reset = this.FindControl<TextBlock>("Reset")!;
        reset.Name = prefix + "UsageReset";
        reset.Text = resetsAt is { } at ? Account.ResetLabel(at) : null;
        reset.IsVisible = reset.Text is not null;
    }
}