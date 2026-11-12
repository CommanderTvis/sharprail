using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Tereditor.Checks;

/// <summary>
/// Running a script with plain `dotnet run` in a directory with a project passes the script to the project and recurses into its build; a tracked file
/// must say `dotnet run --file script.cs` (docs/develop/building.md).
/// </summary>
internal static partial class RunFileChecks
{
    [GeneratedRegex(@"dotnet run (?!--file)(?:-\S+ \S+ )*[^\s`""']+\.cs\b")]
    private static partial Regex Bad();

    internal static void Run()
    {
        if (BoundaryChecks.Checkout() is not { } root) { Console.WriteLine("SKIP run-file: no source checkout"); return; }
        var start = new ProcessStartInfo("git", ["-C", root, "ls-files", "-z"]) { RedirectStandardOutput = true };
        using var git = Process.Start(start)!;
        var files = git.StandardOutput.ReadToEnd().Split('\0', StringSplitOptions.RemoveEmptyEntries);
        git.WaitForExit();
        if (git.ExitCode != 0) { Console.WriteLine("SKIP run-file: not a git checkout"); return; }
        var found = new List<string>();
        foreach (var file in files)
        {
            var path = Path.Combine(root, file);
            if (!File.Exists(path) || new FileInfo(path).Length > 2_000_000) continue;
            string text;
            try { text = File.ReadAllText(path); } catch (IOException) { continue; }
            if (text.Contains('\0')) continue;
            foreach (Match match in Bad().Matches(text)) found.Add(file + ": " + match.Value);
        }
        if (found.Count > 0) throw new InvalidOperationException("Use `dotnet run --file <script>.cs`:\n" + string.Join('\n', found));
        Console.WriteLine("PASS run-file: no tracked file runs a .cs script without --file");
    }
}