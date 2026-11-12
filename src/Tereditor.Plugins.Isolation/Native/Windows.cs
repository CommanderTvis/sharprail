using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

using Microsoft.Win32.SafeHandles;

namespace Tereditor.Plugins.Isolation.Native;

/// <summary>Win32 entry points of the channel and the runner process: the named pipe, the handle list and the job.</summary>
[SupportedOSPlatform("windows")]
internal static class Windows
{
    internal const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    internal const uint OpenExisting = 3, FileShareReadWrite = 3;
    internal const uint PipeAccessDuplex = 3, FileFlagFirstPipeInstance = 0x00080000, FileFlagOverlapped = 0x40000000;
    // PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS
    internal const uint PipeMode = 0x8;
    internal const uint HandleFlagInherit = 1;
    internal const uint DuplicateSameAccess = 2;
    internal const nuint HandleListAttribute = 0x20002;
    internal const uint StartfUseStdHandles = 0x100;
    // EXTENDED_STARTUPINFO_PRESENT | CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED | CREATE_NO_WINDOW
    internal const uint CreateFlags = 0x00080000 | 0x00000400 | 0x00000004 | 0x08000000;
    internal const int JobObjectExtendedLimitInformation = 9;
    internal const uint JobKillOnClose = 0x2000;
    internal const uint Infinite = 0xFFFFFFFF;

    [StructLayout(LayoutKind.Sequential)]
    internal struct SecurityAttributes
    {
        internal int Length;
        internal nint Descriptor;
        internal int Inherit;
    }

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

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ConvertSecurityDescriptor(string sddl, uint revision, out nint descriptor, nint size);
    [DllImport("kernel32.dll")] internal static extern nint LocalFree(nint memory);
    [DllImport("kernel32.dll", EntryPoint = "CreateNamedPipeW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint pipeMode, uint instances, uint outBuffer, uint inBuffer,
        uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFile(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint security, uint size);
    [DllImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static extern bool SetHandleInformation(SafeHandle handle, uint mask, uint flags);
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
    [DllImport("kernel32.dll")] internal static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateHandle(nint sourceProcess, SafeHandle source, SafeFileHandle targetProcess, out nint target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool DuplicateHandle(nint sourceProcess, SafeHandle source, nint targetProcess, out nint target, uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
}