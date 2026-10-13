using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;

namespace SharpRail.Plugins.UI.Kit;

internal sealed class DrawnTreeLabel : Decorator
{
    private readonly DrawnTree tree;
    internal DrawnTreeEntry Entry { get; private set; }
    private TextLayout? title, role;
    private double textWidth = -1;
    private bool selected;
    private bool dropTarget;
    private double activationX;
    private Control? icon;
    private object? iconKey;
    internal bool RoleVisible => Entry.Role is not null && (IsPointerOver || this.FindAncestorOfType<ListBoxItem>()?.IsFocused == true);

    internal DrawnTreeLabel(DrawnTree tree, DrawnTreeEntry entry)
    {
        this.tree = tree; Entry = entry;
        Height = tree.SpecColors ? 28 : 24;
        ClipToBounds = true;
        DataContextChanged += (_, _) =>
        {
            if (DataContext is not DrawnTreeEntry next || ReferenceEquals(next, Entry)) return;
            ContextMenu?.Close(); ContextMenu = null;
            Entry = next;
            ToolTip.SetTip(this, Entry.Tip);
            Reset();
            Refresh();
        };
        PointerPressedEventArgs? pressed = null;
        Point start = default;
        bool dragging = false;
        PointerPressed += (_, e) =>
        {
            pressed = null;
            activationX = e.GetPosition(this).X;
            if (tree.Drag is not null && e.Pointer.Type != PointerType.Touch && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && activationX >= Entry.Depth * 12 + 20)
            { pressed = e; start = e.GetPosition(this); dragging = false; e.Pointer.Capture(this); }
        };
        PointerMoved += async (_, e) =>
        {
            if (pressed is null || tree.Drag is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || Math.Abs(e.GetPosition(this).X - start.X) + Math.Abs(e.GetPosition(this).Y - start.Y) < 6) return;
            var gesture = pressed; pressed = null; dragging = true;
            try { await tree.Drag(Entry, gesture); }
            finally { dragging = false; gesture.Pointer.Capture(null); }
        };
        PointerReleased += (_, e) =>
        {
            if (pressed is { } gesture && !dragging) Entry.Activate?.Invoke(gesture.ClickCount);
            pressed = null;
            if (ReferenceEquals(e.Pointer.Captured, this)) e.Pointer.Capture(null);
        };
        PointerCaptureLost += (_, _) => pressed = null;
        Tapped += (_, e) => activationX = e.GetPosition(this).X;
        RowActivation.OnActivate(this, _ => true, clicks =>
        {
            tree.SelectedItem = Entry;
            if (Entry.CanExpand && activationX < Entry.Depth * 12 + 20) tree.Expand(Entry, !Entry.Expanded);
            else if (pressed is null && !dragging) Entry.Activate?.Invoke(clicks);
        });
        ContextRequested += (_, e) =>
        {
            if (Entry.Menu is null) return;
            OpenMenu();
            e.Handled = true;
        };
        PointerEntered += (_, _) => InvalidateMeasure();
        PointerExited += (_, _) => InvalidateMeasure();
        GotFocus += (_, _) => InvalidateMeasure();
        LostFocus += (_, _) => InvalidateMeasure();
        ToolTip.SetTip(this, entry.Tip);
    }

    internal void OpenMenu()
    {
        if (Entry.Menu is null) return;
        tree.SelectedItem = Entry;
        ContextMenu?.Close();
        ContextMenu = Entry.Menu();
        ContextMenu.Open(this);
    }

    private void Reset()
    {
        title?.Dispose(); title = null;
        role?.Dispose(); role = null;
        textWidth = -1;
        InvalidateMeasure();
    }

    internal void Refresh(bool forceIcon = false)
    {
        if (dropTarget != Entry.DropTarget) { dropTarget = Entry.DropTarget; InvalidateVisual(); }
        var active = ReferenceEquals(tree.SelectedItem, Entry);
        var key = Entry.IconKey?.Invoke(active) ?? Entry;
        if (forceIcon || !Equals(iconKey, key) || (Entry.IconKey is null && selected != active))
        {
            iconKey = key;
            icon = Entry.Icon?.Invoke(active);
            Child = icon;
            Reset();
        }
        else InvalidateMeasure();
        if (selected != active) Reset();
        selected = active;
        if (this.FindAncestorOfType<ListBoxItem>() is { } item)
        {
            Avalonia.Automation.AutomationProperties.SetName(item, Entry.Text);
            Avalonia.Automation.AutomationProperties.SetHelpText(item, Entry.Help ?? Entry.Id);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (this.FindAncestorOfType<ListBoxItem>() is { } item)
        {
            Avalonia.Automation.AutomationProperties.SetName(item, Entry.Text);
            Avalonia.Automation.AutomationProperties.SetHelpText(item, Entry.Help ?? Entry.Id);
        }
        Ui.ThemeChanged += Reset;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Ui.ThemeChanged -= Reset;
        Reset();
        base.OnDetachedFromVisualTree(e);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        icon?.Measure(new Size(14, 14));
        var width = tree.SpecColors ? Math.Max(1, availableSize.Width - Entry.Depth * 12 - 42) : double.PositiveInfinity;
        if (title is not null && textWidth == width && (role is not null) == RoleVisible)
            return new Size(Entry.Depth * 12 + 42 + title.Width + (role is null ? 0 : role.Width + 8), Height);
        title?.Dispose(); role?.Dispose();
        var face = new Typeface(Ui.InterfaceFont, Entry.Italic ? FontStyle.Italic : FontStyle.Normal, Ui.InterfaceWeight);
        role = RoleVisible
            ? new TextLayout(Entry.Role, face, 12, Entry.Main || selected ? Ui.Accent : Ui.Hint) : null;
        textWidth = width;
        title = new TextLayout(Entry.Text, face, Ui.FontSize,
            Entry.Muted ? Ui.Hint : tree.SpecColors && !selected ? Ui.Muted : Ui.TextBrush,
            textTrimming: TextTrimming.CharacterEllipsis, maxWidth: Math.Max(1, width - (role is null ? 0 : role.Width + 8)), maxLines: 1);
        return new Size(Entry.Depth * 12 + 42 + title.Width + (role is null ? 0 : role.Width + 8), Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        icon?.Arrange(new Rect(Entry.Depth * 12 + 24, (finalSize.Height - 14) / 2, 14, 14));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        if (Entry.DropTarget) context.DrawRectangle(Ui.PrimarySubtle, new Pen(Ui.Accent), new Rect(Bounds.Size).Deflate(.5), 4, 4);
        if (Entry.Skeleton)
        {
            context.DrawRectangle(Ui.Hover, null, new Rect(4, 4, Math.Max(0, Bounds.Width - 8), 16), 4, 4);
            return;
        }
        if (title is null) return;
        var x = Entry.Depth * 12d;
        if (tree.SpecColors && selected)
            context.DrawRectangle(Ui.PrimarySubtle, new Pen(Ui.PrimaryMuted), new Rect(Bounds.Size).Deflate(.5), 4, 4);
        if (Entry.CanExpand)
        {
            var pen = new Pen(Ui.Muted, 1.3);
            if (Entry.Expanded)
            {
                context.DrawLine(pen, new(x + 5, 10), new(x + 8, 13));
                context.DrawLine(pen, new(x + 8, 13), new(x + 11, 10));
            }
            else
            {
                context.DrawLine(pen, new(x + 6, 9), new(x + 9, 12));
                context.DrawLine(pen, new(x + 9, 12), new(x + 6, 15));
            }
        }
        title.Draw(context, new Point(x + 42, (Bounds.Height - title.Height) / 2));
        role?.Draw(context, new Point(Math.Max(x + 42 + title.Width + 8, Bounds.Width - role.Width), (Bounds.Height - role.Height) / 2));
    }
}