using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>How one cell of the context map is drawn: a category's full or partial square, free space, or the autocompact reserve.</summary>
internal enum ContextCellKind { Full, Partial, Free, Reserved }

internal readonly record struct ContextCell(string Category, ContextCellKind Kind);

/// <summary>
/// The colour of each <c>/context</c> category, defined once. Claude Code paints them with terminal colours, so they come
/// from the theme's terminal palette (cyan, green, yellow, magenta) and its own roles (subtle grey, the orange of types),
/// and change with the theme like everything else.
/// </summary>
internal static class ContextPalette
{
    private static IBrush Ansi(int index, IBrush fallback) =>
        Ui.Theme?.Ansi is { } ansi && index < ansi.Count ? new SolidColorBrush(ansi[index]) : fallback;

    public static IBrush For(string category) => category switch
    {
        "MCP tools" => Ansi(6, Ui.Info),
        "MCP server instructions" => Ansi(2, Ui.Success),
        "Memory files" => Ui.Theme?.Syntax.TryGetValue("type", out var orange) == true ? new SolidColorBrush(orange) : Ui.Warning,
        "Skills" => Ansi(3, Ui.Warning),
        "Messages" => Ansi(13, Ui.Accent),
        "Custom agents" => Ansi(4, Ui.Info),
        _ => Ui.Hint
    };

    public static bool IsDeferred(string category) => category.EndsWith("(deferred)", StringComparison.Ordinal);
    public static bool IsFree(string category) => category == "Free space";
    public static bool IsReserved(string category) => category.StartsWith("Autocompact", StringComparison.Ordinal);
}

/// <summary>
/// The Context surface's measured part: the window map and legend of <c>claude -p /context</c> for a fresh session started
/// like the tab's, with the tab's live usage in the header, then the MCP tools and skills tables. Static layout lives in
/// the compiled AXAML; this fills it from the host's answer and redraws only when that answer changes.
/// </summary>
internal sealed partial class ClaudeContextUsageView : UserControl
{
    public const int Columns = 20, Rows = 10;

    private readonly UniformGrid grid;
    private readonly TextBlock model, modelId, header, autocompact, message;
    private readonly StackPanel categories, mcpRows, skillRows;
    private readonly Expander mcpTools, skills;
    private readonly Button retry;
    private object? shown;

    /// <summary>Raised when the user asks to measure again after a failure.</summary>
    public event Action? RetryRequested;

    public ClaudeContextUsageView()
    {
        AvaloniaXamlLoader.Load(this);
        grid = this.FindControl<UniformGrid>("ClaudeContextGrid")!;
        model = this.FindControl<TextBlock>("ClaudeContextModel")!;
        modelId = this.FindControl<TextBlock>("ClaudeContextModelId")!;
        header = this.FindControl<TextBlock>("ClaudeContextHeader")!;
        autocompact = this.FindControl<TextBlock>("ClaudeContextAutocompact")!;
        message = this.FindControl<TextBlock>("ClaudeContextMessage")!;
        categories = this.FindControl<StackPanel>("ClaudeContextCategories")!;
        mcpRows = this.FindControl<StackPanel>("ClaudeContextMcpRows")!;
        skillRows = this.FindControl<StackPanel>("ClaudeContextSkillRows")!;
        mcpTools = this.FindControl<Expander>("ClaudeContextMcpTools")!;
        skills = this.FindControl<Expander>("ClaudeContextSkills")!;
        retry = this.FindControl<Button>("ClaudeContextRetry")!;
        retry.Click += (_, _) => RetryRequested?.Invoke();
    }

    /// <summary>"15.1k", "1m" or "640", as Claude Code abbreviates token counts.</summary>
    public static string Tokens(long tokens)
    {
        static string Trim(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
        return tokens < 1_000 ? tokens.ToString(CultureInfo.InvariantCulture) : tokens < 1_000_000 ? Trim(tokens / 1_000.0) + "k" : Trim(tokens / 1_000_000.0) + "m";
    }

    /// <summary>"claude-opus-5-5" as "Opus 5.5"; an id it cannot read is shown as it is.</summary>
    public static string ModelName(string id)
    {
        var bare = id.Split('[')[0];
        var parts = bare.Split('-').Where(part => part != "claude" && !(part.Length == 8 && part.All(char.IsAsciiDigit))).ToArray();
        if (parts.Length == 0 || !parts[0].All(char.IsAsciiLetter)) return id;
        var version = string.Join('.', parts.Skip(1));
        return char.ToUpperInvariant(parts[0][0]) + parts[0][1..] + (version.Length > 0 ? " " + version : "");
    }

    /// <summary>
    /// The map's 10 × 20 cells, each half a percent of the window, in category order. A category
    /// smaller than a cell, or a remainder of at least half a cell, takes a partial cell; deferred tools take none (they
    /// are not loaded); free space fills what is left and the autocompact reserve sits at the end, as in the TUI.
    /// </summary>
    public static IReadOnlyList<ContextCell> Cells(ClaudeContextBreakdown breakdown)
    {
        const int total = Columns * Rows;
        double Share(ClaudeContextCategory category) => breakdown.Window > 0 ? category.Tokens * (double)total / breakdown.Window : 0;
        var cells = new List<ContextCell>();
        foreach (var category in breakdown.Categories)
        {
            if (ContextPalette.IsDeferred(category.Name) || ContextPalette.IsFree(category.Name) || ContextPalette.IsReserved(category.Name) || category.Tokens <= 0) continue;
            var share = Share(category);
            var full = (int)Math.Floor(share);
            for (var index = 0; index < full && cells.Count < total; index++) cells.Add(new(category.Name, ContextCellKind.Full));
            if ((full == 0 || share - full >= 0.5) && cells.Count < total) cells.Add(new(category.Name, ContextCellKind.Partial));
        }
        var reserve = breakdown.Categories.FirstOrDefault(category => ContextPalette.IsReserved(category.Name));
        var reserved = reserve is null ? 0 : Math.Min((int)Math.Round(Share(reserve)), total - cells.Count);
        var free = breakdown.Categories.FirstOrDefault(category => ContextPalette.IsFree(category.Name))?.Name ?? "Free space";
        while (cells.Count < total - reserved) cells.Add(new(free, ContextCellKind.Free));
        while (cells.Count < total) cells.Add(new(reserve!.Name, ContextCellKind.Reserved));
        return cells;
    }

    private static Control Swatch(ContextCellKind kind, IBrush color, double size = 10)
    {
        var host = new Grid { Width = size, Height = size, Margin = new Thickness(1) };
        switch (kind)
        {
            case ContextCellKind.Full:
                host.Children.Add(new Rectangle { Fill = color, RadiusX = 1.5, RadiusY = 1.5 });
                break;
            case ContextCellKind.Partial:
                host.Children.Add(new Rectangle { Fill = color, Opacity = 0.7, Width = size * 0.55, Height = size * 0.55, RadiusX = 1, RadiusY = 1 });
                break;
            case ContextCellKind.Free:
                host.Children.Add(new Rectangle { Stroke = Ui.Hint, StrokeThickness = 1, StrokeDashArray = [2, 1.5], RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0.5) });
                break;
            default:
                host.Children.Add(new Rectangle { Stroke = Ui.Hint, StrokeThickness = 1, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(0.5) });
                host.Children.Add(new Avalonia.Controls.Shapes.Path
                {
                    Stroke = Ui.Hint,
                    StrokeThickness = 1,
                    Data = Geometry.Parse($"M 2.5,2.5 L {size - 2.5},{size - 2.5} M {size - 2.5},2.5 L 2.5,{size - 2.5}")
                });
                break;
        }
        return host;
    }

    private static string Percent(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "%";

    /// <summary>Shows the host's answer and the tab's live usage; the same pair twice does nothing.</summary>
    public void Show(ClaudeContextUsage? usage, long? live)
    {
        var key = (usage, live, Ui.Theme);
        if (Equals(shown, key)) return;
        shown = key;
        var breakdown = usage?.Breakdown;
        message.Foreground = usage?.Error is not null ? Ui.Danger : Ui.Hint;
        message.Text = usage switch
        {
            null or { Running: true, Breakdown: null } => "Measuring with claude -p /context…",
            { Error: { } error } => "Could not measure the context: " + error,
            { Running: true } => "Measuring again…",
            { MeasuredAt: { } at } when DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.None, out var measured) =>
                $"A fresh session's baseline, measured at {measured.ToLocalTime():HH:mm}" + (live is not null ? "; the total is this tab's latest turn" : ""),
            _ => ""
        };
        retry.IsVisible = usage is { Error: not null, Running: false };
        grid.Children.Clear();
        categories.Children.Clear();
        mcpRows.Children.Clear();
        skillRows.Children.Clear();
        grid.IsVisible = breakdown is not null;
        model.Text = modelId.Text = header.Text = autocompact.Text = "";
        mcpTools.IsVisible = skills.IsVisible = false;
        if (breakdown is null) return;

        foreach (var cell in Cells(breakdown))
        {
            var swatch = Swatch(cell.Kind, ContextPalette.For(cell.Category));
            ToolTip.SetTip(swatch, cell.Category);
            grid.Children.Add(swatch);
        }
        model.Text = ModelName(breakdown.Model);
        modelId.Text = breakdown.Model;
        var used = live ?? breakdown.Used;
        header.Text = $"{Tokens(used)}/{Tokens(breakdown.Window)} tokens ({Percent(Math.Round(used * 100.0 / breakdown.Window))})";
        ToolTip.SetTip(header, live is not null ? "What this tab's latest turn sent" : "What a fresh session here starts with");
        foreach (var category in breakdown.Categories)
        {
            var kind = ContextPalette.IsFree(category.Name) ? ContextCellKind.Free : ContextPalette.IsReserved(category.Name) ? ContextCellKind.Reserved : ContextCellKind.Full;
            var deferred = ContextPalette.IsDeferred(category.Name);
            var row = new StackPanel { Name = "ClaudeContextCategory", Tag = category.Name, Orientation = Orientation.Horizontal, Spacing = 6, Opacity = deferred ? 0.6 : 1 };
            var swatch = Swatch(kind, ContextPalette.For(category.Name), 9);
            swatch.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(swatch);
            var amount = ContextPalette.IsFree(category.Name) ? Tokens(category.Tokens) : Tokens(category.Tokens) + " tokens";
            row.Children.Add(new TextBlock { Text = $"{category.Name}: {amount} ({Percent(category.Percent)})", TextWrapping = TextWrapping.NoWrap });
            if (deferred) ToolTip.SetTip(row, "Listed by name only; loaded when Claude searches for it, so it takes no space until then");
            categories.Children.Add(row);
        }
        autocompact.Text = $"Auto-compact window: {Tokens(breakdown.Window)} tokens";

        // MCP tools by server, the heaviest server first, each with its subtotal.
        foreach (var server in breakdown.McpTools.GroupBy(tool => tool.Source).OrderByDescending(group => group.Sum(tool => tool.Tokens)))
        {
            mcpRows.Children.Add(Row("ClaudeContextMcpServer", server.Key, server.Sum(tool => tool.Tokens), null, Ui.TextBrush));
            foreach (var tool in server.OrderByDescending(tool => tool.Tokens))
            {
                var name = tool.Name.StartsWith("mcp__" + server.Key + "__", StringComparison.Ordinal) ? tool.Name[(server.Key.Length + 7)..] : tool.Name;
                var row = Row("ClaudeContextMcpTool", name, tool.Tokens, null, Ui.Muted);
                row.Margin = new Thickness(12, 0, 0, 0);
                ToolTip.SetTip(row, tool.Name);
                mcpRows.Children.Add(row);
            }
        }
        mcpTools.IsVisible = breakdown.McpTools.Count > 0;
        mcpTools.Header = $"MCP tools · {Tokens(breakdown.McpTools.Sum(tool => tool.Tokens))} tokens";
        foreach (var skill in breakdown.Skills.OrderByDescending(skill => skill.Tokens))
            skillRows.Children.Add(Row("ClaudeContextSkill", skill.Name, skill.Tokens, skill.Source, Ui.Muted));
        skills.IsVisible = breakdown.Skills.Count > 0;
        skills.Header = $"Skills · {Tokens(breakdown.Skills.Sum(skill => skill.Tokens))} tokens";
    }

    private static DockPanel Row(string name, string label, long tokens, string? source, IBrush color)
    {
        var row = new DockPanel { Name = name, Tag = label };
        var amount = new TextBlock { Text = Tokens(tokens), FontFamily = Ui.CodeFont, Foreground = color, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(amount, Dock.Right);
        row.Children.Add(amount);
        if (source is not null)
        {
            var origin = new TextBlock { Text = source, Foreground = Ui.Hint, Margin = new Thickness(8, 0, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 160 };
            DockPanel.SetDock(origin, Dock.Right);
            row.Children.Add(origin);
        }
        row.Children.Add(new TextBlock { Text = label, Foreground = color, TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap });
        return row;
    }
}