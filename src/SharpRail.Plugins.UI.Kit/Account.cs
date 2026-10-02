using Avalonia.Controls;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit;

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
}