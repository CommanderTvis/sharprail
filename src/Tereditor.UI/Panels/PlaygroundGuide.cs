using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Tereditor.UI.Panels;

/// <summary>First steps in the Playground, shown on Welcome and in the Default workspace: get a project in, connect a model, then let an agent work on it.</summary>
internal static class PlaygroundGuide
{
    private const string AnthropicCreditsUrl = "https://platform.claude.com/docs/en/about-claude/api-credits-for-subscribers";
    private const string GoogleBenefitsUrl = "https://developers.google.com/program";

    public static Control Create(Action clone, Action providers, Action<string> openLink)
    {
        var steps = new StackPanel { Spacing = 8 };
        steps.Children.Add(Step("1.", "Bring in a project: clone a public repository over HTTPS, or start an empty one.", "Clone a repository", clone, "PlaygroundGuideClone"));
        steps.Children.Add(Step("2.", "Connect a model: paste an API key for Anthropic, OpenAI, Google or another provider.", "Open Model providers", providers, "PlaygroundGuideProviders"));
        steps.Children.Add(Step("3.", "Play: open the project folder, start a Pi chat and let the agent read, edit and commit, or use the terminal for git and file commands."));
        var credits = new StackPanel { Spacing = 4 };
        credits.Children.Add(Wrapped("Have a subscription? Claude Max and Team include monthly API credits: claim them, then create a key in the Claude Console. Google AI Pro and Ultra include Cloud credits for the Gemini API: activate them, then create a Gemini API key. A JetBrains AI licence token works too, but it expires after an hour. A Claude subscription sign-in is not supported here.", Ui.Muted, 12));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        links.Children.Add(Link("Claim Claude credits", () => openLink(AnthropicCreditsUrl), "PlaygroundGuideClaudeCredits"));
        links.Children.Add(Link("Activate Google credits", () => openLink(GoogleBenefitsUrl), "PlaygroundGuideGoogleCredits"));
        credits.Children.Add(links);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Ui.Text("Getting started in the Playground", Ui.TextBrush, 15));
        body.Children.Add(Explanation());
        body.Children.Add(steps);
        body.Children.Add(credits);
        return new Border
        {
            Name = "PlaygroundGuide",
            Background = Ui.Sidebar,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(16),
            MaxWidth = 520,
            Child = body
        };
    }

    /// <summary>What the Playground is and where everything lives; each line is what the browser client really does.</summary>
    private static Control Explanation()
    {
        var lines = new StackPanel { Spacing = 4 };
        lines.Children.Add(Ui.Text("Everything happens in this browser tab", Ui.TextBrush, 13));
        foreach (var line in new[]
        {
            "No server and no account. Your project, its git history and the terminal all run in this tab.",
            "Files, git history and API keys are saved in this browser's storage a moment after each change, so a reload keeps them. Clearing site data deletes them, and other browsers or devices do not see them.",
            "Keys go only to the model provider you chose.",
            "The terminal is a simulated shell with git, coreutils, grep, sed, awk and jq. There is no node or npm.",
            "Only Pi runs in a tab. Claude Code and Codex run on a desktop host, and a browser connected to one can use them freely."
        })
            lines.Children.Add(Wrapped(line, Ui.Muted, 13));
        return new Border { Name = "PlaygroundExplanation", Child = lines };
    }

    private static Button Link(string text, Action run, string name)
    {
        var label = Ui.Text(text, Ui.Accent, 13);
        label.TextDecorations = TextDecorations.Underline;
        var button = new Button
        {
            Name = name,
            Content = label,
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        button.Click += (_, _) => run();
        return button;
    }

    private static TextBlock Wrapped(string text, IBrush brush, double size)
    {
        var block = Ui.Text(text, brush, size);
        block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    private static Control Step(string number, string text, string? action = null, Action? run = null, string? name = null)
    {
        var column = new StackPanel { Spacing = 4 };
        column.Children.Add(Wrapped(text, Ui.Muted, 13));
        if (action is not null && run is not null)
        {
            column.Children.Add(Link(action, run, name!));
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 8 };
        var label = Ui.Text(number, Ui.Muted, 13);
        label.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(column, 1);
        row.Children.Add(label);
        row.Children.Add(column);
        return row;
    }
}