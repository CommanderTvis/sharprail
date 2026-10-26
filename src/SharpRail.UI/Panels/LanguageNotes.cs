using System.Globalization;

namespace SharpRail.UI.Panels;

/// <summary>Wording the Code Intelligence pane and Settings share, so the two always say the same thing.</summary>
internal static class LanguageNotes
{
    internal const string Lifetime = "Servers start on demand within the limits below. Start one here for editor features. If libraries or include paths are missing, ask your agent to fix the workspace and restart the server.";

    /// <summary>"12 min ago" style age of a round-trip UTC time, or empty when it cannot be read.</summary>
    internal static string Ago(string time, DateTime now)
    {
        if (!DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var then)) return "";
        var age = now - then.ToUniversalTime();
        return age.TotalSeconds < 60 ? "just now" : age.TotalMinutes < 60 ? $"{(int)age.TotalMinutes} min ago" : age.TotalHours < 48 ? $"{(int)age.TotalHours} h ago" : $"{(int)age.TotalDays} d ago";
    }
}