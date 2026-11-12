using System.Collections.Generic;

namespace SharpRail.Host.Core;

/// <summary>
/// Lets the environment plugins run a script inside an interactive shell after the user's own start-up files, so a
/// shell keeps mise's and direnv's environment in sync as it changes directory. It needs no edit to the user's
/// dotfiles and no change to how the shell is started: zsh is pointed at shim start-up files through <c>ZDOTDIR</c> that
/// source the user's own, and fish reads a vendor snippet through <c>XDG_DATA_DIRS</c>. Other shells get the resolved
/// environment at start only.
/// </summary>
internal static class ShellEnvironmentHooks
{
    /// <summary>The variable naming the shim directory; the shell never sees it.</summary>
    internal const string DirectoryVariable = "SHARPRAIL_SHELL_INTEGRATION";

    /// <summary>The prefix of the variables carrying a shell's script, followed by the upper-case shell name.</summary>
    internal const string InitPrefix = "SHARPRAIL_SHELL_INIT_";

    private const string UserDirectory = "SHARPRAIL_USER_ZDOTDIR";

    // Each file runs the user's counterpart as if ZDOTDIR were untouched, then hands ZDOTDIR back.
    private static string Pass(string file) => $$"""
        # SharpRail shell integration: runs the user's {{file}} with ZDOTDIR as the user has it.
        if [[ -n "$SHARPRAIL_USER_ZDOTDIR" ]]; then
          SHARPRAIL_SHIM_ZDOTDIR="$ZDOTDIR"
          ZDOTDIR="$SHARPRAIL_USER_ZDOTDIR"
          [[ -f "$ZDOTDIR/{{file}}" ]] && . "$ZDOTDIR/{{file}}"
          SHARPRAIL_USER_ZDOTDIR="$ZDOTDIR"
          ZDOTDIR="$SHARPRAIL_SHIM_ZDOTDIR"
        fi

        """;

    private static readonly string Zshrc = $$"""
        # SharpRail shell integration: runs the user's .zshrc, then the plugins' script, then gives ZDOTDIR back.
        if [[ -n "$SHARPRAIL_USER_ZDOTDIR" ]]; then
          ZDOTDIR="$SHARPRAIL_USER_ZDOTDIR"
          [[ -f "$ZDOTDIR/.zshrc" ]] && . "$ZDOTDIR/.zshrc"
        fi
        [[ -n "$SHARPRAIL_SHELL_INIT_ZSH" ]] && eval "$SHARPRAIL_SHELL_INIT_ZSH"
        [[ "$ZDOTDIR" == "$HOME" ]] && unset ZDOTDIR
        unset SHARPRAIL_SHIM_ZDOTDIR SHARPRAIL_USER_ZDOTDIR SHARPRAIL_SHELL_INIT_ZSH

        """;

    private const string Fish = """
        # SharpRail shell integration: the plugins' script, before the user's config.fish.
        if set -q SHARPRAIL_SHELL_INIT_FISH
            echo "$SHARPRAIL_SHELL_INIT_FISH" | source
            set -e SHARPRAIL_SHELL_INIT_FISH
        end

        """;

    /// <summary>Writes the shim files under <paramref name="root"/> when they are missing or stale, and returns the directory.</summary>
    public static string Ensure(string root)
    {
        Write(Path.Combine(root, "zsh", ".zshenv"), Pass(".zshenv"));
        Write(Path.Combine(root, "zsh", ".zprofile"), Pass(".zprofile"));
        Write(Path.Combine(root, "zsh", ".zshrc"), Zshrc);
        Write(Path.Combine(root, "fish", "vendor_conf.d", "sharprail.fish"), Fish);
        return root;
    }

    private static void Write(string file, string content)
    {
        if (File.Exists(file) && File.ReadAllText(file) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, content);
    }

    /// <summary>Points the shell at its shim when the environment carries a script for it; always removes the plumbing variable.</summary>
    internal static void Apply(IDictionary<string, string> variables, string shell)
    {
        if (!variables.Remove(DirectoryVariable, out var root) || root.Length == 0) return;
        var name = Path.GetFileNameWithoutExtension(shell);
        if (!variables.TryGetValue(InitPrefix + name.ToUpperInvariant(), out var script) || script.Length == 0) return;
        switch (name)
        {
            case "zsh":
                variables[UserDirectory] = variables.TryGetValue("ZDOTDIR", out var user) && user.Length > 0 ? user : variables.TryGetValue("HOME", out var home) ? home : "";
                variables["ZDOTDIR"] = Path.Combine(root, "zsh");
                break;
            case "fish":
                variables["XDG_DATA_DIRS"] = root + ":" + (variables.TryGetValue("XDG_DATA_DIRS", out var data) && data.Length > 0 ? data : "/usr/local/share:/usr/share");
                break;
        }
    }
}