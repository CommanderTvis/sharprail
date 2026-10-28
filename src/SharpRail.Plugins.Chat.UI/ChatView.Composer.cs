using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Chat.UI;

internal sealed partial class ChatView
{
    private static readonly Dictionary<(string, string), string> Drafts = [];
    internal static readonly Dictionary<(string, string), List<string>> Histories = [];

    private TextBox input = null!;
    private Button send = null!;
    private Popup menu = null!;
    private ListBox menuList = null!;
    private StackPanel attachmentRow = null!;
    private readonly List<ChatAttachment> attachments = [];
    private CancellationTokenSource? mentionSearch;
    private int historyAt = -1;
    // The draft typed before the first Up, put back when Down leaves history; recalling is set while history writes the box.
    private string? draftStash;
    private bool recalling;
    private ChatStatus status = ChatStatus.Starting;

    internal TextBox Input => input;
    internal ListBox Menu => menuList;
    internal bool MenuOpen => menu.IsOpen;
    internal Button SendButton => send;

    private sealed record MenuEntry(string Label, string Detail, string Insert, int From, int To)
    {
        /// <summary>A section title in the slash menu: shown, never chosen.</summary>
        public bool IsHeader { get; init; }
        /// <summary>For a built-in action: what choosing it does instead of inserting text.</summary>
        public Action? Run { get; init; }
        /// <summary>A directory in path completion: choosing it inserts the path and opens the next level.</summary>
        public bool Continue { get; init; }
        public override string ToString() => Detail.Length == 0 ? Label : Label + "  —  " + Detail;
    }

    // Laid out as upstream's composer: queued messages above a bordered shell whose input sits over a footer row (pickers, attach and
    // Skills at the left, session stats between, history, Stop and Send at the right); the cwd, IDE and MCP facts sit under the shell.
    /// <summary>The size of the round Send and Stop buttons: also the one-line height of the input, so the shell's insets around both are equal.</summary>
    internal const double SendSize = 28;

    private Control BuildComposer()
    {
        input = new TextBox
        {
            Name = "ChatInput",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = SendSize,
            MaxHeight = 200,
            Padding = new Thickness(2, 4),
            FontSize = 13,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(0),
            PlaceholderText = $"Ask {env.Agent.Short} to edit…",
            PlaceholderForeground = Ui.Muted,
            Text = Drafts.GetValueOrDefault((workspaceId, chatId), "")
        };
        Avalonia.Automation.AutomationProperties.SetName(input, $"Message {env.Agent.Short}");
        send = new Button { Name = "ChatSend", Padding = new Thickness(12, 6), VerticalAlignment = VerticalAlignment.Center };
        send.Classes.Add("primary");
        send.Click += (_, _) => _ = Submit();
        sendMore = new Button { Name = "ChatSendMenu", Content = Ui.Icon("arrowDown", Ui.Muted, 12), Padding = new Thickness(4, 6), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Background = Brushes.Transparent };
        Avalonia.Automation.AutomationProperties.SetName(sendMore, "Send options");
        sendMore.Click += (_, _) => ShowSendMenu();
        stop = new Button { Name = "ChatStop", Padding = new Thickness(10, 6), VerticalAlignment = VerticalAlignment.Center, IsVisible = false, Content = Ui.Text("Stop", Ui.Danger, 13) };
        Avalonia.Automation.AutomationProperties.SetName(stop, "Interrupt the turn");
        stop.Click += (_, _) => _ = Interrupt();
        menuList = MenuList("ChatMenu");
        menuList.PointerReleased += (_, _) => AcceptMenu();
        menuList.ContainerPrepared += (_, e) =>
        {
            if (e.Container is ListBoxItem { DataContext: MenuEntry { IsHeader: true } } header) { header.IsEnabled = false; header.Focusable = false; }
        };
        menuFrame = ChatPopup.Frame("ChatMenuFrame", menuList);
        menu = new Popup
        {
            Name = "ChatMenuPopup",
            PlacementTarget = input,
            Placement = PlacementMode.Top,
            IsLightDismissEnabled = false,
            Child = menuFrame
        };
        attachmentRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, IsVisible = false };
        plusBadge = new Border { Name = "ChatPlusBadge", Width = 6, Height = 6, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, IsVisible = false, IsHitTestVisible = false };
        var plusContent = new Grid { Width = 16, Height = 16 };
        plusContent.Children.Add(Ui.Icon("add", Ui.Muted, 14));
        plusContent.Children.Add(plusBadge);
        plus = TerminalFacts.ChipButton("ChatPlus", plusContent, "Conversations, skills, commands, IDE context, MCP servers", () =>
        {
            var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft };
            flyout.Content = BuildPlusMenu(flyout);
            ChatPopup.Show(flyout, plus!);
        });
        Avalonia.Automation.AutomationProperties.SetName(plus, "Add to the chat");
        // One small usage chip: the context pie, and a popover with the session's tokens, cost and context meter.
        pie = TerminalFacts.ChipButton("ChatContextPie", new PieGlyph(), "Session usage", ShowUsage);
        pie.IsVisible = false;
        mentionRow = new WrapPanel { Name = "ChatMentions", Orientation = Orientation.Horizontal, IsVisible = false, Margin = new Thickness(8, 6, 8, 0) };
        history = Ui.IconButton("search", "Search history", ShowHistory);
        history.Name = "ChatHistory";
        queue = new StackPanel { Name = "ChatQueue", Spacing = 2, IsVisible = false, Margin = new Thickness(4, 0, 4, 4) };
        working = new TextBlock { Name = "ChatWorking", FontSize = 11, Foreground = Ui.Accent, VerticalAlignment = VerticalAlignment.Center, IsVisible = false };

        // Inside the box, at the right of the input: the working time, history, and the round Stop and Send (Queue is the chevron after them).
        // One height for all of them, so none sets the row's height and all are centred in it.
        foreach (var control in new Button[] { history, stop, send, sendMore })
        {
            control.Height = SendSize; control.VerticalAlignment = VerticalAlignment.Center; control.VerticalContentAlignment = VerticalAlignment.Center; control.HorizontalContentAlignment = HorizontalAlignment.Center;
            control.Padding = new Thickness(control.Padding.Left, 0);
        }
        foreach (var round in new Button[] { stop, send })
        {
            round.Width = SendSize; round.MinWidth = 0; round.Padding = new Thickness(0); round.CornerRadius = new CornerRadius(SendSize / 2); round.BorderThickness = new Thickness(0);
        }
        stop.Background = Ui.Hover;
        foreach (var plain in new Button[] { history, sendMore }) { plain.Background = Brushes.Transparent; plain.BorderThickness = new Thickness(0); plain.CornerRadius = new CornerRadius(SendSize / 2); }
        history.Width = SendSize; history.MinWidth = 0; history.Padding = new Thickness(0);
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.Gap, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(working);
        right.Children.Add(history);
        right.Children.Add(sendMore);
        right.Children.Add(stop);
        right.Children.Add(send);
        inputControls = right;
        var inputRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = Space.Gap, Name = "ChatInputRow" };
        input.VerticalAlignment = VerticalAlignment.Center;
        inputRow.Children.Add(input);
        Grid.SetColumn(right, 1);
        inputRow.Children.Add(right);
        // The chips that are content (the selection, attachments, mentions) sit above the input inside the box.
        var content = new StackPanel { Name = "ChatContext", Orientation = Orientation.Horizontal, Spacing = Space.Gap, Margin = new Thickness(0, 0, 0, 4), IsVisible = false };
        contextRow = content;
        content.Children.Add(selectionRow);
        content.Children.Add(attachmentRow);
        pasteNote = new TextBlock { Name = "ChatPasteNote", FontSize = 11, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap, IsVisible = false, Margin = new Thickness(2, 4, 2, 0) };
        mentionRow.Margin = new Thickness(0, 0, 0, 4);
        var shellBody = new StackPanel();
        shellBody.Children.Add(content);
        shellBody.Children.Add(mentionRow);
        shellBody.Children.Add(inputRow);
        shellBody.Children.Add(pasteNote);
        shellBody.Children.Add(menu);
        var shell = new Border
        {
            Name = "ChatComposerShell",
            Background = ChatPopup.Raised,
            BorderBrush = Ui.ControlBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 10, 10, 10),
            Child = shellBody
        };
        // The shell is the one bubble: the input sits on it with no border, fill or radius in any state, and focus shows as a ring on the shell.
        static Style Bare(Func<Selector, Selector> state) => new(x => state(x.OfType<TextBox>().Name("ChatInput")).Template().OfType<Border>().Name("PART_BorderElement"))
        {
            Setters =
            {
                new Setter(Border.BackgroundProperty, Brushes.Transparent), new Setter(Border.BorderBrushProperty, Brushes.Transparent),
                new Setter(Border.BorderThicknessProperty, new Thickness(0)), new Setter(Border.CornerRadiusProperty, new CornerRadius(0))
            }
        };
        shell.Styles.Add(Bare(box => box));
        shell.Styles.Add(Bare(box => box.Class(":pointerover")));
        shell.Styles.Add(Bare(box => box.Class(":focus")));
        shell.Styles.Add(Bare(box => box.Class(":focus").Class(":pointerover")));
        shell.Styles.Add(Bare(box => box.Class(":disabled")));
        shell.Styles.Add(new Style(x => x.OfType<Border>().Name("ChatComposerShell").Class(":focus-within"))
        {
            Setters = { new Setter(Border.BorderBrushProperty, Ui.ControlBorderActive) }
        });
        // Below the box, outside it: plain controls with only a hover wash. The "+" and the context pie at the left, a quiet usage line between,
        // and the model, effort and mode as plain text at the right.
        hint = new TextBlock { Name = "ChatHint", FontSize = 11, Foreground = Ui.Hint, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, Margin = new Thickness(Space.Gap, 0) };
        var leftGroup = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Space.Gap, VerticalAlignment = VerticalAlignment.Center };
        leftGroup.Children.Add(plus);
        leftGroup.Children.Add(pie);
        chips.VerticalAlignment = VerticalAlignment.Center;
        chips.HorizontalAlignment = HorizontalAlignment.Right;
        var footer = new Grid { Name = "ChatFooter", Margin = new Thickness(Space.Inset, 0), ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = Space.Gap };
        Grid.SetColumn(hint, 1);
        Grid.SetColumn(chips, 2);
        footer.Children.Add(leftGroup);
        footer.Children.Add(hint);
        footer.Children.Add(chips);
        footerPanel = footer;
        footerLeft = leftGroup;
        footerRight = chips;
        footer.Styles.Add(new Style(x => x.OfType<Button>().Class("plain"))
        {
            Setters = { new Setter(Button.BackgroundProperty, Brushes.Transparent), new Setter(Button.BorderThicknessProperty, new Thickness(0)), new Setter(Button.PaddingProperty, new Thickness(6, 3)) }
        });
        footer.Styles.Add(new Style(x => x.OfType<Button>().Class("plain").Class(":pointerover").Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Ui.Hover) }
        });
        plus.Classes.Add("plain");
        pie.Classes.Add("plain");
        plus.Background = pie.Background = Brushes.Transparent;
        foreach (var round in new Button[] { plus, pie }) { round.CornerRadius = new CornerRadius(Space.ControlHeight / 2); round.MinWidth = Space.ControlHeight; round.Height = Space.ControlHeight; round.HorizontalContentAlignment = HorizontalAlignment.Center; round.VerticalContentAlignment = VerticalAlignment.Center; }
        var root = new StackPanel { Name = "ChatComposer", Margin = new Thickness(12, 4, 12, 10), Spacing = Space.Inset };
        root.Children.Add(queue);
        root.Children.Add(shell);
        root.Children.Add(footer);
        input.TextChanged += (_, _) => { if (!recalling) { historyAt = -1; draftStash = null; } pasteNote.IsVisible = false; Drafts[(workspaceId, chatId)] = input.Text ?? ""; UpdateMenu(); UpdateSend(); UpdateMentions(); };
        input.AddHandler(KeyDownEvent, InputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // The box's own Paste (its context menu) takes the same road.
        input.PastingFromClipboard += (_, e) => { e.Handled = true; _ = PasteAttachments(); };
        SetValue(FileDrop.AcceptsProperty, () => true);
        SetValue(FileDrop.AttachProperty, paths => { foreach (var path in paths) AddAttachment(path); });
        UpdateSend();
        return root;
    }

    private Button stop = null!;
    private StackPanel queue = null!;
    private string statsText = "";
    private TextBlock pasteNote = null!;
    internal string StatsText => statsText;
    private TextBlock working = null!;
    private Avalonia.Threading.DispatcherTimer? workingTimer;
    private DateTime workingSince;

    internal Button StopButton => stop;
    private Button pie = null!, history = null!;
    private Button? plus;
    private Border plusBadge = null!;
    private StackPanel contextRow = null!;

    /// <summary>The row of content chips above the input takes room only while it holds one.</summary>
    private void ShowContextRow() => contextRow.IsVisible = selectionRow.IsVisible || attachmentRow.IsVisible;
    private TextBlock hint = null!;
    private bool workingActive;
    private WrapPanel mentionRow = null!;
    private string mentionSignature = "";
    private string? activity;

    internal Button ContextPie => pie;
    internal IReadOnlyList<ChatAttachment> Attachments => attachments;

    private async Task AttachFromPicker()
    {
        try { if (await env.PickFile() is { Length: > 0 } picked) AddAttachment(picked); }
        catch (Exception error) when (error is not OperationCanceledException) { env.Notify(PluginNotificationKind.Error, "Couldn't open the file picker", ChatErrors.Text(error)); }
    }

    /// <summary>A finished <c>@path</c>, <c>@"path with spaces"</c> or either followed by <c>#L10-20</c>: what the composer shows as a chip.</summary>
    internal static readonly System.Text.RegularExpressions.Regex MentionPattern = new(@"(?<=^|\s)@(?:""(?<q>[^""]*)""|(?<p>[^\s""]+))(?<r>#L\d+(?:-\d+)?)?(?=\s)", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>The mentions in a text as (label, range text, the span in the text), the range split off an unquoted path.</summary>
    internal static IReadOnlyList<(string Path, string Range, int Start, int Length)> Mentions(string text)
    {
        var found = new List<(string, string, int, int)>();
        foreach (System.Text.RegularExpressions.Match match in MentionPattern.Matches(text))
        {
            var path = match.Groups["q"].Success ? match.Groups["q"].Value : match.Groups["p"].Value;
            var range = match.Groups["r"].Success ? match.Groups["r"].Value : "";
            if (range.Length == 0 && System.Text.RegularExpressions.Regex.Match(path, @"#L\d+(?:-\d+)?$") is { Success: true } tail) { range = tail.Value; path = path[..^range.Length]; }
            found.Add((path, range, match.Index, match.Length));
        }
        return found;
    }

    private void UpdateMentions()
    {
        var text = input.Text ?? "";
        var mentions = Mentions(text);
        var signature = string.Join("|", mentions.Select(mention => mention.Path + mention.Range + mention.Start));
        if (signature == mentionSignature) return;
        mentionSignature = signature;
        mentionRow.Children.Clear();
        mentionRow.IsVisible = mentions.Count > 0;
        foreach (var mention in mentions)
        {
            var name = mention.Path.Replace('\\', '/').Split('/')[^1];
            var label = name + (mention.Range.Length > 0 ? " " + mention.Range[1..] : "");
            var span = (mention.Start, mention.Length);
            var chip = TerminalFacts.ChipButton("ChatMention", Ui.Text("@" + label + "  \u00D7", Ui.Accent, 11), "Remove the mention of " + mention.Path, () =>
            {
                var current = input.Text ?? "";
                if (span.Start + span.Length > current.Length) return;
                input.Text = (current[..span.Start] + current[(span.Start + span.Length)..]).TrimStart(' ');
            });
            chip.Margin = new Thickness(0, 0, 4, 4);
            mentionRow.Children.Add(chip);
        }
    }

    /// <summary>
    /// The context left before auto-compact, in percent, or null when the window is unknown. The extension's window is the model's context
    /// window less the output it reserves (at most 20,000 tokens) and a 13,000-token buffer.
    /// </summary>
    internal static double? ContextRemaining(ChatUsage usage)
    {
        if (usage.ContextTokens is not { } used || usage.ContextWindow is not { } full) return null;
        var window = full - Math.Min(usage.MaxOutputTokens ?? ReservedOutput, ReservedOutput) - CompactBuffer;
        return window > 0 ? Math.Clamp(100.0 - used * 100.0 / window, 0, 100) : null;
    }

    private const long ReservedOutput = 20_000, CompactBuffer = 13_000;

    // The usage chip is there whenever the window is known; under half the context left its tooltip says how much, as the extension's pie does.
    private void ShowPie(ChatUsage usage)
    {
        var remaining = ContextRemaining(usage);
        pie.IsVisible = remaining is not null || statsText.Length > 0;
        if (remaining is not { } left) { ToolTip.SetTip(pie, "Session usage"); return; }
        var percent = (int)Math.Round(left);
        ToolTip.SetTip(pie, left < 50 ? $"{percent}% of context remaining until auto-compact.\nClick for usage, or to compact now." : $"{percent}% of context remaining. Click for usage.");
        Avalonia.Automation.AutomationProperties.SetName(pie, $"{(int)Math.Round(100 - left)}% context used \u2014 click for usage");
        if (pie.Content is PieGlyph glyph) glyph.Fraction = 1 - left / 100;
    }

    /// <summary>A small circle with the used part of the context filled.</summary>
    private sealed class PieGlyph : Control
    {
        private double fraction;
        internal double Fraction { get => fraction; set { fraction = value; InvalidateVisual(); } }
        protected override Size MeasureOverride(Size availableSize) => new(14, 14);

        public override void Render(DrawingContext context)
        {
            var center = new Point(7, 7);
            context.DrawEllipse(null, new Pen(Ui.Muted, 1.2), center, 6, 6);
            if (fraction <= 0) return;
            if (fraction >= 0.999) { context.DrawEllipse(Ui.Muted, null, center, 6, 6); return; }
            var angle = fraction * 2 * Math.PI;
            var end = new Point(7 + 6 * Math.Sin(angle), 7 - 6 * Math.Cos(angle));
            var wedge = new StreamGeometry();
            using (var g = wedge.Open())
            {
                g.BeginFigure(center, true);
                g.LineTo(new Point(7, 1));
                g.ArcTo(end, new Size(6, 6), 0, fraction > 0.5, SweepDirection.Clockwise);
                g.EndFigure(true);
            }
            context.DrawGeometry(Ui.Muted, null, wedge);
        }
    }

    private async Task Interrupt()
    {
        try { await env.Client.RequestAsync(ChatContract.Interrupt, key); }
        catch (Exception error) when (error is not OperationCanceledException) { env.Notify(PluginNotificationKind.Error, "Couldn't interrupt", ChatErrors.Text(error)); }
    }

    // Upstream's session stats bar: token counts and cost, then a five-cell context bar.
    private void ShowStats(ChatUsage usage)
    {
        var parts = new List<string>(TerminalFacts.TokenUsageParts(new(usage.Input, usage.Output, usage.CacheRead, usage.CacheWrite)));
        if (usage.CostUsd > 0) parts.Add("$" + usage.CostUsd.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        if (usage.ContextTokens is { } used && usage.ContextWindow is > 0 and var window)
        {
            var percent = Math.Min(100.0, used * 100.0 / window);
            var filled = (int)Math.Round(percent / 20);
            parts.Add($"{new string('\u25B0', filled)}{new string('\u25B1', 5 - filled)} {percent.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}%/{TerminalFacts.FormatTokens(window)}");
        }
        statsText = string.Join("  \u00B7  ", parts);
        hint.Text = statsText;
        ToolTip.SetTip(hint, "Session usage\n\u2191 input tokens\n\u2193 output tokens\nR tokens read from the prompt cache\nW tokens written to the prompt cache\n$ cost so far\nThe bar: context used / the model's context window");
        hint.IsVisible = densityLevel < 1 && statsText.Length > 0;
    }

    /// <summary>The usage popover: the session's tokens, cost and context meter, and a way to compact now.</summary>
    internal Control BuildUsagePanel()
    {
        var panel = new StackPanel { Name = "ChatUsagePanel", Spacing = Space.Gap, MinWidth = 240 };
        panel.Children.Add(Ui.Text("Session usage", Ui.TextBrush, 13));
        // The footer line already shows the figures; the popup names each one.
        var usage = transcript.Header?.Usage ?? new ChatUsage();
        var lines = new List<string>();
        if (usage.Input != 0) lines.Add("Input  " + TerminalFacts.FormatTokens(usage.Input));
        if (usage.Output != 0) lines.Add("Output  " + TerminalFacts.FormatTokens(usage.Output));
        if (usage.CacheRead != 0) lines.Add("Cache read  " + TerminalFacts.FormatTokens(usage.CacheRead));
        if (usage.CacheWrite != 0) lines.Add("Cache write  " + TerminalFacts.FormatTokens(usage.CacheWrite));
        if (usage.CostUsd > 0) lines.Add("Cost  $" + usage.CostUsd.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
        if (usage.ContextTokens is { } used && usage.ContextWindow is > 0 and var window)
            lines.Add($"Context window  {TerminalFacts.FormatTokens(used)} of {TerminalFacts.FormatTokens(window)}");
        panel.Children.Add(new TextBlock { Name = "ChatStats", Text = lines.Count > 0 ? string.Join("\n", lines) : "No usage yet", FontSize = 11, Foreground = Ui.Muted, LineHeight = 17 });
        var compactNow = Ui.Button("Compact now", () => _ = SendText("/compact", null));
        compactNow.Name = "ChatCompact";
        panel.Children.Add(compactNow);
        return panel;
    }

    private void ShowUsage() => ChatPopup.Show(new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft, Content = ChatPopup.Frame("ChatUsagePopup", BuildUsagePanel()) }, pie);

    // The streaming indicator: elapsed seconds while the chat works. A UI timer, no I/O.
    private void ShowWorking(bool running)
    {
        workingActive = running;
        working.IsVisible = running && densityLevel < 3;
        if (!running) { workingTimer?.Stop(); return; }
        if (workingTimer is { IsEnabled: true }) { TickWorking(null, EventArgs.Empty); return; }
        workingSince = DateTime.UtcNow;
        workingTimer ??= new Avalonia.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        workingTimer.Tick -= TickWorking;
        workingTimer.Tick += TickWorking;
        TickWorking(null, EventArgs.Empty);
        workingTimer.Start();
    }

    private void TickWorking(object? sender, EventArgs e) => working.Text = activity is { Length: > 0 } word
        ? word.TrimEnd('.', '\u2026', ' ') + "..."
        : $"Working \u00B7 {ChatDuration.Format((long)(DateTime.UtcNow - workingSince).TotalSeconds)}";

    private string? editingQueued;
    private readonly Dictionary<string, string> queueNotes = [];
    private string queueSignature = "";

    // Upstream's queue strip: each message waiting behind the turn with Edit and Remove. A message the CLI already took up cannot be
    // changed: the call fails, the message stays, and the strip says so.
    private void ShowQueue()
    {
        var queued = transcript.QueuedMessages();
        var signature = string.Join("\n", queued.Select(message => message.Id + "\t" + message.Text)) + "|" + (transcript.Header?.CanSendQueuedNow == true) + "|" + editingQueued + "|" + string.Join(",", queueNotes.Keys);
        if (signature == queueSignature) return;
        queueSignature = signature;
        queue.Children.Clear();
        queue.IsVisible = queued.Count > 0;
        foreach (var message in queued)
        {
            var row = new DockPanel { Name = "ChatQueueItem", Margin = new Thickness(0, 1) };
            if (editingQueued == message.Id)
            {
                var box = new TextBox { Name = "ChatQueueEdit", Text = message.Text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 120 };
                var save = Ui.Button("Send", () => _ = EditQueued(message, box.Text ?? ""));
                save.Name = "ChatQueueSave";
                var cancel = Ui.Button("Cancel", () => { editingQueued = null; ShowQueue(); });
                DockPanel.SetDock(cancel, Dock.Right); DockPanel.SetDock(save, Dock.Right);
                row.Children.Add(cancel); row.Children.Add(save); row.Children.Add(box);
                editBox = box;
            }
            else
            {
                var remove = Ui.IconButton("close", "Remove the queued message", () => _ = Dequeue(message));
                remove.Name = "ChatQueueRemove";
                var edit = Ui.IconButton("pencil", "Edit the queued message", () => { editingQueued = message.Id; ShowQueue(); });
                edit.Name = "ChatQueueEditButton";
                DockPanel.SetDock(remove, Dock.Right); DockPanel.SetDock(edit, Dock.Right);
                Button? sendNowButton = null;
                if (transcript.Header?.CanSendQueuedNow == true)
                {
                    var sendNow = Ui.IconButton("arrowRight", "Send now", () => _ = SendQueuedNow(message));
                    sendNow.Name = "ChatQueueSendNow";
                    DockPanel.SetDock(sendNow, Dock.Right);
                    sendNowButton = sendNow;
                }
                var line = new TextBlock { Foreground = Ui.Muted, FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, Text = "Queued: " + message.Text.ReplaceLineEndings(" ") };
                ToolTip.SetTip(line, message.Text + " \u2014 runs after the agent finishes");
                row.Children.Add(remove); row.Children.Add(edit);
                if (sendNowButton is not null) row.Children.Add(sendNowButton);
                row.Children.Add(line);
            }
            queue.Children.Add(row);
            if (queueNotes.TryGetValue(message.Id, out var note))
                queue.Children.Add(new TextBlock { Name = "ChatQueueNote", Text = note, Foreground = Ui.Danger, FontSize = 11, TextWrapping = TextWrapping.Wrap });
        }
    }

    private TextBox? editBox;
    internal TextBox? QueueEditBox => editBox;

    internal async Task Dequeue(ChatUserItem message)
    {
        try
        {
            await env.Client.RequestAsync(ChatContract.Dequeue, new ChatQueuedParams(workspaceId, chatId, message.Id));
            queueNotes.Remove(message.Id);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            queueNotes[message.Id] = $"{env.Agent.Name} already took this message up, so it can no longer be removed.";
            env.Notify(PluginNotificationKind.Error, "The message is already running", ChatErrors.Text(error));
        }
        ShowQueue();
    }

    internal async Task SendQueuedNow(ChatUserItem message)
    {
        try
        {
            await env.Client.RequestAsync(ChatContract.SendQueuedNow, new ChatQueuedParams(workspaceId, chatId, message.Id));
            queueNotes.Remove(message.Id);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            queueNotes[message.Id] = $"{env.Agent.Name} already took this message up, so it can no longer be sent now.";
            env.Notify(PluginNotificationKind.Error, "The message is already running", ChatErrors.Text(error));
        }
        ShowQueue();
    }

    internal async Task EditQueued(ChatUserItem message, string text)
    {
        try
        {
            await env.Client.RequestAsync(ChatContract.EditQueued, new ChatEditQueuedParams(workspaceId, chatId, message.Id, text));
            queueNotes.Remove(message.Id);
            editingQueued = null;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            queueNotes[message.Id] = $"{env.Agent.Name} already took this message up, so it can no longer be changed.";
            editingQueued = null;
            env.Notify(PluginNotificationKind.Error, "The message is already running", ChatErrors.Text(error));
        }
        ShowQueue();
    }

    private void ShowSkills()
    {
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        flyout.Content = BuildSkills(flyout);
        ChatPopup.Show(flyout, input);
    }

    internal Control BuildSkills(Flyout flyout)
    {
        var header = transcript.Header;
        var filter = ChatPopup.Search(new TextBox { Name = "ChatSkillsFilter", PlaceholderText = "Filter skills" });
        var list = MenuList("ChatSkillsList");
        list.MaxHeight = Math.Max(120, Bounds.Height * 0.4);
        var width = Math.Clamp(Bounds.Width - 48, 280, MenuMaxWidth);
        void Fill()
        {
            var query = filter.Text ?? "";
            list.ItemsSource = header is null ? [] : header.Commands.Where(command => command.Source is "skill" || command.Source is null)
                .Where(command => command.Name.Contains(query, StringComparison.OrdinalIgnoreCase) || (command.Description ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
                .Select(command => new MenuEntry("/" + command.Name, command.Description ?? "", "/" + command.Name + " ", 0, 0)).ToList();
        }
        filter.TextChanged += (_, _) => Fill();
        list.PointerReleased += (_, _) =>
        {
            if (list.SelectedItem is not MenuEntry entry) return;
            input.Text = entry.Insert + (input.Text ?? "");
            input.CaretIndex = input.Text.Length;
            flyout.Hide();
            input.Focus();
        };
        Fill();
        var panel = ChatPopup.Container("ChatSkillsPopup", "Skills", filter, list);
        panel.Width = width;
        return panel;
    }

    private void ShowState(ChatHeader header)
    {
        status = header.Status;
        input.PlaceholderText = Placeholder();
        activity = header.Activity;
        ShowWorking(header.Status == ChatStatus.Running || activity is { Length: > 0 });
        ShowStats(header.Usage);
        ShowPie(header.Usage);
        UpdateSend();
        ApplyDensity(true);
    }

    // The extension's placeholder: waiting while a request is open, queueing while Claude works, else the plain invitation. The key-hint variants
    // ("Esc to focus or unfocus", "Esc to attach selected text") belong to keybindings SharpRail does not have.
    internal string Placeholder() => transcript.PendingCount > 0 ? (transcript.FirstPending() is ChatQuestionItem ? "Waiting for your answer…" : "Waiting for permission…") : Busy ? "Queue another message…" : $"Ask {env.Agent.Short} to edit…";

    private bool Busy => status is ChatStatus.Running or ChatStatus.AwaitingPermission;

    private void UpdateSend()
    {
        var empty = string.IsNullOrWhiteSpace(input.Text) && attachments.Count == 0;
        stop.IsVisible = Busy;
        sendMore.IsVisible = Busy;
        stop.Content = Ui.Text("\u25A0", Ui.Danger, 12);
        send.Content = Inherit("\u2191", 15);
        send.IsEnabled = !empty && status is not (ChatStatus.Failed or ChatStatus.Exited);
        Avalonia.Automation.AutomationProperties.SetName(send, Busy ? "Queue the message until the turn ends" : "Send the message");
        Avalonia.Automation.AutomationProperties.SetName(stop, "Interrupt the turn");
    }

    internal static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp";

    // The preview chip's picture is decoded small and off the UI thread, then set.
    private static async Task LoadThumbnail(byte[] data, Image target)
    {
        try
        {
            var bitmap = await Task.Run(() =>
            {
                using var stream = new MemoryStream(data);
                return Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 64);
            });
            Avalonia.Threading.Dispatcher.UIThread.Post(() => target.Source = bitmap);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) { }
    }

    /// <summary>The most one pasted or attached image may weigh; a bigger one is refused with a message.</summary>
    internal const int MaxAttachmentBytes = 5 * 1024 * 1024;

    private readonly List<Task> attachmentLoads = [];
    private int pastedCount;

    private static string? MediaTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".pdf" => "application/pdf",
        _ => null
    };

    /// <summary>A file: a path the CLI reads, except an image or PDF, whose bytes are read here (off the UI thread) and sent inline.</summary>
    internal void AddAttachment(string path)
    {
        var name = path.Replace('\\', '/').Split('/')[^1];
        if (attachments.Any(item => item.Path == path)) return;
        var mediaType = MediaTypeOf(path);
        var entry = new ChatAttachment(name, path) { Kind = mediaType is null ? null : mediaType.StartsWith("image/", StringComparison.Ordinal) ? "image" : "document" };
        attachments.Add(entry);
        RebuildAttachmentChips();
        if (mediaType is null) return;
        attachmentLoads.Add(LoadAttachment(entry, mediaType));
    }

    private async Task LoadAttachment(ChatAttachment entry, string mediaType)
    {
        try
        {
            var data = await Task.Run(() =>
            {
                if (new FileInfo(entry.Path).Length > MaxAttachmentBytes) throw new InvalidOperationException($"{entry.Name} is larger than {MaxAttachmentBytes / (1024 * 1024)} MB.");
                return File.ReadAllBytes(entry.Path);
            });
            var at = attachments.IndexOf(entry);
            if (at < 0) return;
            attachments[at] = entry with { Data = data, MediaType = mediaType };
            RebuildAttachmentChips();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            attachments.Remove(entry);
            RebuildAttachmentChips();
            env.Notify(PluginNotificationKind.Error, "Couldn't attach " + entry.Name, error.Message);
        }
    }

    /// <summary>Bytes from the clipboard (or a drop): kept inline, with a thumbnail chip, up to <see cref="MaxAttachmentBytes"/>.</summary>
    internal void AddPasted(byte[] data, string mediaType, string? name = null)
    {
        if (data.Length > MaxAttachmentBytes)
        {
            env.Notify(PluginNotificationKind.Error, "That image is too large to attach", $"{data.Length / (1024 * 1024)} MB; the most is {MaxAttachmentBytes / (1024 * 1024)} MB.");
            return;
        }
        var extension = mediaType switch { "image/jpeg" => "jpg", "application/pdf" => "pdf", _ => mediaType.Split('/')[^1] };
        attachments.Add(new ChatAttachment(name ?? $"pasted-{++pastedCount}.{extension}", "") { Kind = mediaType.StartsWith("image/", StringComparison.Ordinal) ? "image" : "document", Data = data, MediaType = mediaType });
        RebuildAttachmentChips();
    }

    private void RebuildAttachmentChips()
    {
        attachmentRow.Children.Clear();
        attachmentRow.IsVisible = attachments.Count > 0;
        ShowContextRow();
        foreach (var item in attachments.ToArray())
        {
            var chip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            if (item.Kind == "image")
            {
                var thumb = new Image { Width = 20, Height = 20, Stretch = Stretch.UniformToFill, Name = "ChatAttachmentPreview" };
                chip.Children.Add(thumb);
                if (item.Data is { } bytes) _ = LoadThumbnail(bytes, thumb);
                else if (item.Path.Length > 0) _ = LoadThumbnail(item.Path, thumb);
            }
            chip.Children.Add(Ui.Text(item.Name + "  ×", Ui.Muted, 11));
            var remove = TerminalFacts.ChipButton("ChatAttachment_" + item.Name, chip, "Remove " + (item.Path.Length > 0 ? item.Path : item.Name), () =>
            {
                attachments.Remove(item);
                RebuildAttachmentChips();
            });
            remove.Background = Ui.Hover;
            attachmentRow.Children.Add(remove);
        }
        UpdateSend();
        ApplyDensity();
    }

    private static async Task LoadThumbnail(string path, Image target)
    {
        try { await LoadThumbnail(await File.ReadAllBytesAsync(path), target); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal async Task Submit()
    {
        var text = (input.Text ?? "").Trim();
        if (text.Length == 0 && attachments.Count == 0)
        {
            if (!Busy) return;
            try { await env.Client.RequestAsync(ChatContract.Interrupt, key); }
            catch (Exception error) when (error is not OperationCanceledException) { env.Notify(PluginNotificationKind.Error, "Couldn't interrupt", ChatErrors.Text(error)); }
            return;
        }
        // An image still being read from disk is waited for, so it goes out with the message.
        if (attachmentLoads.Count > 0) { await Task.WhenAll(attachmentLoads.ToArray()); attachmentLoads.Clear(); }
        var sent = attachments.ToArray();
        text = WithEditorContext(text);
        if (await SendText(text, sent.Length > 0 ? sent : null))
        {
            var history = Histories.TryGetValue((workspaceId, chatId), out var existing) ? existing : Histories[(workspaceId, chatId)] = [];
            if (text.Length > 0) { history.Add(text); RememberPrompt(text); }
            historyAt = -1;
            draftStash = null;
            input.Text = "";
            attachments.Clear();
            RebuildAttachmentChips();
        }
    }

    // While a turn runs the host queues the message and marks its item; the composer does not wait.
    private async Task<bool> SendText(string text, IReadOnlyList<ChatAttachment>? files)
    {
        try
        {
            await env.Client.RequestAsync(ChatContract.Send, new ChatSendParams(workspaceId, chatId, text) { Attachments = files });
            return true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            env.Notify(PluginNotificationKind.Error, "Couldn't send the message", ChatErrors.Text(error));
            return false;
        }
    }

    private void InputKeyDown(object? sender, KeyEventArgs e)
    {
        // A key the input method is handling (a composition being confirmed with Enter) is never a send.
        if (e.Key == Key.ImeProcessed) return;
        if (menu.IsOpen)
        {
            switch (e.Key)
            {
                case Key.Down: StepMenu(1); e.Handled = true; return;
                case Key.Up: StepMenu(-1); e.Handled = true; return;
                case Key.Tab or Key.Enter: AcceptMenu(); e.Handled = true; return;
                case Key.Escape: menu.IsOpen = false; e.Handled = true; return;
            }
        }
        // Paste is ours, before the box sees it: a clipboard may hold an image or files and no text at all, which the box would silently ignore.
        // Text-only clipboards are inserted below; everything else becomes attachments.
        if (e.Key == Key.V && (e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta)) || e.Key == Key.Insert && e.KeyModifiers == KeyModifiers.Shift)
        {
            e.Handled = true;
            _ = PasteAttachments();
            return;
        }
        var primary = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (e.Key == Key.Enter && SendsOnKeys(primary, e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            _ = Submit();
            e.Handled = true;
            return;
        }
        if (e.Key is Key.Up or Key.Down && (e.Key == Key.Up || historyAt >= 0) && RecentPrompts() is { Count: > 0 } history && OnEdgeLine(e.Key == Key.Up))
        {
            if (e.Key == Key.Up)
            {
                if (historyAt < 0) draftStash = input.Text ?? "";
                historyAt = historyAt < 0 ? history.Count - 1 : Math.Max(0, historyAt - 1);
            }
            else if (++historyAt >= history.Count) historyAt = -1;
            var shown = historyAt < 0 ? draftStash ?? "" : history[historyAt];
            if (historyAt < 0) draftStash = null;
            recalling = true;
            try { input.Text = shown; }
            finally { recalling = false; }
            input.CaretIndex = shown.Length;
            e.Handled = true;
        }
    }

    private const int RecentLimit = 30;
    private ComposerPreferences? composerPreferences;

    private sealed record ComposerPreferences(bool EnterSends, IReadOnlyList<string> Recents, string? Destination = null)
    {
        public static ComposerPreferences Parse(string? json)
        {
            if (string.IsNullOrEmpty(json)) return new(true, []);
            try { return System.Text.Json.JsonSerializer.Deserialize<ComposerPreferences>(json) is { } read ? read with { Recents = read.Recents ?? [] } : new(true, []); }
            catch (System.Text.Json.JsonException) { return new(true, []); }
        }
    }

    private ComposerPreferences Composer => composerPreferences ??= ComposerPreferences.Parse(env.ComposerPreference.Get());

    private void SaveComposer(ComposerPreferences next)
    {
        composerPreferences = next;
        env.ComposerPreference.Set(System.Text.Json.JsonSerializer.Serialize(next));
    }

    /// <summary>Whether Enter with these modifiers sends: by default Enter sends and Shift+Enter breaks the line; with the option off, Ctrl or Cmd+Enter sends.</summary>
    internal bool SendsOnKeys(bool primary, bool shift) => Composer.EnterSends ? !shift : primary;

    internal bool EnterSends => Composer.EnterSends;

    internal void SetEnterSends(bool value) => SaveComposer(Composer with { EnterSends = value });

    // Upstream's recent prompts: this chat's own sends, then those of earlier chats, newest last, remembered between sessions.
    private List<string> RecentPrompts()
    {
        var own = Histories.TryGetValue((workspaceId, chatId), out var history) ? history : [];
        return [.. Composer.Recents.Where(prompt => !own.Contains(prompt)), .. own];
    }

    private void RememberPrompt(string text)
    {
        if (text.Length == 0) return;
        var recents = Composer.Recents.Where(prompt => prompt != text).Append(text).ToList();
        SaveComposer(Composer with { Recents = recents.Skip(Math.Max(0, recents.Count - RecentLimit)).ToList() });
    }

    // The send button's menu while a turn runs, as upstream's send options: queue behind the turn, or stop it and send now.
    private void ShowSendMenu()
    {
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedRight };
        flyout.Content = BuildSendOptions(flyout);
        ChatPopup.Show(flyout, sendMore);
    }

    internal Control BuildSendOptions(Flyout flyout)
    {
        var options = new StackPanel { Spacing = 2 };
        Button Option(string name, string title, string meaning, Action run)
        {
            var body = new StackPanel();
            body.Children.Add(Ui.Text(title, Ui.TextBrush, 13));
            body.Children.Add(Ui.Text(meaning, Ui.Muted, 11));
            var button = new Button { Name = name, Content = body, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Left, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
            button.Click += (_, _) => { flyout.Hide(); run(); };
            return button;
        }
        options.Children.Add(Option("ChatSendQueue", "Queue", "Runs after the agent finishes.", () => _ = Submit()));
        options.Children.Add(Option("ChatSendNow", "Stop and send", "Interrupts the turn, then sends this message.", () => _ = InterruptThenSend()));
        var enter = new CheckBox { Name = "ChatEnterSends", Content = "Enter sends (Shift+Enter breaks the line)", IsChecked = Composer.EnterSends };
        enter.IsCheckedChanged += (_, _) => SetEnterSends(enter.IsChecked == true);
        options.Children.Add(enter);
        var panel = ChatPopup.Container("ChatSendOptions", "Send options", options);
        panel.MinWidth = 280;
        return panel;
    }

    internal async Task InterruptThenSend()
    {
        await Interrupt();
        await Submit();
    }

    private Button sendMore = null!;
    private bool compact;
    private int densityLevel;
    private double densityWidth;
    private Panel inputControls = null!;
    private Panel footerLeft = null!, footerRight = null!, footerPanel = null!;

    internal bool Compact => compact;
    internal int DensityLevel => densityLevel;

    /// <summary>The spacing the composer uses everywhere: one step between neighbours, one inset around the footer.</summary>
    internal static class Space
    {
        public const double Gap = 4;
        public const double Inset = 6;
        public const double ControlHeight = 28;
    }

    /// <summary>The "+" popup: Skills, the command menu and the MCP servers. The IDE switch is also a footer chip while there is room.</summary>
    internal Control BuildPlusMenu(Flyout flyout)
    {
        var header = transcript.Header;
        var rows = new StackPanel { Spacing = 2 };
        Button Row(string name, string icon, string label, string? detail, Action run, bool? check = null, string? tip = null)
        {
            var line = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 8, Height = ChatPopup.RowHeight };
            line.Children.Add(ChatPopup.Check(check == true));
            var text = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Ui.Icon(icon, Ui.Muted, 12));
            text.Children.Add(Ui.Text(label, Ui.TextBrush, 12));
            Grid.SetColumn(text, 1);
            line.Children.Add(text);
            if (detail is { Length: > 0 })
            {
                var muted = Ui.Text(detail, Ui.Muted, 11);
                muted.TextTrimming = TextTrimming.CharacterEllipsis;
                muted.MaxLines = 1;
                muted.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(muted, 2);
                line.Children.Add(muted);
            }
            var button = new Button { Name = name, Content = line, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = ChatPopup.RowPadding, MinHeight = 0, BorderThickness = new Thickness(0) };
            button.Classes.Add("plain");
            button.Background = Brushes.Transparent;
            button.Click += (_, _) => { flyout.Hide(); run(); };
            ToolTip.SetTip(button, tip ?? (detail is { Length: > 0 } ? label + "\n" + detail : label));
            Avalonia.Automation.AutomationProperties.SetName(button, label);
            return button;
        }
        rows.Children.Add(Row("ChatSessions", "list", "Past conversations", "Resume a session", () => _ = ShowSessions()));
        rows.Children.Add(Row("ChatSkills", "sparkling", "Skills", "Skills and commands of this session", ShowSkills));
        rows.Children.Add(Row("ChatCommandMenu", "terminal", "Command menu", "Slash commands", () =>
        {
            var text = input.Text ?? "";
            if (!text.StartsWith('/')) input.Text = "/" + text;
            input.Focus();
            input.CaretIndex = 1;
        }, tip: "Show command menu (/)"));
        rows.Children.Add(Row("TerminalIdeContextToggle", "code", "IDE context", ide ? "on" : "off", () => _ = ToggleIde(), ide,
            ide ? "Stop sending IDE context to this session" : "Send IDE context to this session"));
        if (header is { McpServers.Count: > 0 })
        {
            var down = header.McpServers.Count(server => server.Status is not ("connected" or "ok"));
            rows.Children.Add(Row("TerminalAgentFact_mcp", "server", "MCP servers", down > 0 ? $"{header.McpServers.Count} ({down} down)" : header.McpServers.Count.ToString(),
                () => { }, tip: string.Join("\n", header.McpServers.Select(server => $"{server.Name} \u2014 {server.Status}"))));
        }
        var panel = ChatPopup.Container("ChatPlusMenu", "Add to the chat", rows);
        panel.MinWidth = 300;
        return panel;
    }

    // The levels, in the order things give way as the measured width shrinks: 1 the usage line goes, 2 the values shorten ("default effort" is
    // "default", "Accept edits" is "edits"), 3 the working time goes, 4 history goes. Send, Stop and the model stay; the facts wrap into a second row instead of going.
    private const int LastDensity = 4;

    private void SetDensity(int level)
    {
        if (level == densityLevel) return;
        densityLevel = level;
        compact = level >= 2;
        hint.IsVisible = level < 1 && hint.Text is { Length: > 0 };
        working.IsVisible = workingActive && level < 3;
        history.IsVisible = level < 4;
        if (transcript.Header is { } header) { chipSignature = ""; RebuildChips(header); }
        UpdateSend();
    }

    // The footer row, and the controls at the right of the input (shell padding and a sliver of input around them), whichever needs more.
    private double FooterNeeds() => Math.Max(inputControls.DesiredSize.Width + 24 + 40, FooterRowNeeds());

    private double FooterRowNeeds() => footerLeft.DesiredSize.Width + (densityLevel < 2 || chips.Children.Count == 0 ? footerRight.DesiredSize.Width : chips.Children.Max(chip => chip.DesiredSize.Width)) + (hint.IsVisible ? hint.DesiredSize.Width : 0) + 2 * Space.Gap;

    private bool applyingDensity;

    /// <summary>How much wider than a width where a level did not fit the view must be before it relaxes back to that level: a drag back and forth over a boundary must not flip the chips.</summary>
    internal const double RelaxMargin = 28;

    // The widest view at which each level was found not to fit. A freshly rebuilt footer can measure smaller than the settled one (icons and
    // templates arrive later), so a relax cannot trust its own measurement alone: it also needs the view to be wider than the width that shrank it.
    private readonly double[] failedAt = new double[LastDensity + 1];

    /// <summary>Finds the lowest level that fits. A header push only ever raises the level; a wider view, or a smaller footer, tries from the start, but a wider view relaxes only to a level that fits and that failed at least <see cref="RelaxMargin"/> narrower.</summary>
    private void ApplyDensity(bool relax = false)
    {
        if (applyingDensity || footerPanel is null || Bounds.Width <= 0) return;
        applyingDensity = true;
        try
        {
            var available = Bounds.Width - 24 - 2 * Space.Inset;
            var wider = !relax && Bounds.Width > densityWidth + 0.5;
            var from = relax || wider ? 0 : densityLevel;
            var held = densityLevel;
            if (relax) Array.Clear(failedAt);
            densityWidth = Bounds.Width;
            for (var level = from; level <= LastDensity; level++)
            {
                SetDensity(level);
                footerLeft.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                footerRight.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                inputControls.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                hint.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                var relaxing = wider && level < held;
                var room = relaxing ? available - RelaxMargin : available;
                var fits = FooterNeeds() <= room
                    && (!relaxing || Bounds.Width >= failedAt[level] + RelaxMargin);
                if (fits) break;
                if (!relaxing && level < LastDensity) failedAt[level] = Math.Max(failedAt[level], Bounds.Width);
            }
        }
        finally { applyingDensity = false; }
    }

    /// <summary>
    /// What a paste does, in this order: files become attachments, an image (Avalonia reads PNG and, on macOS, TIFF) becomes a PNG attachment,
    /// and text is inserted where the caret is. Every paste path (the key, Shift+Insert and the box's own Paste menu) comes here.
    /// </summary>
    internal async Task PasteAttachments()
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            var files = await Avalonia.Input.Platform.ClipboardExtensions.TryGetFilesAsync(clipboard);
            if (files is { Length: > 0 })
            {
                foreach (var file in files)
                    if (file.Path is { IsAbsoluteUri: true, IsFile: true } uri && uri.LocalPath is { Length: > 0 } path) AddAttachment(path);
                return;
            }
            if (await Avalonia.Input.Platform.ClipboardExtensions.TryGetBitmapAsync(clipboard) is { } bitmap)
            {
                using (bitmap)
                {
                    var png = await Task.Run(() =>
                    {
                        using var stream = new MemoryStream();
                        bitmap.Save(stream, new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                        return stream.ToArray();
                    });
                    AddPasted(png, "image/png");
                }
                return;
            }
            if (await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard) is { Length: > 0 } text) { input.SelectedText = text; return; }
            // Nothing the box can use: say what the clipboard did hold, so a failure can be told from an empty clipboard.
            var formats = (await Avalonia.Input.Platform.ClipboardExtensions.GetDataFormatsAsync(clipboard)).Select(format => format.Identifier).ToList();
            pasteNote.Text = formats.Count == 0 ? "Nothing to paste. The clipboard is empty." : "Nothing to paste. The clipboard has: " + string.Join(", ", formats);
            pasteNote.IsVisible = true;
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            env.Notify(PluginNotificationKind.Error, "Couldn't attach the pasted content", ChatErrors.Text(error));
        }
    }

    private bool OnEdgeLine(bool up)
    {
        var text = input.Text ?? "";
        var caret = Math.Clamp(input.CaretIndex, 0, text.Length);
        if (input.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault()?.TextLayout is { TextLines.Count: > 0 } layout)
        {
            // Soft-wrapped lines count: the caret must sit on the first (Up) or last (Down) visual line.
            var line = layout.GetLineIndexFromCharacterIndex(caret, caret == text.Length);
            return up ? line <= 0 : line >= layout.TextLines.Count - 1;
        }
        return up ? text.IndexOf('\n') is var first && (first < 0 || caret <= first) : text.LastIndexOf('\n') is var last && caret > last;
    }

    /// <summary>The widest a command, mention or skills menu is, whatever the window.</summary>
    internal const double MenuMaxWidth = 640;
    private const double MenuNameWidth = 168, MenuRowHeight = ChatPopup.RowHeight;

    private Border menuFrame = null!;
    internal Border MenuFrame => menuFrame;

    // One row per entry, one line each: the name in a column of its own, the description after it, ellipsized and muted, the whole text in a
    // tooltip and the accessible name. A section title is a small muted label with room above it. The list virtualizes, so only the rows in
    // view exist however many commands there are.
    internal static ListBox MenuList(string name)
    {
        var list = ChatPopup.Rows(new ListBox { Name = name });
        list.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<MenuEntry>((entry, _) => entry is null ? null : entry.IsHeader ? MenuHeader(entry) : MenuRow(entry), false);
        Avalonia.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        return list;
    }

    private static Control MenuHeader(MenuEntry entry) => new TextBlock
    {
        Text = entry.Label.ToUpperInvariant(),
        FontSize = 10,
        FontWeight = FontWeight.SemiBold,
        Foreground = Ui.Muted,
        IsHitTestVisible = false,
        Margin = new Thickness(0, Space.Inset + Space.Gap, 0, 2),
        MaxLines = 1,
        TextTrimming = TextTrimming.CharacterEllipsis
    };

    private static Control MenuRow(MenuEntry entry)
    {
        var detail = entry.Detail.ReplaceLineEndings(" ");
        var row = new Grid { Height = MenuRowHeight, ColumnDefinitions = new ColumnDefinitions(detail.Length > 0 ? $"{MenuNameWidth},*" : "*"), ColumnSpacing = Space.Inset };
        row.Children.Add(new TextBlock { Text = entry.Label, FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = Ui.TextBrush, MaxLines = 1, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
        if (detail.Length > 0)
        {
            var description = new TextBlock { Text = detail, FontSize = 11, Foreground = Ui.Muted, MaxLines = 1, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(description, 1);
            row.Children.Add(description);
        }
        var full = detail.Length > 0 ? entry.Label + "\n" + entry.Detail : entry.Label;
        ToolTip.SetTip(row, full);
        Avalonia.Automation.AutomationProperties.SetName(row, full.ReplaceLineEndings(" "));
        return row;
    }

    // The menu is as wide as the composer, never over MenuMaxWidth, and no taller than two fifths of the chat.
    private void SizeMenu()
    {
        menuFrame.Width = Math.Clamp(input.Bounds.Width > 0 ? input.Bounds.Width : Bounds.Width - 48, 240, MenuMaxWidth);
        menuList.MaxHeight = Math.Max(96, Bounds.Height * 0.4 - 2 * (ChatPopup.ContainerPadding.Top + 1));
    }

    // Section titles are never chosen: the selection moves over them.
    private void StepMenu(int step)
    {
        var at = menuList.SelectedIndex;
        for (var next = at + step; next >= 0 && next < menuList.ItemCount; next += step)
            if (menuList.Items[next] is MenuEntry { IsHeader: false }) { menuList.SelectedIndex = next; return; }
    }

    private void AcceptMenu()
    {
        if (menuList.SelectedItem is not MenuEntry { IsHeader: false } entry) return;
        var text = input.Text ?? "";
        input.Text = text[..entry.From] + entry.Insert + text[entry.To..];
        input.CaretIndex = entry.From + entry.Insert.Length;
        input.Focus();
        if (entry.Continue) { UpdateMenu(); return; }
        menu.IsOpen = false;
        entry.Run?.Invoke();
    }

    /// <summary>The sections of the slash menu, in the extension's order; sections the host names that are not here follow Slash Commands.</summary>
    internal static readonly string[] SlashSections = ["Context", "Model", "Customize", "Settings", "Slash Commands", "Support", "MCP servers"];

    // Built-in actions are only those SharpRail can honestly do: attach a file, resume or start a conversation, switch the model, open the docs.
    private List<MenuEntry> SlashEntries(ChatHeader header, string query, int from, int to)
    {
        bool Matches(string label, string? detail = null) => label.Contains(query, StringComparison.OrdinalIgnoreCase);
        var groups = new Dictionary<string, List<MenuEntry>>();
        void Add(string section, MenuEntry entry)
        {
            if (!groups.TryGetValue(section, out var list)) groups[section] = list = [];
            list.Add(entry);
        }
        void Action(string section, string label, string detail, Action run)
        {
            if (Matches(label)) Add(section, new MenuEntry(label, detail, "", from, to) { Run = run });
        }
        Action("Context", "Attach file\u2026", "Upload a file to include in conversation", () => _ = AttachFromPicker());
        Action("Context", "Resume conversation", "Continue a previous conversation", () => _ = ShowSessions());
        Action("Context", "New conversation", "Open a new conversation in a new tab or the sidebar", () => env.OpenChat("chat-" + Guid.NewGuid().ToString("N")[..12], env.Agent.Name, null));
        Action("Model", "Switch model\u2026", header.Model is { } model ? ModelLabel(model, header) : "", () => chips.Children.OfType<Button>().FirstOrDefault(chip => chip.Name == "TerminalAgentFact_model")?.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent)));
        if (env.Agent.HelpUrl is { } helpUrl) Action("Support", "View help docs", "", () => OpenUrl(helpUrl));
        foreach (var command in header.Commands.Where(command => Matches(command.Name)).OrderBy(command => !command.Name.StartsWith(query, StringComparison.OrdinalIgnoreCase)))
            Add(command.Section, new MenuEntry("/" + command.Name, string.Join("  ", new[] { command.ArgumentHint, command.Description }.Where(part => !string.IsNullOrEmpty(part))), "/" + command.Name + " ", from, to));
        foreach (var server in header.McpServers.Where(server => Matches(server.Name)))
            Add("MCP servers", new MenuEntry(server.Name, server.Status, "", from, to) { Run = () => { } });
        var entries = new List<MenuEntry>();
        foreach (var section in SlashSections.Take(5).Concat(groups.Keys.Except(SlashSections)).Concat(SlashSections.Skip(5)))
            if (groups.TryGetValue(section, out var list) && list.Count > 0)
            {
                entries.Add(new MenuEntry(section, "", "", from, to) { IsHeader = true });
                entries.AddRange(list.Take(50));
            }
        if (entries.Count == 0) entries.Add(new MenuEntry("No matching commands", "", "", from, to) { IsHeader = true });
        return entries;
    }

    // "/" at the start lists commands, skills and plugin commands; "@" after whitespace searches the workspace's files.
    private void UpdateMenu()
    {
        mentionSearch?.Cancel();
        var text = input.Text ?? "";
        var caret = Math.Min(input.CaretIndex, text.Length);
        var start = caret;
        while (start > 0 && !char.IsWhiteSpace(text[start - 1])) start--;
        var token = text[start..caret];
        if (token.StartsWith('/') && start == 0 && transcript.Header is { } header)
        {
            ShowMenu(SlashEntries(header, token[1..], start, caret));
        }
        else if (token.StartsWith('@')) _ = SearchFiles(token[1..], start, caret);
        else menu.IsOpen = false;
    }

    // Path mode: an absolute, home or explicitly relative path (or a drive); other queries search the workspace's files.
    internal static bool IsPathQuery(string query) => query.StartsWith('/') || query.StartsWith('~') || query.StartsWith("./", StringComparison.Ordinal) || query.StartsWith("../", StringComparison.Ordinal) ||
        query.StartsWith(@".\", StringComparison.Ordinal) || query.StartsWith(@"..\", StringComparison.Ordinal) || query.Length >= 3 && char.IsAsciiLetter(query[0]) && query[1] == ':' && query[2] is '\\' or '/';

    private async Task SearchFiles(string query, int from, int to)
    {
        var search = mentionSearch = new CancellationTokenSource();
        try
        {
            await Task.Delay(120, search.Token);
            var pathFirst = IsPathQuery(query);
            List<MenuEntry>? entries = null;
            if (pathFirst) entries = await CompletePath(query, from, to, search.Token);
            if (entries is not { Count: > 0 } && !(pathFirst && entries is not null))
            {
                var found = await env.Client.RequestAsync(ChatContract.Files, new ChatFilesParams(workspaceId, query), search.Token);
                entries = [.. found.Paths.Take(50).Select(path => new MenuEntry("@" + path, "", "@" + TerminalFacts.QuoteAttachPath(path) + " ", from, to))];
                if (entries.Count == 0 && !pathFirst && query.AsSpan().IndexOfAny('/', '\\') >= 0) entries = await CompletePath(query, from, to, search.Token) ?? entries;
            }
            if (search.IsCancellationRequested) return;
            ShowMenu(entries);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { menu.IsOpen = false; }
    }

    // The host's listing of the directory the query names; null when the host cannot answer (the caller falls back to the workspace search).
    private async Task<List<MenuEntry>?> CompletePath(string query, int from, int to, CancellationToken cancellationToken)
    {
        if (query == "~") query = "~/";
        var directoryText = query[..(query.LastIndexOfAny(['/', '\\']) + 1)];
        try
        {
            var listing = await env.Client.RequestAsync(ChatContract.CompletePath, new ChatPathParams(workspaceId, chatId, query), cancellationToken);
            return [.. listing.Entries.Take(50).Select(entry => entry.IsDirectory
                ? new MenuEntry("@" + directoryText + entry.Name + "/", "", "@" + TerminalFacts.QuoteAttachPath(directoryText + entry.Name + "/"), from, to) { Continue = true }
                : new MenuEntry("@" + directoryText + entry.Name, "", "@" + TerminalFacts.QuoteAttachPath(directoryText + entry.Name) + " ", from, to))];
        }
        catch (Exception error) when (error is not OperationCanceledException) { return null; }
    }

    private void ShowMenu(List<MenuEntry> entries)
    {
        SizeMenu();
        menuList.ItemsSource = entries;
        menuList.SelectedIndex = entries.FindIndex(entry => !entry.IsHeader);
        menu.IsOpen = entries.Count > 0;
    }
}