namespace SharpRail.Host.Core;

/// <summary>
/// The script a POSIX terminal runs when it starts with a program instead of a prompt. The user's own shell runs it
/// as an interactive login shell (<c>$SHELL -l -i -c</c>), so PATH and every rc file apply exactly as at a prompt and
/// the program line keeps that shell's syntax. When the program ends, a non-zero status is printed and the shell is
/// replaced by an interactive, non-login one in the same directory: the environment the login shell built is
/// inherited, and the profile is not run a second time (macOS's <c>path_helper</c> would reorder PATH again).
/// </summary>
internal static class TerminalProgram
{
    public static string Script(string shell, string program)
    {
        var self = "'" + shell.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        // fish has no $? and a different conditional; every other shell SharpRail meets reads POSIX syntax.
        var status = Path.GetFileName(shell) == "fish"
            ? "set -l sharprail_status $status\ntest $sharprail_status -eq 0; or printf '\\r\\n[exited with status %s]\\r\\n' $sharprail_status"
            : "sharprail_status=$?\n[ \"$sharprail_status\" -eq 0 ] || printf '\\r\\n[exited with status %s]\\r\\n' \"$sharprail_status\"";
        return program + "\n" + status + "\nexec " + self + " -i\n";
    }
}