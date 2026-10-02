using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.Codex;
using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Notifications;
using SharpRail.UI.Panels;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>
/// Away notifications end to end without the system: the real Claude Code and Codex UI halves fold status pushes
/// injected on their channels, and a recording notifier stands where the app composes the native one.
/// </summary>
internal static class AwayNotificationChecks
{
    private sealed class Recording : IDesktopNotifier
    {
        public List<DesktopNotification> Shown { get; } = [];
        public int PermissionRequests { get; private set; }
        public event Action<string>? Activated;
        public void RequestPermission() => PermissionRequests++;
        public void Show(DesktopNotification notification) => Shown.Add(notification);
        public void Activate(string id) => Activated?.Invoke(id);
    }

    // The plugin service with each plugin's status channel replaced by pushes the check sends.
    private sealed class InjectedStatus(IPluginService inner) : IPluginService
    {
        private readonly ConcurrentDictionary<string, Channel<object?>> status = [];

        private Channel<object?> Status(string plugin) => status.GetOrAdd(plugin, _ => Channel.CreateUnbounded<object?>());

        public void Push(string plugin, object payload) => Status(plugin).Writer.TryWrite(payload);

        public IAsyncEnumerable<object?> SubscribeAsync(PluginSubscription subscription, CancellationToken cancellationToken = default) =>
            subscription.Channel == "status" ? Read(Status(subscription.PluginId), cancellationToken) : inner.SubscribeAsync(subscription, cancellationToken);

        private static async IAsyncEnumerable<object?> Read(Channel<object?> channel, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await foreach (var payload in channel.Reader.ReadAllAsync(cancellationToken)) yield return payload;
        }

        public ValueTask<IReadOnlyList<SharpRail.Plugins.Api.PluginRosterEntry>> ListAsync(CancellationToken cancellationToken = default) => inner.ListAsync(cancellationToken);
        public ValueTask<IReadOnlyList<SharpRail.Plugins.Api.PluginRosterEntry>> RescanAsync(CancellationToken cancellationToken = default) => inner.RescanAsync(cancellationToken);
        public ValueTask<IReadOnlyList<SharpRail.Plugins.Api.PluginRosterEntry>> RetryAsync(string id, CancellationToken cancellationToken = default) => inner.RetryAsync(id, cancellationToken);
        public ValueTask<object?> CallAsync(PluginCallRequest request, CancellationToken cancellationToken = default) => inner.CallAsync(request, cancellationToken);
        public ValueTask<byte[]?> ReadFileAsync(string id, string path, CancellationToken cancellationToken = default) => inner.ReadFileAsync(id, path, cancellationToken);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        var directory = Path.Combine(root, "away-notifications");
        var isolated = new Dictionary<string, string?>();
        foreach (var name in new[] { "CLAUDE_CONFIG_DIR", "SHARPRAIL_STATE_DIR", "CODEX_HOME" })
        {
            isolated[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, Directory.CreateDirectory(Path.Combine(directory, name.ToLowerInvariant())).FullName);
        }
        try { Scenario(directory); }
        finally { foreach (var (name, value) in isolated) Environment.SetEnvironmentVariable(name, value); }
        Console.WriteLine("PASS away notifications: attention while unfocused, suppression, coalescing, the setting and click routing");
    }

    private static void Scenario(string directory)
    {
        const string claudeTab = "away-claude", codexTab = "away-codex";
        var notifier = new Recording();
        InjectedStatus? plugins = null;
        using var app = new E2eWorkspace(Path.Combine(directory, "project"), openFiles: false, plugins: inner => plugins = new InjectedStatus(inner), notifier: notifier);
        var first = app.Window.WorkspaceRoot;
        var registry = app.Workbench.PluginLoader.Registry;
        app.State!.ChangeAsync([HostStateChange.PluginEnabled("claude-code", true), HostStateChange.PluginEnabled(CodexManifest.Id, true)]).AsTask().GetAwaiter().GetResult();
        Until(() => registry.Active.Contains("claude-code") && registry.Active.Contains(CodexManifest.Id));
        app.Window.Layout.NewTerminal(app.Center, claudeTab);
        app.Window.Layout.NewTerminal(app.Center, codexTab);
        Until(() => app.Workbench.WindowHolding(first, claudeTab) is not null && app.Workbench.WindowHolding(first, codexTab) is not null);
        Settle();

        void Claude(ClaudeCodeStatus? status, string @event, bool? notify = null, string? summary = null) =>
            plugins!.Push("claude-code", new ClaudeCodeStatusPush(first, claudeTab, new(@event) { Project = "demo", Summary = summary, Notify = notify }, status));
        void Codex(CodexStatus status, string @event) =>
            plugins!.Push(CodexManifest.Id, new CodexStatusPush(first, codexTab, status, @event) { Cwd = "/work/api/" });
        // Longer than the collection window, so a notification that was going to be shown has been.
        void Quiet(string message)
        {
            var count = notifier.Shown.Count;
            Settle(600);
            Require(notifier.Shown.Count == count, message + " Shown: " + notifier.Shown.LastOrDefault());
        }
        DesktopNotification Next(Action raise)
        {
            var count = notifier.Shown.Count;
            raise();
            Until(() => notifier.Shown.Count > count);
            Settle(400);
            Require(notifier.Shown.Count == count + 1, "One collection window shows one notification.");
            return notifier.Shown[^1];
        }

        // Headless windows never lose activation to one another, so the platform's own signal is raised by hand.
        void Unfocus()
        {
            var platform = app.Window.PlatformImpl!;
            ((Action)platform.GetType().GetProperty("Deactivated")!.GetValue(platform)!)();
            Require(!app.Workbench.Focused, "The fixture's window lost focus.");
        }
        Until(() => app.Workbench.Focused);
        Claude(ClaudeCodeStatus.Blocked, "permission_request", summary: "Allow Bash?");
        Quiet("A focused app never notifies: its tab already shows the status.");

        Unfocus();

        var blocked = Next(() => Claude(ClaudeCodeStatus.Blocked, "permission_request", summary: "Allow Bash?"));
        Require(blocked is { Title: "Claude needs you — demo", Body: "Allow Bash?" } && blocked.Subtitle == app.Workbench.State.Label(first),
            "A blocked Claude terminal names the agent, the project, its workspace and what it asks: " + blocked);

        Claude(ClaudeCodeStatus.Done, "stop", notify: false);
        Quiet("A continuation's Stop settles status without notifying.");
        Require(Next(() => Claude(ClaudeCodeStatus.Done, "stop")) is { Title: "Claude finished — demo", Body: "Open the terminal for details." },
            "A finished turn notifies, pointing at the terminal when the hook reported nothing more.");
        Require(Next(() => Claude(ClaudeCodeStatus.Failed, "stop_failure")).Title == "Claude hit an error — demo", "A failed turn notifies.");

        Claude(ClaudeCodeStatus.Running, "prompt_submit");
        Claude(ClaudeCodeStatus.Idle, "interrupted");
        Quiet("A run the user cancelled settles idle and never notifies.");

        Claude(ClaudeCodeStatus.Blocked, "permission_request");
        Claude(ClaudeCodeStatus.Running, "tool_complete");
        Quiet("Attention answered within the collection window is dropped.");

        var codex = Next(() => Codex(CodexStatus.Blocked, "PermissionRequest"));
        Require(codex is { Title: "Codex needs you — api", Body: "Open the terminal for details." }, "A blocked Codex terminal notifies under its own name: " + codex);
        Codex(CodexStatus.Running, "PostToolUse");
        Codex(CodexStatus.Idle, "Interrupt");
        Quiet("Codex's running and interrupted reports never notify.");

        var burst = Next(() =>
        {
            Claude(ClaudeCodeStatus.Blocked, "permission_request");
            Claude(ClaudeCodeStatus.Done, "stop");
        });
        Require(burst.Title == "Claude finished — demo" && burst.Id == blocked.Id, "A burst from one terminal shows its latest state, replacing that terminal's earlier notification.");
        var several = Next(() =>
        {
            Claude(ClaudeCodeStatus.Failed, "stop_failure");
            Codex(CodexStatus.Done, "Stop");
        });
        Require(several is { Title: "SharpRail", Subtitle: null, Body: "2 terminals need your attention" }, "Terminals asking together are folded into one notification: " + several);

        app.State.ChangeAsync([HostStateChange.Setting("notifications", "false")]).AsTask().GetAwaiter().GetResult();
        Until(() => !app.Workbench.State.Current.Settings.NotificationsEnabled);
        Codex(CodexStatus.Done, "Stop");
        Quiet("The host's notification setting turns every notification off.");
        app.State.ChangeAsync([HostStateChange.Setting("notifications", "true")]).AsTask().GetAwaiter().GetResult();
        Until(() => app.Workbench.State.Current.Settings.NotificationsEnabled);

        // The user is elsewhere in the app's own state too: another project is on screen when the terminal asks.
        var second = Directory.CreateDirectory(Path.Combine(directory, "second-project")).FullName;
        File.WriteAllText(Path.Combine(second, "README.md"), "second\n");
        _ = app.Window.OpenProjectAsync(second);
        Until(() => app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == second);
        Unfocus();
        var away = Next(() => Codex(CodexStatus.Done, "Stop"));
        Require(away.Title == "Codex finished — api" && away.Subtitle == app.Workbench.State.Label(first), "A terminal of a workspace that is not on screen still notifies.");
        notifier.Activate("not-a-notification");
        Settle();
        Require(app.Window.WorkspaceRoot == second && !app.Workbench.Focused, "An unknown notification id changes nothing.");
        notifier.Activate(away.Id);
        Until(() => app.Window.WorkspaceMounted && app.Window.WorkspaceRoot == first && app.Window.Layout.Selected(app.Center)?.Id == codexTab);
        Require(app.Workbench.Focused, "Activating a notification brings its window forward.");

        Unfocus();
        Codex(CodexStatus.Done, "Stop");
        app.Window.Layout.Close(app.Center, codexTab);
        Quiet("A terminal closed before its notification is shown raises nothing.");

        app.Window.Activate();
        app.Window.ShowSettings("Notifications");
        Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        Switch Toggle() => settings.GetLogicalDescendants().OfType<Switch>().Single(control => control.Name == "NotificationsEnabled");
        Require(Toggle().IsChecked, "Settings shows notifications on by default.");
        app.Click(Toggle(), freshGesture: false);
        Until(() => !app.State.Current.Settings.NotificationsEnabled && !Toggle().IsChecked);
        Require(notifier.PermissionRequests == 0, "Turning notifications off asks the system for nothing.");
        app.Click(Toggle(), freshGesture: false);
        Until(() => app.State.Current.Settings.NotificationsEnabled && Toggle().IsChecked);
        Require(notifier.PermissionRequests == 1, "Turning notifications on asks the system for permission while the user is present.");
        settings.Close();

        // The packaged gate runs inside the bundle, where the system does accept the bridge.
        if (OperatingSystem.IsMacOS() && Environment.GetEnvironmentVariable("SHARPRAIL_PACKAGED_APP") is null)
            Require(System.Runtime.InteropServices.NativeLibrary.TryLoad("SharpRailNotifications", typeof(MacNotifier).Assembly, null, out _) &&
                !MacNotifier.Create().Bundled, "The bridge loads, and tells a process without a bundle identifier that it cannot post through User Notifications.");
    }
}