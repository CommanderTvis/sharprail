namespace Tereditor.Checks;

/// <summary>Running text that names a tab quotes it through <see cref="TabNames.Quote"/>; the listed sites never splice a raw title.</summary>
internal static class TabNameChecks
{
    private static readonly (string File, string Raw)[] Sites =
    [
        ("src/Tereditor.UI/UndoClose.cs", "\"Closed \" + closed.Tab.Title"),
        ("src/Tereditor.UI/CenterTabsInProjects.cs", "\"Open \" + tab.Title"),
        ("src/Tereditor.UI/Docking/DockGroups.cs", "\"Open \" + Session.ToolTitle(tab)"),
        ("src/Tereditor.UI/OwnedProcessNotices.cs", "?.Title ?? \"An agent terminal\""),
        ("src/Tereditor.UI/WorkspaceUnread.cs", "“{TabTitle("),
        ("src/Tereditor.Plugins.Codex/UI/CodexPanel.cs", "tab “{"),
        ("src/Tereditor.Plugins.ClaudeCode/UI/ClaudeConfigPanel.cs", "tab “{"),
    ];

    internal static void Run()
    {
        Require(TabNames.Quote("Terminal 3") == "“Terminal 3”", "A tab name is wrapped in typographic quotes.");
        Require(TabNames.Quote("  Review  ") == "“Review”", "A tab name is trimmed inside the quotes.");
        Require(TabNames.Quote("") == "“Untitled”" && TabNames.Quote(null) == "“Untitled”", "A blank tab name reads Untitled, not an empty pair.");
        Require(TabNames.Quote("say \"hi\"") == "“say \"hi\"”", "Quotes inside a tab name are kept.");
        if (BoundaryChecks.Checkout() is not { } checkout) { Console.WriteLine("SKIP tab name source scan: no source checkout"); return; }
        foreach (var (file, raw) in Sites)
        {
            var text = File.ReadAllText(Path.Combine(checkout, file));
            Require(text.Contains("TabNames.Quote(", StringComparison.Ordinal), $"{file} names a tab through TabNames.Quote.");
            Require(!text.Contains(raw, StringComparison.Ordinal), $"{file} splices a raw tab title: {raw}");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Tab name check failed: " + message);
    }
}