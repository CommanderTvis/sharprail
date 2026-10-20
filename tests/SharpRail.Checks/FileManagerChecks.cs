using SharpRail.Host.Core;

namespace SharpRail.Checks;

/// <summary>The Linux file-manager reveal: URI building, command selection, AppImage environment cleaning and per-platform wording.</summary>
internal static class FileManagerChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("File manager: " + message); }

    internal static void Run()
    {
        (string Path, string Uri)[] uris =
        [
            ("/home/a/plain.txt", "file:///home/a/plain.txt"),
            ("/home/a/my file.txt", "file:///home/a/my%20file.txt"),
            ("/home/a/éλ/日.txt", "file:///home/a/%C3%A9%CE%BB/%E6%97%A5.txt"),
            ("/home/a/c#/50%.txt", "file:///home/a/c%23/50%25.txt"),
            ("/home/a/what?.txt", "file:///home/a/what%3F.txt"),
        ];
        foreach (var (path, uri) in uris) Require(FileManagerReveal.FileUri(path) == uri, $"{path} must encode as {uri}, not {FileManagerReveal.FileUri(path)}.");

        static string Plan(string path, params string[] tools) =>
            string.Join(" | ", FileManagerReveal.LinuxPlan(path, tools.Contains).Select(command => command.File + " " + string.Join(' ', command.Arguments)));
        var dbus = Plan("/h/my file.txt", "gdbus", "xdg-open", "gio");
        Require(dbus == "gdbus call --session --dest org.freedesktop.FileManager1 --object-path /org/freedesktop/FileManager1 --method org.freedesktop.FileManager1.ShowItems ['file:///h/my%20file.txt']  " +
            "| xdg-open /h | gio open /h", "D-Bus comes first, then xdg-open and gio on the parent folder: " + dbus);
        Require(Plan("/h/dir/", "xdg-open") == "xdg-open /h", "A directory reveals in its parent; only xdg-open available gives a single command.");
        Require(Plan("/top", "xdg-open") == "xdg-open /", "An entry at the root has the root as parent.");
        Require(Plan("/h/f", "busctl") == "busctl --user call org.freedesktop.FileManager1 /org/freedesktop/FileManager1 org.freedesktop.FileManager1 ShowItems ass 1 file:///h/f ",
            "busctl passes the array as a count and the URI.");
        Require(FileManagerReveal.LinuxPlan("/h/f", _ => false).Count == 0, "No launcher yields an empty plan, which the caller reports.");
        try { FileManagerReveal.LinuxPlan("rel/f", _ => true); Require(false, "A relative path must be refused."); } catch (ArgumentException) { }

        var clean = FileManagerReveal.CleanEnvironment(new Dictionary<string, string?>
        {
            ["APPDIR"] = "/tmp/.mount_x",
            ["APPIMAGE"] = "/a.AppImage",
            ["LD_LIBRARY_PATH"] = "/tmp/.mount_x/usr/lib:/opt/lib",
            ["GIO_EXTRA_MODULES"] = "/tmp/.mount_x/usr/lib/gio",
            ["PATH"] = "/tmp/.mount_x/usr/bin:/usr/bin",
        });
        Require(clean["LD_LIBRARY_PATH"] == "/opt/lib" && clean["GIO_EXTRA_MODULES"] is null && clean["PATH"] == "/usr/bin" && clean["APPDIR"] is null && clean["APPIMAGE"] is null,
            "AppImage entries are removed from path lists and its markers unset.");
        Require(FileManagerReveal.CleanEnvironment(new Dictionary<string, string?> { ["LD_LIBRARY_PATH"] = "/x" }).Count == 0, "Outside an AppImage the environment is untouched.");

        Require(FileManagerReveal.Label(true, false) == "Reveal in Finder" && FileManagerReveal.Label(false, true) == "Show in Explorer"
            && FileManagerReveal.Label(false, false) == "Show in file manager", "Each platform's menu names its own file manager.");
        Console.WriteLine("PASS file manager reveal: URIs, command plan, environment and labels");
    }
}