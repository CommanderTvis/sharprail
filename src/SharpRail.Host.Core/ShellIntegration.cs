namespace SharpRail.Host.Core;

/// <summary>
/// Loads SharpRail's own shell-integration scripts (<c>shell-integration/</c> next to the assemblies, from
/// <c>src/SharpRail.Host.Core/shell-integration</c>) into a shell the host starts: zsh through <c>ZDOTDIR</c>, bash in
/// POSIX mode through <c>ENV</c>. The scripts then mark prompts, input and command output (OSC 133) and report the
/// working directory (OSC 7). fish 4 marks its prompts itself and gets nothing; Apple's /bin/bash 3.2 ignores
/// <c>ENV</c> in that mode and is left alone.
/// </summary>
internal static class ShellIntegration
{
    /// <summary>Where the app ships the scripts, next to its assemblies; absent where the build has none.</summary>
    internal static string DefaultResources => Path.Combine(AppContext.BaseDirectory, "shell-integration");

    /// <summary>The prefix of the variables the injection sets; the scripts remove them, and the host drops inherited ones.</summary>
    internal const string VariablePrefix = "SHARPRAIL_SHELL_";

    /// <summary>
    /// The variables to set and the arguments to put before the login option, or null when the shell is not one SharpRail
    /// integrates or the scripts are missing. <paramref name="environment"/> is the shell's environment so far.
    /// </summary>
    internal static (Dictionary<string, string> Variables, string[] Arguments)? For(string shell, string resources,
        IReadOnlyDictionary<string, string> environment)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] arguments = [];
        switch (Path.GetFileName(shell))
        {
            case "zsh":
                var zsh = Path.Combine(resources, "zsh");
                if (!File.Exists(Path.Combine(zsh, ".zshenv"))) return null;
                // The script's .zshenv puts the user's ZDOTDIR back (or unsets it) before zsh reads any other file.
                if (environment.TryGetValue("ZDOTDIR", out var zdotdir)) variables[VariablePrefix + "ZDOTDIR"] = zdotdir;
                variables["ZDOTDIR"] = zsh;
                break;
            case "bash":
                var script = Path.Combine(resources, "bash", "sharprail.bash");
                if (OperatingSystem.IsMacOS() && shell == "/bin/bash" || !File.Exists(script)) return null;
                // POSIX mode reads ENV instead of the startup files; the script restores normal mode and sources them.
                arguments = ["--posix"];
                if (environment.TryGetValue("ENV", out var env)) variables[VariablePrefix + "BASH_ENV"] = env;
                variables["ENV"] = script;
                variables[VariablePrefix + "INJECT"] = "1";
                // POSIX mode's history file is ~/.sh_history; the script unexports this again.
                if (!environment.ContainsKey("HISTFILE"))
                {
                    variables["HISTFILE"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bash_history");
                    variables[VariablePrefix + "HISTFILE_UNEXPORT"] = "1";
                }
                break;
            default:
                return null;
        }
        return (variables, arguments);
    }
}