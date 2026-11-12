using System.Net;

using Tereditor.Host.Remote;

var root = Environment.GetEnvironmentVariable("TEREDITOR_ROOT") ?? Directory.GetCurrentDirectory();
var bind = IPAddress.Parse(Environment.GetEnvironmentVariable("TEREDITOR_BIND") ?? "127.0.0.1");
var port = int.Parse(Environment.GetEnvironmentVariable("TEREDITOR_PORT") ?? "54123");
var token = Environment.GetEnvironmentVariable("TEREDITOR_TOKEN") ?? throw new InvalidOperationException("Set TEREDITOR_TOKEN.");
// Shared settings, projects and labels persist on the host machine, apart from any local app state.
var state = Environment.GetEnvironmentVariable("TEREDITOR_STATE_DIR") ??
    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".tereditor", "host");
await using var app = RemoteServer.Create(root, bind, port, token, state, browserRoot: Environment.GetEnvironmentVariable("TEREDITOR_BROWSER_ROOT"), headlessLanguageMcp: Environment.GetEnvironmentVariable("TEREDITOR_HEADLESS_MCP") == "1");
await app.StartAsync();
Console.WriteLine($"TEREDITOR_HOST_READY {DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}");
await app.WaitForShutdownAsync();