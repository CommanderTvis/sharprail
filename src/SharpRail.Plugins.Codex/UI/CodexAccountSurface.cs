using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>Identity, ChatGPT usage windows and the CLI version, read through the plugin's <c>account</c> method on each show.</summary>
public static class CodexAccountSurface
{
    private static string Duration(double minutes) =>
        minutes % 1440 == 0 ? $"{minutes / 1440:0} day" : minutes % 60 == 0 ? $"{minutes / 60:0} hr" : $"{minutes:0} min";

    private static TextBlock Line(string name, string text, IBrush brush)
    {
        var line = Ui.Text(text, brush, 13);
        line.Name = name;
        line.TextWrapping = TextWrapping.Wrap;
        line.TextTrimming = TextTrimming.None;
        return line;
    }

    public static Control Create(IPluginUIContext context)
    {
        var host = new ContentControl { Content = Line("CodexAccountReading", "Reading account…", Ui.Muted), Margin = new Thickness(8) };
        _ = Load();
        return host;

        async Task Load()
        {
            try
            {
                var account = await context.RequestAsync(CodexContract.Account, new CodexNoParams());
                Dispatcher.UIThread.Post(() => host.Content = Render(account));
            }
            catch (Exception error)
            {
                Dispatcher.UIThread.Post(() => host.Content = Line("CodexAccountError", error.Message, Ui.Danger));
            }
        }
    }

    private static Control Render(CodexAccount account)
    {
        var panel = new StackPanel { Name = "CodexAccount", Spacing = 16 };
        var identity = new StackPanel { Name = "CodexAccountSection", Spacing = 4 };
        identity.Children.Add(Account.Heading("Account"));
        if (account.LoggedIn)
        {
            if (account.Email is { } email) identity.Children.Add(Account.Row("Email", email));
            identity.Children.Add(Account.Row(account.Plan is not null ? "Plan" : "Authentication", account.Plan ?? account.AuthMethod ?? "Unknown"));
        }
        else identity.Children.Add(Line("CodexAccountSignedOut", account.RequiresOpenaiAuth
            ? "Not signed in. Run codex login in a terminal."
            : "This provider does not require an OpenAI account.", Ui.Muted));
        panel.Children.Add(identity);

        var usage = new StackPanel { Name = "CodexUsageSection", Spacing = 8 };
        usage.Children.Add(Account.Heading("Usage"));
        if (account.UsageError is { } error) usage.Children.Add(Line("CodexUsageError", error, Ui.Danger));
        else if (account.Usage.Count == 0)
            usage.Children.Add(Line("CodexUsageEmpty", account.AuthMethod == "chatgpt"
                ? "No usage limits returned by Codex."
                : "Remaining usage limits are available for ChatGPT accounts.", Ui.Muted));
        else
            foreach (var window in account.Usage)
                usage.Children.Add(Account.UsageWindow(window.Id,
                    window.WindowDurationMins is { } minutes ? $"{window.Label} ({Duration(minutes)})" : window.Label,
                    window.UsedPercent, window.ResetsAt is { } at ? DateTimeOffset.FromUnixTimeSeconds(at) : null, "Codex"));
        if (account.UsageFetchedAt is { } fetched && DateTimeOffset.TryParse(fetched, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var reading))
            usage.Children.Add(Line("CodexUsageAge", Account.ReadingLabel("Codex", reading), Ui.Hint));
        panel.Children.Add(usage);

        if (account.Version is { } version)
        {
            var cli = new StackPanel { Name = "CodexCliSection", Spacing = 4 };
            cli.Children.Add(Account.Heading("Codex CLI"));
            cli.Children.Add(Account.Row("Version", version));
            panel.Children.Add(cli);
        }
        return panel;
    }
}