using System.Diagnostics;
using System.Text;

namespace SharpRail.Host.Core;

/// <summary>Shows an entry in the desktop file manager of the machine the host runs on.</summary>
public static class FileManagerReveal
{
    /// <summary>One process to try: the executable and its arguments.</summary>
    public sealed record Command(string File, IReadOnlyList<string> Arguments);

    /// <summary>The menu label for the platform, named after its own file manager.</summary>
    public static string Label(bool macOS, bool windows) => macOS ? "Reveal in Finder" : windows ? "Show in Explorer" : "Show in file manager";

    /// <summary>A <c>file://</c> URI with every byte outside the unreserved set percent-encoded (spaces, non-ASCII, <c>#</c>, <c>%</c>, <c>?</c>).</summary>
    public static string FileUri(string path) =>
        "file://" + string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    /// <summary>
    /// The Linux commands to try in order: the freedesktop.org FileManager1 service (selects the entry in whatever file manager runs),
    /// then the containing folder through <c>xdg-open</c> or <c>gio open</c>. <paramref name="available"/> says whether an executable exists.
    /// </summary>
    public static IReadOnlyList<Command> LinuxPlan(string path, Func<string, bool> available)
    {
        if (!path.StartsWith('/')) throw new ArgumentException("A file manager needs an absolute path.", nameof(path));
        var uri = FileUri(path);
        var parent = Path.GetDirectoryName(path.TrimEnd('/')) is { Length: > 0 } directory ? directory : "/";
        var plan = new List<Command>();
        if (available("gdbus"))
            plan.Add(new("gdbus", ["call", "--session", "--dest", "org.freedesktop.FileManager1", "--object-path", "/org/freedesktop/FileManager1",
                "--method", "org.freedesktop.FileManager1.ShowItems", $"['{uri}']", ""]));
        if (available("busctl"))
            plan.Add(new("busctl", ["--user", "call", "org.freedesktop.FileManager1", "/org/freedesktop/FileManager1", "org.freedesktop.FileManager1",
                "ShowItems", "ass", "1", uri, ""]));
        if (available("xdg-open")) plan.Add(new("xdg-open", [parent]));
        if (available("gio")) plan.Add(new("gio", ["open", parent]));
        return plan;
    }

    /// <summary>The environment an AppImage's launcher adds, removed so a system file manager loads its own libraries: entries under <c>APPDIR</c> are dropped from path lists.</summary>
    public static IReadOnlyDictionary<string, string?> CleanEnvironment(IReadOnlyDictionary<string, string?> environment)
    {
        var result = new Dictionary<string, string?>();
        var appDir = environment.GetValueOrDefault("APPDIR");
        if (string.IsNullOrEmpty(appDir)) return result;
        foreach (var name in new[] { "LD_LIBRARY_PATH", "GIO_EXTRA_MODULES", "GIO_MODULE_DIR", "PATH", "XDG_DATA_DIRS", "GST_PLUGIN_PATH", "GTK_PATH", "PYTHONPATH", "QT_PLUGIN_PATH" })
        {
            if (environment.GetValueOrDefault(name) is not { } value) continue;
            var kept = value.Split(':').Where(entry => !entry.StartsWith(appDir, StringComparison.Ordinal)).ToArray();
            result[name] = kept.Length == 0 || kept.All(string.IsNullOrEmpty) ? null : string.Join(':', kept);
        }
        foreach (var name in new[] { "APPDIR", "APPIMAGE", "ARGV0", "OWD" }) if (environment.ContainsKey(name)) result[name] = null;
        return result;
    }

    /// <summary>Shows the entry on this machine; throws a message naming what is missing or what the file manager reported.</summary>
    public static void Reveal(string path)
    {
        if (!OperatingSystem.IsLinux())
        {
            var native = OperatingSystem.IsMacOS() ? new ProcessStartInfo("open") { ArgumentList = { "-R", path } } : new ProcessStartInfo("explorer", "/select," + path);
            native.UseShellExecute = false;
            using var launched = Process.Start(native) ?? throw new IOException("No file manager is available on this computer.");
            return;
        }
        var plan = LinuxPlan(Path.GetFullPath(path), name => Locate(name) is not null);
        if (!plan.Any(command => command.File is "xdg-open" or "gio" or "gdbus" or "busctl"))
            throw new IOException("No file manager launcher was found: install xdg-utils (xdg-open) or GLib's gio.");
        var failures = new List<string>();
        foreach (var command in plan)
        {
            try { Run(command, clean: true); return; }
            catch (IOException error) { failures.Add(command.File + ": " + error.Message); }
        }
        throw new IOException("The file manager could not be opened. " + string.Join(" ", failures));
    }

    /// <summary>Opens a folder itself; on Linux with the AppImage environment removed and a failure reported.</summary>
    public static void OpenFolder(string path, string opener)
    {
        if (OperatingSystem.IsLinux()) { Run(new(opener, [path]), clean: true); return; }
        var start = new ProcessStartInfo(opener) { UseShellExecute = false, ArgumentList = { path } };
        using var process = Process.Start(start) ?? throw new IOException("Failed to launch the file manager.");
    }

    private static void Run(Command command, bool clean)
    {
        var start = new ProcessStartInfo(command.File) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var argument in command.Arguments) start.ArgumentList.Add(argument);
        if (clean)
        {
            var snapshot = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(entry => (string)entry.Key, entry => (string?)entry.Value);
            foreach (var (name, value) in CleanEnvironment(snapshot))
                if (value is null) start.Environment.Remove(name); else start.Environment[name] = value;
        }
        using var process = Process.Start(start) ?? throw new IOException($"{command.File} could not be started.");
        var error = new StringBuilder();
        process.ErrorDataReceived += (_, data) => { if (data.Data is not null) error.AppendLine(data.Data); };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        // A launcher that keeps running after handing over to the file manager is a success.
        if (!process.WaitForExit(10_000)) return;
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException($"{command.File} exited with {process.ExitCode}. {error.ToString().Trim()}".Trim());
    }

    private static string? Locate(string name)
    {
        if (OperatingSystem.IsWindows()) return null;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate) && (File.GetUnixFileMode(candidate) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                return candidate;
        }
        return null;
    }
}