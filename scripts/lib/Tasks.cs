// Shared by the C# task scripts (scripts/tasks/*.cs, scripts/ci-run.cs and others) through `#:include`. It has no entry
// point: the helpers are the few things every task needs, a repository root, child processes with argument lists, the
// pinned tools, a directory lock and copies that keep symbolic links and file modes.
using System.Diagnostics;
using System.Text;

/// <summary>A failure to report as a message and an exit code, without a stack trace.</summary>
sealed class TaskFailure(string message, int code = 1) : Exception(message)
{
    public int Code { get; } = code;
}

static class Tasks
{
    public static readonly string Root = FindRoot();

    /// <summary>
    /// What the packages call the app and host binaries. The publish names them after their projects (Tereditor.UI,
    /// Tereditor.Host.Remote), whose assembly names avares:// URIs and the Android build rely on, so the packaging tasks
    /// rename only the shipped NativeAOT binary. The checks' PackagedBinaries names the same two.
    /// </summary>
    public const string AppExecutable = "tereditor", HostExecutable = "tereditor-host";

    /// <summary>Renames the NativeAOT binary <paramref name="project"/> published into <paramref name="directory"/> to <paramref name="name"/>.</summary>
    public static string RenameExecutable(string directory, string project, string name)
    {
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        var target = Path.Combine(directory, name + extension);
        File.Move(Path.Combine(directory, project + extension), target, true);
        return target;
    }

    /// <summary>The app icon's Icon Composer document and its compiled outputs, checked in (`scripts/mise run icon:macos`).</summary>
    public const string IconDocument = "src/Tereditor.UI/tereditor.icon", IconOutput = "src/Tereditor.UI/AppIcon/compiled",
        IconHashFile = IconOutput + "/tereditor.icon.sha256";

    /// <summary>A hash of the icon document's files (relative path and content, in path order).</summary>
    public static string IconHash()
    {
        var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var file in Directory.EnumerateFiles(IconDocument, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(Path.GetRelativePath(IconDocument, file).Replace('\\', '/') + "\n"));
            hash.AppendData(File.ReadAllBytes(file));
        }
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    public static void VerifyIcon()
    {
        if (!File.Exists(IconHashFile) || !File.Exists(Path.Combine(IconOutput, "Assets.car")) || !File.Exists(Path.Combine(IconOutput, "tereditor.icns")) ||
            File.ReadAllText(IconHashFile).Trim() != IconHash())
            throw new TaskFailure($"The compiled app icon in {IconOutput} is missing or does not match {IconDocument}; run `scripts/mise run icon:macos` (needs Xcode 26 or newer) and commit the result.");
    }

    /// <summary>
    /// Lays out a macOS app bundle at <paramref name="bundle"/> (removed first) around the UI's publish output
    /// <paramref name="published"/>: the executable and its native libraries in Contents/MacOS, terminfo and the built-in
    /// plugins in Contents/Resources, the Info.plist, and the compiled icon. The executable is renamed to
    /// <see cref="AppExecutable"/> and restamped as built with SDK 26, which breaks its signature: the caller signs the
    /// bundle once it has added its own files. Returns the Resources directory.
    /// </summary>
    public static string LayOutMacBundle(string published, string bundle)
    {
        DeleteAll(bundle);
        var macOS = Path.Combine(bundle, "Contents", "MacOS");
        var resources = Path.Combine(bundle, "Contents", "Resources");
        Directory.CreateDirectory(macOS);
        Directory.CreateDirectory(resources);
        // The shell glob this replaced skipped names that start with a dot; a bundle kept inside the output stays out.
        foreach (var item in Directory.EnumerateFileSystemEntries(published).Where(path => !Path.GetFileName(path).StartsWith('.') && !path.EndsWith(".app", StringComparison.Ordinal)))
        {
            var name = Path.GetFileName(item);
            if (name is "terminfo" or "plugins")
            {
                Copy(item, Path.Combine(resources, name));
                if (name == "plugins") File.CreateSymbolicLink(Path.Combine(macOS, "plugins"), "../Resources/plugins");
            }
            else CopyInto(item, macOS);
        }
        WriteLf(Path.Combine(bundle, "Contents", "Info.plist"), $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict>
            <key>CFBundleExecutable</key><string>{AppExecutable}</string>
            <key>CFBundleIdentifier</key><string>dev.thinkrail.tereditor</string>
            <key>CFBundleName</key><string>tereditor</string>
            <key>CFBundleVersion</key><string>1</string>
            <key>CFBundleShortVersionString</key><string>0.2.0</string>
            <key>CFBundlePackageType</key><string>APPL</string>
            <key>CFBundleIconFile</key><string>tereditor</string>
            <key>CFBundleIconName</key><string>tereditor</string>
            <key>NSHighResolutionCapable</key><true/>
            </dict></plist>

            """);
        // Tahoe rounds windows by the SDK the executable was linked against: a 15.x stamp keeps the old small corner
        // radius, so the executable is restamped as built with SDK 26 (the caller redoes the signature).
        var exe = File.Exists(Path.Combine(macOS, "Tereditor.UI")) ? RenameExecutable(macOS, "Tereditor.UI", AppExecutable) : Path.Combine(macOS, AppExecutable);
        var (_, show) = Capture("xcrun", ["vtool", "-show-build", exe]);
        var minos = show.Split('\n').Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .FirstOrDefault(fields => fields.Any(field => field.Contains("minos")) && fields.Length > 1)?[1] ?? "";
        if (Capture("xcrun", ["vtool", "-set-build-version", "macos", minos, "26.0", "-replace", "-output", exe + ".stamped", exe], discardError: true).Code != 0)
            throw new TaskFailure("xcrun vtool -set-build-version failed");
        File.Move(exe + ".stamped", exe, true);
        MakeExecutable(exe);
        // The icon: the compiled Icon Composer document (Assets.car, which macOS 26 draws with the system shape, Liquid Glass and
        // the user's icon style via CFBundleIconName, and tereditor.icns at every size for older macOS via CFBundleIconFile) is
        // checked in, so a bundle needs no actool; the hash file ties it to the document.
        VerifyIcon();
        foreach (var file in new[] { "Assets.car", "tereditor.icns" }) File.Copy(Path.Combine(IconOutput, file), Path.Combine(resources, file), true);
        return resources;
    }

    /// <summary>The dotnet to run builds with: DOTNET when set, else the one on PATH (the pinned SDK under mise).</summary>
    public static string Dotnet => Environment.GetEnvironmentVariable("DOTNET") is { Length: > 0 } dotnet ? dotnet : "dotnet";

    static string FindRoot()
    {
        var entry = AppContext.GetData("EntryPointFilePath") as string;
        for (var directory = new DirectoryInfo(entry is null ? Directory.GetCurrentDirectory() : Path.GetDirectoryName(entry)!); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "global.json"))) return directory.FullName;
        throw new TaskFailure("Cannot find the repository root (global.json) above the script.");
    }

    /// <summary>Runs a task: from the repository root, reporting a failure as one message. Ctrl+C reaches the child too and ends it, so this process waits for it instead of dying first.</summary>
    public static int Guard(Func<int> task)
    {
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        Directory.SetCurrentDirectory(Root);
        try { return task(); }
        catch (TaskFailure failure)
        {
            if (failure.Message.Length > 0) Console.Error.WriteLine(failure.Message);
            return failure.Code;
        }
    }

    /// <summary>A path under the repository root.</summary>
    public static string At(params string[] parts) => Path.Combine([Root, .. parts]);

    public static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public static string Resolve(string file)
    {
        if (!OperatingSystem.IsWindows() || file.Contains('/') || file.Contains('\\') || Path.HasExtension(file)) return file;
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var extension in extensions)
                if (File.Exists(Path.Combine(directory, file + extension))) return Path.Combine(directory, file + extension);
        return file;
    }

    static ProcessStartInfo Start(string file, IEnumerable<string> args, string? directory)
    {
        var info = new ProcessStartInfo(Resolve(file)) { UseShellExecute = false };
        foreach (var argument in args) info.ArgumentList.Add(argument);
        if (directory is not null) info.WorkingDirectory = directory;
        return info;
    }

    /// <summary>Runs a program with the inherited console and returns its exit code.</summary>
    public static int Run(string file, IEnumerable<string> args, string? directory = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var info = Start(file, args, directory);
        foreach (var (key, value) in env ?? new Dictionary<string, string>()) info.Environment[key] = value;
        Process child;
        try { child = Process.Start(info) ?? throw new TaskFailure($"Cannot start {file}."); }
        catch (System.ComponentModel.Win32Exception exception) { throw new TaskFailure($"Cannot start {file}: {exception.Message}", 127); }
        using (child)
        {
            child.WaitForExit();
            return child.ExitCode;
        }
    }

    /// <summary>Runs a program and fails the task, with the program's exit code, when it fails.</summary>
    public static void Check(string file, IEnumerable<string> args, string? directory = null, IReadOnlyDictionary<string, string>? env = null)
    {
        var list = args.ToList();
        var code = Run(file, list, directory, env);
        if (code != 0) throw new TaskFailure($"{file} {string.Join(' ', list)} failed with exit code {code}", code);
    }

    /// <summary>Runs a program and returns its standard output; its standard error goes to the console, or nowhere when discarded.</summary>
    public static (int Code, string Output) Capture(string file, IEnumerable<string> args, string? directory = null, bool discardError = false)
    {
        var info = Start(file, args, directory);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = discardError;
        using var child = Process.Start(info) ?? throw new TaskFailure($"Cannot start {file}.");
        var error = discardError ? child.StandardError.ReadToEndAsync() : null;
        var output = child.StandardOutput.ReadToEnd();
        child.WaitForExit();
        error?.Wait();
        return (child.ExitCode, output);
    }

    /// <summary>Where mise keeps a pinned tool (installing it first when it is missing): scripts/tool, scripts\tool.cmd on Windows.</summary>
    public static string Tool(string name)
    {
        var (code, output) = OperatingSystem.IsWindows()
            ? Capture(At("scripts", "tool.cmd"), [name])
            : Capture("sh", [At("scripts", "tool"), name]);
        if (code != 0 || output.Trim().Length == 0) throw new TaskFailure($"scripts/tool {name} failed with exit code {code}", code == 0 ? 1 : code);
        return output.Trim();
    }

    /// <summary>The UTC date and short commit the About screen shows, as the nightly names its builds.</summary>
    public static string BuildStamp()
    {
        var (code, output) = Capture("git", ["rev-parse", "--short", "HEAD"]);
        if (code != 0) throw new TaskFailure("git rev-parse failed", code);
        return $"{DateTime.UtcNow:yyyyMMdd}-{output.Trim()}";
    }

    public static void DeleteAll(params string[] paths)
    {
        foreach (var path in paths)
        {
            if (Directory.Exists(path) && new DirectoryInfo(path).LinkTarget is null) Directory.Delete(path, true);
            else if (File.Exists(path) || new FileInfo(path).LinkTarget is not null) File.Delete(path);
        }
    }

    /// <summary>Copies a file, a directory tree or a symbolic link to the exact path <paramref name="destination"/>, keeping links and file modes (cp -R).</summary>
    public static void Copy(string source, string destination)
    {
        var info = new FileInfo(source);
        if (info.LinkTarget is { } target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            DeleteAll(destination);
            File.CreateSymbolicLink(destination, target);
        }
        else if (Directory.Exists(source))
        {
            Directory.CreateDirectory(destination);
            foreach (var entry in Directory.EnumerateFileSystemEntries(source)) Copy(entry, Path.Combine(destination, Path.GetFileName(entry)));
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, true);
        }
    }

    /// <summary>Copies <paramref name="source"/> into the directory <paramref name="directory"/> under its own name (cp -R source directory/).</summary>
    public static void CopyInto(string source, string directory) => Copy(source, Path.Combine(directory, Path.GetFileName(source.TrimEnd('/', '\\'))));

    public static void MakeExecutable(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
    }

    public static void WriteLf(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text.ReplaceLineEndings("\n"), new UTF8Encoding(false));
    }

    /// <summary>
    /// Takes a directory lock the way scripts/lock.sh does (mkdir, the owner's PID in a pid file): a lock whose owner is gone
    /// is removed instead of waited for, and a wait names the holder. Dispose releases it.
    /// </summary>
    public static IDisposable AcquireLock(string directory)
    {
        for (var waited = 0; ; waited++)
        {
            try
            {
                // The lock appears with its owner file already inside: the rename fails while another lock stands there.
                var attempt = $"{directory}.{Environment.ProcessId}";
                Directory.CreateDirectory(attempt);
                File.WriteAllText(Path.Combine(attempt, "pid"), Environment.ProcessId.ToString());
                try { Directory.Move(attempt, directory); }
                catch (IOException) { Directory.Delete(attempt, true); throw; }
                return new Lock(directory);
            }
            catch (IOException) when (Directory.Exists(directory))
            {
                var pidFile = Path.Combine(directory, "pid");
                var owner = File.Exists(pidFile) ? File.ReadAllText(pidFile).Trim() : "";
                if (owner.Length > 0)
                {
                    if (!Alive(owner))
                    {
                        Console.Error.WriteLine($"Removing the stale lock {directory}: its owner (PID {owner}) is no longer running.");
                        Directory.Delete(directory, true);
                        waited--;
                        continue;
                    }
                }
                else if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromMinutes(2))
                {
                    Console.Error.WriteLine($"Removing the stale lock {directory}: it names no owner and is older than two minutes.");
                    Directory.Delete(directory, true);
                    waited--;
                    continue;
                }
                if (waited > 0 && waited % 30 == 0)
                    Console.Error.WriteLine($"Waiting for the lock {directory} held by PID {(owner.Length > 0 ? owner : "unknown")} ({waited} s so far).");
                if (waited >= 3600)
                    throw new TaskFailure($"Gave up after an hour waiting for the lock {directory} held by PID {(owner.Length > 0 ? owner : "unknown")}; remove it if that build is hung.");
                Thread.Sleep(1000);
            }
        }
    }

    static bool Alive(string owner)
    {
        if (!int.TryParse(owner, out var pid)) return false;
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }

    sealed class Lock(string directory) : IDisposable
    {
        public void Dispose() { try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    /// <summary>
    /// Installs a .NET workload into the SDK once: the SDK is shared by every checkout (see mise.toml), so installs are
    /// serialised with a lock directory beside it. <paramref name="marker"/> is the pack directory that proves it installed.
    /// </summary>
    public static void EnsureWorkload(string workload, string marker, params string[] options)
    {
        var root = Tool("dotnet");
        bool Installed() => Directory.Exists(Path.Combine(root, "packs", marker))
            || Capture(Dotnet, ["workload", "list"], discardError: true).Output.Split('\n').Any(line => line.StartsWith(workload, StringComparison.Ordinal));
        if (Installed()) return;
        using var _ = AcquireLock(Path.Combine(root, ".workload.lock"));
        if (!Installed()) Check(Dotnet, ["workload", "install", workload, .. options]);
    }
}
