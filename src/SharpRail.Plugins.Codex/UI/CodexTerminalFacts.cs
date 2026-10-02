using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Codex.UI;

/// <summary>
/// A detected Codex terminal's accessory row: the model chip that switches this session's model through Codex's own
/// picker, token totals, the plan, the session-only IDE-context chip and the attach button. Ordinary terminals show
/// nothing. The row rebuilds only when what it shows changes, so an open menu survives unrelated pushes.
/// </summary>
public sealed class CodexTerminalFacts : ContentControl
{
    private const int PickerTailLines = 48;

    private sealed class TerminalIo(ITerminalAccessoryApi terminal) : ICodexTerminalIo
    {
        public void Write(string data) => terminal.Write(data);
        public Task DelayAsync(TimeSpan delay) => Task.Delay(delay);
        public IReadOnlyList<string> ReadLines(bool omitFaint = false) => terminal.BufferTail(PickerTailLines, omitFaint);
    }

    private readonly IPluginUIContext context;
    private readonly CodexStore store;
    private readonly ITerminalAccessoryApi terminal;
    private readonly Func<ITerminalAccessoryApi, bool> startsWithIdeContext;
    private readonly TerminalIo io;
    private IDisposable? watch;
    private bool isCodex;
    private bool driving;
    private bool ideCommandPending;
    private string shown = "";

    public CodexTerminalFacts(IPluginUIContext context, CodexStore store, ITerminalAccessoryApi terminal, Func<ITerminalAccessoryApi, bool> startsWithIdeContext)
    {
        this.context = context; this.store = store; this.terminal = terminal; this.startsWithIdeContext = startsWithIdeContext;
        io = new TerminalIo(terminal);
        Name = "CodexTerminalFacts";
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
    }

    private bool IsCodex(PluginHostProjection host) =>
        host.Terminals.GetValueOrDefault(terminal.WorkspaceId)?.FirstOrDefault(tab => tab.TabKey == terminal.TabKey)?.Agent?.Kind == CodexManifest.Id;

    private string? WorktreePath() =>
        context.Host().Workspaces.Values.SelectMany(list => list).FirstOrDefault(workspace => workspace.Id == terminal.WorkspaceId)?.Path;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        isCodex = IsCodex(context.Host());
        watch = context.WatchHost(IsCodex, (next, _) => { isCodex = next; Update(); });
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
                await CodexModelPicker.SubmitAsync(io, "/ide " + (enabled ? "on" : "off"));
                store.SetIdeContext(terminal.WorkspaceId, terminal.TabKey, enabled);
            }
            catch (Exception) { context.Notify(PluginNotificationKind.Error, "Couldn't change IDE context"); }
            finally { ideCommandPending = false; }
        }
    }

    private void Update()
    {
        if (isCodex && store.IdeContext(terminal.WorkspaceId, terminal.TabKey) is null)
        {
            store.SetIdeContext(terminal.WorkspaceId, terminal.TabKey, false);
            if (startsWithIdeContext(terminal)) SendIdeCommand(true);
            return;
        }
        var state = store.Session(terminal.WorkspaceId, terminal.TabKey);
        var ide = store.IdeContext(terminal.WorkspaceId, terminal.TabKey) ?? false;
        var signature = !isCodex ? "" : string.Join("\n", state?.Model, state?.Cwd, state?.Usage, state?.Plan is { } plan ? string.Join("|", plan) : null,
            ide, driving);
        if (signature == shown) return;
        shown = signature;
        Content = isCodex ? Row(state, ide) : null;
    }

    private Control Row(CodexSessionState? state, bool ide)
    {
        var row = new CodexTerminalRow();
        void Fill(string name, Control? chip)
        {
            var slot = row.FindControl<ContentControl>(name)!;
            if (chip is null) row.Children.Remove(slot);
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
            if (!driving) SendIdeCommand(!(store.IdeContext(terminal.WorkspaceId, terminal.TabKey) ?? false));
        }));
        Fill("Attach", TerminalFacts.AttachButton("Put a file or folder's path in front of Codex", async () => await context.PickFileAsync(),
            path => terminal.Write(TerminalFacts.AttachPath(path, WorktreePath(), store.Session(terminal.WorkspaceId, terminal.TabKey)?.Cwd) + " "),
            error => context.Notify(PluginNotificationKind.Error, "Couldn't open the file picker", error.Message)));
        var status = row.FindControl<TextBlock>("TerminalDrivingOverlay")!;
        if (driving) status.AttachedToVisualTree += (_, _) => status.Focus();
        else row.Children.Remove(status);
        return row;
    }

    private void ModelChip(CodexTerminalRow row, CodexSessionState? state)
    {
        var chip = row.FindControl<Button>("TerminalAgentFact")!;
        row.FindControl<TextBlock>("ModelLabel")!.Text = state?.Model ?? "Model";
        row.FindControl<ContentControl>("ModelArrow")!.Content = Ui.Icon("arrowDown", Ui.Muted, 12);
        chip.IsEnabled = !driving;
        ToolTip.SetTip(chip, $"{state?.Model ?? "Codex's model"} — click to switch");
        var menu = (ContextMenu)chip.ContextMenu!;
        IReadOnlyList<CodexModel>? shownModels = null;
        void Populate()
        {
            shownModels = store.Models;
            menu.Items.Clear();
            if (CodexModelPicker.ComposerDraft(io.ReadLines(omitFaint: true)) is not null)
            {
                menu.Items.Add(new MenuItem { Name = "TerminalMenuDraft", Header = "Send or clear what you typed first", IsEnabled = false });
                return;
            }
            foreach (var model in store.Models)
            {
                var item = new MenuItem { Name = "TerminalModel_" + model.Id, Header = model.Label, Icon = CodexGlyph.Gpt() };
                item.Click += (_, _) => SwitchModel(model.Id);
                menu.Items.Add(item);
            }
        }
        void RefreshModels()
        {
            if (menu.IsOpen && !ReferenceEquals(shownModels, store.Models)) Populate();
        }
        menu.Opening += (_, _) => Populate();
        menu.Opened += (_, _) => store.Changed += RefreshModels;
        menu.Closed += (_, _) => store.Changed -= RefreshModels;
        chip.DetachedFromVisualTree += (_, _) => { store.Changed -= RefreshModels; menu.Close(); };
        chip.Click += (_, _) => { Populate(); menu.Open(chip); };
    }

    private void SwitchModel(string model)
    {
        if (driving || ideCommandPending) return;
        driving = true;
        Update();
        _ = Drive();

        async Task Drive()
        {
            try
            {
                var outcome = await CodexModelPicker.DriveAsync(io, model);
                if (outcome == CodexModelPickerOutcome.Switched)
                {
                    store.Apply(new CodexStatusPush(terminal.WorkspaceId, terminal.TabKey,
                        store.Session(terminal.WorkspaceId, terminal.TabKey)?.Status ?? CodexStatus.Idle, "model_switch")
                    { Model = model });
                    return;
                }
                context.Notify(PluginNotificationKind.Error, "Couldn't switch the model", outcome switch
                {
                    CodexModelPickerOutcome.Draft => "Send or clear what you typed at Codex's prompt first.",
                    CodexModelPickerOutcome.NoPicker => "Codex didn't open its model picker — is the session waiting at its prompt?",
                    CodexModelPickerOutcome.NoSessionKey => "This Codex can only save a model as your default. Update Codex to 0.156 or newer to switch for one session.",
                    _ => $"Codex's model picker didn't offer {model}."
                });
            }
            catch (Exception)
            {
                context.Notify(PluginNotificationKind.Error, "Couldn't switch the model", "The terminal stopped answering while the picker was open.");
            }
            finally
            {
                driving = false;
                Update();
            }
        }
    }
}