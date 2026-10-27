namespace Ghostty.Avalonia;

/// <summary>Keyboard behaviour shared by every terminal view, set from the host's settings.</summary>
public static class TerminalKeyboard
{
    /// <summary>Whether the macOS Option key acts as Alt (an ESC prefix) rather than composing characters, like Ghostty's macos-option-as-alt. Off by default.</summary>
    public static volatile bool OptionAsAlt;
}