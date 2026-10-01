using Avalonia.Input;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>Platform and keyboard-layout matching for window commands and dialogs.</summary>
public static class CommandKeys
{
    private static readonly KeyModifiers Command = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;

    /// <summary>Matches a command letter by typed Latin symbol, with a physical-key fallback for other layouts.</summary>
    public static bool Matches(KeyEventArgs e, char letter)
    {
        if (e.KeyModifiers != Command) return false;
        if (e.KeySymbol is { Length: 1 } symbol && symbol[0] is >= 'A' and <= 'Z' or >= 'a' and <= 'z')
            return char.ToUpperInvariant(symbol[0]) == letter;
        if (e.Key is >= Key.A and <= Key.Z) return e.Key - Key.A + 'A' == letter;
        return e.PhysicalKey == (letter == 'Q' ? PhysicalKey.Q : PhysicalKey.W);
    }

    /// <summary>The close chord: Command-W, or Ctrl+W and Ctrl+F4 off macOS.</summary>
    public static bool IsClose(KeyEventArgs e) =>
        Matches(e, 'W') || (!OperatingSystem.IsMacOS() && e.Key == Key.F4 && e.KeyModifiers == KeyModifiers.Control);

}