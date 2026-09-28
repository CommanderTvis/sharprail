using System.Net;
using SharpRail.Host.Remote;

var root = Environment.GetEnvironmentVariable("SHARPRAIL_ROOT") ?? Directory.GetCurrentDirectory();
var bind = IPAddress.Parse(Environment.GetEnvironmentVariable("SHARPRAIL_BIND") ?? "127.0.0.1");
var port = int.Parse(Environment.GetEnvironmentVariable("SHARPRAIL_PORT") ?? "54123");
var token = Environment.GetEnvironmentVariable("SHARPRAIL_TOKEN") ?? throw new InvalidOperationException("Set SHARPRAIL_TOKEN.");
await using var app = RemoteServer.Create(root, bind, port, token);
await app.StartAsync();
Console.WriteLine($"SHARPRAIL_HOST_READY {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
await app.WaitForShutdownAsync();
