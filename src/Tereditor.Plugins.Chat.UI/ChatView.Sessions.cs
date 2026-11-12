using System.Globalization;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

using Tereditor.Plugins.Api;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Chat.UI;

internal sealed partial class ChatView
{
    internal sealed record SessionBucketHeader(string Title);

    internal sealed record SessionRow(ChatSessionInfo Session, DateTime When, string Relative, bool Current, bool Elsewhere);

    /// <summary>The date bucket a session falls in, by the local calendar: Today, Yesterday, Previous 7 days, Older.</summary>
    internal static string SessionBucket(DateTime local, DateTime now)
    {
        var days = (now.Date - local.Date).Days;
        return days <= 0 ? "Today" : days == 1 ? "Yesterday" : days <= 7 ? "Previous 7 days" : "Older";
    }

    /// <summary>A short, quiet time: "5m ago", "2h ago", "Yesterday", "3d ago", then a date.</summary>
    internal static string RelativeTime(DateTime local, DateTime now)
    {
        var span = now - local;
        var days = (now.Date - local.Date).Days;
        if (span < TimeSpan.FromMinutes(1)) return "just now";
        if (days <= 0) return span < TimeSpan.FromHours(1) ? $"{(int)span.TotalMinutes}m ago" : $"{(int)span.TotalHours}h ago";
        if (days == 1) return "Yesterday";
        if (days <= 7) return $"{days}d ago";
        return local.Year == now.Year ? local.ToString("MMM d", CultureInfo.CurrentCulture) : local.ToString("MMM d, yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>The rows of the flyout for a filter: bucket headers over the sessions, newest first, each marked when it is this chat's or another tab's.</summary>
    internal static List<object> SessionItems(IReadOnlyList<ChatSessionInfo> all, string query, string? ownSession, string ownChat, DateTime now)
    {
        var items = new List<object>();
        string? bucket = null;
        foreach (var session in all.Where(session => session.Title.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(session => (Session: session, When: DateTime.TryParse(session.LastModified, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed) ? parsed.ToLocalTime() : DateTime.MinValue))
            .OrderByDescending(pair => pair.When))
        {
            var title = SessionBucket(session.When, now);
            if (title != bucket) { items.Add(new SessionBucketHeader(title)); bucket = title; }
            items.Add(new SessionRow(session.Session, session.When, RelativeTime(session.When, now),
                ownSession is not null && session.Session.SessionId == ownSession, session.Session.ChatId is { } other && other != ownChat));
        }
        return items;
    }

    private static Control SessionTemplate(object item)
    {
        if (item is SessionBucketHeader header)
            return new TextBlock { Text = header.Title.ToUpperInvariant(), FontSize = 10, FontWeight = FontWeight.SemiBold, Foreground = Ui.Muted, IsHitTestVisible = false, Margin = new Thickness(0, Space.Inset + Space.Gap, 0, 2) };
        var row = (SessionRow)item;
        var markers = new List<string>();
        if (row.Current) markers.Add("this chat");
        if (row.Elsewhere) markers.Add("open in another tab");
        if (row.Session.StartedIn is { } started && !row.Current && !row.Elsewhere) markers.Add(started);
        // Line one is the title, which gives way (an ellipsis); line two is the time, the message count and the markers, which never do.
        var title = new TextBlock { Text = row.Session.Title.ReplaceLineEndings(" "), FontSize = 12, Foreground = Ui.TextBrush, MaxLines = 1, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis };
        var subtitle = new StackPanel { Name = "ChatSessionSubtitle", Orientation = Orientation.Horizontal, Spacing = 4 };
        void Part(string text, IBrush brush)
        {
            if (subtitle.Children.Count > 0) subtitle.Children.Add(new TextBlock { Text = "\u00B7", FontSize = 11, Foreground = Ui.Hint });
            subtitle.Children.Add(new TextBlock { Text = text, FontSize = 11, Foreground = brush, TextWrapping = TextWrapping.NoWrap });
        }
        Part(row.Relative, Ui.Muted);
        if (row.Session.Messages is { } messages) Part(messages == 1 ? "1 message" : $"{messages} messages", Ui.Muted);
        foreach (var marker in markers) Part(marker, row.Current && marker == "this chat" ? Ui.Accent : Ui.Muted);
        // One line on the popup's row grid: the title gives way (an ellipsis), the time, count and markers after it never do.
        var line = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8, Height = ChatPopup.RowHeight };
        title.VerticalAlignment = VerticalAlignment.Center;
        subtitle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(subtitle, 1);
        line.Children.Add(title);
        line.Children.Add(subtitle);
        var separator = new Border { Child = line, Name = "ChatSessionRow" };
        var full = $"{row.Session.Title}\n{row.When.ToString("f", CultureInfo.CurrentCulture)}" + (markers.Count > 0 ? "\n" + string.Join(", ", markers) : "");
        ToolTip.SetTip(separator, full);
        Avalonia.Automation.AutomationProperties.SetName(separator, full.ReplaceLineEndings(", "));
        return separator;
    }

    /// <summary>The flyout's content: a search box over a bounded, virtualized list. <paramref name="pick"/> gets the session chosen.</summary>
    internal (Control Panel, ListBox List, TextBox Search) BuildSessions(IReadOnlyList<ChatSessionInfo> all, Action<ChatSessionInfo> pick, DateTime now)
    {
        // The flyout opens from the + button's left edge, so only the window's width to the right of it is available.
        var top = TopLevel.GetTopLevel(this);
        var left = plus is not null && top is not null ? plus.TranslatePoint(default, top)?.X ?? 0 : 0;
        var room = top is null ? MenuMaxWidth : top.ClientSize.Width - Math.Max(0, left) - 16;
        return BuildSessionList(all, pick, now, transcript.Header?.SessionId, chatId, Math.Max(120, Bounds.Height * 0.4), Math.Clamp(Math.Min(Bounds.Width - 48, room), 280, MenuMaxWidth));
    }

    /// <summary>The same list without a chat: the resume launcher shows it for a workspace that has no chat view yet.</summary>
    internal static (Control Panel, ListBox List, TextBox Search) BuildSessionList(IReadOnlyList<ChatSessionInfo> all, Action<ChatSessionInfo> pick, DateTime now, string? own, string ownChat, double maxHeight, double width)
    {
        var search = ChatPopup.Search(new TextBox { Name = "ChatSessionSearch", PlaceholderText = "Search sessions\u2026" });
        var clear = Ui.IconButton("close", "Clear search", () => search.Text = "");
        clear.Name = "ChatSessionSearchClear";
        clear.IsVisible = false;
        var results = ChatPopup.Rows(new ListBox { Name = "ChatSessionMenu", MaxHeight = maxHeight });
        results.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<object>((item, _) => item is null ? null : SessionTemplate(item), false);
        ScrollViewer.SetHorizontalScrollBarVisibility(results, ScrollBarVisibility.Disabled);
        results.ContainerPrepared += (_, e) =>
        {
            if (e.Container is ListBoxItem { DataContext: SessionBucketHeader } header) { header.IsEnabled = false; header.Focusable = false; }
        };
        var note = Ui.Text("", Ui.Muted, 12);
        void Fill()
        {
            var query = search.Text ?? "";
            clear.IsVisible = query.Length > 0;
            var items = SessionItems(all, query, own, ownChat, now);
            results.ItemsSource = items;
            note.IsVisible = items.Count == 0;
            note.Text = all.Count == 0 ? "No sessions yet" : "No sessions found";
        }
        search.TextChanged += (_, _) => Fill();
        results.PointerReleased += (_, _) => { if (results.SelectedItem is SessionRow { Session: var session }) pick(session); };
        var searchRow = new DockPanel();
        DockPanel.SetDock(clear, Dock.Right);
        searchRow.Children.Add(clear);
        searchRow.Children.Add(search);
        // The list and the note share one cell that is never shorter than the rows of all the sessions (up to the cap), so typing a query that matches few or none keeps the popup its size.
        note.Margin = new Thickness(ChatPopup.RowInset, 4, 0, 0);
        note.VerticalAlignment = VerticalAlignment.Top;
        var body = new Grid { MinHeight = Math.Min(maxHeight, all.Count * ChatPopup.RowHeight) };
        body.Children.Add(results);
        body.Children.Add(note);
        results.VerticalAlignment = VerticalAlignment.Top;
        var panel = ChatPopup.Container("ChatSessionsPanel", "Past conversations", searchRow, body);
        panel.Width = width;
        Avalonia.Automation.AutomationProperties.SetName(panel, "Past conversations");
        Fill();
        return (panel, results, search);
    }

    /// <summary>What choosing a session does: this chat's own does nothing; one another tab runs focuses that tab; any other resumes in a new tab.</summary>
    internal void ChooseSession(ChatSessionInfo session)
    {
        if (session.SessionId == transcript.Header?.SessionId) return;
        if (session.ChatId is { } running && running != chatId) { env.OpenChat(running, session.Title, null); return; }
        env.OpenChat("chat-" + Guid.NewGuid().ToString("N")[..12], session.Title, new ChatOpenParams(workspaceId, "") { ResumeSessionId = session.SessionId });
    }

    // Past conversations: a flyout with a search box over the sessions the host lists, newest first, in date buckets.
    private async Task ShowSessions()
    {
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        flyout.Content = Ui.Text("Loading sessions\u2026", Ui.Muted, 12);
        ChatPopup.Show(flyout, plus!);
        try
        {
            var found = await env.Client.RequestAsync(ChatContract.Sessions, new ChatSessionsParams(workspaceId));
            flyout.Content = BuildSessions(found.Sessions, session => { flyout.Hide(); ChooseSession(session); }, DateTime.Now).Panel;
            ChatPopup.Bare(flyout);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            flyout.Content = Ui.Text("Couldn't list sessions: " + ChatErrors.Text(error), Ui.Muted, 12);
        }
    }
}