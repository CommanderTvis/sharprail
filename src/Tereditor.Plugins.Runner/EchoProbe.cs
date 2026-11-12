using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

using Tereditor.Plugins.Isolation;

namespace Tereditor.Plugins.Runner;

/// <summary>
/// <c>probe echo</c>: the channel and the process with no plugin. It reports how it was started (standard input at
/// end of file, a line on standard output and one on standard error), completes the handshake and serves the
/// <see cref="RunnerProbe"/> members until the host closes the channel. Some members break the protocol on purpose.
/// </summary>
internal static class EchoProbe
{
    private static readonly List<string> notes = [];
    private static int cancelled, sent;

    internal static async Task<int> RunAsync(string? channel)
    {
        string standardInput;
        using (var input = Console.OpenStandardInput()) standardInput = input.Read(new byte[1]) == 0 ? "stdin=eof" : "stdin=data";
        Console.Out.WriteLine(RunnerProbe.StandardOutputLine);
        Console.Out.Flush();
        Console.Error.WriteLine(RunnerProbe.StandardErrorLine);
        Console.Error.Flush();

        var init = new TaskCompletionSource<RunnerInit>(TaskCreationOptions.RunContinuationsAsynchronously);
        IsolationPeer? peer = null;
        peer = new IsolationPeer(FdTransport.ForRunner(channel), IsolationRole.Runner, new()
        {
            Calls = (call, token) => Serve(peer!, channel, call, token),
            OneWays = call =>
            {
                if (call.Member != RunnerProbe.Note) return;
                lock (notes) notes.Add(call.Payload);
            },
            Control = received =>
            {
                if (received.Frame.Kind != IsoFrameKind.Init || init.Task.IsCompleted) throw new IsolationProtocolException($"The host sent {received.Frame.Kind} out of turn.");
                init.TrySetResult(IsoProtocol.DecodeInit(received.Frame.Payload));
            }
        });
        peer.Start();
        peer.Send(new IsoFrame
        {
            Kind = IsoFrameKind.Hello,
            Payload = IsoProtocol.Encode(new RunnerHello(IsoProtocol.Version, Environment.ProcessId, "probe " + RunnerProbe.EchoMode, RunnerTables.Hash, standardInput))
        });
        if (await Task.WhenAny(init.Task, peer.Completion) != init.Task) return 1;
        peer.Send(new IsoFrame { Kind = IsoFrameKind.Ready, Payload = IsoProtocol.Encode(new RunnerReady("probe")) });
        try { await peer.Completion; }
        catch (IsolationProtocolException) { return 1; }
        return 0;
    }

    private static async ValueTask<IsoResult> Serve(IsolationPeer peer, string? channel, IsoCall call, CancellationToken token)
    {
        var member = call.Member;
        if (member == RunnerProbe.Echo) return IsoResult.Of(call.Payload);
        if (member == RunnerProbe.Delay)
        {
            try { await Task.Delay(int.Parse(call.Payload), token); }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref cancelled);
                throw;
            }
            return IsoResult.Of("done");
        }
        if (member == RunnerProbe.Cancelled) return IsoResult.Of(Volatile.Read(ref cancelled).ToString());
        if (member == RunnerProbe.Mint)
        {
            var id = peer.Handles.Mint(new Lent());
            return new IsoResult { Payload = id.ToString(), Handles = [id] };
        }
        if (member == RunnerProbe.OnHandle)
            return call.TargetObject is Lent ? IsoResult.Of("live:" + call.Target) : throw new IsoCallException(IsoError.Failed, "Not a lent handle.");
        if (member == RunnerProbe.Stream)
        {
            Volatile.Write(ref sent, 0);
            return new IsoResult { Items = Items(int.Parse(call.Payload), token) };
        }
        if (member == RunnerProbe.Sent) return IsoResult.Of(Volatile.Read(ref sent).ToString());
        if (member == RunnerProbe.WriteDescriptor)
        {
            if (call.Descriptors is not [SafeFileHandle descriptor]) throw new IsoCallException(IsoError.InvalidParams, "Expected one descriptor.");
            await using (var output = new FileStream(descriptor, FileAccess.Write, 1, false)) await output.WriteAsync(Encoding.UTF8.GetBytes(call.Payload), token);
            return IsoResult.Of("written");
        }
        if (member == RunnerProbe.SendDescriptor)
        {
            if (OperatingSystem.IsWindows())
            {
                // A runner holds no handle to the host process, so it can only claim one.
                peer.Send(new IsoFrame { Kind = IsoFrameKind.OneWay, Member = RunnerProbe.Note, Fds = [4242] });
            }
            else
            {
                using var descriptor = File.OpenHandle("/dev/null");
                peer.Send(new IsoFrame { Kind = IsoFrameKind.OneWay, Member = RunnerProbe.Note }, [descriptor]);
            }
            return IsoResult.Of("sent");
        }
        if (member == RunnerProbe.CallHost)
        {
            try
            {
                var reply = await peer.CallAsync(RunnerProbe.HostEcho, "from the runner", long.Parse(call.Payload), cancellationToken: token);
                return IsoResult.Of("answered:" + reply.Payload);
            }
            catch (IsoCallException error) { return IsoResult.Of(error.Error.ToString()); }
        }
        if (member == RunnerProbe.BadParity)
        {
            peer.Send(new IsoFrame { Kind = IsoFrameKind.Call, Id = 1, Member = RunnerProbe.HostEcho });
            return IsoResult.Of("sent");
        }
        if (member == RunnerProbe.Notes)
            lock (notes) return IsoResult.Of(string.Join(',', notes));
        if (member == RunnerProbe.SpawnChild)
        {
            var start = OperatingSystem.IsWindows()
                ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "ping.exe"), ["-n", "300", "127.0.0.1"])
                : new ProcessStartInfo("/bin/sh", ["-c", "trap '' HUP INT TERM; exec /bin/sleep 300"]);
            start.UseShellExecute = false;
            start.RedirectStandardOutput = true;
            using var child = Process.Start(start) ?? throw new IsoCallException(IsoError.Failed, "The child did not start.");
            return IsoResult.Of(child.Id.ToString());
        }
        if (member == RunnerProbe.ChildChannel)
        {
            if (OperatingSystem.IsWindows())
            {
                // Whether a child would inherit the pipe handle; ping.exe could not show what it holds.
                if (!GetHandleInformation((nint)long.Parse(channel!), out var flags)) throw new IsoCallException(IsoError.Failed, "The channel handle is not valid.");
                return IsoResult.Of((flags & 1) == 0 ? "free" : "held");
            }
            // Redirecting to descriptor 3 fails in the child unless it inherited the channel.
            using var child = Process.Start(new ProcessStartInfo("/bin/sh", ["-c", ": >&3"]) { UseShellExecute = false, RedirectStandardError = true })
                ?? throw new IsoCallException(IsoError.Failed, "The child did not start.");
            await child.WaitForExitAsync(token);
            return IsoResult.Of(child.ExitCode == 0 ? "held" : "free");
        }
        if (member == RunnerProbe.Exit)
        {
            Environment.Exit(int.Parse(call.Payload));
        }
        throw new IsoCallException(IsoError.Unknown, $"The probe has no member {member}.");
    }

    /// <summary>Counts an item as sent once the channel asks for the next one, which it does only after sending.</summary>
    private static async IAsyncEnumerable<string> Items(int count, [EnumeratorCancellation] CancellationToken token)
    {
        for (var index = 0; index < count; index++)
        {
            token.ThrowIfCancellationRequested();
            yield return index.ToString();
            Interlocked.Increment(ref sent);
        }
        await Task.CompletedTask;
    }

    private sealed class Lent;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(nint handle, out uint flags);
}