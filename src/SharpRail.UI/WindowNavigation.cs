using System.Text.RegularExpressions;

using Avalonia.Input;
using Avalonia.Interactivity;

namespace SharpRail.UI;

/// <summary>
/// Where a window is: Welcome, a project's home, a workspace, or a file in a workspace. It serializes to a
/// versioned, host-relative link that carries paths only, never credentials or a host address.
/// </summary>
public sealed partial record WindowLocation(string Project = "", string Workspace = "", string Resource = "")
{
    public static readonly WindowLocation Main = new();

    public string Serialize() => "#/v1" + (Project.Length == 0 ? "" : "/projects/" + Uri.EscapeDataString(Project) +
        (Workspace.Length == 0 ? "" : "/workspaces/" + Uri.EscapeDataString(Workspace) +
        (Resource.Length == 0 ? "" : "/resources/" + Uri.EscapeDataString(Resource))));

    /// <summary>Unknown versions, empty ids, extra segments and malformed encoding are invalid and mean Welcome.</summary>
    public static WindowLocation Parse(string? link)
    {
        var parts = (link ?? "").TrimStart('#').Split('/');
        if (parts.Length is not (4 or 6 or 8) || parts[0].Length > 0 || parts[1] != "v1") return Main;
        string[] names = ["projects", "workspaces", "resources"];
        var ids = new string[3];
        for (var index = 0; index < 3; index++)
        {
            if (parts.Length <= 2 + index * 2) { ids[index] = ""; continue; }
            var segment = parts[3 + index * 2];
            if (parts[2 + index * 2] != names[index] || segment.Length == 0 || MalformedEscape().IsMatch(segment)) return Main;
            ids[index] = Uri.UnescapeDataString(segment);
        }
        return new(ids[0], ids[1], ids[2]);
    }

    [GeneratedRegex("%(?![0-9A-Fa-f]{2})")]
    private static partial Regex MalformedEscape();
}

/// <summary>A window's Back/Forward list. Entries are locations, not tab state; it is never persisted.</summary>
public sealed class NavigationHistory
{
    private const int Limit = 100;
    private readonly List<WindowLocation> entries = [];
    private int index = -1;

    public WindowLocation? Current => index < 0 ? null : entries[index];
    public bool CanGoBack => index > 0;
    public bool CanGoForward => index < entries.Count - 1;

    /// <summary>A user navigation: a new entry after the current one, dropping whatever lay ahead.</summary>
    public void Push(WindowLocation location)
    {
        if (location == Current) return;
        entries.RemoveRange(index + 1, entries.Count - index - 1);
        entries.Add(location);
        if (entries.Count > Limit) entries.RemoveAt(0);
        index = entries.Count - 1;
    }

    /// <summary>Rewrites the current entry to where the window actually landed.</summary>
    public void Replace(WindowLocation location)
    {
        if (index < 0) Push(location); else entries[index] = location;
    }

    public WindowLocation? Move(int step)
    {
        if (index + step < 0 || index + step >= entries.Count) return null;
        index += step;
        return entries[index];
    }
}

/// <summary>Back/Forward over this window's locations and opening serialized links.</summary>
public sealed partial class WorkbenchWindow
{
    private readonly NavigationHistory history = new();
    // Set while a history step or link is applied, so the moves it causes rewrite its entry instead of adding one.
    private bool adopting;

    /// <summary>A link opened once this window has restored its own location.</summary>
    public string? StartLink { get; set; }

    public WindowLocation Location => projectRoot.Length == 0 ? WindowLocation.Main : atHome ? new(projectRoot) :
        new(projectRoot, workspaceRoot, Layout.Selected(Layout.View.FocusedCenter) is { Kind: "file" or "markdown" } tab ? tab.Path : "");
    public bool CanGoBack => history.CanGoBack;
    public bool CanGoForward => history.CanGoForward;

    private void WireNavigation()
    {
        LocationChanged += RecordLocation;
        Layout.Changed += RecordLocation;
        Layout.Focused += RecordLocation;
        Layout.SelectionChanged += _ => RecordLocation();
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            var buttons = e.GetCurrentPoint(this).Properties;
            if (!buttons.IsXButton1Pressed && !buttons.IsXButton2Pressed) return;
            _ = buttons.IsXButton1Pressed ? GoBackAsync() : GoForwardAsync();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    /// <summary>Mod+[ and Mod+] on macOS, Alt+Left and Alt+Right elsewhere; the direction, or 0 for another key.</summary>
    private static int HistoryStep(KeyEventArgs e) => OperatingSystem.IsMacOS()
        ? e.KeyModifiers != KeyModifiers.Meta ? 0 : e.Key == Key.OemOpenBrackets ? -1 : e.Key == Key.OemCloseBrackets ? 1 : 0
        : e.KeyModifiers != KeyModifiers.Alt ? 0 : e.Key == Key.Left ? -1 : e.Key == Key.Right ? 1 : 0;

    private void RecordLocation()
    {
        if (!WorkspaceMounted && !cleanWelcome) return;
        if (adopting) history.Replace(Location); else history.Push(Location);
    }

    public Task GoBackAsync() => history.Move(-1) is { } location ? ApplyLocationAsync(location) : Task.CompletedTask;

    public Task GoForwardAsync() => history.Move(1) is { } location ? ApplyLocationAsync(location) : Task.CompletedTask;

    /// <summary>Opens a serialized location as a new history entry. An invalid link means Welcome.</summary>
    public Task NavigateAsync(string link)
    {
        var location = WindowLocation.Parse(link);
        history.Push(location);
        return ApplyLocationAsync(location);
    }

    /// <summary>
    /// A location is intent, checked against what exists now: a project the host no longer lists falls back to
    /// Welcome, a workspace that is gone to its Project Home, and a file that will not open to its workspace.
    /// </summary>
    private async Task ApplyLocationAsync(WindowLocation target)
    {
        adopting = true;
        try
        {
            if (target.Project.Length == 0 || !state.Current.Projects.Contains(target.Project))
            {
                if (!cleanWelcome) ShowWelcome();
            }
            else if (target.Workspace.Length == 0 || !remote && !Directory.Exists(target.Workspace)) await OpenProjectHomeAsync(target.Project);
            else
            {
                if (!WorkspaceMounted || atHome || workspaceRoot != target.Workspace) await OpenWorkspaceAsync(target.Workspace, true);
                if (target.Resource.Length > 0 && WorkspaceMounted && !atHome && workspaceRoot == target.Workspace)
                    await OpenDocumentAsync(target.Resource, true);
            }
            RecordLocation();
        }
        finally { adopting = false; }
    }
}