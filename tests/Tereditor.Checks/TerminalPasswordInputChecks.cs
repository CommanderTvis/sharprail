using SharpRail.Host.Abstractions;

namespace SharpRail.Checks;

// The host reads a password prompt from the PTY's line discipline (canonical, echo off), as Ghostty's termios poll does.
internal static partial class TerminalHostChecks
{
    private static async Task PasswordInput(ITerminalService terminals, string workspace, string mode)
    {
        var id = mode + "-password-" + Guid.NewGuid().ToString("N");
        var session = await terminals.AttachAsync(new(id, workspace, "client", 100, 30));
        var screen = new Screen(session);
        try
        {
            await screen.Run("printf 'PW_%s\\n' READY");
            await screen.WaitFor("PW_READY", mode);
            Require(!await terminals.IsPasswordInputAsync(id), $"{mode}: a shell at its prompt reported a password prompt.");
            // What sudo, ssh and gpg do: echo off, then a canonical read.
            await screen.Run("sh -c 'stty -echo; printf PROMPT_%s: ASK; read secret; stty echo; printf \"\\nGOT_%s\\n\" \"$secret\"'");
            await screen.WaitFor("PROMPT_ASK:", mode);
            await Until(async () => await terminals.IsPasswordInputAsync(id), mode + " password prompt detected");
            await session.WriteAsync("hunter2\r"u8.ToArray());
            await screen.WaitFor("GOT_hunter2", mode);
            await Until(async () => !await terminals.IsPasswordInputAsync(id), mode + " echo restored");
            Require(!await terminals.IsPasswordInputAsync("absent-session"), $"{mode}: an unknown session reported a password prompt.");
            await screen.Run("sh -c 'stty -echo; read secret'");
            await Until(async () => await terminals.IsPasswordInputAsync(id), mode + " second prompt detected");
        }
        finally { await terminals.CloseAsync(id); }
        Require(!await terminals.IsPasswordInputAsync(id), $"{mode}: a closed session still reported a password prompt.");
        Console.WriteLine($"PASS {mode} password prompt detection: echo-off canonical read reported, prompt and restored echo not, closed session cleared");
    }
}