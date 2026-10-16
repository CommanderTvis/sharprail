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
        StartupPhases.Instrument(builder);
        builder.StartWithClassicDesktopLifetime(args);
    }
}