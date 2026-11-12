using Avalonia;
using Avalonia.Input;

namespace SharpRail.Checks;

/// <summary>Gives the headless platform the host OS's command modifier (Meta on macOS), as the native platforms report it.</summary>
internal static class HostHotkeys
{
    internal static void Apply(AppBuilder _)
    {
        if (OperatingSystem.IsMacOS() && Application.Current?.PlatformSettings is { } settings)
            settings.HotkeyConfiguration.CommandModifiers = KeyModifiers.Meta;
    }
}