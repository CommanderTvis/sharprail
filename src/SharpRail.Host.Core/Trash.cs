using System.Globalization;

namespace SharpRail.Host.Core;

/// <summary>
/// The only way the host removes a user's file: a move into the operating system's trash, never a delete.
/// <c>SHARPRAIL_TRASH_DIR</c> overrides the destination, which lets a headless host (or a check) choose one.
/// </summary>
internal static class Trash
{
    internal const string OverrideVariable = "SHARPRAIL_TRASH_DIR";

    /// <summary>Moves one file, named <paramref name="name"/> in the trash, and returns where it went; a failure leaves the file where it was.</summary>
    internal static string Move(string path, string name)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string directory, infoDirectory = "";
        if (Environment.GetEnvironmentVariable(OverrideVariable) is { Length: > 0 } custom) directory = custom;
        else if (OperatingSystem.IsMacOS()) directory = Path.Combine(home, ".Trash");
        else if (OperatingSystem.IsLinux())
        {
            var data = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(home, ".local", "share");
            directory = Path.Combine(data, "Trash", "files");
            infoDirectory = Path.Combine(data, "Trash", "info");
        }
        else throw new PlatformNotSupportedException("This host has no system trash.");
        Directory.CreateDirectory(directory);
        // The path is a claim named after a revert id; the file's own name is what a person looks for in the trash.
        var target = Path.Combine(directory, name);
        for (var attempt = 2; File.Exists(target) || Directory.Exists(target); attempt++)
            target = Path.Combine(directory, $"{name} {attempt.ToString(CultureInfo.InvariantCulture)}");
        File.Move(path, target);
        if (infoDirectory.Length > 0)
        {
            Directory.CreateDirectory(infoDirectory);
            File.WriteAllText(Path.Combine(infoDirectory, Path.GetFileName(target) + ".trashinfo"),
                $"[Trash Info]\nPath={Uri.EscapeDataString(path)}\nDeletionDate={DateTime.Now:yyyy-MM-ddTHH:mm:ss}\n");
        }
        return target;
    }
}