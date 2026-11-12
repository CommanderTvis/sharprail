using Avalonia.Controls;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.GitHub.UI;

/// <summary>
/// Settings › GitHub (W4): the host's <c>gh</c> sign-in status. It reads the status when shown and again every half
/// minute while visible, so there is nothing to refresh by hand.
/// </summary>
internal sealed class GitHubSettingsPage : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly StackPanel status = new() { Name = "GitHubStatus", Spacing = 4 };
    private readonly TextBlock text = new() { Name = "GitHubStatusText", Text = "Checking…" };
    private readonly TextBlock detail = new() { Name = "GitHubStatusDetail" };
    private readonly DispatcherTimer poll = new() { Interval = TimeSpan.FromSeconds(30) };
    private CancellationTokenSource? lifetime;

    public GitHubSettingsPage(IPluginUIContext context)
    {
        this.context = context;
        Spacing = 8;
        text.Classes.Add("settings-label");
        detail.Classes.Add("settings-description");
        var description = new TextBlock { Text = "SharpRail uses the GitHub CLI on this computer; it stores no credentials." };
        description.Classes.Add("settings-description");
        status.Children.Add(text);
        status.Children.Add(detail);
        var row = new Border { Child = status };
        row.Classes.Add("settings-row");
        Children.Add(description);
        Children.Add(row);
        poll.Tick += async (_, _) => await CheckAsync();
        AttachedToVisualTree += async (_, _) =>
        {
            lifetime = new();
            poll.Start();
            await CheckAsync();
        };
        DetachedFromVisualTree += (_, _) => { poll.Stop(); lifetime?.Cancel(); };
    }

    private async Task CheckAsync()
    {
        var token = lifetime?.Token ?? CancellationToken.None;
        GitHubSignIn result;
        try { result = await Task.Run(async () => await context.RequestAsync(GitHubContract.SignIn, new GitHubSignInQuery(), token).AsTask(), token); }
        catch (OperationCanceledException) { return; }
        catch (Exception error) { result = new(false, error.Message); }
        if (token.IsCancellationRequested) return;
        status.Tag = result.Connected;
        text.Text = result.Connected ? "Connected" : "Not connected";
        detail.Text = result.Detail;
    }
}