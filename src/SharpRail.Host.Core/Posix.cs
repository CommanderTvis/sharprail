using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace SharpRail.Host.Core;

// libc entry points used by the PTY host, the terminal relay and workspace resolution. .NET exposes no PTY API.
internal static class Posix
{
    private const string Libc = "libc";
    internal const int ORdwr = 2;
    internal const short PollIn = 1;
    internal const int Eintr = 4, Eagain = 35, EagainLinux = 11;
    internal const int Sighup = 1, Sigkill = 9, Sigwinch = 28;
    internal const int Wnohang = 1;

    internal static readonly bool Mac = OperatingSystem.IsMacOS();
    internal static int ONoctty => Mac ? 0x20000 : 0x100;
    private static ulong Tiocswinsz => Mac ? 0x80087467UL : 0x5414UL;
    private static ulong Tiocgwinsz => Mac ? 0x40087468UL : 0x5413UL;
    // POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSID (| POSIX_SPAWN_CLOEXEC_DEFAULT on macOS)
    internal static short SpawnFlags => (short)(0x04 | 0x08 | (Mac ? 0x0400 | 0x4000 : 0x80));

    static Posix()
    {
        NativeLibrary.SetDllImportResolver(typeof(Posix).Assembly, (name, _, _) => name == Libc
            ? NativeLibrary.Load(Mac ? "/usr/lib/libSystem.B.dylib" : "libc.so.6")
            : 0);
    }

    internal static void EnsureSupported()
    {
        if (!Mac && !OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Terminals require macOS or Linux.");
        RuntimeHelpers.RunClassConstructor(typeof(Posix).TypeHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinSize { public ushort Rows, Columns, Width, Height; }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PollFd { public int Fd; public short Events, Revents; }

    [DllImport(Libc, EntryPoint = "posix_openpt", SetLastError = true)] internal static extern int OpenPt(int flags);
    [DllImport(Libc, EntryPoint = "grantpt", SetLastError = true)] internal static extern int GrantPt(int fd);
    [DllImport(Libc, EntryPoint = "unlockpt", SetLastError = true)] internal static extern int UnlockPt(int fd);
    [DllImport(Libc, EntryPoint = "ptsname_r")] internal static extern int PtsName(int fd, byte[] buffer, nint length);
    // open is variadic, but its mode argument is read only with O_CREAT.
    [DllImport(Libc, EntryPoint = "open", SetLastError = true)]
    internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport(Libc, EntryPoint = "close")] internal static extern int Close(int fd);
    [DllImport(Libc, EntryPoint = "read", SetLastError = true)] internal static extern nint Read(int fd, byte[] buffer, nint count);
    [DllImport(Libc, EntryPoint = "write", SetLastError = true)] internal static extern nint Write(int fd, ref byte buffer, nint count);
    [DllImport(Libc, EntryPoint = "poll", SetLastError = true)] internal static extern int Poll(ref PollFd fd, uint count, int timeout);
    [DllImport(Libc, EntryPoint = "poll", SetLastError = true)] internal static extern int Poll([In, Out] PollFd[] fds, uint count, int timeout);
    [DllImport(Libc, EntryPoint = "pipe", SetLastError = true)] private static extern int Pipe(int[] fds);
    [DllImport(Libc, EntryPoint = "pipe2", SetLastError = true)] private static extern int Pipe2(int[] fds, int flags);
    [DllImport(Libc, EntryPoint = "waitpid", SetLastError = true)] internal static extern int WaitPid(int pid, out int status, int options);
    [DllImport(Libc, EntryPoint = "kill", SetLastError = true)] internal static extern int Kill(int pid, int signal);
    [DllImport(Libc, EntryPoint = "tcgetpgrp", SetLastError = true)] internal static extern int ForegroundGroup(int fd);
    [DllImport(Libc, EntryPoint = "tcgetattr", SetLastError = true)] internal static extern int GetAttributes(int fd, byte[] termios);
    [DllImport(Libc, EntryPoint = "tcsetattr", SetLastError = true)] internal static extern int SetAttributes(int fd, int actions, byte[] termios);
    [DllImport(Libc, EntryPoint = "cfmakeraw")] internal static extern void MakeRaw(byte[] termios);
    [DllImport(Libc, EntryPoint = "strerror")] private static extern nint StrError(int error);
    [DllImport(Libc, EntryPoint = "realpath", SetLastError = true)] private static extern nint RealPathNative(string path, nint resolved);
    [DllImport(Libc, EntryPoint = "free")] private static extern void Free(nint pointer);

    /// <summary>The physical path with every symbolic link resolved, as Git reports it, or null when it cannot be resolved.</summary>
    internal static string? RealPath(string path)
    {
        if (!Mac && !OperatingSystem.IsLinux()) return null;
        var resolved = RealPathNative(path, 0);
        if (resolved == 0) return null;
        try { return Marshal.PtrToStringUTF8(resolved); }
        finally { Free(resolved); }
    }

    [DllImport(Libc, EntryPoint = "sigemptyset")] internal static extern int SigEmptySet(nint set);
    [DllImport(Libc, EntryPoint = "sigfillset")] internal static extern int SigFillSet(nint set);
    [DllImport(Libc, EntryPoint = "posix_spawnattr_init")] internal static extern int SpawnAttrInit(nint attributes);
    [DllImport(Libc, EntryPoint = "posix_spawnattr_destroy")] internal static extern int SpawnAttrDestroy(nint attributes);
    [DllImport(Libc, EntryPoint = "posix_spawnattr_setflags")] internal static extern int SpawnAttrSetFlags(nint attributes, short flags);
    [DllImport(Libc, EntryPoint = "posix_spawnattr_setsigmask")] internal static extern int SpawnAttrSetMask(nint attributes, nint set);
    [DllImport(Libc, EntryPoint = "posix_spawnattr_setsigdefault")] internal static extern int SpawnAttrSetDefault(nint attributes, nint set);
    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_init")] internal static extern int FileActionsInit(nint actions);
    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_destroy")] internal static extern int FileActionsDestroy(nint actions);
    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_addopen")]
    internal static extern int FileActionsOpen(nint actions, int fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, int mode);
    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_adddup2")] internal static extern int FileActionsDup(nint actions, int fd, int target);
    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_addchdir_np")]
    internal static extern int FileActionsChdir(nint actions, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);
    [DllImport(Libc, EntryPoint = "posix_spawn")]
    internal static extern int Spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint actions, nint attributes,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] envp);

    // LPStr is UTF-8 on Unix.
    // ioctl is variadic. Apple arm64 passes variadic arguments on the stack, so eight register
    // arguments push the pointer into the first stack slot where ioctl reads it.
    [DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlRegister(int fd, ulong request, ref WinSize size);
    [DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)]
    private static extern int IoctlStack(int fd, ulong request, nint r2, nint r3, nint r4, nint r5, nint r6, nint r7, ref WinSize size);

    private static int Ioctl(int fd, ulong request, ref WinSize size) =>
        Mac && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? IoctlStack(fd, request, 0, 0, 0, 0, 0, 0, ref size)
            : IoctlRegister(fd, request, ref size);

    internal static int SetWindowSize(int fd, int columns, int rows)
    {
        var size = new WinSize { Columns = (ushort)Math.Clamp(columns, 1, ushort.MaxValue), Rows = (ushort)Math.Clamp(rows, 1, ushort.MaxValue) };
        return Ioctl(fd, Tiocswinsz, ref size);
    }

    internal static bool TryGetWindowSize(int fd, out int columns, out int rows)
    {
        var size = new WinSize();
        var ok = Ioctl(fd, Tiocgwinsz, ref size) == 0 && size.Columns > 0 && size.Rows > 0;
        columns = size.Columns; rows = size.Rows;
        return ok;
    }

    /// <summary>A pipe no other child inherits: close-on-exec on Linux, while macOS spawns close everything by default.</summary>
    internal static (int Read, int Write) OpenPipe()
    {
        var fds = new int[2];
        if ((Mac ? Pipe(fds) : Pipe2(fds, 0x80000)) != 0) throw new IOException("Could not open a pipe: " + LastError());
        return (fds[0], fds[1]);
    }

    internal static string Error(int error) => Marshal.PtrToStringUTF8(StrError(error)) ?? "error " + error;
    internal static string LastError() => Error(Marshal.GetLastPInvokeError());
}