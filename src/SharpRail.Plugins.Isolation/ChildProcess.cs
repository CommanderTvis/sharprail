using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

using Microsoft.Win32.SafeHandles;

using SharpRail.Plugins.Isolation.Native;

namespace SharpRail.Plugins.Isolation;

/// <summary>What to start. The environment is the whole environment: nothing of the host's is inherited.</summary>
public sealed class ChildProcessStart
{
    public required string Program { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public required IReadOnlyDictionary<string, string> Environment { get; init; }
    public required string WorkingDirectory { get; init; }
    /// <summary>Receives the child's standard output and error, a line at a time, within <see cref="ChildProcess.LogBytesPerSecond"/>.</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// A plugin runner process. It inherits exactly its channel (descriptor 3 on Unix; on Windows one named-pipe handle,
/// named by <c>--channel</c>, which this class appends), standard input from the null device and one log pipe for
/// standard output and error. It leads its own session (Unix) or job (Windows): its group, which a child can leave, is
/// killed when it exits or is killed, and its job, which a child cannot leave, when it is killed or disposed.
/// The exit is observed and the process reaped exactly once, by a thread that waits for it.
/// </summary>
public sealed class ChildProcess : IAsyncDisposable
{
    /// <summary>Output beyond this rate is dropped and counted.</summary>
    public const int LogBytesPerSecond = 64 * 1024;
    private const int LineBytes = 4096;

    private readonly Lock gate = new();
    private readonly TaskCompletionSource<int?> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly SafeFileHandle? process, job;
    private bool reaped;
    private long dropped;

    private ChildProcess(int pid, FdTransport channel, SafeFileHandle? process, SafeFileHandle? job)
    {
        Pid = pid;
        Channel = channel;
        this.process = process;
        this.job = job;
    }

    public int Pid { get; }

    /// <summary>The host's end of the channel. It refuses descriptors from the runner.</summary>
    public FdTransport Channel { get; }

    /// <summary>The exit code (128 plus the signal on Unix), or null when something else reaped the process first.</summary>
    public Task<int?> Exited => exited.Task;

    /// <summary>Output bytes dropped by the rate limit.</summary>
    public long DroppedLogBytes => Interlocked.Read(ref dropped);

    /// <summary>The environment a runner starts with: no variable of the host's, only what it needs.</summary>
    public static Dictionary<string, string> RunnerEnvironment(string home, string temp)
    {
        var environment = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = home,
            ["TMPDIR"] = temp,
            ["TMP"] = temp,
            ["DOTNET_EnableDiagnostics"] = "0",
            ["LANG"] = System.Environment.GetEnvironmentVariable("LANG") is { Length: > 0 } language ? language : "C.UTF-8"
        };
        if (OperatingSystem.IsWindows())
        {
            var system = System.Environment.GetEnvironmentVariable("SystemRoot") ?? @"C:\Windows";
            environment["SystemRoot"] = system;
            environment["TEMP"] = temp;
            environment["USERPROFILE"] = home;
            environment["PATH"] = Path.Combine(system, "System32");
        }
        else environment["PATH"] = "/usr/bin:/bin";
        return environment;
    }

    public static ChildProcess Start(ChildProcessStart start)
    {
        var child = OperatingSystem.IsWindows() ? StartWindows(start) : StartUnix(start);
        return child;
    }

    /// <summary>Kills the process and everything it started; nothing once it has exited.</summary>
    public void Kill()
    {
        lock (gate)
        {
            if (reaped) return;
            if (OperatingSystem.IsWindows()) Native.Windows.TerminateJobObject(job!, 1);
            else if (Unix.Kill(-Pid, Unix.Sigkill) != 0) Unix.Kill(Pid, Unix.Sigkill);
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        await Exited.ConfigureAwait(false);
        Channel.Dispose();
        process?.Dispose();
        job?.Dispose();
    }

    private void Exit(int? code)
    {
        exited.TrySetResult(code);
    }

    private void DrainLog(Stream output, Action<string>? log)
    {
        new Thread(() =>
        {
            using (output)
            {
                var buffer = new byte[8192];
                var line = new MemoryStream();
                var clock = Stopwatch.StartNew();
                var window = TimeSpan.Zero;
                long allowed = LogBytesPerSecond, droppedInWindow = 0;
                void Emit()
                {
                    if (line.Length == 0) return;
                    var text = Encoding.UTF8.GetString(line.GetBuffer(), 0, (int)line.Length).TrimEnd('\r');
                    line.SetLength(0);
                    log?.Invoke(text);
                }
                while (true)
                {
                    int count;
                    try { count = output.Read(buffer, 0, buffer.Length); }
                    catch (IOException) { break; }
                    if (count <= 0) break;
                    if (clock.Elapsed - window >= TimeSpan.FromSeconds(1))
                    {
                        if (droppedInWindow > 0) log?.Invoke($"({droppedInWindow} bytes of plugin output dropped)");
                        window = clock.Elapsed;
                        allowed = LogBytesPerSecond;
                        droppedInWindow = 0;
                    }
                    var taken = (int)Math.Min(count, allowed);
                    allowed -= taken;
                    if (taken < count)
                    {
                        droppedInWindow += count - taken;
                        Interlocked.Add(ref dropped, count - taken);
                    }
                    for (var index = 0; index < taken; index++)
                    {
                        if (buffer[index] == (byte)'\n') Emit();
                        else if (line.Length < LineBytes) line.WriteByte(buffer[index]);
                    }
                }
                Emit();
            }
        })
        { IsBackground = true, Name = "SharpRail plugin runner output " + Pid }.Start();
    }

    [UnsupportedOSPlatform("windows")]
    private static unsafe ChildProcess StartUnix(ChildProcessStart start)
    {
        var (host, child) = OpenChannel();
        int logRead, logWrite;
        try { (logRead, logWrite) = Unix.OpenPipe(); }
        catch { Unix.Close(host); Unix.Close(child); throw; }
        int pid;
        try { pid = Spawn(start, child, logWrite); }
        catch { Unix.Close(host); Unix.Close(logRead); throw; }
        finally { Unix.Close(child); Unix.Close(logWrite); }

        var result = new ChildProcess(pid, new UnixFdTransport(new SafeFileHandle(host, true), acceptDescriptors: false), null, null);
        result.DrainLog(new FileStream(new SafeFileHandle(logRead, true), FileAccess.Read, 1, false), start.Log);
        new Thread(() =>
        {
            var info = stackalloc byte[256];
            // Wait without reaping, so a kill can never reach a recycled pid: the zombie keeps it until the reap below.
            while (Unix.WaitId(Unix.PPid, (uint)pid, info, Unix.Wexited | Unix.Wnowait) != 0)
            {
                if (Marshal.GetLastPInvokeError() == Unix.Eintr) continue;
                lock (result.gate) result.reaped = true;
                result.Exit(null);
                return;
            }
            int? code = null;
            lock (result.gate)
            {
                Unix.Kill(-pid, Unix.Sigkill);
                result.reaped = true;
                int reapedPid, status;
                while ((reapedPid = Unix.WaitPid(pid, out status, Unix.Wnohang)) < 0 && Marshal.GetLastPInvokeError() == Unix.Eintr) { }
                if (reapedPid == pid)
                {
                    var signal = status & 0x7f;
                    code = signal == 0 ? (status >> 8) & 0xff : 128 + signal;
                }
            }
            result.Exit(code);
        })
        { IsBackground = true, Name = "SharpRail plugin runner wait " + pid }.Start();
        return result;
    }

    /// <summary>A socket pair whose child end is above descriptor 3, so placing it at 3 never collides with itself.</summary>
    [UnsupportedOSPlatform("windows")]
    private static (int Host, int Child) OpenChannel()
    {
        var held = new List<int>();
        try
        {
            while (true)
            {
                var (host, child) = Unix.OpenSocketPair();
                if (child > 3) return (host, child);
                held.Add(host);
                held.Add(child);
            }
        }
        finally
        {
            foreach (var fd in held) Unix.Close(fd);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static int Spawn(ChildProcessStart start, int channel, int log)
    {
        // posix_spawnattr_t and posix_spawn_file_actions_t are pointers on macOS and structs on Linux.
        var attributes = Marshal.AllocHGlobal(1024);
        var actions = Marshal.AllocHGlobal(1024);
        var mask = Marshal.AllocHGlobal(256);
        var defaults = Marshal.AllocHGlobal(256);
        var attributesReady = false; var actionsReady = false;
        try
        {
            Check(Unix.SpawnAttrInit(attributes)); attributesReady = true;
            Check(Unix.FileActionsInit(actions)); actionsReady = true;
            Unix.SigEmptySet(mask); Unix.SigFillSet(defaults);
            Check(Unix.SpawnAttrSetMask(attributes, mask));
            Check(Unix.SpawnAttrSetDefault(attributes, defaults));
            // A new session: no controlling terminal, and the pid names the group a kill takes down. On macOS only the
            // descriptors placed below survive the spawn (POSIX_SPAWN_CLOEXEC_DEFAULT).
            Check(Unix.SpawnAttrSetFlags(attributes, Unix.SpawnFlags));
            Check(Unix.FileActionsOpen(actions, 0, "/dev/null", 0, 0));
            Check(Unix.FileActionsDup(actions, log, 1));
            Check(Unix.FileActionsDup(actions, log, 2));
            Check(Unix.FileActionsDup(actions, channel, 3));
            // Linux: glibc 2.34 closes the rest in the child; before that, every SharpRail descriptor is close-on-exec.
            Unix.TryCloseFrom(actions, 4);
            Check(Unix.FileActionsChdir(actions, start.WorkingDirectory));
            var argv = new string?[start.Arguments.Count + 2];
            argv[0] = start.Program;
            for (var index = 0; index < start.Arguments.Count; index++) argv[index + 1] = start.Arguments[index];
            var envp = start.Environment.Select(pair => pair.Key + "=" + pair.Value).Append(null).ToArray();
            var result = Unix.Spawn(out var pid, start.Program, actions, attributes, argv, envp);
            if (result != 0) throw new Win32Exception(result, $"Could not start the plugin runner: {Unix.Error(result)}.");
            return pid;
        }
        finally
        {
            if (actionsReady) Unix.FileActionsDestroy(actions);
            if (attributesReady) Unix.SpawnAttrDestroy(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(mask); Marshal.FreeHGlobal(defaults);
        }

        static void Check(int result)
        {
            if (result != 0) throw new IOException($"Could not prepare the plugin runner: {Unix.Error(result)}.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static ChildProcess StartWindows(ChildProcessStart start)
    {
        SafePipeHandle? server = null;
        SafeFileHandle? client = null, input = null, logRead = null, logWrite = null, process = null, job = null;
        nint attributes = 0, list = 0, environment = 0;
        var attributesReady = false;
        try
        {
            (server, client) = WindowsPipe.Create();
            input = Native.Windows.CreateFile("NUL", Native.Windows.GenericRead, Native.Windows.FileShareReadWrite, 0, Native.Windows.OpenExisting, 0, 0);
            if (input.IsInvalid) throw new Win32Exception();
            Check(Native.Windows.CreatePipe(out logRead, out logWrite, 0, 0));
            // Only these three are inheritable, and only while the process is created; the handle list names them all.
            SafeHandle[] inherited = [client, input, logWrite];
            foreach (var handle in inherited) Check(Native.Windows.SetHandleInformation(handle, Native.Windows.HandleFlagInherit, Native.Windows.HandleFlagInherit));
            list = Marshal.AllocHGlobal(IntPtr.Size * inherited.Length);
            for (var index = 0; index < inherited.Length; index++) Marshal.WriteIntPtr(list, index * IntPtr.Size, inherited[index].DangerousGetHandle());
            nuint bytes = 0;
            Native.Windows.InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((nint)bytes));
            Check(Native.Windows.InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
            attributesReady = true;
            Check(Native.Windows.UpdateProcThreadAttribute(attributes, 0, Native.Windows.HandleListAttribute, list, (nuint)(IntPtr.Size * inherited.Length), 0, 0));
            var startup = new Native.Windows.StartupInfoEx
            {
                Startup = new()
                {
                    Size = Marshal.SizeOf<Native.Windows.StartupInfoEx>(),
                    Flags = Native.Windows.StartfUseStdHandles,
                    Input = input.DangerousGetHandle(),
                    Output = logWrite.DangerousGetHandle(),
                    Error = logWrite.DangerousGetHandle()
                },
                Attributes = attributes
            };
            var command = new StringBuilder(Quote(start.Program));
            foreach (var argument in start.Arguments.Append("--channel").Append(client.DangerousGetHandle().ToString()))
                command.Append(' ').Append(Quote(argument));
            environment = Marshal.StringToHGlobalUni(string.Concat(start.Environment.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => pair.Key + "=" + pair.Value + "\0")) + "\0");
            var created = Native.Windows.CreateProcess(start.Program, command, 0, 0, true, Native.Windows.CreateFlags, environment, start.WorkingDirectory,
                ref startup, out var info);
            if (!created) throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not start the plugin runner.");
            process = new SafeFileHandle(info.Process, true);
            using var thread = new SafeFileHandle(info.Thread, true);
            job = Native.Windows.CreateJobObject(0, null);
            Check(!job.IsInvalid);
            var limits = new Native.Windows.JobLimits { Basic = new() { Flags = Native.Windows.JobKillOnClose } };
            Check(Native.Windows.SetInformationJobObject(job, Native.Windows.JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<Native.Windows.JobLimits>()));
            Check(Native.Windows.AssignProcessToJobObject(job, process));
            if (Native.Windows.ResumeThread(thread) == uint.MaxValue) Check(false);

            var target = process;
            var channel = new StreamFdTransport(new NamedPipeServerStream(PipeDirection.InOut, isAsync: true, isConnected: true, server),
                export: handle => Native.Windows.DuplicateHandle(Native.Windows.GetCurrentProcess(), handle, target, out var duplicate, 0, false, Native.Windows.DuplicateSameAccess)
                    ? duplicate
                    : throw new IOException("Could not pass a handle to the plugin runner.", new Win32Exception()),
                adopt: false);
            var result = new ChildProcess((int)info.ProcessId, channel, process, job);
            result.DrainLog(new FileStream(logRead, FileAccess.Read, 1, false), start.Log);
            new Thread(() =>
            {
                Native.Windows.WaitForSingleObject(target, Native.Windows.Infinite);
                int? code = Native.Windows.GetExitCodeProcess(target, out var exit) ? (int)exit : null;
                lock (result.gate) result.reaped = true;
                result.Exit(code);
            })
            { IsBackground = true, Name = "SharpRail plugin runner wait " + info.ProcessId }.Start();
            server = null; logRead = null; process = null; job = null;
            return result;
        }
        finally
        {
            if (process is not null) { Native.Windows.TerminateProcess(process, 1); process.Dispose(); }
            job?.Dispose();
            server?.Dispose();
            logRead?.Dispose();
            // The runner holds its own copies now.
            client?.Dispose(); input?.Dispose(); logWrite?.Dispose();
            if (attributesReady) Native.Windows.DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (list != 0) Marshal.FreeHGlobal(list);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }

        static void Check(bool success)
        {
            if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static string Quote(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            result.Append('\\', character == '"' ? slashes * 2 + 1 : slashes).Append(character);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }
}