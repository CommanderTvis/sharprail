using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Win32.SafeHandles;

using SharpRail.Plugins.Isolation.Native;

namespace SharpRail.Plugins.Isolation;

/// <summary>A frame encoded for one transport, with the descriptors that ride beside its bytes.</summary>
public sealed class PreparedFrame
{
    internal PreparedFrame(byte[] bytes, SafeHandle[] descriptors) { Bytes = bytes; Descriptors = descriptors; }
    internal byte[] Bytes { get; }
    internal SafeHandle[] Descriptors { get; }
    /// <summary>The encoded length, length prefix included.</summary>
    public int Length => Bytes.Length;
}

/// <summary>A received frame and the descriptors it carried; the receiver owns and disposes them.</summary>
public sealed record ReceivedFrame(IsoFrame Frame, SafeHandle[] Descriptors);

/// <summary>
/// The framed byte stream of one plugin channel, and the descriptors that travel with frames: SCM_RIGHTS over a Unix
/// stream socket, or, on Windows, handles the host duplicates into the runner. Reading and writing are blocking: one
/// thread reads, one thread writes. A side that does not accept descriptors treats any it receives as a protocol
/// violation and closes them.
/// </summary>
public abstract class FdTransport : IDisposable
{
    /// <summary>Encodes a frame. Descriptors are referenced, not owned: the caller may dispose them after the write.</summary>
    public abstract PreparedFrame Prepare(IsoFrame frame, IReadOnlyList<SafeHandle> descriptors);

    /// <summary>Writes a prepared frame whole. Throws <see cref="IOException"/> when the channel is gone.</summary>
    public abstract void Write(PreparedFrame frame);

    /// <summary>The next frame, or null at the end of the channel. Throws <see cref="IsolationProtocolException"/> on a violation.</summary>
    public abstract ReceivedFrame? Read();

    /// <summary>Ends the channel: a blocked read or write returns.</summary>
    public abstract void Dispose();

    /// <summary>The runner's end of its channel: descriptor 3 on Unix, the handle named by <c>--channel</c> on Windows.</summary>
    public static FdTransport ForRunner(string? windowsHandle)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!long.TryParse(windowsHandle, out var value)) throw new ArgumentException("The runner needs --channel <handle> on Windows.");
            var handle = new SafePipeHandle((nint)value, true);
            // The handle was inherited, so it is still inheritable: a process the runner starts must not hold the channel.
            if (!Windows.SetHandleInformation(handle, Windows.HandleFlagInherit, 0))
                throw new IOException("Could not keep the plugin channel from being inherited: " + Marshal.GetLastPInvokeError());
            var pipe = new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, handle);
            return new StreamFdTransport(pipe, export: null, adopt: true);
        }
        // Descriptor 3 was inherited without close-on-exec; a process the runner starts must not hold the channel.
        Unix.CloseOnExec(3);
        return new UnixFdTransport(new SafeFileHandle(3, true), acceptDescriptors: true);
    }

    /// <summary>
    /// For checks: a host-side transport (refusing descriptors) and the raw stream at its other end, in this process.
    /// </summary>
    public static (FdTransport Transport, Stream Peer) Loopback()
    {
        if (OperatingSystem.IsWindows())
        {
            var (server, client) = WindowsPipe.Pair();
            return (new StreamFdTransport(server, export: null, adopt: false), client);
        }
        var (host, peer) = Unix.OpenSocketPair();
        var socket = new Socket(new SafeSocketHandle(peer, true));
        return (new UnixFdTransport(new SafeFileHandle(host, true), acceptDescriptors: false), new NetworkStream(socket, ownsSocket: true));
    }

    private protected static int CheckedLength(ReadOnlySpan<byte> prefix)
    {
        var length = BinaryPrimitives.ReadInt32LittleEndian(prefix);
        if (length is < 0 or > IsoWire.FrameBytes) throw new IsolationProtocolException($"A plugin channel frame announced {length} bytes; the limit is 4 MiB.");
        return length;
    }
}

/// <summary>A Unix stream socket; descriptors travel as SCM_RIGHTS on the first byte of their frame.</summary>
[UnsupportedOSPlatform("windows")]
internal sealed unsafe class UnixFdTransport(SafeFileHandle socket, bool acceptDescriptors) : FdTransport
{
    private readonly Queue<int> received = new();
    private byte[] buffer = new byte[64 * 1024];
    private int start, end;
    private int disposed;

    public override PreparedFrame Prepare(IsoFrame frame, IReadOnlyList<SafeHandle> descriptors)
    {
        if (descriptors.Count > IsoWire.FrameDescriptors) throw new ArgumentException($"A frame carries at most {IsoWire.FrameDescriptors} descriptors.");
        if (descriptors.Count > 0) frame.Fds = [.. descriptors.Select(_ => 0L)];
        return new(IsoWire.Encode(frame), [.. descriptors]);
    }

    public override void Write(PreparedFrame frame)
    {
        var added = new List<SafeHandle>();
        var success = false;
        socket.DangerousAddRef(ref success);
        try
        {
            foreach (var descriptor in frame.Descriptors)
            {
                var referenced = false;
                descriptor.DangerousAddRef(ref referenced);
                added.Add(descriptor);
            }
            var fd = (int)socket.DangerousGetHandle();
            var control = stackalloc byte[Unix.CmsgSpace(IsoWire.FrameDescriptors * sizeof(int))];
            var controlLength = 0;
            if (frame.Descriptors.Length > 0)
            {
                controlLength = Unix.CmsgSpace(frame.Descriptors.Length * sizeof(int));
                new Span<byte>(control, controlLength).Clear();
                Unix.WriteCmsg(control, Unix.SolSocket, Unix.ScmRights, frame.Descriptors.Length * sizeof(int));
                var data = (int*)(control + Unix.CmsgHeader);
                for (var index = 0; index < frame.Descriptors.Length; index++) data[index] = (int)frame.Descriptors[index].DangerousGetHandle();
            }
            var header = stackalloc byte[Unix.MsgHeaderSize];
            var iov = stackalloc nint[2];
            fixed (byte* bytes = frame.Bytes)
            {
                var sent = 0;
                while (sent < frame.Bytes.Length)
                {
                    iov[0] = (nint)(bytes + sent);
                    iov[1] = frame.Bytes.Length - sent;
                    // Descriptors ride on the frame's first byte only.
                    Unix.WriteMsgHeader(header, iov, 1, sent == 0 && controlLength > 0 ? control : null, sent == 0 ? controlLength : 0);
                    var count = Unix.SendMsg(fd, header, Unix.SendFlags);
                    if (count < 0)
                    {
                        if (Marshal.GetLastPInvokeError() == Unix.Eintr) continue;
                        throw new IOException("The plugin channel closed: " + Unix.LastError());
                    }
                    sent += (int)count;
                }
            }
        }
        finally
        {
            foreach (var descriptor in added) descriptor.DangerousRelease();
            if (success) socket.DangerousRelease();
        }
    }

    public override ReceivedFrame? Read()
    {
        while (true)
        {
            if (end - start >= 4)
            {
                var length = CheckedLength(buffer.AsSpan(start, 4));
                if (end - start >= 4 + length)
                {
                    var frame = IsoWire.Decode(buffer.AsSpan(start + 4, length));
                    start += 4 + length;
                    return new(frame, Claim(frame));
                }
                if (4 + length > buffer.Length - start)
                {
                    if (4 + length > buffer.Length) Array.Resize(ref buffer, Math.Max(4 + length, buffer.Length * 2));
                    Compact();
                }
            }
            else if (start > 0 && start == end) start = end = 0;
            else if (buffer.Length - end < 4) Compact();
            if (!Receive())
            {
                if (end > start) throw new IsolationProtocolException("The plugin channel ended inside a frame.");
                return null;
            }
        }
    }

    private void Compact()
    {
        buffer.AsSpan(start, end - start).CopyTo(buffer);
        end -= start;
        start = 0;
    }

    private SafeHandle[] Claim(IsoFrame frame)
    {
        var count = frame.Fds.Count;
        if (!acceptDescriptors && (count > 0 || received.Count > 0))
        {
            CloseReceived();
            throw new IsolationProtocolException("The plugin sent a descriptor; only the host may send them.");
        }
        if (count > IsoWire.FrameDescriptors || count > received.Count)
        {
            CloseReceived();
            throw new IsolationProtocolException("A plugin channel frame announced descriptors that did not arrive with it.");
        }
        var claimed = new SafeHandle[count];
        for (var index = 0; index < count; index++)
        {
            var fd = received.Dequeue();
            frame.Fds[index] = fd;
            claimed[index] = new SafeFileHandle(fd, true);
        }
        return claimed;
    }

    private void CloseReceived()
    {
        while (received.TryDequeue(out var fd)) Unix.Close(fd);
    }

    private bool Receive()
    {
        var success = false;
        socket.DangerousAddRef(ref success);
        try
        {
            var fd = (int)socket.DangerousGetHandle();
            var controlLength = Unix.CmsgSpace(IsoWire.FrameDescriptors * sizeof(int));
            var control = stackalloc byte[controlLength];
            var header = stackalloc byte[Unix.MsgHeaderSize];
            var iov = stackalloc nint[2];
            fixed (byte* bytes = buffer)
            {
                while (true)
                {
                    iov[0] = (nint)(bytes + end);
                    iov[1] = buffer.Length - end;
                    Unix.WriteMsgHeader(header, iov, 1, control, controlLength);
                    var count = Unix.ReceiveMsg(fd, header, Unix.ReceiveFlags);
                    if (count < 0)
                    {
                        var error = Marshal.GetLastPInvokeError();
                        if (error == Unix.Eintr) continue;
                        if (Volatile.Read(ref disposed) != 0) { CloseReceived(); return false; }
                        throw new IOException("The plugin channel failed: " + Unix.Error(error));
                    }
                    Collect(header, control);
                    if ((Unix.MsgFlags(header) & Unix.MsgCtrunc) != 0)
                    {
                        CloseReceived();
                        throw new IsolationProtocolException("A plugin channel frame carried more descriptors than allowed.");
                    }
                    end += (int)count;
                    if (count == 0) CloseReceived();
                    return count > 0;
                }
            }
        }
        finally
        {
            if (success) socket.DangerousRelease();
        }
    }

    private void Collect(byte* header, byte* control)
    {
        var length = Unix.MsgControlLength(header);
        var refused = false;
        for (var offset = 0; offset + Unix.CmsgHeader <= length;)
        {
            var (size, level, type) = Unix.ReadCmsg(control + offset);
            if (size < Unix.CmsgHeader || offset + size > length) break;
            if (level == Unix.SolSocket && type == Unix.ScmRights)
            {
                var data = (int*)(control + offset + Unix.CmsgHeader);
                for (var index = 0; index < (size - Unix.CmsgHeader) / sizeof(int); index++)
                {
                    if (!acceptDescriptors)
                    {
                        Unix.Close(data[index]);
                        refused = true;
                        continue;
                    }
                    // macOS has no MSG_CMSG_CLOEXEC: mark it before anything can spawn.
                    if (Unix.Mac) Unix.CloseOnExec(data[index]);
                    received.Enqueue(data[index]);
                }
            }
            offset += Unix.CmsgAlign(size);
        }
        if (refused) throw new IsolationProtocolException("The plugin sent a descriptor; only the host may send them.");
        if (received.Count > 2 * IsoWire.FrameDescriptors)
        {
            CloseReceived();
            throw new IsolationProtocolException("The plugin channel carried more descriptors than its frames announced.");
        }
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        var success = false;
        socket.DangerousAddRef(ref success);
        try { Unix.Shutdown((int)socket.DangerousGetHandle(), Unix.ShutRdwr); }
        finally { if (success) socket.DangerousRelease(); }
        socket.Dispose();
    }
}

/// <summary>
/// A Windows named pipe. The host passes a handle by duplicating it into the runner, so the frame carries the value
/// the runner will see; the runner has no handle to the host process and so cannot send any.
/// </summary>
internal sealed class StreamFdTransport(Stream stream, Func<SafeHandle, long>? export, bool adopt) : FdTransport
{
    private readonly byte[] prefix = new byte[4];
    private int disposed;

    public override PreparedFrame Prepare(IsoFrame frame, IReadOnlyList<SafeHandle> descriptors)
    {
        if (descriptors.Count > IsoWire.FrameDescriptors) throw new ArgumentException($"A frame carries at most {IsoWire.FrameDescriptors} descriptors.");
        if (descriptors.Count > 0)
        {
            if (export is null) throw new NotSupportedException("Only the host can send handles on Windows.");
            frame.Fds = [.. descriptors.Select(export)];
        }
        return new(IsoWire.Encode(frame), []);
    }

    public override void Write(PreparedFrame frame)
    {
        try
        {
            stream.Write(frame.Bytes);
            stream.Flush();
        }
        catch (ObjectDisposedException error) { throw new IOException("The plugin channel closed.", error); }
    }

    public override ReceivedFrame? Read()
    {
        try
        {
            if (!Fill(prefix, atStart: true)) return null;
            var body = new byte[CheckedLength(prefix)];
            if (!Fill(body, atStart: false)) throw new IsolationProtocolException("The plugin channel ended inside a frame.");
            var frame = IsoWire.Decode(body);
            if (frame.Fds.Count == 0) return new(frame, []);
            if (!adopt) throw new IsolationProtocolException("The plugin sent a descriptor; only the host may send them.");
            if (frame.Fds.Count > IsoWire.FrameDescriptors) throw new IsolationProtocolException("A plugin channel frame carried more descriptors than allowed.");
            return new(frame, [.. frame.Fds.Select(value => (SafeHandle)new SafeFileHandle((nint)value, true))]);
        }
        catch (Exception error) when (error is ObjectDisposedException or OperationCanceledException || (error is IOException and not IsolationProtocolException && Volatile.Read(ref disposed) != 0))
        {
            return null;
        }
    }

    private bool Fill(byte[] target, bool atStart)
    {
        var filled = 0;
        while (filled < target.Length)
        {
            var count = stream.Read(target, filled, target.Length - filled);
            if (count == 0)
            {
                if (atStart && filled == 0) return false;
                throw new IsolationProtocolException("The plugin channel ended inside a frame.");
            }
            filled += count;
        }
        return true;
    }

    public override void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stream.Dispose();
    }
}

/// <summary>Windows named pipes: one instance, this user only, connected end to end by the host.</summary>
[SupportedOSPlatform("windows")]
internal static class WindowsPipe
{
    /// <summary>The host's overlapped server end and the client end, not yet inheritable.</summary>
    internal static (SafePipeHandle Server, SafeFileHandle Client) Create()
    {
        var name = @"\\.\pipe\sharprail-plugin-" + Guid.NewGuid().ToString("N");
        var user = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value ?? throw new IOException("The current user has no security identifier.");
        if (!Native.Windows.ConvertSecurityDescriptor($"D:P(A;;GA;;;{user})", 1, out var descriptor, 0))
            throw new IOException("Could not build the plugin channel's access list.", new System.ComponentModel.Win32Exception());
        try
        {
            var security = new Native.Windows.SecurityAttributes { Length = Marshal.SizeOf<Native.Windows.SecurityAttributes>(), Descriptor = descriptor };
            var server = Native.Windows.CreateNamedPipe(name,
                Native.Windows.PipeAccessDuplex | Native.Windows.FileFlagFirstPipeInstance | Native.Windows.FileFlagOverlapped,
                Native.Windows.PipeMode, 1, 64 * 1024, 64 * 1024, 0, ref security);
            if (server.IsInvalid) throw new IOException("Could not create the plugin channel.", new System.ComponentModel.Win32Exception());
            var client = Native.Windows.CreateFile(name, Native.Windows.GenericRead | Native.Windows.GenericWrite, 0, 0,
                Native.Windows.OpenExisting, Native.Windows.FileFlagOverlapped, 0);
            if (client.IsInvalid)
            {
                server.Dispose();
                throw new IOException("Could not connect the plugin channel.", new System.ComponentModel.Win32Exception());
            }
            return (server, client);
        }
        finally { Native.Windows.LocalFree(descriptor); }
    }

    internal static (Stream Server, Stream Client) Pair()
    {
        var (server, client) = Create();
        var handle = client.DangerousGetHandle();
        client.SetHandleAsInvalid();
        return (new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: true, server),
            new NamedPipeClientStream(PipeDirection.InOut, isAsync: true, isConnected: true, new SafePipeHandle(handle, true)));
    }
}