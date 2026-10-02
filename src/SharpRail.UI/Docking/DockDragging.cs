using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;


namespace SharpRail.UI.Docking;

public sealed partial class DockSurface
{
    private sealed record DragDraft(string Tab, string Source, Point Origin, long Epoch);
    /// <summary>A drop: an insertion, a new group at an edge, a hidden region, or (with <see cref="PaneWith"/>) sharing a pane with that tab.</summary>
    private sealed record DropTarget(string Group, int Index, string Edge, Rect Bounds, string Region = "", string PaneWith = "", string Direction = "");
    private sealed record DropSite(DropTarget Target, Action<bool> Paint, bool Priority = false);
    private readonly Dictionary<(string Group, int Index, string Edge), bool> dropValidity = [];
    private DragDraft? draft;
    private DropTarget? drop;
    private IPointer? capturedPointer;
    private bool dragging;
    public bool IsDragging => dragging;

    private void InstallPointerGestures()
    {
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (draft is null || draft.Epoch != Session.Epoch) return;
            var position = e.GetPosition(this);
            if (!dragging)
            {
                if (Math.Abs(position.X - draft.Origin.X) + Math.Abs(position.Y - draft.Origin.Y) < 5) return;
                dragging = true; capturedPointer = e.Pointer; e.Pointer.Capture(this);
            }
            PaintTargets(position);
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (!dragging) { draft = null; FlushRefresh(); return; }
            var origin = draft; var destination = drop;
            CancelDrag();
            if (origin is not null && destination is not null && origin.Epoch == Session.Epoch)
            {
                if (destination.PaneWith.Length > 0) Session.GroupTabs(destination.Group, origin.Tab, destination.PaneWith, destination.Direction);
                else if (destination.Region.Length > 0) Session.MoveToRegion(origin.Tab, origin.Source, destination.Region);
                else Session.Move(origin.Tab, origin.Source, destination.Group, destination.Index, destination.Edge);
            }
            e.Handled = true;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        PointerCaptureLost += (_, _) => { if (dragging) CancelDrag(); };
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape && draft is not null) { CancelDrag(); e.Handled = true; }
            else if (e.Key == Key.F6 && e.KeyModifiers.HasFlag(KeyModifiers.Control))
            {
                var visible = groupHeaders.Keys.OrderBy(id => Session.Group(id).Region == "bottom" ? 1 : 0)
                    .ThenBy(id => RectOf(groupHeaders[id]).Top).ThenBy(id => RectOf(groupHeaders[id]).Left).ToArray();
                if (visible.Length == 0) return;
                var active = Array.IndexOf(visible, Session.View.FocusedGroup);
                var direction = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
                var next = visible[(active + direction + visible.Length) % visible.Length];
                var tab = Session.Selected(next);
                if (tab is not null) Session.Select(next, tab.Id);
                else Session.Focus(next);
                FocusGroup(next); e.Handled = true;
            }
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void ArmDrag(PointerPressedEventArgs e, string tab, string source)
    {
        dropValidity.Clear();
        draft = new(tab, source, e.GetPosition(this), Session.Epoch);
    }

    private void CancelDrag()
    {
        draft = null; drop = null; dragging = false; overlay.Children.Clear(); dropValidity.Clear();
        var pointer = capturedPointer; capturedPointer = null;
        pointer?.Capture(null);
        FlushRefresh();
    }

    private Rect RectOf(Control control)
    {
        var position = control.TranslatePoint(default, this);
        return position is null ? default : new(position.Value, control.Bounds.Size);
    }

    private void PaintTargets(Point point)
    {
        overlay.Children.Clear(); drop = null;
        if (draft is null) return;
        var tab = Session.Tabs(draft.Source).FirstOrDefault(item => item.Id == draft.Tab);
        if (tab is null) return;
        var candidates = new List<DropSite>();
        bool Legal(string group, int index, string edge = "")
        {
            var key = (group, index, edge);
            if (!dropValidity.TryGetValue(key, out var valid))
            {
                valid = group.StartsWith("restore:", StringComparison.Ordinal)
                    ? Session.CanMoveToRegion(draft.Tab, draft.Source, group[8..])
                    : Session.CanMove(draft.Tab, draft.Source, group, index, edge);
                dropValidity[key] = valid;
            }
            return valid;
        }
        foreach (var site in sites.OrderByDescending(site => site.Header))
        {
            var bounds = RectOf(site.Control);
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;
            if (site.Group.StartsWith("restore:", StringComparison.Ordinal))
            {
                if (!Legal(site.Group, 0)) continue;
                candidates.Add(new(new("", 0, "", bounds, site.Group[8..]), active =>
                {
                    var hint = Hint(bounds, active);
                    hint.CornerRadius = new CornerRadius(0);
                    hint.BorderThickness = new Thickness(active ? 2 : 1);
                }));
                continue;
            }
            var group = Session.Group(site.Group);
            if (tab.Kind != "terminal" && tab.IsTool == (group.Region == "center")) continue;
            if (site.Header && stripLayouts.TryGetValue(site.Group, out var layout) && layout.Style != StripStyle.Horizontal)
            {
                ColumnTargets(site.Group, tab, layout.Scroller, Legal, candidates);
                continue;
            }
            if (site.Header)
            {
                var members = tabSites.GetValueOrDefault(site.Group) ?? [];
                var tabArea = new Rect(bounds.TopLeft, new Size(((Grid)site.Control).ColumnDefinitions[0].ActualWidth, bounds.Height));
                // Tabs are the only visible strip targets. The rest of the strip still appends, but it shows the
                // same insertion line after the last tab instead of a strip-wide frame; only an empty strip is framed.
                if (Legal(site.Group, members.Count))
                {
                    var lastTab = members.Count > 0 ? RectOf(members[^1].Control) : default;
                    var endMarker = new Rect(lastTab.Right - 2, bounds.Top, 2, bounds.Height).Intersect(tabArea);
                    candidates.Add(new(new(site.Group, members.Count, "", bounds), active =>
                    {
                        if (members.Count > 0)
                        {
                            if (!active || endMarker.Width <= 0) return;
                            var marker = Hint(endMarker, true);
                            marker.Background = Ui.Accent;
                            marker.BorderThickness = new Thickness(0);
                            marker.CornerRadius = new CornerRadius(0);
                            return;
                        }
                        var hint = Hint(bounds, active);
                        hint.CornerRadius = new CornerRadius(0);
                        hint.BorderThickness = new Thickness(active ? 2 : 1);
                        hint.Background = active ? Ui.PrimarySubtle : Brushes.Transparent;
                    }));
                }
                for (var i = 0; i < members.Count; i++)
                {
                    var member = RectOf(members[i].Control);
                    foreach (var after in new[] { false, true })
                    {
                        var index = i + (after ? 1 : 0);
                        if (!Legal(site.Group, index)) continue;
                        var half = new Rect(after ? member.Center.X : member.Left, member.Top, member.Width / 2, member.Height).Intersect(tabArea);
                        if (half.Width <= 0 || half.Height <= 0) continue;
                        var markerBounds = new Rect(after ? member.Right - 2 : member.Left, bounds.Top, 2, bounds.Height).Intersect(tabArea);
                        candidates.Add(new(new(site.Group, index, "", half), active =>
                        {
                            if (!active || markerBounds.Width <= 0) return;
                            var marker = Hint(markerBounds, true);
                            marker.Background = Ui.Accent;
                            marker.BorderThickness = new Thickness(0);
                            marker.CornerRadius = new CornerRadius(0);
                        }));
                    }
                }
                continue;
            }
            var header = RectOf(groupHeaders[group.Id]);
            var section = group.Folded ? bounds : new Rect(bounds.Left, header.Top, bounds.Width, bounds.Bottom - header.Top);
            if (group.Region == "center")
            {
                // With vertical tabs on, the centre cannot be split: grouping is what that layout does instead.
                if (CenterTabs?.Invoke() is { Vertical: true }) continue;
                foreach (var edge in new[] { "left", "right", "top", "bottom" })
                {
                    var horizontal = edge is "left" or "right";
                    if ((horizontal ? section.Width < 640 : section.Height < 360) || !Legal(site.Group, 0, edge)) continue;
                    var hint = SplitTarget(section, edge);
                    var half = Edge(section, edge, .5);
                    candidates.Add(new(new(site.Group, 0, edge, hint), active => Hint(active ? half : hint, active, active)));
                }
                continue;
            }
            if (group.Folded && Legal(site.Group, Session.Tabs(site.Group).Count))
                candidates.Add(new(new(site.Group, Session.Tabs(site.Group).Count, "", bounds), active =>
                {
                    var hint = Hint(bounds, active);
                    hint.CornerRadius = new CornerRadius(0);
                    hint.BorderThickness = new Thickness(0);
                }));
            foreach (var edge in new[] { "before", "after" })
            {
                if (!Legal(site.Group, 0, edge)) continue;
                var direction = group.Region == "bottom" ? edge == "before" ? "left" : "right" : edge == "before" ? "top" : "bottom";
                var half = AuxiliaryTarget(section, direction);
                if (half.Width <= 0 || half.Height <= 0) continue;
                candidates.Add(new(new(site.Group, 0, edge, half), active => Hint(half, active)));
            }
        }
        if ((!Session.State.BottomVisible || !Session.State.Groups.Any(group => group.Region == "bottom")) && Legal("restore:bottom", 0))
        {
            var bounds = HiddenBottomBounds();
            candidates.Add(new(new("", 0, "", bounds, "bottom"), active =>
            {
                var hint = Hint(bounds, active);
                hint.CornerRadius = new CornerRadius(0);
                hint.BorderThickness = new Thickness(0, active ? 2 : 1, 0, 0);
                hint.BorderBrush = Ui.Accent;
            }, true));
        }
        var winner = candidates.Where(candidate => candidate.Target.Bounds.Contains(point))
            .OrderByDescending(candidate => candidate.Priority)
            .ThenBy(candidate => CornerDistance(candidate.Target.Bounds, point)).FirstOrDefault();
        drop = winner?.Target;
        foreach (var candidate in candidates) candidate.Paint(ReferenceEquals(candidate, winner));
    }

    // A column's rows: the top and bottom quarters insert before and after, and the band between them shows the dragged
    // tab together with that one, in the setting's arrangement or, over a pane member, the arrangement it joins.
    private void ColumnTargets(string group, DockTab tab, ScrollViewer scroller, Func<string, int, string, bool> legal, List<DropSite> candidates)
    {
        var members = tabSites.GetValueOrDefault(group) ?? [];
        var area = RectOf(scroller);
        if (area.Width <= 0 || area.Height <= 0) return;
        Action<bool> Line(Rect line) => active =>
        {
            if (!active || line.Width <= 0 || line.Height <= 0) return;
            var marker = Hint(line, true);
            marker.Background = Ui.Accent; marker.BorderThickness = new Thickness(0); marker.CornerRadius = new CornerRadius(0);
        };
        if (legal(group, members.Count, ""))
        {
            var last = members.Count > 0 ? RectOf(members[^1].Control) : new Rect(area.Left, area.Top, area.Width, 0);
            candidates.Add(new(new(group, members.Count, "", area), Line(new Rect(area.Left, last.Bottom - 2, area.Width, 2).Intersect(area))));
        }
        var sameGroup = Session.Tabs(group).Any(item => item.Id == tab.Id) && !tab.IsTool && Session.Group(group).Region == "center";
        var defaultDirection = CenterTabs?.Invoke().DefaultPaneDirection ?? "horizontal";
        for (var i = 0; i < members.Count; i++)
        {
            var row = RectOf(members[i].Control).Intersect(area);
            if (row.Height <= 0) continue;
            var quarter = row.Height / 4;
            foreach (var after in new[] { false, true })
            {
                var index = i + (after ? 1 : 0);
                if (!legal(group, index, "")) continue;
                var zone = new Rect(row.Left, after ? row.Bottom - quarter : row.Top, row.Width, quarter);
                candidates.Add(new(new(group, index, "", zone), Line(new Rect(row.Left, after ? row.Bottom - 2 : row.Top, row.Width, 2))));
            }
            var target = members[i].Tab;
            var pane = Session.PaneFor(group, target);
            if (!sameGroup || target == tab.Id || pane?.TabIds.Contains(tab.Id) == true) continue;
            var band = new Rect(row.Left, row.Top + quarter, row.Width, row.Height - 2 * quarter);
            var direction = pane?.Direction ?? defaultDirection;
            candidates.Add(new(new(group, 0, "", band, PaneWith: target, Direction: direction), active =>
            {
                var hint = Hint(band, active);
                hint.Tag = direction == "vertical" ? "DropShowUnder" : "DropShowBeside";
                hint.CornerRadius = new CornerRadius(0);
                hint.BorderThickness = new Thickness(active ? 2 : 0);
            }));
        }
    }

    private Rect HiddenBottomBounds()
    {
        var centers = sites.Where(site => !site.Header && !site.Group.StartsWith("restore:", StringComparison.Ordinal) &&
            Session.Group(site.Group).Region == "center").Select(site => RectOf(site.Control)).ToArray();
        var leftRail = sites.FirstOrDefault(site => site.Group == "restore:left").Control;
        var rightRail = sites.FirstOrDefault(site => site.Group == "restore:right").Control;
        var left = leftRail is null ? 0 : RectOf(leftRail).Right;
        var right = rightRail is null ? Bounds.Width : RectOf(rightRail).Left;
        var x = centers.Length == 0 ? left : centers.Min(rect => rect.Left);
        var end = centers.Length == 0 ? right : centers.Max(rect => rect.Right);
        if (Session.State.BottomAlignment is "full" or "center-left") x = left;
        if (Session.State.BottomAlignment is "full" or "center-right") end = right;
        return new Rect(x, Bounds.Height - 24, Math.Max(0, end - x), 24);
    }

    private Border Hint(Rect rect, bool active, bool split = false)
    {
        var color = Ui.Accent.Color;
        var hint = new Border
        {
            Width = rect.Width,
            Height = rect.Height,
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(split ? 2 : 1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(active ? (byte)255 : (byte)51, color.R, color.G, color.B)),
            Background = new SolidColorBrush(Color.FromArgb(active ? (byte)51 : (byte)26, color.R, color.G, color.B))
        };
        Canvas.SetLeft(hint, rect.Left); Canvas.SetTop(hint, rect.Top); overlay.Children.Add(hint);
        return hint;
    }

    private static Rect Edge(Rect bounds, string edge, double ratio) => edge switch
    {
        "left" => new(bounds.Left, bounds.Top, bounds.Width * ratio, bounds.Height),
        "right" => new(bounds.Right - bounds.Width * ratio, bounds.Top, bounds.Width * ratio, bounds.Height),
        "top" => new(bounds.Left, bounds.Top, bounds.Width, bounds.Height * ratio),
        _ => new(bounds.Left, bounds.Bottom - bounds.Height * ratio, bounds.Width, bounds.Height * ratio)
    };

    private static Rect SplitTarget(Rect bounds, string edge) => edge switch
    {
        "left" => new(bounds.Left + 4, bounds.Top + bounds.Height / 4, bounds.Width / 5, bounds.Height / 2),
        "right" => new(bounds.Right - 4 - bounds.Width / 5, bounds.Top + bounds.Height / 4, bounds.Width / 5, bounds.Height / 2),
        "top" => new(bounds.Left + bounds.Width / 4, bounds.Top + 32, bounds.Width / 2, bounds.Height / 5),
        _ => new(bounds.Left + bounds.Width / 4, bounds.Bottom - 4 - bounds.Height / 5, bounds.Width / 2, bounds.Height / 5)
    };

    private static Rect AuxiliaryTarget(Rect bounds, string edge) => edge switch
    {
        "left" => new(bounds.Left + 4, bounds.Top + 4, bounds.Width / 2 - 4, bounds.Height - 8),
        "right" => new(bounds.Center.X, bounds.Top + 4, bounds.Width / 2 - 4, bounds.Height - 8),
        "top" => new(bounds.Left + 4, bounds.Top + 4, bounds.Width - 8, bounds.Height / 2 - 4),
        _ => new(bounds.Left + 4, bounds.Center.Y, bounds.Width - 8, bounds.Height / 2 - 4)
    };

    private static double CornerDistance(Rect bounds, Point point)
    {
        double Distance(Point corner) => Math.Sqrt(Math.Pow(corner.X - point.X, 2) + Math.Pow(corner.Y - point.Y, 2));
        return Math.Round((Distance(bounds.TopLeft) + Distance(bounds.TopRight) + Distance(bounds.BottomLeft) + Distance(bounds.BottomRight)) / 4, 4);
    }
}