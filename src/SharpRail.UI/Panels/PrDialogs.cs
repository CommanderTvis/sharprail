using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Host.Abstractions;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Panels;

/// <summary>Problem is pushAuth, missing or unauthenticated; the compare link is offered when the host built one.</summary>
public sealed record PrSetup(string Problem, string CompareUrl = "");

public enum PrSetupChoice { Closed, Retry, Compare }

public static class PrDialogs
{
    /// <summary>
    /// Edits the title and description before anything is pushed. <paramref name="submit"/> answers whether the dialog
    /// may close; a failure keeps it open with the edits. Returns false when the user cancelled.
    /// </summary>
    public static async Task<bool> ComposeAsync(Window owner, PrDraft draft, bool titleEdited, bool asDraft, bool updating, Func<PrRequest, Task<bool>> submit)
    {
        var action = updating ? "Push updates" : asDraft ? "Open draft PR" : "Open PR";
        var window = Dialogs.Create(action, 640);
        window.Tag = "PrComposeDialog";
        var explanation = window.FindControl<TextBlock>("DialogExplanation")!;
        explanation.Text = updating
            ? "Review the refreshed description before pushing — it overwrites the open PR's."
            : "Review the title and description before the PR is created.";
        explanation.IsVisible = true;
        var fields = window.FindControl<StackPanel>("DialogFields")!;
        var title = new TextBox { Name = "PrComposeTitle", Text = draft.Title };
        var body = new TextBox
        {
            Name = "PrComposeBody",
            Text = draft.Body,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 240,
            FontFamily = Ui.CodeFont,
            VerticalContentAlignment = VerticalAlignment.Top
        };
        AutomationProperties.SetName(title, "Title");
        AutomationProperties.SetName(body, "Description");
        fields.Children.Add(Ui.Text("Title", size: 12)); fields.Children.Add(title);
        fields.Children.Add(Ui.Text("Description", size: 12)); fields.Children.Add(body);
        var busy = false;
        Button accept = null!;
        void Sync()
        {
            accept.IsEnabled = !busy && !string.IsNullOrWhiteSpace(title.Text);
            ((TextBlock)accept.Content!).Text = busy ? "Pushing…" : action;
        }
        accept = Dialogs.Primary(Ui.Button(action, async () =>
        {
            busy = true; Sync();
            bool close;
            try
            {
                close = await submit(new(title.Text ?? "", titleEdited || (title.Text ?? "").Trim() != draft.Title.Trim(), body.Text ?? "", asDraft));
            }
            finally { busy = false; Sync(); }
            if (close) window.Close(true);
        }));
        accept.Name = "PrComposeSubmit";
        title.TextChanged += (_, _) => Sync();
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        actions.Children.Add(Ui.Button("Cancel", () => window.Close(false)));
        actions.Children.Add(accept);
        // A push in flight cannot be abandoned by closing the dialog under it.
        window.Closing += (_, e) => e.Cancel = busy;
        window.Opened += (_, _) => { Sync(); title.Focus(); };
        return await window.ShowDialog<bool>(owner);
    }

    /// <summary>
    /// Explains a fixable setup gap with copyable commands. <paramref name="platform"/> is the host's (darwin, linux,
    /// win32) or null when unknown, which gets the generic commands rather than one platform's.
    /// </summary>
    public static async Task<PrSetupChoice> SetupAsync(Window owner, PrSetup setup, string? platform)
    {
        var pushAuth = setup.Problem == "pushAuth";
        var missing = setup.Problem == "missing";
        var window = Dialogs.Create(pushAuth ? "Git push couldn't authenticate" : missing ? "GitHub CLI isn't installed" : "GitHub CLI isn't signed in", 520);
        window.Tag = "PrSetupDialog";
        var explanation = window.FindControl<TextBlock>("DialogExplanation")!;
        explanation.Text = pushAuth
            ? "SharpRail pushes without a terminal, so SSH passphrase and credential prompts can't appear. Make your git auth non-interactive, then try again."
            : missing
                ? "The branch was pushed, but creating the PR directly needs the GitHub CLI on the host. Install it and sign in, then Open PR creates and updates PRs in one click."
                : "The branch was pushed, but the GitHub CLI on the host isn't signed in, so the PR can't be created directly. Sign in, then try again.";
        explanation.IsVisible = true;
        var fields = window.FindControl<StackPanel>("DialogFields")!;
        void Hint(string text)
        {
            var hint = Ui.Text(text);
            hint.TextWrapping = TextWrapping.Wrap;
            fields.Children.Add(hint);
        }
        void Command(string command)
        {
            var code = Ui.Text(command, Ui.TextBrush, 12);
            code.FontFamily = Ui.CodeFont;
            var copy = Ui.IconButton("file", "Copy command", () => _ = window.Clipboard?.SetTextAsync(command));
            copy.Name = "PrSetupCopy"; copy.Tag = command; copy.Width = 24; copy.Height = 24; copy.Padding = new Thickness(4);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
            Ui.Place(row, code); Ui.Place(row, copy, 0, 1);
            fields.Children.Add(new Border
            {
                Child = row,
                Padding = new Thickness(12, 4, 4, 4),
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4)
            });
        }
        if (pushAuth)
        {
            Hint(platform switch
            {
                "darwin" => "SSH remote: load your key into the agent once — Keychain remembers the passphrase.",
                "win32" => "SSH remote: load your key into the ssh-agent once (needs the OpenSSH Agent service).",
                _ => "SSH remote: load your key into the ssh-agent once."
            });
            Command(platform switch
            {
                "darwin" => "ssh-add --apple-use-keychain ~/.ssh/id_ed25519",
                "win32" => "ssh-add $env:USERPROFILE\\.ssh\\id_ed25519",
                _ => "ssh-add ~/.ssh/id_ed25519"
            });
            Hint("HTTPS remote: sign in with the GitHub CLI instead.");
            Command("gh auth login");
        }
        else
        {
            var install = platform switch { "darwin" => "brew install gh", "linux" => "sudo apt install gh", "win32" => "winget install --id GitHub.cli", _ => null };
            if (missing && platform is null) Hint("Install the GitHub CLI first — cli.github.com — then sign in:");
            if (missing && platform == "linux") Hint("apt is the Debian/Ubuntu route — use your distro's package manager otherwise.");
            if (missing && install is not null) Command(install);
            Command("gh auth login");
            if (setup.CompareUrl.Length > 0) Hint("No rush — GitHub's compare page can create this PR right now, prefilled with your description.");
        }
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        if (!pushAuth && setup.CompareUrl.Length > 0)
        {
            var compare = Ui.Button("Open compare page", () => window.Close(PrSetupChoice.Compare));
            compare.Name = "PrSetupCompare";
            actions.Children.Add(compare);
        }
        var retry = Dialogs.Primary(Ui.Button("Try again", () => window.Close(PrSetupChoice.Retry)));
        retry.Name = "PrSetupRetry";
        actions.Children.Add(retry);
        window.Opened += (_, _) => retry.Focus();
        return await window.ShowDialog<PrSetupChoice>(owner);
    }
}