using System.Globalization;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.UI.Panels;

/// <summary>Wording the Code Intelligence pane and Settings share, so the two always say the same thing.</summary>
internal static class LanguageNotes
{
    internal const string Lifetime = "Servers start on demand within the limits below. Agents can start the ones they need and manage this configuration.";

    /// <summary>"12 min ago" style age of a round-trip UTC time, or empty when it cannot be read.</summary>
    internal static string Ago(string time, DateTime now)
    {
        if (!DateTime.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var then)) return "";
        var age = now - then.ToUniversalTime();
        return age.TotalSeconds < 60 ? "just now" : age.TotalMinutes < 60 ? $"{Units.Pair((int)age.TotalMinutes, "min")} ago" : age.TotalHours < 48 ? $"{Units.Pair((int)age.TotalHours, "h")} ago" : $"{Units.Pair((int)age.TotalDays, "d")} ago";
    }
}