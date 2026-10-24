using System.Globalization;

using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.AirGovernance.UI;

/// <summary>What the header item shows: nothing, the credits, or a warning with its reason in the tooltip.</summary>
internal sealed record AirCreditsShown(bool Visible, string Text = "", string Tip = "", bool Warning = false);

/// <summary>
/// The credits the header items show, one per activation however many windows there are. It reads Central (the
/// host's read-only <c>central limit --json</c>, through the status method) only while Central governs at least one
/// agent and an item is attached: when an item attaches or its window activates, after a governed agent's terminal
/// ends, every <see cref="Interval"/>, at most once per <see cref="Throttle"/>, and at once when the source or an
/// agent switch changes. Reads never overlap; a forced read during one runs after it. Status pushes from the pane or
/// the agents' Account tabs update it without a read.
/// </summary>
internal sealed class AirCreditsFeed : IDisposable
{
    private readonly IPluginUIContext context;
    private readonly List<IDisposable> observers = [];
    private DispatcherTimer? timer;
    private AirStatus? status;
    private string? failure;
    private bool read;
    private bool reading;
    private bool again;
    private int views;
    private DateTime lastRead = DateTime.MinValue;

    /// <summary>Time between reads while an item is shown.</summary>
    internal static TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Least time between reads that were not forced by a settings change.</summary>
    internal static TimeSpan Throttle { get; set; } = TimeSpan.FromSeconds(15);

    public AirCreditsFeed(IPluginUIContext context)
    {
        this.context = context;
        observers.Add(context.OnSettings<AirSettings>(changed => { read = false; failure = null; Schedule(); Changed?.Invoke(); _ = RefreshAsync(true); }));
        // A governed agent that ended has spent credits: its terminal leaving the projection triggers a read.
        observers.Add(context.WatchHost(Governed, (next, previous) => { if (previous.Except(next).Any()) _ = RefreshAsync(false); }));
    }

    public event Action? Changed;

    public AirCreditsShown Shown => Present(context.Settings<AirSettings>(), status, read, failure);

    /// <summary>A status from the plugin's channel or a reply: the latest the host knows.</summary>
    public void Apply(AirStatus next) { status = next; Changed?.Invoke(); }

    public void Attach() { views++; Schedule(); _ = RefreshAsync(false); }

    public void Detach() { views--; Schedule(); }

    /// <summary>A window showing an item activated: a throttled read.</summary>
    public void Activated() => _ = RefreshAsync(false);

    private static HashSet<string> Governed(PluginHostProjection host) =>
        [.. host.Terminals.Values.SelectMany(tabs => tabs).Where(tab => tab.Agent is { } agent && AirLaunch.CentralNames.Values.Any(name => agent.Command.Contains(" run " + name + " --", StringComparison.Ordinal))).Select(tab => tab.TabKey)];

    private bool Active => views > 0 && AirLaunch.Wired(context.Settings<AirSettings>());

    private void Schedule()
    {
        if (Active && timer is null)
        {
            timer = new DispatcherTimer { Interval = Interval };
            timer.Tick += (_, _) => _ = RefreshAsync(false);
            timer.Start();
        }
        else if (!Active && timer is not null) { timer.Stop(); timer = null; }
    }

    internal async Task RefreshAsync(bool force)
    {
        if (!Active) return;
        if (reading) { again |= force; return; }
        if (!force && DateTime.UtcNow - lastRead < Throttle) return;
        reading = true;
        try
        {
            // The embedded host runs the method inline until its first real await, so the call starts off the UI thread.
            var next = await Task.Run(() => context.RequestAsync(AirGovernanceContract.Status, new AirStatusParams(true)).AsTask());
            status = next; failure = null;
        }
        catch (PluginCallException error) { failure = error.Message; }
        finally
        {
            reading = false; read = true; lastRead = DateTime.UtcNow;
            Changed?.Invoke();
        }
        if (again) { again = false; await RefreshAsync(true); }
    }

    /// <summary>
    /// Nothing unless Central governs an agent. Then the credits when the host has them, and after a read that found
    /// none, a warning naming why. Before the first read nothing is shown rather than a guess.
    /// </summary>
    internal static AirCreditsShown Present(AirSettings settings, AirStatus? status, bool read, string? failure)
    {
        if (!AirLaunch.Wired(settings)) return new(false);
        var governs = string.Join(" and ", AirLaunch.CentralNames.Keys.Append(AirLaunch.OmpLauncher).Where(id => AirLaunch.Enabled(settings, id))
            .Select(id => id switch { "claude" => "Claude Code", "codex" => "Codex", _ => "omp" }));
        const string Open = "Click to open Settings › Air Governance.";
        if (failure is null && status is { Source: AirSources.Central, Credits: { } credits })
        {
            var culture = CultureInfo.InvariantCulture;
            var lines = new List<string>
            {
                "JetBrains AI credits through Central, for " + governs,
                string.Create(culture, $"{credits.Remaining:0.00} of {credits.Total:0.00} credits remaining in the plan")
            };
            if (credits.TopUp > 0) lines.Add(string.Create(culture, $"Top-up: {credits.TopUp:0.00} credits"));
            if (credits.RefillNext is { } refill && refill > 0 && refill < 253_402_300_799_000)
                lines.Add("Refills " + DateTimeOffset.FromUnixTimeMilliseconds(refill).ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture));
            if (credits.ReadAt is { } at && at > 0 && at < 253_402_300_799_000)
                lines.Add("Updated " + DateTimeOffset.FromUnixTimeMilliseconds(at).ToLocalTime().ToString("yyyy-MM-dd HH:mm", culture));
            lines.Add(Open);
            var low = credits.Total > 0 && credits.Remaining + credits.TopUp < credits.Total * LowShare;
            return new(true, string.Create(culture, $"{credits.Remaining:0.00} / {credits.Total:0.00} credits"), string.Join("\n", lines), low);
        }
        if (!read) return new(false);
        var reason = failure ?? Problem(status);
        return new(true, "credits ?", "Could not show the JetBrains AI credits for " + governs + ": " + reason + "\n" + Open, true);
    }

    /// <summary>Below this share of the plan left (top-up included), the credits show in the warning colour.</summary>
    internal const double LowShare = 0.1;

    private static string Problem(AirStatus? status) => status?.Central switch
    {
        null => "Central has not reported yet.",
        { State: "absent" } => "Central is not installed.",
        { State: "outdated" } => "Central is outdated; update it to read credits.",
        { State: not "supported" } => "Central cannot be used on this host.",
        { Auth: "signed-out" } => "Not signed in to Central.",
        { Auth: not "connected" } => "Could not check whether Central is signed in.",
        _ => "Central did not report the remaining credits."
    };

    public void Dispose()
    {
        timer?.Stop(); timer = null;
        foreach (var observer in observers) observer.Dispose();
        observers.Clear();
        Changed = null;
    }
}

/// <summary>The header item (Plugin API W24): the remaining credits beside Settings, which it opens on Air Governance.</summary>
internal sealed class AirCreditsHeader : Button
{
    private readonly AirCreditsFeed feed;
    private readonly TextBlock label;
    private readonly ContentControl icon = new();
    private Window? window;
    private bool warning;

    protected override Type StyleKeyOverride => typeof(Button);

    public AirCreditsHeader(IPluginUIContext context, AirCreditsFeed feed)
    {
        this.feed = feed;
        Name = "AirCreditsHeader";
        Background = Brushes.Transparent; BorderThickness = default; Padding = new(4, 0);
        VerticalAlignment = VerticalAlignment.Center;
        label = Typography.Apply(new TextBlock { Name = "AirCreditsHeaderText", VerticalAlignment = VerticalAlignment.Center }, Typography.UiMetadata);
        Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Children = { icon, label } };
        Click += (_, _) => context.OpenSettings(UiPages.PluginSection(AirGovernanceContract.Id));
        IsVisible = false;
        AttachedToVisualTree += (_, _) =>
        {
            feed.Changed += Render;
            window = TopLevel.GetTopLevel(this) as Window;
            if (window is not null) window.Activated += Activated;
            Render();
            feed.Attach();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            feed.Changed -= Render;
            if (window is not null) window.Activated -= Activated;
            window = null;
            feed.Detach();
        };
    }

    private void Activated(object? sender, EventArgs e) => feed.Activated();

    private void Render()
    {
        var shown = feed.Shown;
        IsVisible = shown.Visible;
        label.Text = shown.Text;
        label.Foreground = shown.Warning ? Ui.Warning : Ui.Muted;
        if (icon.Content is null || warning != shown.Warning)
        {
            warning = shown.Warning;
            icon.Content = Ui.Icon(shown.Warning ? "alertWarning" : "sparkling", shown.Warning ? Ui.Warning : Ui.Muted, 14);
        }
        ToolTip.SetTip(this, shown.Tip.Length > 0 ? shown.Tip : null);
        AutomationProperties.SetName(this, shown.Text.Length > 0 ? "JetBrains AI " + shown.Text : "JetBrains AI credits");
    }
}