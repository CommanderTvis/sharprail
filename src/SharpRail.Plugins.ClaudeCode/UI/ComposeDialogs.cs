using System.Text.RegularExpressions;

using Avalonia.Controls;
using Avalonia.Layout;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>An edit composed by a dialog, with the title its review dialog wears.</summary>
internal sealed record PendingEdit(ClaudeEdit Edit, string Title);

/// <summary>
/// The dialogs that describe a capability before anything is asked about where it goes: an MCP server, a skill, a hook
/// and a plugin. Each validates as it is typed and hands back the edit for the review dialog.
/// </summary>
internal static partial class ComposeDialogs
{
    private static async Task<PendingEdit?> ShowAsync(Window owner, string name, string title, double width, IEnumerable<Control> fields, string? note,
        Func<string?> problem, Func<PendingEdit> compose, Action<Action> watch)
    {
        var window = DialogWindow.Create(title, width);
        window.Tag = name + "Dialog";
        var panel = window.FindControl<StackPanel>("DialogFields")!;
        foreach (var field in fields) panel.Children.Add(field);
        if (note is not null) panel.Children.Add(ClaudeParts.Wrapped(note));
        var problemText = ClaudeParts.Wrapped("", Ui.Warning);
        problemText.Name = name + "Problem";
        panel.Children.Add(problemText);
        var actions = window.FindControl<StackPanel>("DialogActions")!;
        PendingEdit? result = null;
        actions.Children.Add(Ui.Button("Cancel", () => window.Close()));
        var submit = ClaudeParts.Primary(Ui.Button("Review the change", () => { }));
        submit.Name = name + "Continue";
        submit.Click += (_, _) =>
        {
            if (problem() is not null) return;
            result = compose();
            window.Close();
        };
        actions.Children.Add(submit);
        void Validate()
        {
            var reason = problem();
            problemText.Text = reason ?? "";
            problemText.IsVisible = reason is not null;
            submit.IsEnabled = reason is null;
        }
        watch(Validate);
        Validate();
        window.Opened += (_, _) => panel.Children.OfType<StackPanel>().SelectMany(field => field.Children.OfType<TextBox>()).FirstOrDefault()?.Focus();
        await window.ShowDialog(owner);
        return result;
    }

    private static IReadOnlyList<string> Lines(string? text) =>
        [.. (text ?? "").Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0)];

    // KEY=value for environment, Name: value for headers: the two shapes people already have to hand.
    private static Dictionary<string, string> Pairs(string? text, string separator)
    {
        var pairs = new Dictionary<string, string>();
        foreach (var line in Lines(text))
        {
            var at = line.IndexOf(separator, StringComparison.Ordinal);
            if (at <= 0) continue;
            pairs[line[..at].Trim()] = line[(at + separator.Length)..].Trim();
        }
        return pairs;
    }

    private static StackPanel Segments(string prefix, IReadOnlyList<(string Value, string Label)> options, Func<string> current, Action<string> choose)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        void Render()
        {
            row.Children.Clear();
            foreach (var (value, label) in options)
            {
                var segment = Ui.Segment(prefix + value, label);
                segment.IsChecked = current() == value;
                segment.Click += (_, _) => { choose(value); Render(); };
                row.Children.Add(segment);
            }
        }
        Render();
        return row;
    }

    public static Task<PendingEdit?> McpServerAsync(Window owner)
    {
        var name = ClaudeParts.Input("ClaudeMcpName", "git");
        var transport = ClaudeMcpTransport.Stdio;
        var command = ClaudeParts.Input("ClaudeMcpCommand", "uvx");
        var args = ClaudeParts.Input("ClaudeMcpArgs", "mcp-server-git\n--repository\n.", multiline: true);
        var url = ClaudeParts.Input("ClaudeMcpUrl", "https://example.com/mcp");
        var headers = ClaudeParts.Input("ClaudeMcpHeaders", "Authorization: Bearer …", multiline: true);
        var env = ClaudeParts.Input("ClaudeMcpEnv", "GIT_AUTHOR_NAME=you", multiline: true);
        var stdioFields = new StackPanel { Spacing = 12 };
        stdioFields.Children.Add(ClaudeParts.Field("Command", command));
        stdioFields.Children.Add(ClaudeParts.Field("Arguments", args, "One per line."));
        var remoteFields = new StackPanel { Spacing = 12, IsVisible = false };
        remoteFields.Children.Add(ClaudeParts.Field("URL", url));
        remoteFields.Children.Add(ClaudeParts.Field("Headers", headers, "One per line, as Name: value."));
        Action validate = () => { };
        var transports = Segments("ClaudeMcpTransport_", [("stdio", "Command"), ("http", "HTTP"), ("sse", "SSE")],
            () => transport.ToString().ToLowerInvariant(),
            value =>
            {
                transport = Enum.Parse<ClaudeMcpTransport>(value, ignoreCase: true);
                stdioFields.IsVisible = transport == ClaudeMcpTransport.Stdio;
                remoteFields.IsVisible = !stdioFields.IsVisible;
                validate();
            });
        string? Problem()
        {
            var server = name.Text?.Trim() ?? "";
            if (server.Length == 0) return "A name is needed.";
            if (server.Any(char.IsWhiteSpace)) return "A server name cannot contain spaces.";
            if (transport == ClaudeMcpTransport.Stdio && string.IsNullOrWhiteSpace(command.Text)) return "A command is needed.";
            if (transport != ClaudeMcpTransport.Stdio && string.IsNullOrWhiteSpace(url.Text)) return "A URL is needed.";
            return null;
        }
        PendingEdit Compose()
        {
            var draft = transport == ClaudeMcpTransport.Stdio
                ? new ClaudeMcpServerDraft(transport) { Command = command.Text!.Trim(), Args = Lines(args.Text) }
                : new ClaudeMcpServerDraft(transport) { Url = url.Text!.Trim(), Headers = Pairs(headers.Text, ":") };
            var server = name.Text!.Trim();
            return new(new McpAddEdit(server, draft with { Env = Pairs(env.Text, "=") }), $"Add the MCP server \"{server}\"");
        }
        return ShowAsync(owner, "ClaudeMcp", "Add an MCP server", 576,
            [ClaudeParts.Field("Name", name), ClaudeParts.Field("How it runs", transports), stdioFields, remoteFields, ClaudeParts.Field("Environment", env, "One per line, as KEY=value.")],
            "A secret typed here is written to a configuration file in the scope you choose next, and the diff you approve will show it. Prefer an environment variable the server reads for itself.",
            Problem, Compose,
            changed =>
            {
                validate = changed;
                foreach (var box in new[] { name, command, url }) box.TextChanged += (_, _) => changed();
            });
    }

    public static Task<PendingEdit?> SkillAsync(Window owner)
    {
        var name = ClaudeParts.Input("ClaudeSkillName", "reviewing-a-migration");
        var description = ClaudeParts.Input("ClaudeSkillDescription", "Use when a change moves data between schema versions.", multiline: true);
        description.FontFamily = Ui.InterfaceFont;
        string? Problem() =>
            string.IsNullOrWhiteSpace(name.Text) ? "A name is needed."
            : string.IsNullOrWhiteSpace(description.Text) ? "A description is needed — it is the whole of what Claude reads when deciding to use a skill."
            : null;
        return ShowAsync(owner, "ClaudeSkill", "Create a skill", 544,
            [ClaudeParts.Field("Name", name, "Becomes the directory name, lowercased and hyphenated."),
                ClaudeParts.Field("Description", description, "When Claude should reach for it, in one sentence.")],
            "This writes the skill's SKILL.md with its frontmatter and a heading. What it should actually do, you write in the file.",
            Problem,
            () => new(new SkillCreateEdit(name.Text!.Trim(), description.Text!.Trim()), $"Create the skill \"{name.Text!.Trim()}\""),
            changed => { name.TextChanged += (_, _) => changed(); description.TextChanged += (_, _) => changed(); });
    }

    // Only these events take a tool matcher; for the rest the field would be a control that does nothing.
    private static readonly ClaudeHookEvent[] MatchedEvents = [ClaudeHookEvent.PreToolUse, ClaudeHookEvent.PostToolUse];

    public static Task<PendingEdit?> HookAsync(Window owner)
    {
        var chosen = ClaudeHookEvent.PreToolUse;
        var events = new ComboBox { Name = "ClaudeHookEvent", ItemsSource = ClaudeValues.HookEvents.Select(item => item.ToString()).ToArray(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        var matcher = ClaudeParts.Input("ClaudeHookMatcher", "Edit|Write");
        var matcherField = ClaudeParts.Field("For which tools", matcher, "A regex over tool names; empty means every tool.");
        var command = ClaudeParts.Input("ClaudeHookCommand", "$CLAUDE_PROJECT_DIR/.claude/hooks/format.sh");
        events.SelectionChanged += (_, _) =>
        {
            chosen = ClaudeValues.HookEvents[Math.Max(0, events.SelectedIndex)];
            matcherField.IsVisible = MatchedEvents.Contains(chosen);
        };
        return ShowAsync(owner, "ClaudeHook", "Add a hook", 544,
            [ClaudeParts.Field("When it runs", events), matcherField, ClaudeParts.Field("Command", command)],
            "A hook is a shell command Claude Code runs on your machine, with your permissions, every time the event fires.",
            () => string.IsNullOrWhiteSpace(command.Text) ? "A command is needed." : null,
            () => new(new HookEdit(chosen, MatchedEvents.Contains(chosen) ? matcher.Text?.Trim() ?? "" : "", command.Text!.Trim()), $"Add a {chosen} hook"),
            changed => command.TextChanged += (_, _) => changed());
    }

    [GeneratedRegex(@"^[^/\s]+/[^/\s]+$")]
    private static partial Regex OwnerRepo();

    public static Task<PendingEdit?> PluginAsync(Window owner)
    {
        var kind = "github";
        var repo = ClaudeParts.Input("ClaudePluginRepo", "anthropics/claude-code");
        var path = ClaudeParts.Input("ClaudePluginPath");
        var marketplace = ClaudeParts.Input("ClaudePluginMarketplace", "claude-code-plugins");
        var plugin = ClaudeParts.Input("ClaudePluginName", "typescript-lsp");
        var repoField = ClaudeParts.Field("Repository", repo);
        var pathField = ClaudeParts.Field("Path", path, "An absolute path to the marketplace directory.");
        pathField.IsVisible = false;
        Action validate = () => { };
        var sources = Segments("ClaudePluginSource_", [("github", "GitHub"), ("directory", "Directory")], () => kind, value =>
        {
            kind = value;
            repoField.IsVisible = kind == "github";
            pathField.IsVisible = !repoField.IsVisible;
            validate();
        });
        string? Problem() =>
            string.IsNullOrWhiteSpace(marketplace.Text) ? "A marketplace name is needed — it is what the plugin's id is scoped by."
            : string.IsNullOrWhiteSpace(plugin.Text) ? "A plugin name is needed."
            : kind == "github" && !OwnerRepo().IsMatch(repo.Text?.Trim() ?? "") ? "A GitHub marketplace is owner/repo."
            : kind == "directory" && string.IsNullOrWhiteSpace(path.Text) ? "A path is needed."
            : null;
        PendingEdit Compose()
        {
            ClaudeMarketplaceSource source = kind == "github" ? new GithubMarketplaceSource(repo.Text!.Trim()) : new DirectoryMarketplaceSource(path.Text!.Trim());
            return new(new PluginAddEdit(marketplace.Text!.Trim(), source, plugin.Text!.Trim()), $"Add \"{plugin.Text!.Trim()}\" from \"{marketplace.Text!.Trim()}\"");
        }
        return ShowAsync(owner, "ClaudePlugin", "Add a plugin", 544,
            [ClaudeParts.Field("Where it comes from", sources), repoField, pathField,
                ClaudeParts.Field("Marketplace name", marketplace, "How the entry is keyed, and what scopes the plugin id."), ClaudeParts.Field("Plugin", plugin)],
            "A plugin brings its own hooks, skills and MCP servers, which run with your permissions. Claude Code fetches it on next start; this only records that you want it.",
            Problem, Compose,
            changed =>
            {
                validate = changed;
                foreach (var box in new[] { repo, path, marketplace, plugin }) box.TextChanged += (_, _) => changed();
            });
    }
}