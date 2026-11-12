using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Processes.UI;

/// <summary>A row's CPU trend: one polyline over its last samples, scaled to the row's own peak (at least 1 percent).</summary>
internal sealed class Sparkline : Control
{
    private IReadOnlyList<double> values = [];

    internal Sparkline() { Width = 56; Height = 16; IsHitTestVisible = false; }

    internal void Show(IReadOnlyList<double> next)
    {
        values = next;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (values.Count < 2) return;
        var max = Math.Max(values.Max(), 1);
        var geometry = new StreamGeometry();
        using (var figure = geometry.Open())
        {
            for (var i = 0; i < values.Count; i++)
            {
                var point = new Point(i * Bounds.Width / (values.Count - 1), Bounds.Height - 1 - values[i] / max * (Bounds.Height - 2));
                if (i == 0) figure.BeginFigure(point, false); else figure.LineTo(point);
            }
            figure.EndFigure(false);
        }
        context.DrawGeometry(null, new Pen(Ui.Accent, 1), geometry);
    }
}