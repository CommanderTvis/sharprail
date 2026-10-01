using System.Diagnostics;
using System.Runtime.InteropServices;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

public sealed partial class ProjectServices
{
    public async ValueTask ApplyFileActionAsync(FileAction action, CancellationToken cancellationToken = default)
    {
        await mutations.WaitAsync(cancellationToken);
        try
        {
            var currentRoot = root;
            var path = Resolve(currentRoot, action.Path);
            var exists = File.Exists(path) || Directory.Exists(path);
            if (action.Kind is "create-file" or "create-folder")
            {
                if (exists) throw new IOException($"{action.Path} already exists");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                if (action.Kind == "create-folder") Directory.CreateDirectory(path);
                else await new FileStream(path, FileMode.CreateNew).DisposeAsync();
                return;
            }
            if (path == currentRoot) throw new InvalidOperationException("The workspace folder itself cannot be changed here.");
            if (!exists) throw new FileNotFoundException($"{action.Path} no longer exists", action.Path);
            switch (action.Kind)
            {
                case "rename":
                    var target = Resolve(currentRoot, action.To);
                    // Only a case-only rename may land on an existing entry: on a case-insensitive disk it is the same file.
                    if ((File.Exists(target) || Directory.Exists(target)) && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"{action.To} already exists");
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    if (Directory.Exists(path)) Directory.Move(path, target);
                    else File.Move(path, target);
                    break;
                case "trash":
                    Trash(path);
                    break;
                case "reveal":
                    RevealFile(path);
                    break;
                default:
                    throw new ArgumentException($"Unknown file action \"{action.Kind}\".");
            }
        }
        finally { mutations.Release(); }
    }

    // Selects the entry in its folder rather than opening it; Linux has no portable select verb, so it opens the folder.
    private static void RevealFile(string path)
    {
        var start = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { "-R", path } }
            : OperatingSystem.IsWindows() ? new ProcessStartInfo("explorer", "/select," + path)
            : new ProcessStartInfo("xdg-open") { ArgumentList = { Path.GetDirectoryName(path)! } };
        start.UseShellExecute = false;
        using var process = Process.Start(start) ?? throw new IOException("No file manager is available on this computer.");
    }

    private static void Trash(string path)
    {
        if (OperatingSystem.IsMacOS()) { MacTrash.Move(path); return; }
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException("Moving files to the trash is not supported on this platform.");
        using var process = Process.Start(new ProcessStartInfo("gio") { ArgumentList = { "trash", "--", path }, UseShellExecute = false, RedirectStandardError = true })
            ?? throw new IOException("gio is not available to move files to the trash.");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException(error.Trim());
    }

    // NSFileManager's trashItemAtURL, so a deleted entry can be put back from the Trash like one Finder deleted.
    private static class MacTrash
    {
        private const string Objc = "/usr/lib/libobjc.A.dylib";

        static MacTrash() => NativeLibrary.Load("/System/Library/Frameworks/Foundation.framework/Foundation");

        [DllImport(Objc, EntryPoint = "objc_getClass")] private static extern nint Class([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Objc, EntryPoint = "sel_registerName")] private static extern nint Selector([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector);
        [DllImport(Objc, EntryPoint = "objc_msgSend")] private static extern nint Send(nint receiver, nint selector, nint argument);
        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        private static extern nint SendUtf8(nint receiver, nint selector, [MarshalAs(UnmanagedType.LPUTF8Str)] string argument);
        [DllImport(Objc, EntryPoint = "objc_msgSend")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool SendTrash(nint receiver, nint selector, nint url, nint resulting, out nint error);
        [DllImport(Objc, EntryPoint = "objc_autoreleasePoolPush")] private static extern nint PoolPush();
        [DllImport(Objc, EntryPoint = "objc_autoreleasePoolPop")] private static extern void PoolPop(nint pool);

        internal static void Move(string path)
        {
            var pool = PoolPush();
            try
            {
                var text = SendUtf8(Class("NSString"), Selector("stringWithUTF8String:"), path);
                var url = Send(Class("NSURL"), Selector("fileURLWithPath:"), text);
                var manager = Send(Class("NSFileManager"), Selector("defaultManager"));
                if (SendTrash(manager, Selector("trashItemAtURL:resultingItemURL:error:"), url, 0, out var error)) return;
                var description = error == 0 ? 0 : Send(error, Selector("localizedDescription"));
                var message = description == 0 ? null : Marshal.PtrToStringUTF8(Send(description, Selector("UTF8String")));
                throw new IOException(message ?? "The file could not be moved to the Trash.");
            }
            finally { PoolPop(pool); }
        }
    }
}