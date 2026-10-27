namespace SharpRail.Plugins.Codex.UI;

/// <summary>How the terminal accessory types a slash command into Codex: what it writes, how long it waits, and a hold on the user's keys.</summary>
public interface ICodexTerminalIo
{
    void Write(string data);
    Task DelayAsync(TimeSpan delay);
    /// <summary>Holds the user's keys out of the terminal until disposed, so a typed command is not interleaved with them.</summary>
    IDisposable HoldInput();
}

/// <summary>Submits slash commands to Codex. It never drives a picker: the user chooses inside Codex's own UI.</summary>
public static class CodexTerminalCommands
{
    /// <summary>Writes the command, waits for Codex's paste-burst handling to settle, then writes Enter separately.</summary>
    public static async Task SubmitAsync(ICodexTerminalIo io, string command)
    {
        using var hold = io.HoldInput();
        io.Write(command);
        await io.DelayAsync(TimeSpan.FromMilliseconds(250));
        io.Write("\r");
    }
}