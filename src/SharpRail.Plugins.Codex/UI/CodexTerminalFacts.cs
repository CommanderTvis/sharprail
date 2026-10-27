using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>
/// A detected Codex terminal's accessory row: the model chip that opens Codex's own /model picker, token totals, the plan,
/// the session-only IDE-context chip and the attach button. Ordinary terminals show nothing. The row rebuilds only when
/// what it shows changes.
/// </summary>
public sealed class CodexTerminalFacts : ContentControl
{
    private sealed class TerminalIo(ITerminalAccessoryApi terminal) : ICodexTerminalIo
    {
        public void Write(string data) => terminal.Write(data);
        public Task DelayAsync(TimeSpan delay) => Task.Delay(delay);
        public IDisposable HoldInput() => terminal.HoldInput();
    }

    private readonly IPluginUIContext context;
    private readonly CodexStore store;
    private readonly ITerminalAccessoryApi terminal;
    private readonly Func<ITerminalAccessoryApi, bool> startsWithIdeContext;
    private readonly TerminalIo io;
    private IDisposable? watch;
    private bool isCodex;
    private ManualLaunchNotice? launchNotice;
    private bool ideCommandPending;
    private string shown = "";

    public CodexTerminalFacts(IPluginUIContext context, CodexStore store, ITerminalAccessoryApi terminal, Func<ITerminalAccessoryApi, bool> startsWithIdeContext)
    {
        this.context = context; this.store = store; this.terminal = terminal; this.startsWithIdeContext = startsWithIdeContext;
        io = new TerminalIo(terminal);
        SetValue(FileDrop.AcceptsProperty, () => isCodex);
        SetValue(FileDrop.AttachProperty, paths => terminal.Write(string.Join(" ", paths.Select(path => TerminalFacts.QuoteAttachPath(TerminalFacts.AttachPath(path, WorktreePath(), store.Session(terminal.WorkspaceId, terminal.TabKey)?.Cwd)))) + " "));
        Name = "CodexTerminalFacts";
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    private TerminalAgentRecord? Agent(PluginHostProjection host) =>
        host.Terminals.GetValueOrDefault(terminal.WorkspaceId)?.FirstOrDefault(tab => tab.TabKey == terminal.TabKey)?.Agent;

    private string? WorktreePath() =>
        context.Host().Workspaces.Values.SelectMany(list => list).FirstOrDefault(workspace => workspace.Id == terminal.WorkspaceId)?.Path;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        isCodex = Agent(context.Host())?.Kind == CodexManifest.Id;
        watch = context.WatchHost(host => (Agent(host)?.Kind, Agent(host)?.LaunchedByUi), (next, _) =>
        {
            isCodex = next.Kind == CodexManifest.Id;
            Update();
        });
        store.Changed += Update;
        Update();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        watch?.Dispose();
        watch = null;
        store.Changed -= Update;
    }

    private void SendIdeCommand(bool enabled)
    {
        if (ideCommandPending) return;
        ideCommandPending = true;
        _ = Send();

        async Task Send()
        {
            try
            {
                await CodexTerminalCommands.SubmitAsync(io, "/ide " + (enabled ? "on" : "off"));
                store.SetIdeContext(terminal.WorkspaceId, terminal.TabKey, enabled);
            }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't change IDE context"); }
            finally { ideCommandPending = false; }
        }
    }

    private void Update()
    {
        if (launchNotice is not null) launchNotice.IsVisible = isCodex && Agent(context.Host())?.LaunchedByUi == false;
        if (isCodex && store.IdeContext(terminal.WorkspaceId, terminal.TabKey) is null)
        {
            store.SetIdeContext(terminal.WorkspaceId, terminal.TabKey, false);
            if (startsWithIdeContext(terminal)) SendIdeCommand(true);
            return;
        }
        var state = store.Session(terminal.WorkspaceId, terminal.TabKey);
        var ide = store.IdeContext(terminal.WorkspaceId, terminal.TabKey) ?? false;
        var signature = !isCodex ? "" : string.Join("\n", state?.Model, state?.Cwd, state?.Usage, state?.Plan is { } plan ? string.Join("|", plan) : null,
            ide, state?.Status);
        if (signature == shown) return;
        shown = signature;
        Content = isCodex ? Row(state, ide) : null;
    }

    private Control Row(CodexSessionState? state, bool ide)
    {
        var row = new CodexTerminalRow();
        var facts = row.FindControl<WrapPanel>("TerminalAgentFacts")!;
        launchNotice = row.FindControl<ManualLaunchNotice>("LaunchNotice")!;
        launchNotice.Name = "TerminalManualLaunchNotice";
        launchNotice.IsVisible = Agent(context.Host())?.LaunchedByUi == false;
        void Fill(string name, Control? chip)
        {
            var slot = row.FindControl<ContentControl>(name)!;
            if (chip is null) facts.Children.Remove(slot);
            else slot.Content = chip;
        }
        ModelChip(row, state);
        Fill("Usage", state?.Usage is { } usage ? TerminalFacts.UsageChip("Codex", new(usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite)) : null);
        Fill("Plan", state?.Plan is { Count: > 0 } plan
            ? TerminalFacts.Plan("Codex", [.. plan.Select(item => new TerminalTodo(item.Content, item.Status switch
            {
                CodexPlanStatus.Completed => TerminalTodoStatus.Completed,
                CodexPlanStatus.InProgress => TerminalTodoStatus.InProgress,
                _ => TerminalTodoStatus.Pending
            }))])
            : null);
        Fill("Ide", TerminalFacts.IdeContextChip(ide, () =>
        {
            SendIdeCommand(!(store.IdeContext(terminal.WorkspaceId, terminal.TabKey) ?? false));
        }));
        Fill("Attach", TerminalFacts.AttachButton("Put a file or folder's path in front of Codex", async () => await context.PickFileAsync(),
            path => terminal.Write(TerminalFacts.AttachPath(path, WorktreePath(), store.Session(terminal.WorkspaceId, terminal.TabKey)?.Cwd) + " "),
            error => context.Notify(PluginNotificationKind.Error, "Couldn't open the file picker", error.Message)));
        return row;
    }

    // Clicking only opens Codex's own /model picker; the user chooses inside it, and the chip keeps what Codex last reported.
    private void ModelChip(CodexTerminalRow row, CodexSessionState? state)
    {
        var chip = row.FindControl<Button>("TerminalAgentFact")!;
        row.FindControl<TextBlock>("ModelLabel")!.Text = state?.Model ?? "Model";
        var working = state?.Status == CodexStatus.Running;
        chip.IsEnabled = !working && !ideCommandPending;
        ToolTip.SetShowOnDisabled(chip, true);
        var what = state?.Model ?? "Codex's model";
        var title = working ? $"{what} (as last reported by Codex). Codex is working: open the model picker when it is at its prompt" : $"{what} (as last reported by Codex). Click to open Codex's /model picker";
        ToolTip.SetTip(chip, title);
        Avalonia.Automation.AutomationProperties.SetName(chip, "Open model picker");
        chip.Click += async (_, _) =>
        {
            if (ideCommandPending || store.Session(terminal.WorkspaceId, terminal.TabKey)?.Status == CodexStatus.Running) return;
            try
            {
                await CodexTerminalCommands.SubmitAsync(io, "/model");
                terminal.Focus();
            }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't open the model picker"); }
        };
    }
}