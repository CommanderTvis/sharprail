using Avalonia.Controls;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Agent.UI;

/// <summary>How alarmed a provider is about a usage window; the bar wears the provider's answer, never the kit's.</summary>
public enum AccountSeverity
{
    Normal,
    Warning,
    Critical
}

/// <summary>The common agent account presentation: labelled rows, usage windows and the reading's timestamp.</summary>
public static class Account
{
    /// <summary>"<paramref name="provider"/> read this 5m ago, on …": the reading's age and absolute time.</summary>
    public static string ReadingLabel(string provider, DateTimeOffset fetchedAt)
    {
        var minutes = Math.Max(0, (int)Math.Round((DateTimeOffset.Now - fetchedAt).TotalMinutes, MidpointRounding.AwayFromZero));
        var hours = (int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero);
        var age = minutes < 1 ? "just now" : minutes < 60 ? $"{minutes}m ago" : hours < 48 ? $"{hours}h ago" : $"{(int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero)}d ago";
        return $"{provider} read this {age}, on {fetchedAt.ToLocalTime():G}.";
    }

    /// <summary>A window whose reset has already passed says so: the reading is old, not the window empty.</summary>
    public static string? ResetLabel(DateTimeOffset at)
    {
        var minutes = (int)Math.Round((at - DateTimeOffset.Now).TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes <= 0) return "Reset since this reading";
        if (minutes < 60) return $"Resets in {minutes}m";
        var hours = (int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero);
        return hours < 48 ? $"Resets in {hours}h" : $"Resets in {(int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero)}d";
    }

    /// <summary>A label with its value right-aligned; the value's full text is its tooltip.</summary>
    public static Control Row(string label, string value)
    {
        return new AccountRow(label, value);
    }

    /// <summary>
    /// One usage window: its label, the clamped percentage used, an accessible progress bar and the reset time. A
    /// window without a reset shows none. Names are <c>&lt;prefix&gt;UsageWindow</c> and <c>&lt;prefix&gt;UsagePercent</c>.
    /// </summary>
    public static Control UsageWindow(string id, string label, double percent, DateTimeOffset? resetsAt, string namePrefix,
        AccountSeverity severity = AccountSeverity.Normal)
    {
        return new AccountUsageWindow(id, label, percent, resetsAt, namePrefix, severity);
    }

    /// <summary>A section heading in the account surface's eyebrow style.</summary>
    public static TextBlock Heading(string text)
    {
        var heading = Ui.Text(text.ToUpperInvariant(), Ui.Muted, 11);
        heading.FontWeight = FontWeight.SemiBold;
        return heading;
    }

    /// <summary>
    /// The credits line of a governed account: "16.27 of 20.00 credits remaining, refills 2026-11-01 (plus 5.00 top-up credits)".
    /// The refill is Unix milliseconds and is left out when absent or out of range.
    /// </summary>
    public static string CreditsLine(double remaining, double total, double topUp = 0, long? refillNextMillis = null)
    {
        var text = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{remaining:0.00} of {total:0.00} credits remaining");
        if (refillNextMillis is { } millis && millis > 0 && millis < 253_402_300_799_000)
            text += ", refills " + DateTimeOffset.FromUnixTimeMilliseconds(millis).ToLocalTime().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
        if (topUp > 0) text += string.Create(System.Globalization.CultureInfo.InvariantCulture, $" (plus {topUp:0.00} top-up credits)");
        return text;
    }

    /// <summary>
    /// The account section of an agent whose launches another plugin governs, shown in place of the vendor account and
    /// usage. With a <paramref name="problem"/> only the actionable message is shown; otherwise the source, identity,
    /// plan and, when known, the credits bar and line. Names are <c>&lt;prefix&gt;GovernedAccount</c>,
    /// <c>&lt;prefix&gt;GovernedProblem</c> and <c>&lt;prefix&gt;GovernedCredits</c>.
    /// </summary>
    public static Control Governed(string source, string? identity, string? plan, double? remaining, double? total, double topUp, long? refillNextMillis, string? problem, string namePrefix)
    {
        var panel = new StackPanel { Name = namePrefix + "GovernedAccount", Spacing = 16 };
        var section = new StackPanel { Spacing = 4 };
        section.Children.Add(Heading($"{source} account"));
        section.Children.Add(Row("Source", source));
        if (problem is not null)
        {
            var text = Ui.Text(problem, Ui.Muted, 13);
            text.Name = namePrefix + "GovernedProblem";
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            section.Children.Add(text);
            panel.Children.Add(section);
            return panel;
        }
        if (identity is not null) section.Children.Add(Row("Account", identity));
        if (plan is not null) section.Children.Add(Row("Plan", plan));
        panel.Children.Add(section);
        if (remaining is { } left && total is { } size && size > 0)
        {
            var credits = new StackPanel { Spacing = 8 };
            credits.Children.Add(Heading("Credits"));
            var refill = refillNextMillis is { } millis && millis > 0 && millis < 253_402_300_799_000 ? DateTimeOffset.FromUnixTimeMilliseconds(millis) : (DateTimeOffset?)null;
            credits.Children.Add(UsageWindow("credits", "Credits used", Math.Clamp((size - left) / size * 100, 0, 100), refill, namePrefix + "Credit"));
            var line = Ui.Text(CreditsLine(left, size, topUp, refillNextMillis), Ui.Muted, 13);
            line.Name = namePrefix + "GovernedCredits";
            line.TextWrapping = TextWrapping.Wrap;
            line.TextTrimming = TextTrimming.None;
            credits.Children.Add(line);
            panel.Children.Add(credits);
        }
        return panel;
    }
}