using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.ClaudeCode.UI;

/// <summary>
/// The terminal accessory row: while the tab runs Claude, its facts (directory, model and effort pickers, token usage,
/// plan), the IDE context switch and the attach button.
/// </summary>
internal sealed class ClaudeTerminalAccessory : StackPanel
{
    private const int PickerTailLines = 48;

    private readonly IPluginUIContext context;
    private readonly ClaudeCodeStore store;
    private readonly ClaudeGlyph glyph;
    private readonly ITerminalAccessoryApi terminal;
    private readonly ManualLaunchNotice launchNotice = new();
    private readonly WrapPanel facts = new() { Name = "TerminalAgentFacts", Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock driving = new() { Name = "TerminalDrivingNotice", IsVisible = false, FontSize = 11 };
    private string? drivingWhat;
    private string signature = "";
    private IDisposable? watch;
    private bool attached;
    private CancellationTokenSource? modelDiscovery;
    private long discoveryGeneration;

    public ClaudeTerminalAccessory(IPluginUIContext context, ClaudeCodeStore store, ClaudeGlyph glyph, ITerminalAccessoryApi terminal)
    {
        this.context = context; this.store = store; this.glyph = glyph; this.terminal = terminal;
        SetValue(FileDrop.AcceptsProperty, () => RunsClaude);
        SetValue(FileDrop.AttachProperty, paths => terminal.Write(string.Join(" ", paths.Select(path => "@" + TerminalFacts.QuoteAttachPath(TerminalFacts.AttachPath(path, terminal.WorkspaceId, State?.Cwd)))) + " "));
        Name = "ClaudeTerminalAccessory";
        Margin = new Thickness(8, 2);
        Spacing = 2;
        Children.Add(launchNotice);
        var line = new DockPanel();
        driving.Foreground = Ui.Muted;
        DockPanel.SetDock(driving, Dock.Right);
        line.Children.Add(driving);
        line.Children.Add(facts);
        Children.Add(line);
        AttachedToVisualTree += (_, _) =>
        {
            attached = true;
            store.Changed += StoreChanged;
            watch ??= context.WatchHost(host => (Agent(host)?.Kind, Agent(host)?.LaunchedByUi), (_, _) => Refresh());
            Refresh();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            attached = false;
            CancelModelDiscovery();
            store.Changed -= StoreChanged;
            watch?.Dispose();
            watch = null;
            terminal.SetKeyEncoding(TerminalKeyEncoding.Default);
        };
    }

    private TerminalAgentRecord? Agent(PluginHostProjection host) =>
        host.Terminals.GetValueOrDefault(terminal.WorkspaceId)?.FirstOrDefault(tab => tab.TabKey == terminal.TabKey)?.Agent;

    private bool RunsClaude => Agent(context.Host())?.Kind == "claude";

    private void StoreChanged(string workspaceId, string tabKey)
    {
        if (workspaceId == terminal.WorkspaceId && tabKey == terminal.TabKey) Refresh();
    }

    private ClaudeSessionState? State => store.Session(terminal.WorkspaceId, terminal.TabKey);

    // A model id is long and mostly prefix: the part that names it is what fits in a chip, the id itself on hover.
    internal static IReadOnlyList<(string Kind, string Label, string Title)> Facts(ClaudeSessionState? state)
    {
        if (state is null) return [];
        var facts = new List<(string, string, string)>();
        if (TerminalFacts.CwdLabel(state.Cwd) is { } directory) facts.Add(("cwd", directory, $"Claude started in {state.Cwd}"));
        if (state.Model is { } model) facts.Add(("model", System.Text.RegularExpressions.Regex.Replace(model.StartsWith("claude-", StringComparison.Ordinal) ? model[7..] : model, @"-\d{8}$", ""), model));
        if (state.Effort is { } effort) facts.Add(("effort", $"{effort} effort", "Reasoning effort"));
        return facts;
    }

    // The row rebuilds only when what it shows changes: rebuilding unchanged chips would close an open picker menu.
    private void Refresh()
    {
        var claude = RunsClaude;
        if (!claude) CancelModelDiscovery();
        launchNotice.IsVisible = claude && Agent(context.Host())?.LaunchedByUi == false;
        terminal.SetKeyEncoding(claude ? TerminalKeyEncoding.AgentNewline : TerminalKeyEncoding.Default);
        if (claude && store.IdeContext(terminal.WorkspaceId, terminal.TabKey) is null) store.SetIdeContext(terminal.WorkspaceId, terminal.TabKey, true);
        var state = State;
        var ide = store.IdeContext(terminal.WorkspaceId, terminal.TabKey) ?? true;
        var next = !claude ? "" : string.Join("\n", Facts(state).Select(fact => $"{fact.Kind}\t{fact.Label}\t{fact.Title}"))
            + $"\nusage:{state?.Usage}\nplan:{string.Join("|", state?.Todos?.Select(todo => $"{todo.Status}:{todo.Content}:{todo.ActiveForm}") ?? [])}\nide:{ide}";
        driving.IsVisible = drivingWhat is not null;
        driving.Text = drivingWhat is null ? "" : $"Driving Claude Code's {drivingWhat} picker…";
        if (next == signature) return;
        signature = next;
        facts.Children.Clear();
        facts.IsVisible = claude;
        if (!claude) return;
        foreach (var (kind, label, title) in Facts(state))
        {
            var chip = kind switch
            {
                "model" => TerminalFacts.PickerChip("model", label, $"{title} — click to switch", ModelItems),
                "effort" => TerminalFacts.PickerChip("effort", label, $"{title} — click to change", EffortItems),
                _ => TerminalFacts.FactChip(kind, label, title)
            };
            chip.Margin = new Thickness(0, 0, 4, 0);
            facts.Children.Add(chip);
        }
        if (state?.Usage is { } usage && TerminalFacts.UsageChip("Claude", new(usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite)) is { } spent)
        {
            spent.Margin = new Thickness(0, 0, 4, 0);
            facts.Children.Add(spent);
        }
        if (state?.Todos is { Count: > 0 } todos)
        {
            var plan = TerminalFacts.Plan("Claude", [.. todos.Select(todo => new TerminalTodo(todo.Content, todo.Status switch
            {
                AgentTodoStatus.Completed => TerminalTodoStatus.Completed,
                AgentTodoStatus.InProgress => TerminalTodoStatus.InProgress,
                _ => TerminalTodoStatus.Pending
            }, todo.ActiveForm))]);
            plan.Margin = new Thickness(0, 0, 4, 0);
            facts.Children.Add(plan);
        }
        var ideChip = TerminalFacts.IdeContextChip(ide, () =>
        {
            // Claude's own command, for this session only; the integration's configuration is untouched.
            var enabled = !(store.IdeContext(terminal.WorkspaceId, terminal.TabKey) ?? true);
            terminal.Write($"/ide {(enabled ? "on" : "off")}\r");
            store.SetIdeContext(terminal.WorkspaceId, terminal.TabKey, enabled);
        });
        ideChip.Margin = new Thickness(0, 0, 4, 0);
        facts.Children.Add(ideChip);
        facts.Children.Add(TerminalFacts.AttachButton("Put a file or folder in front of Claude, as @path",
            async () => await context.PickFileAsync(new FilePickOptions(terminal.WorkspaceId)),
            path => terminal.Write($"@{TerminalFacts.AttachPath(path, terminal.WorkspaceId, State?.Cwd)} "),
            error => context.Notify(PluginNotificationKind.Error, "Couldn't open the file picker", ClaudeParts.ErrorText(error))));
    }

    // A half-typed prompt would swallow the slash command, so the menu says so instead of driving.
    private IReadOnlyList<Control>? DraftNote()
    {
        if (ModelPicker.ComposerDraft(terminal.BufferTail(PickerTailLines, omitFaint: true)) is null) return null;
        var note = new MenuItem { Name = "TerminalMenuDraft", Header = "Send or clear what you typed first", IsEnabled = false };
        return [note];
    }

    private void CancelModelDiscovery()
    {
        if (modelDiscovery is not { } discovery) return;
        modelDiscovery = null;
        discoveryGeneration++;
        discovery.Cancel();
        drivingWhat = null;
    }

    private IReadOnlyList<Control> ModelItems()
    {
        if (DraftNote() is { } draft) return draft;
        var loading = new MenuItem { Header = "Reading Claude Code's models…", IsEnabled = false };
        if (drivingWhat is not null) { loading.Header = "Wait for the current picker"; return [loading]; }
        if (!attached || !RunsClaude) { loading.Header = "Claude Code is no longer active"; return [loading]; }
        drivingWhat = "model";
        var discovery = modelDiscovery = new CancellationTokenSource();
        var generation = ++discoveryGeneration;
        var token = discovery.Token;
        bool Current() => attached && generation == discoveryGeneration && ReferenceEquals(modelDiscovery, discovery) && RunsClaude;
        var io = new PickerIo(
            data => Dispatcher.UIThread.Invoke(() =>
            {
                if (data == "\u001b" && !Current()) return;
                if (!Current()) throw new OperationCanceledException(token);
                token.ThrowIfCancellationRequested();
                terminal.Write(data);
            }),
            omitFaint => Dispatcher.UIThread.Invoke(() =>
            {
                if (!Current()) throw new OperationCanceledException(token);
                return terminal.BufferTail(PickerTailLines, omitFaint);
            }), delay => Task.Delay(delay, token));
        _ = Task.Run(async () =>
        {
            IReadOnlyList<string> models = [];
            string? failure = null;
            try { (_, models) = await ModelPicker.DiscoverAsync(io); }
            catch (OperationCanceledException) { }
            catch (Exception error) { failure = error.Message; }
            Dispatcher.UIThread.Post(() =>
            {
                var current = Current();
                if (ReferenceEquals(modelDiscovery, discovery))
                {
                    modelDiscovery = null;
                    drivingWhat = null;
                }
                discovery.Dispose();
                if (!current) return;
                Refresh();
                if (loading.Parent is not ContextMenu menu || !menu.IsOpen) return;
                if (models.Count == 0)
                {
                    loading.Header = "Couldn't read models — open /model at Claude's prompt";
                    if (failure is not null) ToolTip.SetTip(loading, failure);
                    return;
                }
                menu.Items.Clear();
                foreach (var model in models)
                {
                    var item = new MenuItem { Header = model, Icon = glyph.Create(14) };
                    item.Click += (_, _) => { if (attached && RunsClaude) Drive("model", model, ModelPicker.DriveAsync); };
                    menu.Items.Add(item);
                }
            });
        });
        return [loading];
    }

    private IReadOnlyList<Control> EffortItems() => DraftNote() ?? [.. EffortPicker.Levels.Select(level =>
    {
        var item = new MenuItem { Name = "TerminalEffort_" + level, Header = level };
        item.Click += (_, _) => Drive("effort", level, EffortPicker.DriveAsync);
        return (Control)item;
    })];

    private void Drive(string what, string choice, Func<PickerIo, string, Task<PickerOutcome>> drive)
    {
        if (drivingWhat is not null) return;
        drivingWhat = what;
        Refresh();
        var io = new PickerIo(
            data => Dispatcher.UIThread.Invoke(() => terminal.Write(data)),
            omitFaint => Dispatcher.UIThread.Invoke(() => terminal.BufferTail(PickerTailLines, omitFaint)),
            Task.Delay);
        _ = Task.Run(async () =>
        {
            PickerOutcome? outcome = null;
            try { outcome = await drive(io, choice); }
            catch (Exception error) when (error is not OperationCanceledException) { }
            Dispatcher.UIThread.Post(() =>
            {
                drivingWhat = null;
                if (outcome == PickerOutcome.Switched)
                {
                    // Nothing reports an effort switch, so the chip takes the pick as soon as the slider confirms it.
                    if (what == "effort" && State is { } state)
                        store.Apply(new(terminal.WorkspaceId, terminal.TabKey, new AgentStatusReport("effort_switch") { Effort = choice }, state.Status));
                }
                else
                    context.Notify(PluginNotificationKind.Error, $"Couldn't switch the {what}", outcome switch
                    {
                        PickerOutcome.Draft => "Send or clear what you typed at Claude's prompt first.",
                        PickerOutcome.NoPicker => $"Claude Code didn't open its {what} picker — is the session waiting at its prompt?",
                        PickerOutcome.NotFound => $"The {what} picker didn't offer {choice}.",
                        _ => "The terminal stopped answering while the picker was open."
                    });
                Refresh();
            });
        });
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
            ClaudeCodeStatus.Running => Spinner(),
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

    // A turning arc, driven while attached; nothing keeps ticking for a tab that is gone.
    private static Control Spinner()
    {
        var rotation = new RotateTransform();
        var arc = new Arc { Width = 8, Height = 8, StartAngle = 0, SweepAngle = 270, Stroke = Ui.Muted, StrokeThickness = 2, RenderTransform = rotation };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(80) };
        timer.Tick += (_, _) => rotation.Angle = (rotation.Angle + 30) % 360;
        arc.AttachedToVisualTree += (_, _) => timer.Start();
        arc.DetachedFromVisualTree += (_, _) => timer.Stop();
        return arc;
    }
}