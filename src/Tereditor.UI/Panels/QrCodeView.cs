using System.Collections;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

using QRCoder;

namespace SharpRail.UI.Panels;

/// <summary>Draws text as a QR code, dark on white whatever the theme, so a camera reads it.</summary>
public sealed class QrCodeView : Control
{
    private List<BitArray> modules = [];

    public string Text
    {
        get;
        set
        {
            field = value;
            using var generator = new QRCodeGenerator();
            using var code = generator.CreateQrCode(value, QRCodeGenerator.ECCLevel.M);
            // The matrix carries its quiet zone.
            modules = [.. code.ModuleMatrix];
            InvalidateVisual();
        }
    } = "";

    public override void Render(DrawingContext context)
    {
        var side = Math.Min(Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.White, new Rect(0, 0, side, side));
        if (modules.Count == 0) return;
        var cell = side / modules.Count;
        for (var row = 0; row < modules.Count; row++)
            for (var column = 0; column < modules.Count; column++)
                if (modules[row][column])
                    // Cells overlap by a hair so no seam shows between them at fractional sizes.
                    context.FillRectangle(Brushes.Black, new Rect(column * cell, row * cell, cell + .5, cell + .5));
    }
}