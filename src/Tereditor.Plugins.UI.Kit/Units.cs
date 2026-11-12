using System.Globalization;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>Words a number with its unit so a line break never separates them: the two are joined by a no-break space.</summary>
public static class Units
{
    /// <summary>U+00A0. Copied text keeps it.</summary>
    public const char NoBreak = ' ';

    /// <summary>The number and its unit as one unbreakable piece: <c>1 s</c>, <c>12 MB</c>.</summary>
    public static string Pair(string number, string unit) => number + NoBreak + unit;

    /// <summary>A whole number and its unit.</summary>
    public static string Pair(long number, string unit) => Pair(number.ToString(CultureInfo.InvariantCulture), unit);

    /// <summary>A count and its noun, singular for one: <c>1 file</c>, <c>3 files</c>.</summary>
    public static string Count(long count, string singular, string? plural = null) => Pair(count, count == 1 ? singular : plural ?? singular + "s");

    /// <summary>Whole seconds as <c>5 s</c>.</summary>
    public static string Seconds(long seconds) => Pair(seconds, "s");

    /// <summary>Whole seconds as <c>45 s</c>, <c>5 min 3 s</c> or <c>1 h 5 min</c>; only the two largest parts show, and each number stays with its unit.</summary>
    public static string Duration(long seconds)
    {
        seconds = Math.Max(0, seconds);
        var (hours, minutes, rest) = (seconds / 3600, seconds / 60 % 60, seconds % 60);
        if (hours > 0) return minutes > 0 ? Pair(hours, "h") + " " + Pair(minutes, "min") : Pair(hours, "h");
        if (minutes > 0) return rest > 0 ? Pair(minutes, "min") + " " + Pair(rest, "s") : Pair(minutes, "min");
        return Pair(rest, "s");
    }

    /// <summary>A byte count as a file manager shows it: <c>512 B</c>, <c>12 KB</c>, <c>1.5 MB</c>.</summary>
    public static string Size(long bytes)
    {
        if (bytes < 1024) return Pair(bytes, "B");
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1024.0; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return Pair(value >= 10 ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.0", CultureInfo.InvariantCulture), units[unit]);
    }
}