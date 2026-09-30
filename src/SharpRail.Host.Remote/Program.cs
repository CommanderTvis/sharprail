using System.Net;
using SharpRail.Host.Remote;

var root = Environment.GetEnvironmentVariable("SHARPRAIL_ROOT") ?? Directory.GetCurrentDirectory();
var bind = IPAddress.Parse(Environment.GetEnvironmentVariable("SHARPRAIL_BIND") ?? "127.0.0.1");
var port = int.Parse(Environment.GetEnvironmentVariable("SHARPRAIL_PORT") ?? "54123");
var token = Environment.GetEnvironmentVariable("SHARPRAIL_TOKEN") ?? throw new InvalidOperationException("Set SHARPRAIL_TOKEN.");
// Shared settings, projects and labels persist on the host machine, apart from any local app state.
var state = Environment.GetEnvironmentVariable("SHARPRAIL_STATE_DIR") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sharprail", "host");
await using var app = RemoteServer.Create(root, bind, port, token, state);
await app.StartAsync();
Console.WriteLine($"SHARPRAIL_HOST_READY {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
await app.WaitForShutdownAsync();
