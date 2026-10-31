namespace SharpRail.Plugins.UI.Kit;

/// <summary>The one way running text names a tab: its title in typographic double quotes (“Terminal 3”), the way the app quotes other names in sentences.</summary>
public static class TabNames
{
    /// <summary>The title in “ ” quotes, trimmed; a blank title reads “Untitled” rather than an empty pair. Not for labels that are the tab's name themselves.</summary>
    public static string Quote(string? title) => "“" + (string.IsNullOrWhiteSpace(title) ? "Untitled" : title.Trim()) + "”";
}