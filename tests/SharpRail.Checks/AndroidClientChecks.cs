using System.Net;

using SharpRail.UI.Android;
using SharpRail.UI.State;

namespace SharpRail.Checks;

/// <summary>The Android client's host endpoint: what the connect screen accepts and what the app remembers.</summary>
internal static class AndroidClientChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        Require(HostEndpoint.Parse("192.168.1.20")?.ToString() == "http://192.168.1.20:54123/", "An address without a port must use the host's default port.");
        Require(HostEndpoint.Parse(" 192.168.1.20:8080 ")?.ToString() == "http://192.168.1.20:8080/", "A written port must be kept and surrounding space ignored.");
        Require(HostEndpoint.Parse("192.168.1.20:80")?.Port == 80, "A written port equal to the scheme's default must not become the host's default.");
        Require(HostEndpoint.Parse("https://workstation.example")?.ToString() == "https://workstation.example:54123/", "An HTTPS address must keep its scheme.");
        Require(HostEndpoint.Parse("[::1]:9000")?.Port == 9000, "An IPv6 address must parse with its port.");
        foreach (var invalid in new[] { "", "   ", "two words", "ftp://host", "http://", "host:port" })
            Require(HostEndpoint.Parse(invalid) is null, $"'{invalid}' is not a host address.");

        var link = HostLink.Format("192.168.1.20:54123", "s3cr&t=/+ key");
        Require(HostLink.TryParse(link, out var host, out var token) && host == "192.168.1.20:54123" && token == "s3cr&t=/+ key",
            "A connect link must carry the address and the token unchanged, whatever characters the token has.");
        foreach (var invalid in new[] { null, "", "https://example.com", "sharprail://connect?host=1.2.3.4", "sharprail://connect?token=x" })
            Require(!HostLink.TryParse(invalid, out _, out _), $"'{invalid}' is not a connect link.");
        var addresses = new[] { IPAddress.Loopback, IPAddress.Parse("fe80::1"), IPAddress.Parse("192.168.1.20"), IPAddress.Parse("10.0.0.2") };
        Require(HostLink.Reachable(new Uri("http://0.0.0.0:6000"), () => addresses) == "192.168.1.20:6000", "A listener on every interface must be named by this computer's first network address.");
        Require(HostLink.Reachable(new Uri("http://192.168.1.7:6000"), () => addresses) == "192.168.1.7:6000", "A listener on one address must be named by it.");
        Require(HostLink.Reachable(new Uri("http://0.0.0.0:6000"), () => [IPAddress.Loopback]) == "0.0.0.0:6000", "Without a network the listener keeps its own address.");
        var view = new SharpRail.UI.Panels.QrCodeView { Text = link };

        var directory = Path.Combine(root, "android-client");
        Directory.CreateDirectory(directory);
        Require(HostEndpoint.Load(directory) is null, "A device that never connected remembers no host.");
        new HostEndpoint("10.0.0.5:6000", "secret").Save(directory);
        Require(HostEndpoint.Load(directory) == new HostEndpoint("10.0.0.5:6000", "secret"), "The connected host must be remembered.");
        File.WriteAllText(Path.Combine(directory, "host.json"), "{");
        Require(HostEndpoint.Load(directory) is null, "A damaged record must read as no remembered host.");
        HostEndpoint.Forget(directory);
        Require(!File.Exists(Path.Combine(directory, "host.json")), "Forgetting must remove the record.");
        Console.WriteLine("PASS Android client: host addresses parse with the default port, connect links round-trip and the endpoint is remembered");
    }
}