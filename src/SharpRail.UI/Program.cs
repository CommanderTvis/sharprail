using Avalonia;

namespace SharpRail.UI;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        Console.WriteLine($"SHARPRAIL_MAIN {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
        AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace().StartWithClassicDesktopLifetime(args);
    }
}
