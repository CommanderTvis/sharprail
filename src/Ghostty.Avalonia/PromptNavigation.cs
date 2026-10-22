using Avalonia.Input;

namespace Ghostty.Avalonia;

/// <summary>
/// Ghostty's default <c>jump_to_prompt</c> bindings: Command+Up/Down on macOS (as Terminal.app), Ctrl+Shift+Up/Down
/// elsewhere. They need shell integration's prompt marks (OSC 133).
/// </summary>
internal static class PromptNavigation
{
    /// <summary>-1 for the previous prompt, 1 for the next, 0 when the key is not a jump.</summary>
    internal static int Delta(KeyEventArgs e, bool mac)
    {
        var jump = mac ? KeyModifiers.Meta : KeyModifiers.Control | KeyModifiers.Shift;
        if (e.KeyModifiers != jump) return 0;
        return e.PhysicalKey switch { PhysicalKey.ArrowUp => -1, PhysicalKey.ArrowDown => 1, _ => 0 };
    }
}