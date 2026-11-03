using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace SharpRail.UI.Panels;

/// <summary>First steps in the Playground, shown on Welcome: get a project in, connect a model, then let an agent work on it.</summary>
internal static class PlaygroundGuide
{
    private const string AnthropicCreditsUrl = "https://platform.claude.com/docs/en/about-claude/api-credits-for-subscribers";
    private const string GoogleBenefitsUrl = "https://developers.google.com/program";

    public static Control Create(Action clone, Action providers, Action<string> openLink)
    {
        var steps = new StackPanel { Spacing = 8 };
        steps.Children.Add(Step("1.", "Bring in a project: clone a public repository over HTTPS, or start an empty one.", "Clone a repository", clone, "PlaygroundGuideClone"));
        steps.Children.Add(Step("2.", "Connect a model: paste an API key for Anthropic, OpenAI, Google or another provider. It stays in this browser.", "Open Model providers", providers, "PlaygroundGuideProviders"));
        steps.Children.Add(Step("3.", "Play: open the project folder, start a Pi chat and let the agent read, edit and commit, or use the terminal for git and shell commands."));
        var credits = new StackPanel { Spacing = 4 };
        credits.Children.Add(Wrapped("Have a subscription? Claude Max and Team include monthly API credits: claim them, then create a key in the Claude Console. Google AI Pro and Ultra include Cloud credits for the Gemini API: activate them, then create a Gemini API key. Then paste the key in Model providers. A Claude subscription sign-in is not supported here.", Ui.Muted, 12));
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        links.Children.Add(Ui.Button("Claim Claude credits", () => openLink(AnthropicCreditsUrl)));
        links.Children.Add(Ui.Button("Activate Google credits", () => openLink(GoogleBenefitsUrl)));
        credits.Children.Add(links);
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(Ui.Text("Getting started in the Playground", Ui.TextBrush, 15));
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
            var button = Ui.Button(action, run);
            button.Name = name;
            button.HorizontalAlignment = HorizontalAlignment.Left;
            column.Children.Add(button);
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