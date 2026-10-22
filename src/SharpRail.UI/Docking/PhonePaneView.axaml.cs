using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace SharpRail.UI.Docking;

/// <summary>
/// A tab group on a phone: one member over the whole centre, under a strip of chips naming every member. Which member
/// shows is the phone's own choice; the group's layout (members, direction, weights) stays as the host keeps it.
/// </summary>
public sealed partial class PhonePaneView : UserControl
{
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(260);
    private readonly PageSlide pageSlide = new(SlideTime);
    private readonly StackPanel chips;
    private readonly Panel hosts;
    private readonly Func<DockTab, Control> content;
    private readonly Action<string> shown;
    private readonly Func<string, bool> active;
    private DockTab[] members = [];
    private Border? current;
    private int index;
    // A member sliding in beside the shown one: under a finger while held, then the rest of the way.
    private (Border To, int Index)? slide;
    private double progress;
    private bool held;
    private int run;

    public PhonePaneView()
    {
        AvaloniaXamlLoader.Load(this);
        chips = this.FindControl<StackPanel>("PaneChips")!;
        hosts = this.FindControl<Panel>("PaneMembers")!;
        content = _ => new Panel();
        shown = _ => { };
        active = _ => false;
    }

    internal PhonePaneView(string groupId, string paneId, Func<DockTab, Control> content, Action<string> shown, Func<string, bool> active) : this()
    {
        GroupId = groupId; PaneId = paneId;
        this.content = content; this.shown = shown; this.active = active;
    }

    internal string GroupId { get; } = "";
    internal string PaneId { get; } = "";

    /// <summary>The member on screen.</summary>
    internal string Shown => members.ElementAtOrDefault(index)?.Id ?? "";

    /// <summary>Whether the tab is a member this phone is not showing.</summary>
    internal bool Hides(string tabId) => tabId != Shown && members.Any(member => member.Id == tabId);

    /// <summary>Shows these members with <paramref name="shownId"/> on screen, keeping the chips and hosts that still fit.</summary>
    internal void Show(IReadOnlyList<DockTab> next, string shownId)
    {
        if (slide is not null) End(false);
        var same = next.Select(tab => (tab.Id, tab.Title)).SequenceEqual(members.Select(tab => (tab.Id, tab.Title)));
        members = [.. next];
        index = Math.Max(0, Array.FindIndex(members, member => member.Id == shownId));
        if (!same) BuildChips();
        current ??= Host();
        if (current.Parent is null) hosts.Children.Add(current);
        Fill(current, members[index]);
        Paint();
    }

    /// <summary>Repaints the selection and activity marks.</summary>
    internal void Paint()
    {
        foreach (var chip in chips.Children.OfType<Button>())
        {
            var id = chip.Tag as string;
            chip.Classes.Set("selected", id == Shown);
            if (chip.Content is StackPanel { Children: [_, Ellipse dot] }) dot.IsVisible = id != Shown && id is not null && active(id);
        }
    }

    /// <summary>Slides to a member, as a chip does.</summary>
    internal void Select(string tabId)
    {
        var target = Array.FindIndex(members, member => member.Id == tabId);
        if (target < 0 || target == index && slide is null) return;
        if (slide is { } running) End(running.Index == target || progress >= .5);
        if (target == index) return;
        Begin(target);
        Settle(true);
    }

    /// <summary>Whether a finger can drag the next (+1) or previous (-1) member in.</summary>
    internal bool CanSwipe(int direction) => slide is null && index + direction >= 0 && index + direction < members.Length;

    /// <summary>Whether the visual lies in the members' area, where a sideways drag moves between members.</summary>
    internal bool Holds(Visual? visual) => visual is not null && (ReferenceEquals(visual, hosts) || hosts.IsVisualAncestorOf(visual));

    internal void Hold(int direction)
    {
        held = true;
        Begin(index + direction);
    }

    /// <summary>Places the held members for a finger that has travelled this far sideways.</summary>
    internal void Drag(double travelled)
    {
        if (!held || slide is not { } pages) return;
        progress = Math.Clamp(-Math.Sign(pages.Index - index) * travelled / Math.Max(1, hosts.Bounds.Width), 0, 1);
        Place();
    }

    internal void Release(bool complete)
    {
        if (!held) return;
        held = false;
        if (slide is not null) Settle(complete);
    }

    private void BuildChips()
    {
        chips.Children.Clear();
        foreach (var member in members)
        {
            var dot = new Ellipse { Classes = { "PaneChipDot" }, IsVisible = false };
            var label = new TextBlock { Text = member.Title, VerticalAlignment = VerticalAlignment.Center, FontSize = 13 };
            var chip = new Button
            {
                Name = "PaneChip",
                Tag = member.Id,
                Classes = { "PaneChip" },
                Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { label, dot } }
            };
            AutomationProperties.SetName(chip, member.Title);
            var id = member.Id;
            chip.Click += (_, _) => Select(id);
            chips.Children.Add(chip);
        }
    }

    private static Border Host() => new() { Name = "PaneMemberHost", ClipToBounds = true };

    private void Fill(Border host, DockTab tab)
    {
        host.Tag = tab.Id;
        var body = content(tab);
        if (ReferenceEquals(host.Child, body)) return;
        host.Child = null;
        host.Child = DockSurface.Adopt(body);
    }

    private void Begin(int target)
    {
        var to = Host();
        hosts.Children.Add(to);
        Fill(to, members[target]);
        slide = (to, target);
        progress = 0;
        Place();
    }

    private void Place()
    {
        if (slide is not { } pages || current is null) return;
        // As on the workbench's pages: a new transform each frame, which PageSlide then sets, is what moves on Android.
        current.RenderTransform = new TranslateTransform();
        pages.To.RenderTransform = new TranslateTransform();
        pageSlide.Update(progress, current, pages.To, pages.Index > index, hosts.Bounds.Width, []);
    }

    private void Settle(bool complete)
    {
        var mine = ++run;
        var from = progress;
        var to = complete ? 1.0 : 0;
        var time = SlideTime * Math.Max(0.4, Math.Abs(to - from));
        var started = System.Diagnostics.Stopwatch.StartNew();
        void Step(TimeSpan _)
        {
            if (mine != run || slide is null) return;
            var elapsed = Math.Min(1, started.Elapsed / time);
            if (elapsed < 1 && hosts.Bounds.Width > 0 && TopLevel.GetTopLevel(this) is { } top)
            {
                progress = from + (to - from) * (1 - Math.Pow(1 - elapsed, 3));
                Place();
                top.RequestAnimationFrame(Step);
                return;
            }
            End(complete);
        }
        Step(default);
    }

    // The member left behind leaves the tree with its content, as an unselected tab does.
    private void End(bool complete)
    {
        run++;
        held = false;
        if (slide is not { } pages || current is null) return;
        slide = null;
        pageSlide.Reset(current);
        pageSlide.Reset(pages.To);
        var gone = complete ? current : pages.To;
        hosts.Children.Remove(gone);
        gone.Child = null;
        if (complete) { current = pages.To; index = pages.Index; shown(Shown); }
        Paint();
    }
}