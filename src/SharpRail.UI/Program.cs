using Avalonia;

namespace SharpRail.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        StartupPhases.Mark("managed-entry");
        if (args is [Terminal.TerminalRelay.Argument]) Environment.Exit(Terminal.TerminalRelay.Run());
        Console.WriteLine($"SHARPRAIL_MAIN {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        var builder = AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software] })
            .LogToTrace();
        // Avalonia's X11 backend ignores the extended client area unless it may draw the window frame itself.
        // The option is marked experimental in Avalonia 12.1; the desktop allowlist and SHARPRAIL_WINDOW_DECORATIONS=system contain that.
#pragma warning disable AVALONIA_X11_CSD
        if (LinuxWindowChrome.Client) builder.With(new X11PlatformOptions { EnableDrawnDecorations = true });
#pragma warning restore AVALONIA_X11_CSD
        // The profile and embedded host need the asset loader, but none of native windowing or Skia.
        // Start them as soon as runtime platform services exist, overlapping the remaining UI bootstrap.
        if (!args.Contains("--tailscale", StringComparer.Ordinal) && builder.RuntimePlatformServicesInitializer is { } platform)
            builder.UseRuntimePlatformSubsystem(() =>
            {
                platform();
                App.PrepareStartup();
            }, builder.RuntimePlatformServicesName ?? "");
        StartupPhases.Instrument(builder);
        builder.StartWithClassicDesktopLifetime(args);
    }
}