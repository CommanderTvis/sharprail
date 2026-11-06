using SharpRail.Plugins.Isolation;

namespace SharpRail.Plugins.Runner;

/// <summary>
/// The plugin runner: <c>SharpRail.PluginRunner plugin|probe echo|avalonia-private|ui-context [--channel &lt;handle&gt;]</c>. The host starts it
/// with its channel at descriptor 3 (Unix) or the handle <c>--channel</c> names (Windows); it exits when the channel ends.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
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
                return await PluginMode.RunAsync(channel);
            case ["probe", RunnerProbe.EchoMode]:
                return await EchoProbe.RunAsync(channel);
            case ["probe", RunnerProbe.AvaloniaPrivateMode]:
                return AvaloniaPrivateProbe.Run();
            case ["probe", RunnerProbe.UIContextMode]:
                return await UIContextProbe.RunAsync(channel);
            default:
                await Console.Error.WriteLineAsync("Usage: SharpRail.PluginRunner plugin|probe echo|avalonia-private|ui-context");
                return 2;
        }
    }
}