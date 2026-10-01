using Avalonia;

namespace SharpRail.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (args is [Terminal.TerminalRelay.Argument]) Environment.Exit(Terminal.TerminalRelay.Run());
        Console.WriteLine($"SHARPRAIL_MAIN {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        AppBuilder.Configure<App>().UsePlatformDetect()
            .With(new AvaloniaNativePlatformOptions { RenderingMode = [AvaloniaNativeRenderingMode.Metal, AvaloniaNativeRenderingMode.OpenGl, AvaloniaNativeRenderingMode.Software] })
            .LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}