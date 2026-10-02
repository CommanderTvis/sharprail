using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>Who Claude Code is signed in as, the usage windows it caches with the severity it chose, and its version.</summary>
internal sealed class ClaudeAccountSurface(IPluginUIContext context) : StackPanel
{
    private ClaudeAccount? account;
    private string? error;
    private bool busy;
    private int generation;

    // The mount's read has nothing to force; only a press asks Claude Code to fetch the numbers again.
    public void Read(bool refresh)
    {
        var mine = ++generation;
        error = null;
        busy = refresh;
        Render();
        _ = Task.Run(async () =>
        {
            ClaudeAccount? next = null;
            string? failure = null;
            try { next = await context.RequestAsync(ClaudeCodeContract.Account, new AccountParams { Refresh = refresh ? true : null }); }
            catch (Exception exception) when (exception is not OperationCanceledException) { failure = "Could not read the account."; }
            Dispatcher.UIThread.Post(() =>
            {
                if (mine != generation) return;
                account = next ?? account;
                error = failure;
                busy = false;
                Render();
            });
        });
    }

    private static string Ago(TimeSpan elapsed)
    {
        var minutes = Math.Max(0, (int)Math.Round(elapsed.TotalMinutes, MidpointRounding.AwayFromZero));
        if (minutes < 1) return "just now";
        if (minutes < 60) return $"{minutes}m ago";
        var hours = (int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero);
        return hours < 48 ? $"{hours}h ago" : $"{(int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero)}d ago";
    }

    // A window whose reset has already passed says so: the reading is old, not the window empty.
    private static string? ResetLabel(string resetsAt)
    {
        if (!DateTimeOffset.TryParse(resetsAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at)) return null;
        var minutes = (int)Math.Round((at - DateTimeOffset.UtcNow).TotalMinutes, MidpointRounding.AwayFromZero);
        if (minutes <= 0) return "Reset since this reading";
        if (minutes < 60) return $"Resets in {minutes}m";
        var hours = (int)Math.Round(minutes / 60.0, MidpointRounding.AwayFromZero);
        return hours < 48 ? $"Resets in {hours}h" : $"Resets in {(int)Math.Round(hours / 24.0, MidpointRounding.AwayFromZero)}d";
    }

    private static Control Row(string label, string value)
    {
        var row = new DockPanel();
        var name = Ui.Text(label, Ui.Muted, 13);
        DockPanel.SetDock(name, Dock.Left);
        row.Children.Add(name);
        var text = Ui.Text(value, Ui.TextBrush, 13);
        text.HorizontalAlignment = HorizontalAlignment.Right;
        row.Children.Add(text);
        return row;
    }

    private static Control Heading(string text) => Ui.Text(text.ToUpperInvariant(), Ui.Muted, 11);

    // Claude Code has already decided how alarmed to be; the bar wears its answer, not a second opinion.
    private static Control UsageBar(ClaudeUsageWindow window)
    {
        var bar = new StackPanel { Name = "ClaudeUsageWindow", Tag = window.Id, Spacing = 2 };
        var head = new DockPanel();
        var percent = Ui.Text($"{window.Percent}%", Ui.TextBrush, 13);
        percent.Name = "ClaudeUsagePercent";
        DockPanel.SetDock(percent, Dock.Right);
        head.Children.Add(percent);
        head.Children.Add(Ui.Text(window.Label, Ui.TextBrush, 13));
        bar.Children.Add(head);
        var fill = new Border
        {
            Background = window.Severity switch { ClaudeUsageSeverity.Critical => Ui.Danger, ClaudeUsageSeverity.Warning => Ui.Warning, _ => Ui.Accent },
            HorizontalAlignment = HorizontalAlignment.Left
        };
        var track = new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = Ui.Elevated, ClipToBounds = true, Child = fill };
        track.SizeChanged += (_, size) => fill.Width = size.NewSize.Width * window.Percent / 100.0;
        bar.Children.Add(track);
        if (window.ResetsAt is { } resetsAt && ResetLabel(resetsAt) is { } reset) bar.Children.Add(Ui.Text(reset, Ui.Hint, 12));
        return bar;
    }

    private void Render()
    {
        Children.Clear();
        if (error is not null)
        {
            var text = Ui.Text(error, Ui.Danger, 13);
            text.Name = "ClaudeAccountError";
            Children.Add(text);
            return;
        }
        if (account is null)
        {
            Children.Add(Ui.Text("Reading account…", Ui.Muted, 13));
            return;
        }
        var identity = new StackPanel { Spacing = 4 };
        identity.Children.Add(Heading("Account"));
        if (account.LoggedIn)
        {
            if (account.Email is { } email) identity.Children.Add(Row("Email", email));
            if (account.Subscription is { } plan) identity.Children.Add(Row("Plan", plan));
            if (account.Organization is { } organization) identity.Children.Add(Row("Organization", organization));
        }
        else
        {
            var signedOut = ClaudeParts.Wrapped("Not signed in. Run claude auth login in a terminal.", Ui.Muted, 13);
            signedOut.Name = "ClaudeAccountSignedOut";
            identity.Children.Add(signedOut);
        }
        Children.Add(identity);

        var usage = new StackPanel { Spacing = 8 };
        usage.Children.Add(Heading("Usage"));
        if (account.Usage.Count == 0)
        {
            var empty = ClaudeParts.Wrapped("No usage reading yet — Claude Code records one while it works.", Ui.Muted, 13);
            empty.Name = "ClaudeUsageEmpty";
            usage.Children.Add(empty);
        }
        else foreach (var window in account.Usage) usage.Children.Add(UsageBar(window));
        if (account.UsageFetchedAt is { } fetched && DateTimeOffset.TryParse(fetched, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            var age = ClaudeParts.Wrapped(busy ? "Asking Claude Code for the current numbers…"
                : $"Claude Code read this {Ago(DateTimeOffset.UtcNow - at)}, on {at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}.", Ui.Hint);
            age.Name = "ClaudeUsageAge";
            usage.Children.Add(age);
        }
        Children.Add(usage);

        if (account.Version is { } version)
        {
            var cli = new StackPanel { Spacing = 4 };
            cli.Children.Add(Heading("Claude Code CLI"));
            cli.Children.Add(Row("Version", version));
            Children.Add(cli);
        }
    }
}