using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;

namespace SharpRail.Scintilla;

/// <summary>A diagnostic underline in UTF-8 document byte offsets. Severity is 1 (error) through 4 (hint).</summary>
public sealed record ScintillaDiagnostic(int Offset, int Length, int Severity, string Message = "");

public sealed partial class ScintillaEditor
{
    private IReadOnlyList<ScintillaDiagnostic> diagnosticMessages = [];
    private readonly DispatcherTimer diagnosticHoverTimer = new() { Interval = TimeSpan.FromMilliseconds(350) };
    private string diagnosticHoverMessage = "";

    private void HideDiagnosticHover()
    {
        diagnosticHoverTimer.Stop();
        diagnosticHoverMessage = "";
        ToolTip.SetIsOpen(this, false);
        ToolTip.SetTip(this, null);
    }

    private void HoverDiagnostic(Point point)
    {
        var position = (int)document.Send(ScintillaMessage.PositionFromPointClose, (nint)Math.Round(point.X), (nint)Math.Round(point.Y + SubLine));
        var message = position < 0 ? "" : string.Join("\n\n", diagnosticMessages
            .Where(item => item.Message.Length > 0 && position >= item.Offset && position < item.Offset + Math.Max(1, item.Length))
            .OrderBy(item => item.Severity).Select(item => item.Message).Distinct());
        if (message == diagnosticHoverMessage) return;
        HideDiagnosticHover();
        if (message.Length == 0) return;
        diagnosticHoverMessage = message;
        ToolTip.SetTip(this, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 });
        ToolTip.SetPlacement(this, PlacementMode.Pointer);
        diagnosticHoverTimer.Start();
    }

    private void ShowDiagnosticHover(object? sender, EventArgs e)
    {
        diagnosticHoverTimer.Stop();
        if (!disposed && IsEffectivelyVisible && diagnosticHoverMessage.Length > 0) ToolTip.SetIsOpen(this, true);
    }

    /// <summary>Replaces diagnostic squiggles without editing the document or its undo history.</summary>
    public void SetDiagnostics(IReadOnlyList<ScintillaDiagnostic> diagnostics, Color error, Color warning, Color information)
    {
        if (!diagnosticMessages.SequenceEqual(diagnostics)) HideDiagnosticHover();
        diagnosticMessages = diagnostics.ToArray();
        var length = document.Send(ScintillaMessage.GetLength);
        for (var severity = 1; severity <= 4; severity++)
        {
            var indicator = severity + 7;
            var color = severity == 1 ? error : severity == 2 ? warning : information;
            document.Send(ScintillaMessage.IndicSetStyle, indicator, 1);
            document.Send(ScintillaMessage.IndicSetFore, indicator, color.R | color.G << 8 | color.B << 16);
            document.Send(ScintillaMessage.SetIndicatorCurrent, indicator);
            document.Send(ScintillaMessage.IndicatorClearRange, 0, length);
        }
        foreach (var diagnostic in diagnostics)
        {
            if (diagnostic.Offset < 0 || diagnostic.Length < 0 || diagnostic.Offset > length || diagnostic.Length > length - diagnostic.Offset) continue;
            var start = diagnostic.Offset;
            var count = diagnostic.Length == 0 ? Math.Min(1, (int)length - start) : diagnostic.Length;
            document.Send(ScintillaMessage.SetIndicatorCurrent, Math.Clamp(diagnostic.Severity, 1, 4) + 7);
            document.Send(ScintillaMessage.IndicatorFillRange, start, count);
        }
        InvalidateVisual();
    }
}