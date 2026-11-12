using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace SharpRail.Plugins.Isolation.Native;

/// <summary>
/// libc entry points of the channel and the runner process on macOS and Linux (64-bit only). Layouts and constants
/// come from the macOS SDK headers and glibc's (bits/socket.h, spawn.h): msghdr, cmsghdr and the spawn flags differ.
/// </summary>
[UnsupportedOSPlatform("windows")]
internal static unsafe class Unix
{
    private const string Libc = "libc";
    internal static readonly bool Mac = OperatingSystem.IsMacOS();

    internal const int Eintr = 4, Echild = 10, Esrch = 3;
    internal const int Sigkill = 9;
    internal const int ScmRights = 1, ShutRdwr = 2;
    internal const int AfUnix = 1, SockStream = 1;
    internal const int PPid = 1, Wnohang = 1, Wexited = 4;

    internal static int SolSocket => Mac ? 0xffff : 1;
    internal static int MsgCtrunc => Mac ? 0x20 : 0x08;
    // MSG_CMSG_CLOEXEC on receive and MSG_NOSIGNAL on send exist only on Linux; macOS marks received descriptors itself.
    internal static int ReceiveFlags => Mac ? 0 : 0x40000000;
    internal static int SendFlags => Mac ? 0 : 0x4000;
    internal static int SockCloexec => Mac ? 0 : 0x80000;
    internal static int Wnowait => Mac ? 0x20 : 0x01000000;
    // POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK | POSIX_SPAWN_SETSID (| POSIX_SPAWN_CLOEXEC_DEFAULT on macOS).
    internal static short SpawnFlags => (short)(0x04 | 0x08 | (Mac ? 0x0400 | 0x4000 : 0x80));
    private static ulong Fioclex => Mac ? 0x20006601UL : 0x5451UL;

    // struct cmsghdr: socklen_t len, int level, int type on macOS (data aligned to 4); size_t len on Linux (aligned to 8).
    internal static int CmsgHeader => Mac ? 12 : 16;
    internal static int CmsgAlign(int length) => Mac ? (length + 3) & ~3 : (length + 7) & ~7;
    internal static int CmsgSpace(int length) => CmsgHeader + CmsgAlign(length);
    // struct msghdr: msg_iovlen is int and msg_controllen socklen_t on macOS, size_t on Linux.
    internal static int MsgHeaderSize => Mac ? 48 : 56;

    static Unix()
    {
        NativeLibrary.SetDllImportResolver(typeof(Unix).Assembly, (name, _, _) => name == Libc
            ? NativeLibrary.Load(Mac ? "/usr/lib/libSystem.B.dylib" : "libc.so.6")
            : 0);
    }

    internal static void WriteMsgHeader(byte* header, void* iov, int iovCount, void* control, int controlLength)
    {
        new Span<byte>(header, MsgHeaderSize).Clear();
        *(void**)(header + 16) = iov;
        if (Mac) *(int*)(header + 24) = iovCount; else *(ulong*)(header + 24) = (ulong)iovCount;
        *(void**)(header + 32) = control;
        if (Mac) *(uint*)(header + 40) = (uint)controlLength; else *(ulong*)(header + 40) = (ulong)controlLength;
    }

    internal static int MsgFlags(byte* header) => *(int*)(header + (Mac ? 44 : 48));
    internal static int MsgControlLength(byte* header) => Mac ? (int)*(uint*)(header + 40) : (int)*(ulong*)(header + 40);

    internal static void WriteCmsg(byte* cmsg, int level, int type, int dataLength)
    {
        var length = CmsgHeader + dataLength;
        if (Mac) { *(uint*)cmsg = (uint)length; *(int*)(cmsg + 4) = level; *(int*)(cmsg + 8) = type; }
        else { *(ulong*)cmsg = (ulong)length; *(int*)(cmsg + 8) = level; *(int*)(cmsg + 12) = type; }
    }

    internal static (int Length, int Level, int Type) ReadCmsg(byte* cmsg) => Mac
        ? ((int)*(uint*)cmsg, *(int*)(cmsg + 4), *(int*)(cmsg + 8))
        : ((int)*(ulong*)cmsg, *(int*)(cmsg + 8), *(int*)(cmsg + 12));

    /// <summary>
    /// Marks a descriptor close-on-exec: ioctl FIOCLEX takes no third argument, so the variadic call is safe, where
    /// fcntl F_SETFD would pass its flag where Apple's arm64 ABI does not read variadic arguments.
    /// </summary>
    internal static void CloseOnExec(int fd)
    {
        if (Ioctl(fd, Fioclex) != 0) throw new IOException("Could not mark a descriptor close-on-exec: " + LastError());
    }

    [DllImport(Libc, EntryPoint = "socketpair", SetLastError = true)] internal static extern int SocketPair(int domain, int type, int protocol, int* fds);
    [DllImport(Libc, EntryPoint = "sendmsg", SetLastError = true)] internal static extern nint SendMsg(int fd, byte* message, int flags);
    [DllImport(Libc, EntryPoint = "recvmsg", SetLastError = true)] internal static extern nint ReceiveMsg(int fd, byte* message, int flags);
    [DllImport(Libc, EntryPoint = "shutdown", SetLastError = true)] internal static extern int Shutdown(int fd, int how);
    [DllImport(Libc, EntryPoint = "close")] internal static extern int Close(int fd);
    [DllImport(Libc, EntryPoint = "ioctl", SetLastError = true)] private static extern int Ioctl(int fd, ulong request);
    [DllImport(Libc, EntryPoint = "pipe", SetLastError = true)] private static extern int Pipe(int* fds);
    [DllImport(Libc, EntryPoint = "pipe2", SetLastError = true)] private static extern int Pipe2(int* fds, int flags);
    [DllImport(Libc, EntryPoint = "waitid", SetLastError = true)] internal static extern int WaitId(int idType, uint id, byte* info, int options);
    [DllImport(Libc, EntryPoint = "waitpid", SetLastError = true)] internal static extern int WaitPid(int pid, out int status, int options);
    [DllImport(Libc, EntryPoint = "kill", SetLastError = true)] internal static extern int Kill(int pid, int signal);
    [DllImport(Libc, EntryPoint = "strerror")] private static extern nint StrError(int error);

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
    // glibc 2.34 and later; the packages' glibc 2.31 baseline may lack it.
    [DllImport(Libc, EntryPoint = "posix_spawn_file_actions_addclosefrom_np")] private static extern int FileActionsCloseFrom(nint actions, int from);
    [DllImport(Libc, EntryPoint = "posix_spawn")]
    internal static extern int Spawn(out int pid, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint actions, nint attributes,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] argv,
        [MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.LPStr)] string?[] envp);

    /// <summary>Closes every descriptor from <paramref name="from"/> in the child, where glibc offers it; false otherwise.</summary>
    internal static bool TryCloseFrom(nint actions, int from)
    {
        if (Mac) return false;
        try { return FileActionsCloseFrom(actions, from) == 0; }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>A pipe whose ends no other child inherits.</summary>
    internal static (int Read, int Write) OpenPipe()
    {
        var fds = stackalloc int[2];
        if ((Mac ? Pipe(fds) : Pipe2(fds, 0x80000)) != 0) throw new IOException("Could not open a pipe: " + LastError());
        if (Mac) { CloseOnExec(fds[0]); CloseOnExec(fds[1]); }
        return (fds[0], fds[1]);
    }

    /// <summary>A connected stream socket pair whose ends no other child inherits.</summary>
    internal static (int First, int Second) OpenSocketPair()
    {
        var fds = stackalloc int[2];
        if (SocketPair(AfUnix, SockStream | SockCloexec, 0, fds) != 0) throw new IOException("Could not open a socket pair: " + LastError());
        if (Mac) { CloseOnExec(fds[0]); CloseOnExec(fds[1]); }
        return (fds[0], fds[1]);
    }

    internal static string Error(int error) => Marshal.PtrToStringUTF8(StrError(error)) ?? "error " + error;
    internal static string LastError() => Error(Marshal.GetLastPInvokeError());
}