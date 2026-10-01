using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;


namespace SharpRail.UI.Docking;

public sealed class ResizeHandle : Border
{
    private Point? origin;
    private IPointer? pointer;
    public bool IsActive => origin is not null;
    public event Action? GestureEnded;
    internal bool AbortGesture()
    {
        if (origin is null) return false;
        origin = null; var captured = pointer; pointer = null;
        captured?.Capture(null); GestureEnded?.Invoke();
        return true;
    }
    private readonly bool horizontal;
    private readonly Action<double> preview;
    private readonly Action<double> commit;
    private readonly Action cancel;
    private Func<(double Value, double Minimum, double Maximum)> range = () => (0, 0, 0);
    private Action<double>? setValue;
    internal void ConfigureRange(Func<(double Value, double Minimum, double Maximum)> read, Action<double> write)
    {
        range = read; setValue = write;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new SeparatorPeer(this);

    private sealed class SeparatorPeer(ResizeHandle owner) : ControlAutomationPeer(owner), IRangeValueProvider
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Separator;
        public bool IsReadOnly => owner.setValue is null || Minimum >= Maximum;
        public double Value => owner.range().Value * 100;
        public double Minimum => owner.range().Minimum * 100;
        public double Maximum => owner.range().Maximum * 100;
        public double SmallChange => 10;
        public double LargeChange => 100;
        public void SetValue(double value)
        {
            if (IsReadOnly) throw new InvalidOperationException("This separator cannot be resized.");
            if (!double.IsFinite(value) || value < Minimum || value > Maximum)
                throw new ArgumentOutOfRangeException(nameof(value));
            owner.setValue!(value / 100);
        }
    }
    public ResizeHandle(bool horizontal, Action<double> preview, Action<double> commit, Action cancel)
    {
        this.horizontal = horizontal; this.preview = preview; this.commit = commit; this.cancel = cancel;
        Name = "PaneSeparator";
        AutomationProperties.SetName(this, horizontal ? "Vertical pane separator" : "Horizontal pane separator");
        AutomationProperties.SetHelpText(this, horizontal ? "Resize with Left and Right arrows" : "Resize with Up and Down arrows");
        Focusable = true;
        Background = Brushes.Transparent;
        Margin = horizontal ? new Thickness(-3.5, 0) : new Thickness(0, -3.5);
        ZIndex = 20;
        var line = new Border { Background = Ui.BorderBrush };
        if (horizontal) { line.Width = 1; line.HorizontalAlignment = HorizontalAlignment.Center; }
        else { line.Height = 1; line.VerticalAlignment = VerticalAlignment.Center; }
        Child = line;
        Cursor = new Cursor(horizontal ? StandardCursorType.SizeWestEast : StandardCursorType.SizeNorthSouth);
        PointerEntered += (_, _) => line.Background = Ui.Accent;
        PointerExited += (_, _) => { if (origin is null) line.Background = Ui.BorderBrush; };
        PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            origin = e.GetPosition(TopLevel.GetTopLevel(this)); pointer = e.Pointer; pointer.Capture(this);
            Focus(); e.Handled = true;
        };
        PointerMoved += (_, e) =>
        {
            if (origin is null) return;
            var now = e.GetPosition(TopLevel.GetTopLevel(this));
            preview(horizontal ? now.X - origin.Value.X : now.Y - origin.Value.Y);
        };
        PointerReleased += (_, e) =>
        {
            if (origin is null) return;
            var now = e.GetPosition(TopLevel.GetTopLevel(this));
            var delta = horizontal ? now.X - origin.Value.X : now.Y - origin.Value.Y;
            origin = null; pointer = null; e.Pointer.Capture(null); commit(delta); GestureEnded?.Invoke(); e.Handled = true;
        };
        PointerCaptureLost += (_, _) => { pointer = null; if (origin is not null) { origin = null; cancel(); GestureEnded?.Invoke(); } };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && origin is not null)
            {
                origin = null; var captured = pointer; pointer = null; captured?.Capture(null);
                cancel(); GestureEnded?.Invoke(); e.Handled = true;
            }
            else if (e.Key is Key.Home or Key.End || (horizontal ? e.Key is Key.Left or Key.Right : e.Key is Key.Up or Key.Down))
            {
                var extent = Parent is Control parent ? horizontal ? parent.Bounds.Width : parent.Bounds.Height : 0;
                var step = e.Key is Key.Home or Key.End || e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? 1 : .1;
                var topLevel = TopLevel.GetTopLevel(this);
                commit((e.Key is Key.Left or Key.Up or Key.Home ? -1 : 1) * step * extent);
                topLevel?.GetLogicalDescendants().OfType<ResizeHandle>().FirstOrDefault(handle => handle.Name == Name)?.Focus();
                e.Handled = true;
            }
        };
    }
}