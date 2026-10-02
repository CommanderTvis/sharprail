using System.Diagnostics;

using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks.E2E;

/// <summary>
/// Gives fixture repositories, and the git processes the app starts, a private global
/// configuration with an identity and no commit signing, restoring the environment on dispose.
/// </summary>
internal sealed class IsolatedGit : IDisposable
{
    private static readonly string[] Names = ["GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM"];
    private readonly string?[] previous = Names.Select(Environment.GetEnvironmentVariable).ToArray();

    internal IsolatedGit(string directory) => Isolate(directory);

    /// <summary>
    /// Isolates every git process this checks run starts, the app's and the fixtures' alike, so no fixture commit ever
    /// reads the developer's global or system configuration: no signing prompt, no identity, no hooks or aliases from
    /// it. Scoped instances restore to this, never to the real configuration.
    /// </summary>
    internal static void ForProcess() => Isolate(Path.Combine(Path.GetTempPath(), "sharprail-checks-git", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)));

    /// <summary>A fixture commit sees signing off and the private identity, never the developer's configuration.</summary>
    internal static void CheckIsolation()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var global = Environment.GetEnvironmentVariable("GIT_CONFIG_GLOBAL") ?? "";
        Require(global.Length > 0 && !global.StartsWith(home + Path.DirectorySeparatorChar + ".", StringComparison.Ordinal) &&
            Environment.GetEnvironmentVariable("GIT_CONFIG_NOSYSTEM") == "1", "Checks must not read the developer's git configuration.");
        var repository = Repository(Path.Combine(Path.GetDirectoryName(global)!, "isolation-" + Guid.NewGuid().ToString("N")[..8]));
        Require(Run(repository, "config", "--get", "commit.gpgsign").Trim() == "false" &&
            Run(repository, "log", "-1", "--format=%an %G?").Trim() == "SharpRail Checks N",
            "A fixture commit is unsigned and made by the checks identity.");
        Directory.Delete(repository, true);
    }

    private static void Isolate(string directory)
    {
        Directory.CreateDirectory(directory);
        var config = Path.Combine(directory, "gitconfig");
        File.WriteAllText(config, "[user]\n\tname = SharpRail Checks\n\temail = checks@sharprail.invalid\n[commit]\n\tgpgsign = false\n[tag]\n\tgpgsign = false\n[init]\n\tdefaultBranch = main\n");
        Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
        Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
    }

    public void Dispose()
    {
        for (var index = 0; index < Names.Length; index++) Environment.SetEnvironmentVariable(Names[index], previous[index]);
    }

    internal static string Repository(string path, params (string Name, string Text)[] files)
    {
        Directory.CreateDirectory(path);
        Run(path, "init", "-b", "main");
        File.WriteAllText(Path.Combine(path, "README.md"), "# " + Path.GetFileName(path) + "\n");
        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(path, name), text);
        Run(path, "add", "-A");
        Run(path, "commit", "-m", "seed");
        return path;
    }

    internal static string Run(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        Require(process.ExitCode == 0, error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }
}