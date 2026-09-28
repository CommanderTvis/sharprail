using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using SharpRail.UI.Rendering;

namespace SharpRail.UI;

public sealed partial class WorkbenchWindow
{
    private void WireGestureNotification()
    {
        var toast = this.FindControl<Border>("GestureToast")!;
        var dismiss = this.FindControl<Button>("DismissGestureToast")!;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        void Appearance()
        {
            toast.Width = Bounds.Width < 640 ? Math.Max(0, Bounds.Width - 24) : 356;
            toast.BoxShadow = new BoxShadows(new BoxShadow
            {
                OffsetY = 4,
                Blur = 16,
                Color = Color.FromArgb(ActualThemeVariant == ThemeVariant.Light ? (byte)31 : (byte)89, 0, 0, 0)
            });
        }
        SizeChanged += (_, _) => Appearance();
        ActualThemeVariantChanged += (_, _) => Appearance();
        Appearance();
        void Hide() { timer.Stop(); toast.IsVisible = false; }
        timer.Tick += (_, _) => Hide();
        dismiss.Content = Ui.Icon("close", Ui.Muted, 14);
        dismiss.Click += (_, _) => Hide();
        surface.GestureCanceled += () => { timer.Stop(); toast.IsVisible = true; timer.Start(); };
        Closed += (_, _) => timer.Stop();
    }
}
