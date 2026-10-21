using System.Reflection;
using System.Text;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks;

// The desktop app's local terminals go through a wrapper; any interface member it leaves to the interface's
// default (an empty or throwing fallback) silently breaks that feature locally while remote hosts work.
internal static class LoopbackTerminalChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Loopback terminals: " + message); }

    internal static async Task Run()
    {
        foreach (var wrapper in new[] { typeof(LoopbackTerminals), typeof(LocalTerminalAdapter) })
        {
            var map = wrapper.GetInterfaceMap(typeof(ITerminalService));
            for (var index = 0; index < map.InterfaceMethods.Length; index++)
                Require(map.TargetMethods[index].DeclaringType == wrapper,
                    $"{wrapper.Name} does not forward {map.InterfaceMethods[index].Name}; it falls back to the interface default.");
        }
        Require(typeof(ITerminalService).GetMethods(BindingFlags.Instance | BindingFlags.Public).Any(method => !method.IsAbstract),
            "The forwarding audit must cover the interface's default-implemented members.");

        var root = CheckRoot.Create("loopback-terminals");
        await using (var host = new PtyTerminalService(shell: "/bin/sh"))
        {
            await using var loopback = new LoopbackServer(host);
            ITerminalService local = new LoopbackTerminals(new LocalTerminalAdapter(host), loopback);
            var session = "clipboard-" + Guid.NewGuid().ToString("N");
            var attached = await local.AttachAsync(new(session, root, "client"));
            try
            {
                byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, .. Encoding.ASCII.GetBytes("fixture")];
                var text = await local.SaveClipboardImageAsync(session, png);
                var path = text.Trim().Trim('\'', '"');
                Require(File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(png), "A pasted clipboard image was not stored on the host: " + text);
            }
            finally
            {
                await attached.DisposeAsync();
                await local.CloseAsync(session);
            }
        }
        Directory.Delete(root, true);
        Console.WriteLine("PASS loopback terminals: every ITerminalService member forwarded; clipboard image paste stores the PNG through the app's local wiring");
    }
}