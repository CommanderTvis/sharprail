using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace SharpRail.UI.Rendering;

/// <summary>
/// Draws every text box's caret itself, one <see cref="LayoutRoles.CaretGap"/> clear of the character before it at the end of the text.
/// Avalonia's presenter paints the caret flush against the last character and seals that drawing, so its own caret is made transparent
/// and an overlay in the same scrolled panel draws the visible one. The text, selection and input method are the presenter's, untouched.
/// </summary>
public static class TextBoxCaret
{
    public static readonly AttachedProperty<bool> IsGappedProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsGapped", typeof(TextBoxCaret));

    static TextBoxCaret() => IsGappedProperty.Changed.AddClassHandler<TextBox>((box, args) =>
    {
        if (args.NewValue is not true) return;
        box.TemplateApplied += Apply;
    });

    public static bool GetIsGapped(TextBox box) => box.GetValue(IsGappedProperty);
    public static void SetIsGapped(TextBox box, bool value) => box.SetValue(IsGappedProperty, value);

    private static void Apply(object? sender, TemplateAppliedEventArgs e)
    {
        if (sender is TextBox box) Install(box, e.NameScope);
    }

    private static void Install(TextBox box, INameScope scope)
    {
        var presenter = scope?.Find<TextPresenter>("PART_TextPresenter");
        if (presenter?.Parent is not Panel panel || panel.Children.OfType<CaretOverlay>().Any()) return;
        presenter.CaretBrush = Brushes.Transparent;
        presenter.Margin = new Thickness(0, 0, LayoutRoles.CaretGap, 0);
        panel.Children.Add(new CaretOverlay(box, presenter));
    }

    private sealed class CaretOverlay : Control
    {
        private static readonly TimeSpan Blink = TimeSpan.FromMilliseconds(500);
        private readonly TextBox _box;
        private readonly TextPresenter _presenter;
        private readonly DispatcherTimer _timer = new() { Interval = Blink };
        private bool _on = true;

        public CaretOverlay(TextBox box, TextPresenter presenter)
        {
            _box = box;
            _presenter = presenter;
            IsHitTestVisible = false;
            _timer.Tick += (_, _) => { _on = !_on; InvalidateVisual(); };
            box.PropertyChanged += (_, args) =>
            {
                if (args.Property == TextBox.CaretIndexProperty || args.Property == TextBox.TextProperty ||
                    args.Property == TextBox.SelectionStartProperty || args.Property == TextBox.SelectionEndProperty ||
                    args.Property == InputElement.IsKeyboardFocusWithinProperty) Restart();
            };
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); Restart(); }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { base.OnDetachedFromVisualTree(e); _timer.Stop(); }

        /// <summary>Shows the caret solid and restarts the blink, as typing or moving does in any editor.</summary>
        private void Restart()
        {
            _on = true;
            _timer.Stop();
            if (_box.IsKeyboardFocusWithin && VisualRoot is not null) _timer.Start();
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (!_on || !_box.IsKeyboardFocusWithin || _box.SelectionStart != _box.SelectionEnd) return;
            var length = _box.Text?.Length ?? 0;
            var index = Math.Clamp(_presenter.CaretIndex, 0, length);
            var rect = _presenter.TextLayout.HitTestTextPosition(index);
            var x = Math.Floor(rect.X) + 0.5 + (index > 0 && index == length ? LayoutRoles.CaretGap : 0);
            var brush = _box.CaretBrush ?? _box.Foreground ?? Ui.TextBrush;
            context.DrawLine(new Pen(brush), new Point(x, Math.Floor(rect.Y) + 0.5), new Point(x, Math.Ceiling(rect.Bottom) - 0.5));
        }
    }
}