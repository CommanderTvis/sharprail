using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>
/// The terminal accessory row: while the tab runs Claude, its facts (directory, model and effort chips, token usage,
/// plan), the IDE context switch and the attach button.
/// </summary>
internal sealed class ClaudeTerminalAccessory : StackPanel
{
    private readonly IPluginUIContext context;
    private readonly ClaudeCodeStore store;
    private readonly ITerminalAccessoryApi terminal;
    private readonly ManualLaunchNotice launchNotice = new();
    private readonly WrapPanel facts = new() { Name = "TerminalAgentFacts", Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private string signature = "";
    private IDisposable? watch;

    public ClaudeTerminalAccessory(IPluginUIContext context, ClaudeCodeStore store, ITerminalAccessoryApi terminal)
    {
        this.context = context; this.store = store; this.terminal = terminal;
        SetValue(FileDrop.AcceptsProperty, () => RunsClaude);
        SetValue(FileDrop.AttachProperty, paths => terminal.Write(string.Join(" ", paths.Select(path => "@" + TerminalFacts.QuoteAttachPath(TerminalFacts.AttachPath(path, terminal.WorkspaceId, State?.Cwd)))) + " "));
        Name = "ClaudeTerminalAccessory";
        Margin = new Thickness(8, 0);
        Children.Add(launchNotice);
        Children.Add(facts);
        AttachedToVisualTree += (_, _) =>
        {
            store.Changed += StoreChanged;
            watch ??= context.WatchHost(host => (Agent(host)?.Kind, Agent(host)?.LaunchedByUi), (_, _) => Refresh());
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            store.Changed -= StoreChanged;
            watch?.Dispose();
            watch = null;
            terminal.SetKeyEncoding(TerminalKeyEncoding.Default);
        };
    }

    private TerminalAgentRecord? Agent(PluginHostProjection host) =>
        host.Terminals.GetValueOrDefault(terminal.WorkspaceId)?.FirstOrDefault(tab => tab.TabKey == terminal.TabKey)?.Agent;

    private bool RunsClaude => Agent(context.Host())?.Kind == "claude";

    // Clicking only opens Claude Code's own picker; the user chooses inside the TUI, and the chip keeps what hooks report.
    private Button OpenerChip(string kind, string label, string title, ClaudeSessionState? state, string command, string what)
    {
        var working = state?.Status == ClaudeCodeStatus.Running;
        var chip = TerminalFacts.ActionFactChip(kind, label, working ? $"{title}. Claude is working: open the {what} picker when it is at its prompt" : $"{title}. Click to open Claude Code's {command} picker", () =>
        {
            if (RunsClaude) _ = OpenAsync(command);
        });
        chip.IsEnabled = !working;
        ToolTip.SetShowOnDisabled(chip, true);
        Avalonia.Automation.AutomationProperties.SetName(chip, $"Open {what} picker");
        return chip;
    }

    // The click handler only starts the screen read; typing happens after it, if the prompt is empty and the tab is unchanged.
    private async Task OpenAsync(string command)
    {
        var tab = terminal.TabKey;
        if (await TypeAsync(command + "\r", () => RunsClaude && terminal.TabKey == tab)) terminal.Focus();
    }

    private Task<bool> TypeAsync(string text, Func<bool> stillCurrent) =>
        TypeIfPromptEmptyAsync(terminal, text, State?.Status, stillCurrent, notice => context.Notify(PluginNotificationKind.Info, notice));

    private static readonly System.Text.RegularExpressions.Regex PickerRow = new(@"^\s*❯\s*\d+\.\s+", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    private static readonly System.Text.RegularExpressions.Regex Placeholder = new(@"^Message @.+(?:…|\.\.\.)$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>What Claude Code's prompt box shows on these screen rows; its own empty-prompt placeholder is <c>Message @agent…</c>.</summary>
    internal static PromptState Prompt(IReadOnlyList<string> lines) => PromptGuard.Classify(lines, "❯", PickerRow, text => Placeholder.IsMatch(text));

    /// <summary>Types a command only into an empty prompt, once: not over a draft, not on an unknown screen, never over a running turn.</summary>
    internal static async Task<bool> TypeIfPromptEmptyAsync(ITerminalAccessoryApi terminal, string text, ClaudeCodeStatus? status, Func<bool> stillCurrent, Action<string> refuse)
    {
        if (status == ClaudeCodeStatus.Running) return false;
        if (!await PromptGuard.MayTypeAsync(terminal.ReadScreenTailAsync, Prompt, stillCurrent, refuse)) return false;
        terminal.Write(text);
        return true;
    }

    private bool idePending;

    private async Task SendIdeAsync(bool enabled)
    {
        var tab = terminal.TabKey;
        try
        {
            if (await TypeAsync($"/ide {(enabled ? "on" : "off")}\r", () => RunsClaude && terminal.TabKey == tab))
                store.SetIdeContext(terminal.WorkspaceId, terminal.TabKey, enabled);
        }
        finally { idePending = false; }
    }

    private void StoreChanged(string workspaceId, string tabKey)
    {
        if (workspaceId == terminal.WorkspaceId && tabKey == terminal.TabKey) Refresh();
    }

    private ClaudeSessionState? State => store.Session(terminal.WorkspaceId, terminal.TabKey);

    // A model id is long and mostly prefix: the part that names it is what fits in a chip, the id itself on hover.
    internal static IReadOnlyList<(string Kind, string Label, string Title)> Facts(ClaudeSessionState? state, string? command = null)
    {
        if (state is null) return [];
        var facts = new List<(string, string, string)>();
        // The launch choices hooks do not report are read off the recorded command line.
        var words = (command ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var advisorAt = Array.IndexOf(words, "--advisor");
        if (advisorAt >= 0 && advisorAt + 1 < words.Length) facts.Add(("advisor", $"advisor {words[advisorAt + 1].Trim('\'')}", "Started with --advisor"));
        if (words.Contains("--chrome")) facts.Add(("chrome", "chrome", "Started with Claude in Chrome (--chrome)"));
        if (TerminalFacts.CwdLabel(state.Cwd) is { } directory) facts.Add(("cwd", directory, $"Claude started in {state.Cwd}"));
        // Read-only: Claude Code's hooks report the model and effort; SharpRail never types a command to change them.
        if (state.Model is { } model) facts.Add(("model", System.Text.RegularExpressions.Regex.Replace(model.StartsWith("claude-", StringComparison.Ordinal) ? model[7..] : model, @"-\d{8}$", ""), $"{model} — as last reported by Claude Code's hooks"));
        if (state.Effort is { } effort) facts.Add(("effort", $"{effort} effort", "Reasoning effort as last reported by Claude Code's hooks"));
        return facts;
    }

    // The chip row, left to right: folder, model, effort, IDE context, then the conditional chips the user did not place
    // (Chrome, the plan), then spending, advisor and attach.
    private static readonly string[] ChipOrder = ["cwd", "model", "effort", "ide", "chrome", "plan", "usage", "advisor", "attach"];

    internal static IReadOnlyList<string> RowOrder(IEnumerable<string> present) => [.. ChipOrder.Where(present.ToHashSet().Contains)];

    // The row rebuilds only when what it shows changes.
    private void Refresh()
    {
        var claude = RunsClaude;
        launchNotice.IsVisible = claude && Agent(context.Host())?.LaunchedByUi == false;
        terminal.SetKeyEncoding(claude ? TerminalKeyEncoding.AgentNewline : TerminalKeyEncoding.Default);
        if (claude && store.IdeContext(terminal.WorkspaceId, terminal.TabKey) is null) store.SetIdeContext(terminal.WorkspaceId, terminal.TabKey, true);
        var state = State;
        var ide = store.IdeContext(terminal.WorkspaceId, terminal.TabKey) ?? true;
        var next = !claude ? "" : string.Join("\n", Facts(state, Agent(context.Host())?.Command).Select(fact => $"{fact.Kind}\t{fact.Label}\t{fact.Title}"))
            + $"\nstatus:{state?.Status}\nusage:{state?.Usage}\nplan:{string.Join("|", state?.Todos?.Select(todo => $"{todo.Status}:{todo.Content}:{todo.ActiveForm}") ?? [])}\nide:{ide}";
        if (next == signature) return;
        signature = next;
        facts.Children.Clear();
        facts.IsVisible = claude;
        if (!claude) return;
        var chips = new Dictionary<string, Control>();
        foreach (var (kind, label, title) in Facts(state, Agent(context.Host())?.Command))
        {
            chips[kind] = kind switch
            {
                "model" => OpenerChip(kind, label, title, state, "/model", "model"),
                "effort" => OpenerChip(kind, label, title, state, "/effort", "effort"),
                "chrome" => TerminalFacts.FactChip(kind, label, title, Ui.Icon("chrome", Ui.Muted, 12)),
                _ => TerminalFacts.FactChip(kind, label, title)
            };
        }
        if (state?.Usage is { } usage && TerminalFacts.UsageChip("Claude", new(usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite)) is { } spent) chips["usage"] = spent;
        if (state?.Todos is { Count: > 0 } todos)
        {
            chips["plan"] = TerminalFacts.Plan("Claude", [.. todos.Select(todo => new TerminalTodo(todo.Content, todo.Status switch
            {
                AgentTodoStatus.Completed => TerminalTodoStatus.Completed,
                AgentTodoStatus.InProgress => TerminalTodoStatus.InProgress,
                _ => TerminalTodoStatus.Pending
            }, todo.ActiveForm))]);
        }
        chips["ide"] = TerminalFacts.IdeContextChip(ide, () =>
        {
            // Claude's own command, for this session only; the integration's configuration is untouched.
            if (idePending) return;
            var enabled = !(store.IdeContext(terminal.WorkspaceId, terminal.TabKey) ?? true);
            idePending = true;
            _ = SendIdeAsync(enabled);
        });
        chips["attach"] = TerminalFacts.AttachButton("Put a file or folder in front of Claude, as @path",
            async () => await context.PickFileAsync(new FilePickOptions(terminal.WorkspaceId)),
            path => terminal.Write($"@{TerminalFacts.AttachPath(path, terminal.WorkspaceId, State?.Cwd)} "),
            error => context.Notify(PluginNotificationKind.Error, "Couldn't open the file picker", ClaudeParts.ErrorText(error)));
        foreach (var name in RowOrder(chips.Keys))
        {
            chips[name].Margin = Chips.Gap;
            facts.Children.Add(chips[name]);
        }
    }
}

/// <summary>The status a Claude terminal's tab wears beside its title: a spinner while running, a dot for the rest.</summary>
internal sealed class ClaudeStatusAdornment : ContentControl
{
    private readonly ClaudeCodeStore store;
    private readonly string workspaceId;
    private readonly string tabKey;
    private ClaudeCodeStatus? shown;
    private bool rendered;

    public ClaudeStatusAdornment(ClaudeCodeStore store, string workspaceId, string tabKey)
    {
        this.store = store; this.workspaceId = workspaceId; this.tabKey = tabKey;
        Name = "TerminalClaudeCodeStatus";
        VerticalAlignment = VerticalAlignment.Center;
        Margin = new Thickness(4, 0, 0, 0);
        AttachedToVisualTree += (_, _) => { store.Changed += Changed; Update(); };
        DetachedFromVisualTree += (_, _) => store.Changed -= Changed;
        Update();
    }

    private void Changed(string workspace, string tab)
    {
        if (workspace == workspaceId && tab == tabKey) Update();
    }

    private void Update()
    {
        var status = store.Session(workspaceId, tabKey)?.Status;
        if (rendered && status == shown) return;
        rendered = true;
        shown = status;
        Tag = status;
        IsVisible = status is ClaudeCodeStatus.Running or ClaudeCodeStatus.Blocked or ClaudeCodeStatus.Done or ClaudeCodeStatus.Failed;
        Content = status switch
        {
            ClaudeCodeStatus.Running => new ClaudeSpinner(),
            ClaudeCodeStatus.Blocked => Dot(Ui.Warning, "Claude needs your input"),
            ClaudeCodeStatus.Done => Dot(Ui.Success, "Claude finished"),
            ClaudeCodeStatus.Failed => Dot(Ui.Danger, "Claude hit an error"),
            _ => null
        };
    }

    private static Control Dot(IBrush brush, string label)
    {
        var dot = new Ellipse { Width = 8, Height = 8, Fill = brush };
        ToolTip.SetTip(dot, label);
        Avalonia.Automation.AutomationProperties.SetName(dot, label);
        return dot;
    }
}