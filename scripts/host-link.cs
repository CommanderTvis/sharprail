#:package QRCoder@1.8.0
#:property ManagePackageVersionsCentrally=false

using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

using QRCoder;

var host = Environment.GetEnvironmentVariable("SHARPRAIL_CONNECT_HOST");
if (string.IsNullOrWhiteSpace(host))
{
    host = NetworkInterface.GetAllNetworkInterfaces()
        .Where(network => network.OperationalStatus == OperationalStatus.Up && network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(network => network.GetIPProperties().UnicastAddresses)
        .Select(unicast => unicast.Address)
        .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))?.ToString();
}
if (string.IsNullOrWhiteSpace(host))
    throw new InvalidOperationException("No network IPv4 address found. Set SHARPRAIL_CONNECT_HOST to an address clients can reach.");

var port = Environment.GetEnvironmentVariable("SHARPRAIL_PORT") ?? "54123";
var token = Environment.GetEnvironmentVariable("SHARPRAIL_TOKEN") ?? throw new InvalidOperationException("Set SHARPRAIL_TOKEN.");
var address = $"{host}:{port}";
var link = $"sharprail://connect?host={Uri.EscapeDataString(address)}&token={Uri.EscapeDataString(token)}";
using var generator = new QRCodeGenerator();
using var data = generator.CreateQrCode(link, QRCodeGenerator.ECCLevel.M);
using var code = new AsciiQRCode(data);
Console.WriteLine($"Connect to {address} by scanning this session QR code:");
foreach (var line in code.GetGraphic(1).Split('\n'))
    Console.WriteLine($"\u001b[30;47m{line.TrimEnd('\r')}\u001b[0m");
Console.WriteLine(link);
