using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

using Microsoft.Win32.SafeHandles;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

internal sealed class ConPtySession : ITerminalProcess
{
    private readonly FileStream input, output;
    private readonly SafeFileHandle process, job;
    private nint console;
    private readonly Lock consoleGate = new(), writing = new();
    private readonly Channel<ReadOnlyMemory<byte>> chunks = Channel.CreateBounded<ReadOnlyMemory<byte>>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int reading, disposed;

    public string Id { get; }
    public int Pid { get; }
    public Task<int> Exit => exit.Task;
    public string? CurrentDirectory => null;
    public bool IsBusy => !exited.Task.IsCompleted && Volatile.Read(ref disposed) == 0 && WindowsProcesses.Parents().Values.Contains(Pid);

    private ConPtySession(string id, int pid, nint console, SafeFileHandle process, SafeFileHandle job, SafeFileHandle input, SafeFileHandle output)
    {
        Id = id; Pid = pid; this.console = console; this.process = process; this.job = job;
        this.input = new FileStream(input, FileAccess.Write, 4096, false);
        this.output = new FileStream(output, FileAccess.Read, 4096, false);
        new Thread(Pump) { IsBackground = true, Name = "SharpRail ConPTY read " + pid }.Start();
        new Thread(Wait) { IsBackground = true, Name = "SharpRail ConPTY wait " + pid }.Start();
    }

    internal static ConPtySession Start(string id, string shell, string directory, int columns, int rows, string? mcpUrl,
        IReadOnlyDictionary<string, string>? contributed)
    {
        TerminalGrid.Require(columns, rows);
        SafeFileHandle? inputRead = null, inputWrite = null, outputRead = null, outputWrite = null, process = null, job = null;
        nint console = 0, attributes = 0, environment = 0;
        var attributesReady = false;
        try
        {
            Check(Native.CreatePipe(out inputRead, out inputWrite, 0, 0));
            Check(Native.CreatePipe(out outputRead, out outputWrite, 0, 0));
            Marshal.ThrowExceptionForHR(Native.CreatePseudoConsole(new((short)columns, (short)rows), inputRead, outputWrite, 0, out console));
            nuint bytes = 0;
            Native.InitializeProcThreadAttributeList(0, 1, 0, ref bytes);
            attributes = Marshal.AllocHGlobal(checked((nint)bytes));
            Check(Native.InitializeProcThreadAttributeList(attributes, 1, 0, ref bytes));
            attributesReady = true;
            Check(Native.UpdateProcThreadAttribute(attributes, 0, 0x20016, console, (nuint)IntPtr.Size, 0, 0));
            var startup = new Native.StartupInfoEx { Startup = new() { Size = Marshal.SizeOf<Native.StartupInfoEx>() }, Attributes = attributes };
            environment = Marshal.StringToHGlobalUni(string.Join('\0', PtyTerminalService.ShellEnvironment(mcpUrl, contributed).OfType<string>()) + "\0\0");
            Check(Native.CreateProcess(shell, new StringBuilder(CommandLine(shell)), 0, 0, false,
                0x00080000 | 0x00000400 | 0x00000004, environment, directory, ref startup, out var info));
            process = new SafeFileHandle(info.Process, true);
            using var thread = new SafeFileHandle(info.Thread, true);
            job = Native.CreateJobObject(0, null);
            Check(!job.IsInvalid);
            var limits = new Native.JobLimits { Basic = new() { Flags = 0x2000 } };
            Check(Native.SetInformationJobObject(job, 9, ref limits, (uint)Marshal.SizeOf<Native.JobLimits>()));
            Check(Native.AssignProcessToJobObject(job, process));
            if (Native.ResumeThread(thread) == uint.MaxValue) Check(false);
            var result = new ConPtySession(id, (int)info.ProcessId, console, process, job, inputWrite, outputRead);
            console = 0; process = null; job = null; inputWrite = null; outputRead = null;
            return result;
        }
        catch (Exception error) when (error is Win32Exception or COMException)
        {
            throw new IOException("Couldn't start the Windows pseudo-terminal.", error);
        }
        finally
        {
            if (process is not null) { Native.TerminateProcess(process, 1); process.Dispose(); }
            job?.Dispose();
            inputRead?.Dispose(); outputWrite?.Dispose(); inputWrite?.Dispose();
            // A failed launch has no pump to drain a final frame.
            outputRead?.Dispose();
            if (console != 0) Native.ClosePseudoConsole(console);
            if (attributesReady) Native.DeleteProcThreadAttributeList(attributes);
            if (attributes != 0) Marshal.FreeHGlobal(attributes);
            if (environment != 0) Marshal.FreeHGlobal(environment);
        }
    }

    internal static string CommandLine(string shell)
    {
        var name = Path.GetFileNameWithoutExtension(shell);
        var arguments = name.Equals("pwsh", StringComparison.OrdinalIgnoreCase) || name.Equals("powershell", StringComparison.OrdinalIgnoreCase) ? " -NoLogo"
            : name.Equals("bash", StringComparison.OrdinalIgnoreCase) || name.Equals("zsh", StringComparison.OrdinalIgnoreCase) ? " -l" : "";
        return Quote(shell) + arguments;
    }

    internal static string Quote(string value)
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

    private static void Check(bool success)
    {
        if (!success) throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    private void Wait()
    {
        var code = -1;
        try
        {
            if (Native.WaitForSingleObject(process, uint.MaxValue) == 0 && Native.GetExitCodeProcess(process, out var status)) code = unchecked((int)status);
        }
        finally
        {
            lock (consoleGate)
            {
                if (console != 0) { Native.ClosePseudoConsole(console); console = 0; }
            }
            exited.TrySetResult(code);
        }
    }

    private void Pump()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int count;
            while ((count = output.Read(buffer)) > 0)
            {
                var chunk = buffer.AsSpan(0, count).ToArray();
                if (!chunks.Writer.TryWrite(chunk)) chunks.Writer.WriteAsync(chunk).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (IOException) { }
        finally
        {
            chunks.Writer.TryComplete();
            exited.Task.ContinueWith(task => exit.TrySetResult(task.Result), TaskScheduler.Default);
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref reading, 1) != 0) throw new InvalidOperationException("A terminal session has a single reader.");
        await foreach (var chunk in chunks.Reader.ReadAllAsync(cancellationToken)) yield return chunk;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (writing)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (exited.Task.IsCompleted) return ValueTask.CompletedTask;
            input.Write(data.Span); input.Flush();
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        TerminalGrid.Require(columns, rows);
        lock (consoleGate)
            if (console != 0 && Volatile.Read(ref disposed) == 0) Marshal.ThrowExceptionForHR(Native.ResizePseudoConsole(console, new((short)columns, (short)rows)));
        return ValueTask.CompletedTask;
    }

    public async ValueTask KillAsync(CancellationToken cancellationToken = default)
    {
        if (exited.Task.IsCompleted) return;
        Check(Native.TerminateJobObject(job, 1));
        await exited.Task.WaitAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await KillAsync();
        await exit.Task;
        lock (writing) input.Dispose();
        output.Dispose(); process.Dispose(); job.Dispose();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal readonly record struct Coord(short X, short Y);
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        {
            internal int Size;
            internal nint Reserved, Desktop, Title;
            internal uint X, Y, Width, Height, Columns, Rows, Fill, Flags;
            internal ushort Show, ReservedSize;
            internal nint ReservedBytes, Input, Output, Error;
        }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { internal StartupInfo Startup; internal nint Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { internal nint Process, Thread; internal uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicJobLimits
        {
            internal long ProcessTime, JobTime;
            internal uint Flags;
            internal nuint MinimumWorkingSet, MaximumWorkingSet;
            internal uint ActiveProcessLimit;
            internal nuint Affinity;
            internal uint Priority, Scheduling;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobLimits
        {
            internal BasicJobLimits Basic;
            internal ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            internal nuint ProcessMemory, JobMemory, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint security, uint size);
        [DllImport("kernel32.dll")] internal static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);
        [DllImport("kernel32.dll")] internal static extern int ResizePseudoConsole(nint console, Coord size);
        [DllImport("kernel32.dll")] internal static extern void ClosePseudoConsole(nint console);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool InitializeProcThreadAttributeList(nint list, int count, uint flags, ref nuint size);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UpdateProcThreadAttribute(nint list, uint flags, nuint attribute, nint value, nuint size, nint previous, nint returnedSize);
        [DllImport("kernel32.dll")] internal static extern void DeleteProcThreadAttributeList(nint list);
        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CreateProcess(string program, StringBuilder command, nint processSecurity, nint threadSecurity, [MarshalAs(UnmanagedType.Bool)] bool inherit,
            uint flags, nint environment, string directory, ref StartupInfoEx startup, out ProcessInfo info);
        [DllImport("kernel32.dll", SetLastError = true)] internal static extern uint ResumeThread(SafeFileHandle thread);
        [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool GetExitCodeProcess(SafeFileHandle process, out uint code);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateProcess(SafeFileHandle process, uint code);
        [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)] internal static extern SafeFileHandle CreateJobObject(nint security, string? name);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetInformationJobObject(SafeFileHandle job, int kind, ref JobLimits limits, uint size);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool TerminateJobObject(SafeFileHandle job, uint code);
    }
}