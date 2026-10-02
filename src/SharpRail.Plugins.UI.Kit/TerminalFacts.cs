using System.Globalization;

using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>A session's token spending, as its agent recorded it: never estimated here.</summary>
public sealed record TokenUsage(long Input, long Output, long CacheRead, long CacheWrite);

/// <summary>Where one item of an agent's own plan stands.</summary>
public enum TerminalTodoStatus { Pending, InProgress, Completed }

/// <summary>One item of an agent's own todo list, as the agent wrote it.</summary>
public sealed record TerminalTodo(string Content, TerminalTodoStatus Status, string? ActiveForm = null);

/// <summary>
/// The chips every terminal agent's accessory row shows: the fact chip (working directory, model, effort), token
/// usage, the agent's plan behind a done/total toggle, a session-only IDE context switch and the attach-file button.
/// The caller owns the agent, the commands and the state; nothing here holds terminal or plugin state.
/// </summary>
public static class TerminalFacts
{
    private const int CwdLabelMax = 40;

    /// <summary>A home-abbreviated directory whose middle, not its identifying end, is what a long path loses.</summary>
    public static string? CwdLabel(string? cwd)
    {
        if (string.IsNullOrEmpty(cwd)) return null;
        var abbreviated = ScopedSetting.AbbreviateHomePath(cwd);
        if (abbreviated.Length <= CwdLabelMax) return abbreviated;
        var segments = abbreviated.Split('/');
        var head = segments[0];
        return $"{head}/…/{string.Join('/', segments[^2..])}";
    }

    private static bool IsAbsolute(string path) =>
        path.StartsWith('/') || path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/';

    /// <summary>A picked path written the way the agent resolves it: relative to its directory when inside it, else absolute.</summary>
    public static string AttachPath(string path, string? worktreePath, string? cwd)
    {
        var absolute = IsAbsolute(path) ? path : worktreePath is { Length: > 0 } ? worktreePath + "/" + path : null;
        if (absolute is null) return path;
        if (string.IsNullOrEmpty(cwd)) return absolute;
        return absolute.StartsWith(cwd + "/", StringComparison.Ordinal) ? absolute[(cwd.Length + 1)..] : absolute;
    }

    /// <summary>Token counts with pi's compact thresholds: 999, 1.2k, 12k, 1.2M, 10M.</summary>
    public static string FormatTokens(long count) => count switch
    {
        < 1_000 => count.ToString(CultureInfo.InvariantCulture),
        < 10_000 => (count / 1_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "k",
        < 1_000_000 => Math.Round(count / 1_000.0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "k",
        < 10_000_000 => (count / 1_000_000.0).ToString("0.0", CultureInfo.InvariantCulture) + "M",
        _ => Math.Round(count / 1_000_000.0, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture) + "M"
    };

    /// <summary><c>↑in ↓out R cache-read W cache-write</c>, in pi's order, omitting zero fields.</summary>
    public static IReadOnlyList<string> TokenUsageParts(TokenUsage usage)
    {
        var parts = new List<string>();
        if (usage.Input != 0) parts.Add("↑" + FormatTokens(usage.Input));
        if (usage.Output != 0) parts.Add("↓" + FormatTokens(usage.Output));
        if (usage.CacheRead != 0) parts.Add("R" + FormatTokens(usage.CacheRead));
        if (usage.CacheWrite != 0) parts.Add("W" + FormatTokens(usage.CacheWrite));
        return parts;
    }

    private static Border Chip(Control content, string tooltip) => Tip(new Border
    {
        Background = Ui.Elevated,
        CornerRadius = new CornerRadius(4),
        Padding = new Thickness(4, 1),
        MaxWidth = 256,
        VerticalAlignment = VerticalAlignment.Center,
        Child = content
    }, tooltip);

    private static T Tip<T>(T control, string tooltip) where T : Control
    {
        ToolTip.SetTip(control, tooltip);
        AutomationProperties.SetName(control, tooltip);
        return control;
    }

    private static TextBlock Label(string text) => Ui.Text(text, Ui.Muted, 11);

    private static StackPanel Content(string? icon, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (icon is not null) row.Children.Add(Ui.Icon(icon, Ui.Muted, 12));
        row.Children.Add(Label(text));
        return row;
    }

    /// <summary>A read-only fact chip; <paramref name="kind"/> is <c>cwd</c>, <c>model</c>, <c>effort</c> or the caller's own.</summary>
    public static Control FactChip(string kind, string label, string title)
    {
        var chip = Chip(Content(kind == "cwd" ? "folder" : null, label), title);
        chip.Name = "TerminalAgentFact_" + kind;
        return chip;
    }

    /// <summary>A chip-shaped button, the trigger every interactive chip (pickers, toggles, attach) uses.</summary>
    public static Button ChipButton(string name, Control content, string tooltip, Action onClick)
    {
        var button = Tip(new Button
        {
            Name = name,
            Content = content,
            Padding = new Thickness(4, 1),
            MinHeight = 0,
            Background = Ui.Elevated,
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(4),
            VerticalAlignment = VerticalAlignment.Center
        }, tooltip);
        button.Resources["ButtonBackgroundPointerOver"] = Ui.Hover;
        button.Resources["ButtonBackgroundPressed"] = Ui.Hover;
        button.Click += (_, _) => onClick();
        return button;
    }

    /// <summary>A fact chip that opens a menu, such as a model or effort picker; the caller supplies the items when it opens.</summary>
    public static Button PickerChip(string kind, string label, string title, Func<IReadOnlyList<Control>> items)
    {
        var content = Content(null, label);
        content.Children.Add(Ui.Icon("arrowDown", Ui.Muted, 12));
        Button? button = null;
        button = ChipButton("TerminalAgentFact_" + kind, content, title, () =>
        {
            var menu = new ContextMenu { Name = "TerminalPicker_" + kind, Placement = PlacementMode.TopEdgeAlignedLeft, PlacementTarget = button };
            foreach (var item in items()) menu.Items.Add(item);
            button!.ContextMenu = menu;
            menu.Open(button);
        });
        return button;
    }

    /// <summary>The session's token spending: <c>↑in ↓out R cache-read W cache-write</c>, or nothing before any.</summary>
    public static Control? UsageChip(string agent, TokenUsage usage)
    {
        var parts = TokenUsageParts(usage);
        if (parts.Count == 0) return null;
        var chip = Chip(Label(string.Join(" · ", parts)),
            $"Tokens this {agent} session has spent, from its own transcript: ↑ input · ↓ output · R cache read · W cache write");
        chip.Name = "TerminalAgentFact_usage";
        return chip;
    }

    /// <summary>The controlled session-only <c>/ide</c> switch; the caller issues the command and owns the state.</summary>
    public static Button IdeContextChip(bool enabled, Action onToggle)
    {
        var button = ChipButton("TerminalIdeContextToggle", Content("code", enabled ? "IDE context on" : "IDE context off"),
            enabled ? "Stop sending IDE context to this session" : "Send IDE context to this session", onToggle);
        button.Tag = enabled;
        return button;
    }

    /// <summary>Asks for a file or folder and hands the picked path to <paramref name="onAttach"/>.</summary>
    public static Button AttachButton(string title, Func<Task<string?>> pickFile, Action<string> onAttach, Action<Exception> onError) =>
        ChipButton("TerminalAttachFile", Content("attachment", "attach file"), title, async () =>
        {
            try
            {
                if (await pickFile() is { Length: > 0 } path) onAttach(path);
            }
            catch (Exception error) when (error is not OperationCanceledException) { onError(error); }
        });

    /// <summary>The agent's own todo list behind a done/total toggle, named after the agent in its titles.</summary>
    public static Control Plan(string agent, IReadOnlyList<TerminalTodo> todos)
    {
        var done = todos.Count(todo => todo.Status == TerminalTodoStatus.Completed);
        var list = new StackPanel { Name = "TerminalPlan", Spacing = 4, Margin = new Thickness(8), MaxWidth = 448 };
        foreach (var todo in todos)
        {
            var (icon, brush) = todo.Status switch
            {
                TerminalTodoStatus.Completed => ("checkboxCircleFill", (IBrush)Ui.Success),
                TerminalTodoStatus.InProgress => ("loader", Ui.Accent),
                _ => ("checkboxBlankCircle", Ui.Muted)
            };
            var text = Ui.Text(todo.Status == TerminalTodoStatus.InProgress && todo.ActiveForm is { Length: > 0 } active ? active : todo.Content,
                todo.Status == TerminalTodoStatus.InProgress ? Ui.TextBrush : Ui.Muted, 12);
            text.TextWrapping = TextWrapping.Wrap;
            text.TextTrimming = TextTrimming.None;
            if (todo.Status == TerminalTodoStatus.Completed) { text.TextDecorations = TextDecorations.Strikethrough; text.Opacity = 0.7; }
            var row = new DockPanel { Name = "TerminalPlanItem", Tag = todo.Status };
            var glyph = Ui.Icon(icon, brush, 12);
            glyph.Margin = new Thickness(0, 2, 8, 0);
            glyph.VerticalAlignment = VerticalAlignment.Top;
            DockPanel.SetDock(glyph, Dock.Left);
            row.Children.Add(glyph);
            row.Children.Add(text);
            list.Children.Add(row);
        }
        var popup = new Popup
        {
            Placement = PlacementMode.TopEdgeAlignedLeft,
            IsLightDismissEnabled = true,
            Child = new Border
            {
                Background = Ui.Elevated,
                BorderBrush = Ui.BorderBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                MaxHeight = 360,
                Child = new ScrollViewer { Content = list }
            }
        };
        Button? toggle = null;
        toggle = ChipButton("TerminalPlanToggle", Content("listCheck", $"{done}/{todos.Count}"), $"Show {agent}'s plan", () =>
        {
            popup.IsOpen = !popup.IsOpen;
            ToolTip.SetTip(toggle!, popup.IsOpen ? $"Hide {agent}'s plan" : $"Show {agent}'s plan");
        });
        popup.PlacementTarget = toggle;
        popup.Closed += (_, _) => ToolTip.SetTip(toggle, $"Show {agent}'s plan");
        var host = new Panel();
        host.Children.Add(toggle);
        host.Children.Add(popup);
        return host;
    }
}