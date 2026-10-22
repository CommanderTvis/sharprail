namespace SharpRail.Host.Core;

/// <summary>
/// Loads Ghostty's own shell-integration scripts into a shell the host starts, the way Ghostty's
/// <c>src/termio/shell_integration.zig</c> does at the pinned libghostty-vt commit: zsh through <c>ZDOTDIR</c>, bash in
/// POSIX mode through <c>ENV</c>, fish and elvish through <c>XDG_DATA_DIRS</c>. The scripts then mark prompts, input and
/// command output (OSC 133) and report the working directory (OSC 7). Apple's /bin/bash 3.2 ignores <c>ENV</c> and is
/// left alone, as Ghostty leaves it.
/// </summary>
internal static class ShellIntegration
{
    /// <summary>Where the app ships the scripts, next to its assemblies; absent where the build has none.</summary>
    internal static string DefaultResources => Path.Combine(AppContext.BaseDirectory, "ghostty-shell");

    // Ghostty's default features: a bar cursor at the prompt and the prompt's title. Its `path` feature adds Ghostty's
    // own binary directory, which SharpRail does not have, and sudo and the ssh features are off by default.
    internal const string Features = "cursor:blink,title";

    /// <summary>
    /// The variables to set and the arguments to put before the login option, or null when the shell is not one Ghostty
    /// integrates or the scripts are missing. <paramref name="environment"/> is the shell's environment so far.
    /// </summary>
    internal static (Dictionary<string, string> Variables, string[] Arguments)? For(string shell, string resources,
        IReadOnlyDictionary<string, string> environment)
    {
        var integration = Path.Combine(resources, "shell-integration");
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        string[] arguments = [];
        switch (Path.GetFileName(shell))
        {
            case "zsh":
                var zsh = Path.Combine(integration, "zsh");
                if (!File.Exists(Path.Combine(zsh, ".zshenv"))) return null;
                if (environment.TryGetValue("ZDOTDIR", out var zdotdir)) variables["GHOSTTY_ZSH_ZDOTDIR"] = zdotdir;
                variables["ZDOTDIR"] = zsh;
                break;
            case "bash":
                var script = Path.Combine(integration, "bash", "ghostty.bash");
                if (OperatingSystem.IsMacOS() && shell == "/bin/bash" || !File.Exists(script)) return null;
                // POSIX mode reads ENV instead of the startup files; the script restores normal mode and sources them.
                arguments = ["--posix"];
                if (environment.TryGetValue("ENV", out var env)) variables["GHOSTTY_BASH_ENV"] = env;
                variables["ENV"] = script;
                variables["GHOSTTY_BASH_INJECT"] = "1";
                // POSIX mode's history file is ~/.sh_history; the script unexports this again.
                if (!environment.ContainsKey("HISTFILE"))
                {
                    variables["HISTFILE"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".bash_history");
                    variables["GHOSTTY_BASH_UNEXPORT_HISTFILE"] = "1";
                }
                break;
            case "fish" or "elvish":
                if (!Directory.Exists(integration)) return null;
                // The script removes its directory from XDG_DATA_DIRS again once it has loaded.
                variables["GHOSTTY_SHELL_INTEGRATION_XDG_DIR"] = integration;
                variables["XDG_DATA_DIRS"] = integration + ":" +
                    (environment.GetValueOrDefault("XDG_DATA_DIRS") is { Length: > 0 } dirs ? dirs : "/usr/local/share:/usr/share");
                break;
            default:
                return null;
        }
        variables["GHOSTTY_RESOURCES_DIR"] = resources;
        variables["GHOSTTY_SHELL_FEATURES"] = Features;
        return (variables, arguments);
    }
}