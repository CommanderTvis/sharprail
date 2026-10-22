using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.UI;
using SharpRail.UI.Notifications;
using SharpRail.UI.Panels;
using SharpRail.UI.Terminal;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>
/// Programs' signals in the workbench: OSC 9;4 progress on terminal tabs and in Projects, OSC 9/777 notifications
/// only while their terminal is out of view, bells marking tabs until viewed, the Terminal settings, and no second
/// notification for an agent's tab.
/// </summary>
internal static class TerminalSignalUiChecks
{
    private sealed class Recording : IDesktopNotifier
    {
        public List<DesktopNotification> Shown { get; } = [];
        public int Attention { get; private set; }
        public int Beeps { get; private set; }
        public event Action<string>? Activated { add { } remove { } }
        public void RequestPermission() { }
        public void Show(DesktopNotification notification) => Shown.Add(notification);
        public void RequestAttention() => Attention++;
        public void Beep() => Beeps++;
    }

    internal static void Run(string root)
    {
        const string a = "signal-a", b = "signal-b";
        var notifier = new Recording();
        using var app = new E2eWorkspace(Path.Combine(root, "terminal-signals", "project"), openFiles: false, notifier: notifier);
        var workspace = app.Window.WorkspaceRoot;
        app.Window.Layout.NewTerminal(app.Center, a);
        app.Window.Layout.NewTerminal(app.Center, b);
        Until(() => app.Workbench.WindowHolding(workspace, a) is not null && app.Workbench.WindowHolding(workspace, b) is not null);
        app.Window.Layout.Select(app.Center, a);
        HostTerminal View(string tab) => app.Terminals.Views.Last(view => view.SessionId == TerminalLaunch.SessionFor(workspace, tab));
        Until(() => app.Terminals.Views.Any(view => view.SessionId == TerminalLaunch.SessionFor(workspace, a)) && View(a).Text.Length > 0);
        Settle();

        Control Tab(string tab) => app.Window.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "DockTab_" + tab.Replace(':', '_'));
        TerminalProgressStrip Strip(string tab) => Tab(tab).GetLogicalDescendants().OfType<TerminalProgressStrip>().Single();
        bool Bell(string tab) => Tab(tab).GetLogicalDescendants().OfType<TerminalBellMark>().Single().IsVisible;
        void Progress(string tab, string sequence, TerminalProgressState state, int percent, string what)
        {
            View(tab).Run($"printf '{sequence}'");
            Until(() => Strip(tab) is var strip && strip.IsVisible == (state != TerminalProgressState.None) && strip.State == state && strip.Percent == percent, what);
        }

        // Progress, as a real shell's output reports it through the host.
        Progress(a, "\\033]9;4;1;42\\007", TerminalProgressState.Normal, 42, "normal progress with its percent");
        Require(!Strip(a).Animating, "A determinate bar does not animate.");
        Require(Strip(b) is { IsVisible: false }, "Another terminal's tab shows no progress.");
        var listed = app.Window.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "WorkspaceTabPreview" && Equals(button.Tag, a))
            .SelectMany(button => button.GetLogicalDescendants().OfType<TerminalProgressStrip>()).ToArray();
        Require(listed.All(strip => strip.IsVisible && strip.Percent == 42), "Projects' tab list shows the same progress where it lists the tab.");
        Progress(a, "\\033]9;4;2;70\\033\\\\", TerminalProgressState.Error, 70, "error progress");
        Progress(a, "\\033]9;4;3\\007", TerminalProgressState.Indeterminate, -1, "indeterminate progress");
        Require(Strip(a).Animating, "An indeterminate bar animates.");
        Progress(a, "\\033]9;4;4\\007", TerminalProgressState.Paused, -1, "paused progress");
        Require(!Strip(a).Animating, "A paused bar without a percent is full, not animated.");
        Progress(a, "\\033]9;4;0\\007", TerminalProgressState.None, 0, "progress removed by state 0");

        // Notifications: only while their terminal is out of view.
        Require(app.Workbench.Focused, "The fixture's window is focused.");
        View(a).Run("printf '\\033]9;Visible\\007'");
        Quiet(notifier, "A program's notification from the terminal in view raises nothing.");
        var hidden = Next(notifier, () => View(b).Run("printf '\\033]777;notify;Tests;All passed\\007'"));
        Require(hidden is { Title: "Tests", Body: "All passed" }, "A terminal out of view notifies even in a focused app: " + hidden);
        View(b).Run("printf '\\033]777;notify;Tests;All passed\\007'");
        Quiet(notifier, "An identical notification within five seconds is dropped.");
        Unfocus(app);
        var titled = Next(notifier, () => View(a).Run("printf '\\033]9;Deploy done\\007'"));
        Require(titled.Body == "Deploy done" && titled.Title.Length > 0, "OSC 9 notifies under its tab's title once the app is away: " + titled);

        var kitty = Next(notifier, () => View(a).Run("printf '\\033]99;i=1:d=0;Kitty\\033\\\\\\033]99;i=1:p=body;Done\\033\\\\'"));
        Require(kitty is { Title: "Kitty", Body: "Done" }, "An OSC 99 notification in two chunks notifies once: " + kitty);
        // An agent's own attention for the same terminal wins: one notification, the agent's.
        var agent = Next(notifier, () =>
        {
            app.Workbench.RequestAttention(new(workspace, a, "Claude finished — demo", "") { Reason = "done" });
            app.Workbench.OfferSignal(new(TerminalLaunch.SessionFor(workspace, a), TerminalSignalKind.Notification) { Body = "From the program" });
        });
        Require(agent.Title == "Claude finished — demo", "A program's notification never adds to an agent's: " + agent);
        // An agent tab whose CLI notifies by OSC 9 itself (Codex's notification_method = osc9) notifies from the background,
        // naming its terminal; within five seconds of the agent's own attention for that terminal it is a duplicate.
        app.Workbench.PluginRegistry.AddTabDecorator("signal-agent", tab => tab is TerminalTabRef { TabKey: b } ? new TabDecoration { Icon = "terminal" } : null);
        var tabB = app.Workbench.TerminalCatalog!.Workspaces[workspace].Single(tab => tab.Key == b).Title;
        var codex = Next(notifier, () => View(b).Run("printf '\\033]9;Codex wants input\\007'"));
        Require(codex is { Body: "Codex wants input" } && codex.Subtitle == tabB + " — " + app.Workbench.State.Label(workspace),
            "A program's notification names its tab and workspace: " + codex);
        Next(notifier, () => app.Workbench.RequestAttention(new(workspace, b, "Codex needs you — demo", "") { Reason = "blocked" }));
        View(b).Run("printf '\\033]9;Codex wants input again\\007'");
        Quiet(notifier, "A program's notification just after its agent's attention for the same terminal is a duplicate.");

        app.State!.ChangeAsync([HostStateChange.Setting("terminal-notifications", "false")]).AsTask().GetAwaiter().GetResult();
        Until(() => !app.Workbench.State.Current.Settings.TerminalNotificationsEnabled);
        View(a).Run("printf '\\033]9;Silenced\\007'");
        Quiet(notifier, "Turning program notifications off silences them.");
        app.State.ChangeAsync([HostStateChange.Setting("terminal-notifications", "true")]).AsTask().GetAwaiter().GetResult();

        // A bell marks a terminal out of view and asks for attention while the app is away; viewing it clears the mark.
        // Past the agent duplicate window, so the bell's own attention request is not taken for the agent's.
        Settle(5200);
        app.Window.Activate();
        Until(() => app.Workbench.Focused);
        var attention = notifier.Attention;
        View(b).Run("printf '\\a'");
        Until(() => Bell(b), "a bell marks its tab");
        Require(notifier.Attention == attention, "A focused app asks for no attention.");
        Unfocus(app);
        View(a).Run("printf '\\a'");
        Until(() => Bell(a), "an agent-free terminal's bell while away marks its tab");
        Settle(300);
        Require(notifier.Attention == attention + 1, "A bell while the app is away requests attention once.");
        Require(notifier.Beeps == 0, "The system sound is off by default, as in Ghostty.");
        app.Window.Activate();
        app.Window.Layout.Select(app.Center, b);
        Until(() => app.Workbench.ShowsTerminal(workspace, b));
        app.Window.GetLogicalDescendants().OfType<TerminalView>().Single(view => view.Launch.SessionId == TerminalLaunch.SessionFor(workspace, b)).Focus();
        Until(() => !Bell(b), "viewing the terminal clears its bell");

        app.State.ChangeAsync([HostStateChange.Setting("bell-features", "system")]).AsTask().GetAwaiter().GetResult();
        Until(() => app.Workbench.State.Current.Settings.BellFeatures == "system");
        View(a).Run("printf '\\a'");
        Until(() => notifier.Beeps == 1, "the system feature plays the alert sound");
        Settle(300);
        Require(Bell(a), "The mark set earlier stays until the terminal is viewed.");

        // Settings › Terminal follows the host's state and changes it.
        app.Window.ShowSettings("Terminal");
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        T Named<T>(string name) where T : Control => settings.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
        Require(Named<SharpRail.Plugins.UI.Kit.Switch>("TerminalNotificationsEnabled").IsChecked && Named<CheckBox>("BellSystem").IsChecked == true &&
            Named<CheckBox>("BellTitle").IsChecked == false, "Settings show the host's terminal notification and bell choices.");
        Named<CheckBox>("BellTitle").IsChecked = true;
        Until(() => app.State.Current.Settings.BellFeatures == "system,title", "a bell box changes the host's bell features");
        app.Click(Named<SharpRail.Plugins.UI.Kit.Switch>("TerminalNotificationsEnabled"), freshGesture: false);
        Until(() => !app.State.Current.Settings.TerminalNotificationsEnabled, "the switch turns program notifications off");
        settings.Close();

        // A shell's exit removes the progress it showed.
        Progress(b, "\\033]9;4;1;5\\007", TerminalProgressState.Normal, 5, "progress before exit");
        View(b).Run("exit");
        Until(() => Strip(b) is { IsVisible: false, State: TerminalProgressState.None }, "the shell's exit clears its progress");
        Console.WriteLine("PASS terminal signals UI: tab progress, program notifications, bells, settings and agent dedupe");
    }

    private static void Unfocus(E2eWorkspace app)
    {
        var platform = app.Window.PlatformImpl!;
        ((Action)platform.GetType().GetProperty("Deactivated")!.GetValue(platform)!)();
        Require(!app.Workbench.Focused, "The fixture's window lost focus.");
    }

    private static void Quiet(Recording notifier, string message)
    {
        var count = notifier.Shown.Count;
        Settle(600);
        Require(notifier.Shown.Count == count, message + " Shown: " + notifier.Shown.LastOrDefault());
    }

    private static DesktopNotification Next(Recording notifier, Action raise)
    {
        var count = notifier.Shown.Count;
        raise();
        Until(() => notifier.Shown.Count > count);
        Settle(400);
        Require(notifier.Shown.Count == count + 1, "One collection window shows one notification.");
        return notifier.Shown[^1];
    }
}
