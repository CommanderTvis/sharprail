using System.Runtime.InteropServices;

namespace SharpRail.Plugins.Codex.Host.IdeBridge;

/// <summary>What the bridge needs of a path's metadata: its type and permission bits, owner and identity.</summary>
internal readonly record struct UnixStat(uint Mode, uint Uid, ulong Inode, ulong Device)
{
    private const uint TypeMask = 0xF000;
    public bool IsDirectory => (Mode & TypeMask) == 0x4000;
    public bool IsSocket => (Mode & TypeMask) == 0xC000;
}

/// <summary>
/// The libc calls .NET does not expose: <c>lstat</c> (owner and inode), <c>getuid</c> and <c>realpath</c>. The
/// <c>struct stat</c> layouts read are macOS's 64-bit-inode one and Linux's x86-64 and generic (arm64) ones.
/// </summary>
internal static partial class Unix
{
    private const int Enoent = 2;

    [LibraryImport("libc", EntryPoint = "lstat", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LstatNative(string path, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "lstat$INODE64", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int LstatInode64(string path, Span<byte> buffer);

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint GetUid();

    [LibraryImport("libc", EntryPoint = "realpath", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial nint RealPathNative(string path, nint resolved);

    [LibraryImport("libc", EntryPoint = "free")]
    private static partial void Free(nint pointer);

    public static uint Uid => GetUid();

    /// <summary>The path's own metadata (a symlink is not followed); null when it does not exist.</summary>
    public static UnixStat? Lstat(string path)
    {
        Span<byte> buffer = stackalloc byte[512];
        var result = OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64 ? LstatInode64(path, buffer) : LstatNative(path, buffer);
        if (result != 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            if (errno == Enoent) return null;
            throw new IOException($"lstat {path} failed with errno {errno}");
        }
        if (OperatingSystem.IsMacOS())
            return new(BitConverter.ToUInt16(buffer[4..]), BitConverter.ToUInt32(buffer[16..]), BitConverter.ToUInt64(buffer[8..]), BitConverter.ToUInt32(buffer));
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64)
            return new(BitConverter.ToUInt32(buffer[24..]), BitConverter.ToUInt32(buffer[28..]), BitConverter.ToUInt64(buffer[8..]), BitConverter.ToUInt64(buffer));
        return new(BitConverter.ToUInt32(buffer[16..]), BitConverter.ToUInt32(buffer[24..]), BitConverter.ToUInt64(buffer[8..]), BitConverter.ToUInt64(buffer));
    }

    /// <summary>The canonical absolute path, symlinks resolved; the full path itself when it cannot be resolved.</summary>
    public static string RealPath(string path)
    {
        if (OperatingSystem.IsWindows()) return Path.GetFullPath(path);
        var resolved = RealPathNative(path, 0);
        if (resolved == 0) return Path.GetFullPath(path);
        try { return Marshal.PtrToStringUTF8(resolved) ?? Path.GetFullPath(path); }
        finally { Free(resolved); }
    }
}