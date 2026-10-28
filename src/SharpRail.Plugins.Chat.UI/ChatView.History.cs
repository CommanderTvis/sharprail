using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Chat.UI;

internal sealed partial class ChatView
{
    internal sealed record HistoryHit(string Id, bool Prompt, string Label, string Snippet, string Full)
    {
        public override string ToString() => Label + "  " + Snippet;
    }

    private CancellationTokenSource? historySearch;
    private Border? historyOverlay;
    private TextBox? historyQuery;
    private ListBox? historyResults;
    private TextBlock? historyPreview;
    private TextBlock? historyHint;
    private bool historyZoomed;

    internal TextBox? HistoryQuery => historyQuery;
    internal ListBox? HistoryResults => historyResults;
    internal bool HistoryOpen => historyOverlay?.IsVisible == true;
    internal bool HistoryZoomed => historyZoomed;

    internal static string Text(ChatItem item) => item switch
    {
        ChatUserItem user => user.Text,
        ChatAssistantItem assistant => assistant.Text,
        ChatToolItem tool => tool.Name + " " + tool.Summary + " " + tool.Output,
        ChatNoticeItem notice => notice.Text,
        _ => ""
    };

    /// <summary>
    /// Items whose text holds <paramref name="query"/>, newest first, your prompts before the conversation's other messages when
    /// <paramref name="includeMessages"/> (upstream's zoomed stage); pure, so it runs off the UI thread over a copy of the references.
    /// </summary>
    internal static List<HistoryHit> Search(IReadOnlyList<ChatItem> items, string query, CancellationToken token, bool includeMessages = true)
    {
        var prompts = new List<HistoryHit>();
        var messages = new List<HistoryHit>();
        for (var index = items.Count - 1; index >= 0 && prompts.Count + messages.Count < 100; index--)
        {
            token.ThrowIfCancellationRequested();
            var item = items[index];
            var prompt = item is ChatUserItem;
            if (!prompt && !includeMessages) continue;
            var text = Text(item);
            var at = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
            if (at < 0) continue;
            var start = Math.Max(0, at - 24);
            var snippet = text[start..Math.Min(text.Length, at + query.Length + 48)].ReplaceLineEndings(" ");
            (prompt ? prompts : messages).Add(new HistoryHit(item.Id, prompt, item switch { ChatUserItem => "You", ChatAssistantItem => "Assistant", ChatToolItem tool => tool.Name, _ => "" }, snippet, text));
        }
        return [.. prompts, .. messages];
    }

    // Upstream's history overlay: a modal card over the conversation. The compact stage lists matching prompts; Tab zooms to prompts and
    // messages with a preview. Up and Down move, Enter jumps to the row, Esc closes. The scope is this chat: the contract has no wider search.
    private Border BuildHistoryOverlay()
    {
        historyQuery = new TextBox { Name = "ChatHistoryQuery", PlaceholderText = "Search prompts and conversation…", MinWidth = 420 };
        historyResults = Tight(new ListBox { Name = "ChatHistoryResults", MaxHeight = 300 });
        historyPreview = new TextBlock { Name = "ChatHistoryPreview", TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Ui.Muted, IsVisible = false, MaxHeight = 300 };
        historyHint = new TextBlock { Name = "ChatHistoryHint", FontSize = 11, Foreground = Ui.Hint, Text = "Tab shows messages and a preview · Enter jumps · Esc closes" };
        historyQuery.TextChanged += (_, _) => _ = RunSearch();
        historyResults.SelectionChanged += (_, _) => historyPreview.Text = (historyResults.SelectedItem as HistoryHit)?.Full;
        historyResults.DoubleTapped += (_, _) => JumpToSelectedHit();
        historyQuery.AddHandler(KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            switch (e.Key)
            {
                case Key.Down: historyResults.SelectedIndex = Math.Min(historyResults.ItemCount - 1, historyResults.SelectedIndex + 1); e.Handled = true; break;
                case Key.Up: historyResults.SelectedIndex = Math.Max(0, historyResults.SelectedIndex - 1); e.Handled = true; break;
                case Key.Tab: SetHistoryZoom(!historyZoomed); e.Handled = true; break;
                case Key.Enter: JumpToSelectedHit(); e.Handled = true; break;
                case Key.Escape: CloseHistory(); e.Handled = true; break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var close = Ui.IconButton("close", "Close", CloseHistory);
        var top = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        top.Children.Add(close);
        top.Children.Add(Ui.Text("Search history", Ui.TextBrush, 13));
        var card = new StackPanel { Spacing = 6, Width = 560 };
        card.Children.Add(top);
        card.Children.Add(historyQuery);
        card.Children.Add(historyResults);
        card.Children.Add(historyPreview);
        card.Children.Add(historyHint);
        var shell = new Border
        {
            Background = Ui.Elevated,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 60, 0, 0),
            Child = card
        };
        var scrim = new Border { Name = "ChatHistoryOverlay", Background = Ui.Overlay, IsVisible = false, Child = shell, ZIndex = 20 };
        scrim.PointerPressed += (_, e) => { if (ReferenceEquals(e.Source, scrim)) CloseHistory(); };
        SetRowSpan(scrim, 6);
        return scrim;
    }

    private void ShowHistory()
    {
        if (historyOverlay is null) { historyOverlay = BuildHistoryOverlay(); content.Children.Add(historyOverlay); }
        historyOverlay.IsVisible = true;
        SetHistoryZoom(false);
        historyQuery!.Focus();
        historyQuery.SelectAll();
    }

    internal void OpenHistory() => ShowHistory();

    private void CloseHistory()
    {
        if (historyOverlay is null) return;
        historyOverlay.IsVisible = false;
        input.Focus();
    }

    private void SetHistoryZoom(bool zoomed)
    {
        historyZoomed = zoomed;
        historyPreview!.IsVisible = zoomed;
        historyHint!.Text = zoomed ? "Prompts and messages · Tab returns to prompts · Enter jumps · Esc closes" : "Prompts · Tab shows messages and a preview · Enter jumps · Esc closes";
        _ = RunSearch();
    }

    private void JumpToSelectedHit()
    {
        if (historyResults?.SelectedItem is not HistoryHit hit) return;
        CloseHistory();
        Reveal(hit.Id);
    }

    private async Task RunSearch()
    {
        historySearch?.Cancel();
        var query = (historyQuery?.Text ?? "").Trim();
        if (query.Length == 0) { historyResults!.ItemsSource = new List<HistoryHit>(); return; }
        var source = historySearch = new CancellationTokenSource();
        var snapshot = transcript.AllItems();
        var zoomed = historyZoomed;
        try
        {
            var found = await Task.Run(() => Search([.. snapshot.OrderBy(item => item.Seq)], query, source.Token, zoomed), source.Token);
            if (source.IsCancellationRequested) return;
            historyResults!.ItemsSource = found;
            historyResults.SelectedIndex = found.Count > 0 ? 0 : -1;
        }
        catch (OperationCanceledException) { }
    }

    internal void Reveal(string itemId)
    {
        if (transcript.RowFor(itemId) is { } row) list.ScrollToRow(row);
    }
}