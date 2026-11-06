using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

using SharpRail.Plugins.Isolation;

namespace SharpRail.Checks;

/// <summary>
/// The plugin channel and the runner process (src/SharpRail.Plugins.Api/Security.SPEC.md): the frame codec; protocol
/// violations against a raw in-process end; then the real runner's <c>probe echo</c> for calls, cancellation, deadlines,
/// handles and tombstones, stream credits, one-way order, descriptor passing, refused descriptors, kills, exit codes,
/// reaping and what the runner inherits; and <c>probe avalonia-private</c>.
/// </summary>
internal static class PluginChannelChecks
{
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static async Task Run()
    {
        Codec();
        Console.WriteLine("PASS plugin channel codec: every IsoFrame field round-trips behind its length; oversized and malformed frames are refused");
        await Violations();
        Console.WriteLine("PASS plugin channel protocol: oversized, malformed, misnumbered, unknown and never-lent traffic ends the channel; tombstones answer Released or Denied");
        if (!OperatingSystem.IsWindows())
        {
            Descriptors();
            Console.WriteLine("PASS plugin channel descriptors: the host closes a runner's descriptor as it arrives, inside an unfinished frame; the runner refuses more than two frames' worth");
        }
        if (Runner() is not { } runner)
        {
            Console.WriteLine("SKIP plugin channel runner: these checks run self-contained, and the framework-dependent runner needs a shared .NET runtime");
            return;
        }
        await Echo(runner);
        Console.WriteLine("PASS plugin runner probe: handshake with the member tables' hash, stdio, no inherited handles, a child of the runner does not hold the channel, calls, cancel, deadline, handles, credits, one-way order, descriptor passing, callbacks, clean exit, reaped");
        await Refusals(runner);
        Console.WriteLine("PASS plugin runner refusals: a runner generated from other member tables, a sent descriptor, a never-lent handle and a misnumbered call kill the runner; an exit code is captured; nothing is left unreaped; a runner's child dies with it");
        await AvaloniaPrivate(runner);
        Console.WriteLine("PASS plugin runner avalonia-private: a stub IWindowingPlatform registers and resolves");
    }

    private static void Codec()
    {
        var frame = new IsoFrame
        {
            Kind = IsoFrameKind.Call,
            Id = 41,
            Member = IsoMember.Id("probe.codec"),
            Target = 7,
            Payload = "{\"a\":[1,2]}",
            DeadlineMs = 250,
            Error = IsoError.Denied,
            Detail = "detail",
            Handles = [1, 0, long.MaxValue],
            Fds = [0, 0],
            Permission = "files.read",
            Bytes = [1, 2, 3],
            Count = 16
        };
        var bytes = IsoWire.Encode(frame);
        Require(BinaryPrimitives.ReadInt32LittleEndian(bytes) == bytes.Length - 4, "A frame's length prefix must count its body.");
        var back = IsoWire.Decode(bytes.AsSpan(4));
        Require(back.Kind == frame.Kind && back.Id == frame.Id && back.Member == frame.Member && back.Target == frame.Target && back.Payload == frame.Payload
            && back.DeadlineMs == frame.DeadlineMs && back.Error == frame.Error && back.Detail == frame.Detail && back.Handles.SequenceEqual(frame.Handles)
            && back.Fds.SequenceEqual(frame.Fds) && back.Permission == frame.Permission && back.Bytes.SequenceEqual(frame.Bytes) && back.Count == frame.Count,
            "An IsoFrame must round-trip every field.");
        var refused = false;
        try { IsoWire.Encode(new IsoFrame { Payload = new string('x', IsoWire.FrameBytes + 1) }); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused, "A frame over 4 MiB must not encode.");
        refused = false;
        try { IsoWire.Decode([0xFF]); }
        catch (IsolationProtocolException) { refused = true; }
        Require(refused, "A malformed frame must be a protocol violation.");
    }

    private static async Task Violations()
    {
        await Broken("an oversized length", raw => Write(raw, BitConverter.GetBytes(IsoWire.FrameBytes + 1)), "4 MiB");
        await Broken("a malformed body", raw => Write(raw, [3, 0, 0, 0, 0xFF, 0xFF, 0xFF]), "malformed");
        await Broken("a call numbered as the host's", raw => Send(raw, new IsoFrame { Kind = IsoFrameKind.Call, Id = 1 }), "misnumbered");
        await Broken("a call on a never-lent handle", raw => Send(raw, new IsoFrame { Kind = IsoFrameKind.Call, Id = 2, Target = 5 }), "never lent");
        await Broken("a release of a never-lent handle", raw => Send(raw, new IsoFrame { Kind = IsoFrameKind.Release, Target = 9 }), "never lent");
        await Broken("an answer to a call never made", raw => Send(raw, new IsoFrame { Kind = IsoFrameKind.Reply, Id = 1 }), "never made");
        await Broken("an unknown frame kind", raw => Send(raw, new IsoFrame { Kind = (IsoFrameKind)99, Id = 2 }), "unknown frame kind");
        await Broken("a handshake frame nobody expects", raw => Send(raw, new IsoFrame { Kind = IsoFrameKind.Hello }), "unexpected");

        var (transport, raw) = FdTransport.Loopback();
        var peer = new IsolationPeer(transport, IsolationRole.Host, new()
        {
            Calls = (call, _) => ValueTask.FromResult(IsoResult.Of("on:" + call.Target))
        });
        await using (peer)
        await using (raw)
        {
            peer.Start();
            var lent = peer.Handles.Mint(new object());
            Send(raw, new IsoFrame { Kind = IsoFrameKind.Call, Id = 2, Target = lent });
            var reply = await Receive(raw);
            Require(reply is { Kind: IsoFrameKind.Reply, Id: 2 } && reply.Payload == "on:" + lent, "A call on a lent handle must reach it.");
            Send(raw, new IsoFrame { Kind = IsoFrameKind.Release, Target = lent });
            Send(raw, new IsoFrame { Kind = IsoFrameKind.Call, Id = 4, Target = lent });
            reply = await Receive(raw);
            Require(reply is { Kind: IsoFrameKind.Fault, Id: 4, Error: IsoError.Released }, "A call on a released handle must fault with Released.");
            Send(raw, new IsoFrame { Kind = IsoFrameKind.Release, Target = lent });
            var revoked = peer.Handles.Mint(new object());
            peer.Handles.Revoke(revoked, "files.read");
            Send(raw, new IsoFrame { Kind = IsoFrameKind.Call, Id = 6, Target = revoked });
            reply = await Receive(raw);
            Require(reply is { Kind: IsoFrameKind.Fault, Id: 6, Error: IsoError.Denied, Permission: "files.read" }, "A call on a revoked handle must be denied with its permission.");
            Send(raw, new IsoFrame { Kind = IsoFrameKind.Ping, Id = 8 });
            reply = await Receive(raw);
            Require(reply is { Kind: IsoFrameKind.Pong, Id: 8 }, "A ping must be answered.");
            Require(!peer.Completion.IsCompleted, "Releasing a released handle again is no violation: " + peer.Violation);
        }
    }

    /// <summary>Descriptors sent beside a frame that never completes, at the Unix transport.</summary>
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void Descriptors()
    {
        using var devNull = File.OpenHandle("/dev/null");
        byte[] unfinished = [100, 0, 0, 0, 0];

        var (hostEnd, runnerEnd) = SharpRail.Plugins.Isolation.Native.Unix.OpenSocketPair();
        using (var host = new UnixFdTransport(new SafeFileHandle(hostEnd, true), acceptDescriptors: false))
        using (var runner = new UnixFdTransport(new SafeFileHandle(runnerEnd, true), acceptDescriptors: true))
        {
            runner.Write(new PreparedFrame(unfinished, [devNull]));
            Refused(host, "only the host", "The host must refuse a descriptor as it arrives, before its frame completes.");
        }

        (hostEnd, runnerEnd) = SharpRail.Plugins.Isolation.Native.Unix.OpenSocketPair();
        using (var host = new UnixFdTransport(new SafeFileHandle(hostEnd, true), acceptDescriptors: false))
        using (var runner = new UnixFdTransport(new SafeFileHandle(runnerEnd, true), acceptDescriptors: true))
        {
            host.Write(new PreparedFrame(unfinished[..4], []));
            var many = Enumerable.Repeat<SafeHandle>(devNull, IsoWire.FrameDescriptors).ToArray();
            for (var index = 0; index < 3; index++) host.Write(new PreparedFrame([0], many));
            Refused(runner, "more descriptors", "The runner must refuse descriptors piling up beyond two frames' worth.");
        }

        static void Refused(FdTransport transport, string reason, string message)
        {
            try
            {
                Task.Run(() => transport.Read()).WaitAsync(Patience).GetAwaiter().GetResult();
                Require(false, message);
            }
            catch (TimeoutException) { Require(false, message); }
            catch (IsolationProtocolException violation) { Require(violation.Message.Contains(reason, StringComparison.OrdinalIgnoreCase), message + " " + violation.Message); }
        }
    }

    private static async Task Broken(string what, Action<Stream> send, string reason)
    {
        var (transport, raw) = FdTransport.Loopback();
        var peer = new IsolationPeer(transport, IsolationRole.Host, new());
        await using (peer)
        await using (raw)
        {
            peer.Start();
            send(raw);
            try
            {
                await peer.Completion.WaitAsync(Patience);
                Require(false, $"The channel must end on {what}.");
            }
            catch (IsolationProtocolException violation)
            {
                Require(violation.Message.Contains(reason, StringComparison.OrdinalIgnoreCase), $"The channel ended on {what} for another reason: {violation.Message}");
            }
        }
    }

    private static void Write(Stream raw, byte[] bytes)
    {
        raw.Write(bytes);
        raw.Flush();
    }

    private static void Send(Stream raw, IsoFrame frame) => Write(raw, IsoWire.Encode(frame));

    private static async Task<IsoFrame> Receive(Stream raw)
    {
        var prefix = new byte[4];
        await raw.ReadExactlyAsync(prefix).AsTask().WaitAsync(Patience);
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(prefix)];
        await raw.ReadExactlyAsync(body).AsTask().WaitAsync(Patience);
        return IsoWire.Decode(body);
    }

    /// <summary>The checks' framework-dependent runner, started through its apphost on this process's runtime.</summary>
    internal sealed record RunnerLaunch(string Program, string DotnetRoot);

    internal static RunnerLaunch? Runner()
    {
        var shared = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var root = Path.GetFullPath(Path.Combine(shared, "..", "..", ".."));
        if (!Directory.Exists(Path.Combine(root, "shared", "Microsoft.NETCore.App"))) return null;
        var program = Path.Combine(AppContext.BaseDirectory, "plugin-runner", OperatingSystem.IsWindows() ? "SharpRail.PluginRunner.exe" : "SharpRail.PluginRunner");
        Require(File.Exists(program), "The checks' build must place the plugin runner at " + program);
        return new(program, root);
    }

    internal static ChildProcessStart Start(RunnerLaunch runner, string mode, ConcurrentQueue<string> log)
    {
        var home = CheckRoot.Create("plugin-runner");
        var temp = Path.Combine(home, "tmp");
        Directory.CreateDirectory(temp);
        var environment = ChildProcess.RunnerEnvironment(home, temp);
        environment["DOTNET_ROOT"] = runner.DotnetRoot;
        return new() { Program = runner.Program, Arguments = ["probe", mode], Environment = environment, WorkingDirectory = home, Log = log.Enqueue };
    }

    private static IsolationPeerOptions HostOptions() => new()
    {
        Calls = (call, _) => call.Member == RunnerProbe.HostEcho && call.TargetObject is not null
            ? ValueTask.FromResult(IsoResult.Of("host:" + call.Payload))
            : throw new IsoCallException(IsoError.Unknown, "Not a host probe member.")
    };

    private static async Task Until(Func<Task<bool>> condition, string message)
    {
        var clock = Stopwatch.StartNew();
        while (!await condition())
        {
            Require(clock.Elapsed < Patience, message);
            await Task.Delay(20);
        }
    }

    private static async Task Echo(RunnerLaunch runner)
    {
        var log = new ConcurrentQueue<string>();
        // A handle any child could inherit: the runner must not, so the pipe ends once the host drops its copy.
        using var marker = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        var session = await RunnerSession.StartAsync(Start(runner, RunnerProbe.EchoMode, log), HostOptions(), new RunnerInit("probe"), Patience, apiTableHash: TableHash);
        var peer = session.Peer;
        marker.DisposeLocalCopyOfClientHandle();
        try { Require(await marker.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)) == 0, "The runner wrote to a handle it must not have."); }
        catch (TimeoutException) { Require(false, "The runner inherited a host handle it was not given."); }

        Require(session.Hello.Pid == session.Process.Pid && session.Hello.Mode == "probe echo", $"The runner introduced itself wrongly: {session.Hello}");
        Require(session.Hello.Detail == "stdin=eof", "The runner's standard input must be the null device: " + session.Hello.Detail);
        Require((await peer.CallAsync(RunnerProbe.ChildChannel, "")).Payload == "free", "A process the runner starts must not hold the runner's channel.");
        await Until(() => Task.FromResult(log.Contains(RunnerProbe.StandardOutputLine) && log.Contains(RunnerProbe.StandardErrorLine)),
            "The runner's standard output and error must reach the host's log: " + string.Join(" | ", log));

        Require((await peer.CallAsync(RunnerProbe.Echo, "hello")).Payload == "hello", "An echo must come back.");
        var large = new string('a', 1024 * 1024) + "end";
        Require((await peer.CallAsync(RunnerProbe.Echo, large)).Payload == large, "A 1 MiB echo must come back whole.");
        var answers = await Task.WhenAll(Enumerable.Range(0, 64).Select(index => peer.CallAsync(RunnerProbe.Echo, "call " + index)));
        Require(answers.Select(answer => answer.Payload).SequenceEqual(Enumerable.Range(0, 64).Select(index => "call " + index)), "Concurrent calls must each get their own answer.");

        var clock = Stopwatch.StartNew();
        using (var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            try
            {
                await peer.CallAsync(RunnerProbe.Delay, "30000", cancellationToken: cancel.Token);
                Require(false, "A cancelled call must not complete.");
            }
            catch (OperationCanceledException) { }
        }
        Require(clock.Elapsed < TimeSpan.FromSeconds(10), "A cancelled call must end promptly.");
        await Until(async () => (await peer.CallAsync(RunnerProbe.Cancelled, "")).Payload == "1", "The runner must see the cancellation.");
        clock.Restart();
        try
        {
            await peer.CallAsync(RunnerProbe.Delay, "30000", deadline: TimeSpan.FromMilliseconds(200));
            Require(false, "A call past its deadline must not complete.");
        }
        catch (IsoCallException error) { Require(error.Error == IsoError.DeadlineExceeded, "A missed deadline must fail as DeadlineExceeded: " + error.Error); }
        Require(clock.Elapsed < TimeSpan.FromSeconds(10), "A missed deadline must end the call promptly.");
        await Until(async () => (await peer.CallAsync(RunnerProbe.Cancelled, "")).Payload == "2", "The runner must stop a call past its deadline.");

        var minted = await peer.CallAsync(RunnerProbe.Mint, "");
        var handle = minted.Handles.Single();
        Require(minted.Payload == handle.ToString(), "A lent handle must be listed with the reply.");
        Require((await peer.CallAsync(RunnerProbe.OnHandle, "", handle)).Payload == "live:" + handle, "A call on a lent handle must reach it.");
        peer.Release(handle);
        try
        {
            await peer.CallAsync(RunnerProbe.OnHandle, "", handle);
            Require(false, "A call on a released handle must fail.");
        }
        catch (IsoCallException error) { Require(error.Error == IsoError.Released, "A released handle must fail as Released: " + error.Error); }
        peer.Release(handle);
        Require((await peer.CallAsync(RunnerProbe.Echo, "after")).Payload == "after", "Releasing a released handle again must leave the channel up.");

        await using (var items = peer.StreamAsync(RunnerProbe.Stream, "100").GetAsyncEnumerator())
        {
            Require(await items.MoveNextAsync() && items.Current == "0", "A stream must start with its first item.");
            string? previous = null;
            var stable = 0;
            await Until(async () =>
            {
                var sent = (await peer.CallAsync(RunnerProbe.Sent, "")).Payload;
                stable = sent == previous ? stable + 1 : 0;
                previous = sent;
                return stable >= 5;
            }, "The stream's sender never settled.");
            Require(previous == (IsoWire.StreamWindow + 1).ToString(), $"With one item read the runner must stop at {IsoWire.StreamWindow + 1} sent items, not {previous}.");
            var rest = new List<string>();
            while (await items.MoveNextAsync()) rest.Add(items.Current);
            Require(rest.SequenceEqual(Enumerable.Range(1, 99).Select(index => index.ToString())), "A stream must deliver every item in order.");
        }

        for (var index = 0; index < 100; index++) peer.OneWay(RunnerProbe.Note, index.ToString());
        var expected = string.Join(',', Enumerable.Range(0, 100));
        string notes = "";
        await Until(async () => (notes = (await peer.CallAsync(RunnerProbe.Notes, "")).Payload).Split(',').Length >= 100, "The runner must run every one-way.");
        Require(notes == expected, "One-ways must run in arrival order: " + notes);

        using (var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.None))
        {
            Require((await peer.CallAsync(RunnerProbe.WriteDescriptor, "descriptor-ok", descriptors: [pipe.ClientSafePipeHandle])).Payload == "written",
                "The runner must write into the descriptor it was sent.");
            pipe.DisposeLocalCopyOfClientHandle();
            using var reader = new StreamReader(pipe, Encoding.UTF8);
            Require(await reader.ReadToEndAsync().WaitAsync(Patience) == "descriptor-ok", "The descriptor the host sent must be the one the runner wrote to.");
        }

        var lent = peer.Handles.Mint(new object());
        Require((await peer.CallAsync(RunnerProbe.CallHost, lent.ToString())).Payload == "answered:host:from the runner", "The runner must be able to call the host on a lent handle.");
        peer.Handles.Release(lent);
        Require((await peer.CallAsync(RunnerProbe.CallHost, lent.ToString())).Payload == nameof(IsoError.Released), "A runner call on a released host handle must fail as Released.");
        await peer.PingAsync(Patience);

        var pid = session.Process.Pid;
        var process = session.Process;
        await session.DisposeAsync();
        Require(await process.Exited == 0, "The runner must exit cleanly when its channel closes, not " + process.Exited.Result);
        Require(peer.Violation is null, "A clean run must break no protocol rule: " + peer.Violation);
        Gone(pid);
    }

    /// <summary>The member tables the runner must have been generated with: the host context's, then the UI context's.</summary>
    internal const string TableHash = SharpRail.Plugins.Isolation.PluginHostContextMembers.Hash + "." + SharpRail.Plugins.Isolation.UI.PluginUIContextMembers.Hash;

    private static async Task Refusals(RunnerLaunch runner)
    {
        try
        {
            await using var stale = await RunnerSession.StartAsync(Start(runner, RunnerProbe.EchoMode, new()), HostOptions(), new RunnerInit("probe"), Patience,
                apiTableHash: "0000000000000000.0000000000000000");
            Require(false, "A runner generated from other member tables must be refused.");
        }
        catch (IOException refused) when (refused.Message.Contains("plugin API table", StringComparison.Ordinal)) { }
        await Killed(runner, RunnerProbe.SendDescriptor, "", "descriptor");
        await Killed(runner, RunnerProbe.CallHost, "99999", "never lent");
        await Killed(runner, RunnerProbe.BadParity, "", "misnumbered");

        // Other children come and go meanwhile, so .NET's own SIGCHLD handling runs beside the runner's reaping.
        var log = new ConcurrentQueue<string>();
        var session = await RunnerSession.StartAsync(Start(runner, RunnerProbe.EchoMode, log), HostOptions(), new RunnerInit("probe"), Patience);
        var others = Enumerable.Range(0, 4).Select(_ => Process.Start(OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false }
            : new ProcessStartInfo("/bin/sh", ["-c", "exit 0"]) { UseShellExecute = false })!).ToList();
        var child = int.Parse((await session.Peer.CallAsync(RunnerProbe.SpawnChild, "")).Payload);
        var exiting = session.Peer.CallAsync(RunnerProbe.Exit, "7");
        foreach (var other in others)
        {
            await other.WaitForExitAsync().WaitAsync(Patience);
            other.Dispose();
        }
        Require(await session.Process.Exited.WaitAsync(Patience) == 7, "The runner's exit code must be captured, not " + session.Process.Exited.Result);
        var pid = session.Process.Pid;
        await session.DisposeAsync();
        try { await exiting.WaitAsync(Patience); }
        catch (IOException) { }
        Require(session.Peer.Violation is null, "An exiting runner breaks no protocol rule: " + session.Peer.Violation);
        Gone(pid);
        await Until(() => Task.FromResult(!Alive(child)), $"Child {child} of a runner that exited on its own must not outlive it.");
    }

    private static async Task Killed(RunnerLaunch runner, int member, string payload, string reason)
    {
        var log = new ConcurrentQueue<string>();
        var session = await RunnerSession.StartAsync(Start(runner, RunnerProbe.EchoMode, log), HostOptions(), new RunnerInit("probe"), Patience);
        try { await session.Peer.CallAsync(member, payload).WaitAsync(Patience); }
        catch (IOException) { }
        try
        {
            await session.Peer.Completion.WaitAsync(Patience);
            Require(false, $"The host must cut off a runner for {reason}.");
        }
        catch (IsolationProtocolException violation) { Require(violation.Message.Contains(reason, StringComparison.OrdinalIgnoreCase), $"The runner was cut off for another reason than {reason}: {violation.Message}"); }
        Require(await session.Process.Exited.WaitAsync(Patience) is not null, "A cut-off runner must be reaped with its status.");
        var pid = session.Process.Pid;
        await session.DisposeAsync();
        Gone(pid);
    }

    private static async Task AvaloniaPrivate(RunnerLaunch runner)
    {
        var log = new ConcurrentQueue<string>();
        await using var process = ChildProcess.Start(Start(runner, RunnerProbe.AvaloniaPrivateMode, log));
        Require(await process.Exited.WaitAsync(Patience) == 0, "probe avalonia-private must succeed: " + string.Join(" | ", log));
        await Until(() => Task.FromResult(log.Contains(RunnerProbe.AvaloniaPrivateResolved)), "probe avalonia-private must report the stub: " + string.Join(" | ", log));
    }

    /// <summary>The runner left no process behind: on Unix not even a zombie, which kill(pid, 0) would still find.</summary>
    private static void Gone(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                Require(process.HasExited, $"Runner {pid} is still running.");
            }
            catch (ArgumentException) { }
            return;
        }
        var result = OperatingSystem.IsMacOS() ? MacKill(pid, 0) : LinuxKill(pid, 0);
        Require(result != 0 && Marshal.GetLastPInvokeError() == 3, $"Runner {pid} was not reaped.");
    }

    private static bool Alive(int pid)
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                return !process.HasExited;
            }
            catch (ArgumentException) { return false; }
        }
        if (OperatingSystem.IsMacOS()) return MacKill(pid, 0) == 0;
        try { return File.ReadAllText($"/proc/{pid}/stat") is var stat && stat[(stat.LastIndexOf(')') + 2)..] is not ['Z', ..]; }
        catch (IOException) { return false; }
    }

    [DllImport("/usr/lib/libSystem.B.dylib", EntryPoint = "kill", SetLastError = true)]
    private static extern int MacKill(int pid, int signal);

    [DllImport("libc.so.6", EntryPoint = "kill", SetLastError = true)]
    private static extern int LinuxKill(int pid, int signal);
}