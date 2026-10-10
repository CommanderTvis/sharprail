using SharpRail.UI.Android;

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

        var directory = Path.Combine(root, "android-client");
        Directory.CreateDirectory(directory);
        Require(HostEndpoint.Load(directory) is null, "A device that never connected remembers no host.");
        new HostEndpoint("10.0.0.5:6000", "secret").Save(directory);
        Require(HostEndpoint.Load(directory) == new HostEndpoint("10.0.0.5:6000", "secret"), "The connected host must be remembered.");
        File.WriteAllText(Path.Combine(directory, "host.json"), "{");
        Require(HostEndpoint.Load(directory) is null, "A damaged record must read as no remembered host.");
        HostEndpoint.Forget(directory);
        Require(!File.Exists(Path.Combine(directory, "host.json")), "Forgetting must remove the record.");
        Console.WriteLine("PASS Android client: host addresses parse with the default port and the endpoint is remembered");
    }
}