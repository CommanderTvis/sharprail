using System.Globalization;

namespace SharpRail.UI.Panels;

/// <summary>Wording the Code Intelligence pane and Settings share, so the two always say the same thing.</summary>
internal static class LanguageNotes
{
    internal const string Lifetime = "Language servers run while their workspace uses them, within the limits below. Orchestrators start the ones agents need when they create a workspace, and agent tools start any other on demand. Start one here for language features in the editor.";

    /// <summary>"12 min ago" style age of a round-trip UTC time, or empty when it cannot be read.</summary>
    internal static string Ago(string time, DateTime now)
    {
        if (!DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var then)) return "";
        var age = now - then.ToUniversalTime();
        return age.TotalSeconds < 60 ? "just now" : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago" : age.TotalHours < 48 ? $"{(int)age.TotalHours} h ago" : $"{(int)age.TotalDays} d ago";
    }
}