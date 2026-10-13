using System.Runtime.InteropServices;

namespace Ghostty.Avalonia;

[StructLayout(LayoutKind.Sequential)]
internal struct RowMetrics
{
    internal float Left, Top, CellWidth, CellHeight, Baseline, Width, Height;
    internal int Row;
}

internal static unsafe class NativeTextBatch
{
    [DllImport("GhosttyAvaloniaVt", EntryPoint = "gav_draw_ascii_row")]
    internal static extern int Draw(nint canvas, nint fill, nint text, Frame* frame, nint* blobs, RowMetrics* metrics);
}