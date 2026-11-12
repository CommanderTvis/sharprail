using Tereditor.Plugins.Isolation;

namespace Tereditor.Plugins.Runner;

/// <summary>
/// The plugin runner: <c>Tereditor.PluginRunner plugin|probe echo|avalonia-private|ui-context [--channel &lt;handle&gt;]</c>. The host starts it
/// with its channel at descriptor 3 (Unix) or the handle <c>--channel</c> names (Windows); it exits when the channel ends.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? channel = null;
        var words = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--channel" && index + 1 < args.Length) channel = args[++index];
            else words.Add(args[index]);
        }
        switch (words)
        {
            case [RunnerPlugin.Mode]:
                return PluginMode.Run(channel);
            case ["probe", RunnerProbe.EchoMode]:
                return EchoProbe.RunAsync(channel).GetAwaiter().GetResult();
            case ["probe", RunnerProbe.AvaloniaPrivateMode]:
                return AvaloniaPrivateProbe.Run();
            case ["probe", RunnerProbe.UIContextMode]:
                return UIContextProbe.RunAsync(channel).GetAwaiter().GetResult();
            default:
                Console.Error.WriteLine("Usage: Tereditor.PluginRunner plugin|probe echo|avalonia-private|ui-context");
                return 2;
        }
    }
}