using System.Runtime.InteropServices;

namespace Ghostty.Avalonia;

/// <summary>Caller-owned terminal transport. Callbacks run on Ghostty's IO thread.</summary>
public sealed class GhosttyExternalIo
{
    private readonly TaskCompletionSource failure = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public event EventHandler<ReadOnlyMemory<byte>>? Input;
    public event EventHandler<TerminalSize>? GridResized;
    public Task Failure => failure.Task;

    internal void Write(ReadOnlySpan<byte> data)
    {
        try { Input?.Invoke(this, data.ToArray()); }
        catch (Exception error) { failure.TrySetException(error); }
    }

    internal void Resize(ushort columns, ushort rows)
    {
        try { GridResized?.Invoke(this, new(columns, rows)); }
        catch (Exception error) { failure.TrySetException(error); }
    }
}

public sealed partial class GhosttyView
{
    private readonly Lock ioGate = new();
    private readonly GhosttyExternalIo? externalIo;

    /// <summary>Feed PTY output in-process. Safe against concurrent disposal.</summary>
    public unsafe void WriteOutput(ReadOnlySpan<byte> data)
    {
        lock (ioGate)
        {
            ObjectDisposedException.ThrowIf(view == 0, this);
            if (externalIo is null) throw new InvalidOperationException("The surface does not use external IO.");
            fixed (byte* pointer = data) ExternalNative.Output(view, pointer, (nuint)data.Length);
        }
    }

    public TerminalSize Size
    {
        get
        {
            ObjectDisposedException.ThrowIf(view == 0, this);
            ExternalNative.Grid(view, out var columns, out var rows);
            return new(columns, rows);
        }
    }

    [UnmanagedCallersOnly]
    private static unsafe void OnInput(nint context, byte* data, nuint length) =>
        ((GhosttyView)GCHandle.FromIntPtr(context).Target!).externalIo!.Write(new ReadOnlySpan<byte>(data, checked((int)length)));

    [UnmanagedCallersOnly]
    private static void OnResize(nint context, ushort columns, ushort rows) =>
        ((GhosttyView)GCHandle.FromIntPtr(context).Target!).externalIo!.Resize(columns, rows);

    private static unsafe class ExternalNative
    {
        private const string Library = "GhosttyAvaloniaView";
        [DllImport(Library, EntryPoint = "gav_view_create_external")]
        internal static extern nint Create([MarshalAs(UnmanagedType.LPUTF8Str)] string directory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? clipboardDirectory, nint context,
            delegate* unmanaged<nint, byte*, nuint, void> write, delegate* unmanaged<nint, ushort, ushort, void> resize);
        [DllImport(Library, EntryPoint = "gav_view_output")]
        internal static extern void Output(nint view, byte* data, nuint length);
        [DllImport(Library, EntryPoint = "gav_view_grid")]
        internal static extern void Grid(nint view, out ushort columns, out ushort rows);
    }
}