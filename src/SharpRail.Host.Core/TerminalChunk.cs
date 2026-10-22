using System.Buffers;

namespace SharpRail.Host.Core;

// One PTY read in a pooled array, shared by every consumer of a hosted terminal's output without copying.
// Each holder owns one reference: whoever keeps the chunk past the call that handed it over retains it first,
// and releases it once it no longer reads it. The last release returns the array to the pool.
internal sealed class TerminalChunk
{
    private byte[]? buffer;
    private readonly int length;
    private int references = 1;

    private TerminalChunk(byte[] buffer, int length) { this.buffer = buffer; this.length = length; }

    // A chunk holding a copy of the bytes, owned by the caller.
    internal static TerminalChunk Copy(ReadOnlySpan<byte> data)
    {
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, data.Length));
        data.CopyTo(rented);
        return new(rented, data.Length);
    }

    internal int Length => length;

    internal ReadOnlyMemory<byte> Memory => (buffer ?? throw new ObjectDisposedException(nameof(TerminalChunk))).AsMemory(0, length);

    internal ReadOnlySpan<byte> Span => Memory.Span;

    internal TerminalChunk Retain()
    {
        if (Interlocked.Increment(ref references) <= 1) throw new ObjectDisposedException(nameof(TerminalChunk));
        return this;
    }

    internal void Release()
    {
        var left = Interlocked.Decrement(ref references);
        if (left > 0) return;
        if (left < 0) throw new InvalidOperationException("A terminal chunk was released more often than retained.");
        var returned = Interlocked.Exchange(ref buffer, null);
        if (returned is not null) ArrayPool<byte>.Shared.Return(returned);
    }
}