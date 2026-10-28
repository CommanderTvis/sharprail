using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>
/// Every language-server death is explained: the exit record carries the exit code, decoded signal, stderr tail, recent JSON-RPC
/// messages and who asked for the stop, reaches clients, survives a host restart and toasts only when nobody asked.
/// </summary>
internal static class LanguageExitChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    internal static async Task Run(string fixture)
    {
        Records();
        await StderrFlood(fixture);
        await DisposeWaitsForStops(fixture);
        foreach (var remote in new[] { false, true }) await Exercise(Path.Combine(fixture, "language-exit", remote ? "remote" : "local"), remote);
        Console.WriteLine("PASS language server exit records: exit code and signal, bounded stderr, stop reasons, workspace removal, persistence, transport and notifications");
    }

    /// <summary>A workspace removal still stopping its server (entry already out of the map) must finish before DisposeAsync returns, so nothing recreates files under a state directory being deleted.</summary>
    private static async Task DisposeWaitsForStops(string fixture)
    {
        var root = Path.Combine(fixture, "language-exit", "dispose-race"); Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "Fixture.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        var settings = JsonSerializer.Serialize(new { csharp = new { command = executable, arguments } });
        var state = Path.Combine(root, ".tools", "state");
        var service = new LanguageService(state, () => true, () => settings);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            LanguageResult status;
            do
            {
                status = await service.ExecuteAsync(new(root, Operation: "status", Server: "csharp"), timeout.Token);
                if (status.State is "installing" or "initializing") await Task.Delay(30, timeout.Token);
            } while (status.State is "installing" or "initializing");
            Require(status.State == "ready", "The fixture server did not start: " + status.Detail);
            var removal = service.CloseWorkspaceAsync(root).AsTask();
            await service.DisposeAsync();
            Directory.Delete(state, true);
            // The exit watcher's grace period is two seconds; a late record would recreate files within it.
            await Task.Delay(3000);
            await removal;
            Require(!Directory.Exists(state), "A language service wrote to its state directory after DisposeAsync returned.");
        }
        finally { await service.DisposeAsync(); Directory.Delete(root, true); }
    }

    /// <summary>
    /// A server that floods stderr (megabytes, with and without newlines, lines past the 2000-character cap) while it answers requests must never
    /// stall on a full pipe: the connection keeps draining stderr, keeps a bounded tail, and the one-shot installer runner finishes with a bounded error prefix.
    /// </summary>
    private static async Task StderrFlood(string fixture)
    {
        var root = Path.Combine(fixture, "language-exit", "stderr-flood"); Directory.CreateDirectory(root);
        var executable = Environment.ProcessPath!;
        string[] Arguments(params string[] more) => [.. (Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location] : Array.Empty<string>()), "--fake-stderr-flood", .. more];
        var stopwatch = Stopwatch.StartNew();

        var start = new ProcessStartInfo(executable);
        foreach (var argument in Arguments()) start.ArgumentList.Add(argument);
        var exits = new List<LanguageExit>();
        await using (var connection = new LanguageConnection(start, root, new LanguageLog(), "flood", exits.Add))
        {
            for (var round = 0; round < 4; round++)
            {
                var result = await connection.RequestAsync("flood", new { round }, CancellationToken.None, TimeSpan.FromSeconds(30));
                Require(result?["round"]?.GetValue<int>() == round, "The flooding server's answer was lost in round " + round);
                Require(!connection.Exited, "The flooding server connection died in round " + round);
            }
            await connection.RequestAsync("flood-last", null, CancellationToken.None, TimeSpan.FromSeconds(30));
            using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            while (connection.ExitRecord is null) await Task.Delay(50, wait.Token);
            var details = connection.ExitRecord.Details;
            Require(connection.ExitRecord.ExitCode == 3, "The flooding server exit code was lost: " + connection.ExitRecord.Reason);
            Require(details.Contains($"Last {LanguageConnection.StderrLines} stderr lines", StringComparison.Ordinal) && details.Contains("tail 299", StringComparison.Ordinal) &&
                details.Contains("tail 100\n", StringComparison.Ordinal) && !details.Contains("tail 99\n", StringComparison.Ordinal),
                "The retained stderr tail is not the most recent lines after a flood.");
            Require(details.Length < 2 * 24 * 1024 + 60_000 && !details.Contains(new string('y', 2100), StringComparison.Ordinal), "The retained stderr tail after a flood is not bounded in size or line length.");
        }

        foreach (var code in new[] { 0, 5 })
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var oneShot = new ProcessStartInfo(executable);
            foreach (var argument in Arguments("oneshot", code.ToString())) oneShot.ArgumentList.Add(argument);
            try
            {
                var output = await LanguageServers.RunAsync(oneShot, root, deadline.Token);
                Require(code == 0 && output.Trim() == "ok", "A one-shot command that floods stderr returned the wrong output.");
            }
            catch (IOException error)
            {
                Require(code != 0 && error.Message.Contains("BEGIN", StringComparison.Ordinal) && !error.Message.Contains("END", StringComparison.Ordinal) && error.Message.Length < 3200,
                    "A failed one-shot command did not carry a bounded stderr prefix: " + error.Message[..Math.Min(error.Message.Length, 300)]);
            }
        }
        Console.WriteLine("PASS language server stderr flood: connection and one-shot runner keep draining stderr (" + stopwatch.ElapsedMilliseconds + " ms)");
    }

    /// <summary>The flooding fixture server: writes megabytes to stderr before answering each request, so an undrained pipe stalls it.</summary>
    internal static async Task FloodServer(string[] args)
    {
        var error = Console.OpenStandardError();
        async Task Flood()
        {
            var text = new StringBuilder();
            for (var line = 0; line < 20_000; line++) text.Append("flood line ").Append(line).Append('\n');
            for (var line = 0; line < 3; line++) text.Append('x', 5000).Append('\n');
            text.Append('y', 300_000); // no newline
            await error.WriteAsync(Encoding.UTF8.GetBytes(text.ToString()));
            await error.FlushAsync();
        }
        if (args is ["oneshot", var code])
        {
            await error.WriteAsync("BEGIN\n"u8.ToArray());
            for (var index = 0; index < 4; index++) await Flood();
            await error.WriteAsync("\nEND\n"u8.ToArray());
            await error.FlushAsync();
            await Console.Out.WriteAsync("ok");
            await Console.Out.FlushAsync();
            Environment.Exit(int.Parse(code));
        }
        var input = Console.OpenStandardInput();
        var output = Console.OpenStandardOutput();
        var single = new byte[1];
        while (true)
        {
            var header = new StringBuilder();
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (await input.ReadAsync(single) == 0) return;
                header.Append((char)single[0]);
            }
            var length = int.Parse(header.ToString().Split("\r\n").First(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))[15..].Trim());
            var body = new byte[length];
            await input.ReadExactlyAsync(body);
            var request = System.Text.Json.Nodes.JsonNode.Parse(body)!;
            if (request["id"] is not { } id) continue;
            var method = request["method"]!.ToString();
            await Flood();
            if (method == "flood-last")
                for (var line = 0; line < 300; line++) await error.WriteAsync(Encoding.UTF8.GetBytes("tail " + line + "\n"));
            await error.FlushAsync();
            var reply = Encoding.UTF8.GetBytes(new System.Text.Json.Nodes.JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = request["params"]?.DeepClone() }.ToJsonString());
            await output.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {reply.Length}\r\n\r\n"));
            await output.WriteAsync(reply);
            await output.FlushAsync();
            if (method == "flood-last") Environment.Exit(3);
        }
    }

    private static void Records()
    {
        var lines = Enumerable.Range(0, 5000).Select(index => "line " + index + " " + new string('x', 3000)).ToArray();
        var exit = LanguageExits.Build("csharp", "/w", DateTime.UtcNow.AddSeconds(-70), DateTime.UtcNow, 137, null, false, lines, ["12:00:00.000 -> request initialize #1"], ["textDocument/hover #4 for 2s"], 1L << 31, DateTime.UtcNow);
        Require(exit.Details.Length <= 2 * 24 * 1024 && exit.Reason.Length < 1000, "A huge stderr tail was not bounded in the exit record.");
        if (!OperatingSystem.IsWindows())
            Require(exit.Signal == "SIGKILL" && exit.Reason.Contains("not confirmed", StringComparison.Ordinal) && exit.Details.Contains("2.0 GiB", StringComparison.Ordinal) && exit.Reason.Contains("Up for 1m 10s", StringComparison.Ordinal),
                "Exit 137 was not reported as a suspected SIGKILL with uptime and last memory: " + exit.Reason);
        Require(LanguageExits.Build("x", "/w", DateTime.UtcNow, DateTime.UtcNow, 0, "a stop was requested", false, [], [], [], 0, default) is { Unexpected: false, Initiator: "a stop was requested" },
            "A requested stop was reported as unexpected.");
        Require(LanguageExits.Build("x", "/w", DateTime.UtcNow, DateTime.UtcNow, 0, null, false, [], [], [], 0, default).Reason.Contains("it wrote nothing to stderr", StringComparison.Ordinal),
            "Missing stderr was not stated.");
    }

    private static async Task Exercise(string root, bool remote)
    {
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "code.ts"), "😀 target");
        var storage = Path.Combine(root, "state");
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        await using var server = remote ? RemoteServer.Create(workspace, IPAddress.Loopback, 0, "exit-test", storage, plugins: seams => seams with { Builtins = SharpRail.Host.Core.Plugins.BuiltinPlugins.LanguageServerEntries }) : null;
        if (server is not null) await server.StartAsync();
        var state = server is null ? new HostStateStore(storage) : (HostStateStore)server.Services.GetRequiredService<IHostStateService>();
        var endpoint = server is null ? null : new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        using var remoteHost = endpoint is null ? null : new RemoteProjectAdapter(endpoint, "exit-test");
        using var remoteState = endpoint is null ? null : new RemoteStateAdapter(endpoint, "exit-test");
        IProjectServices host = remoteHost ?? (IProjectServices)new LocalProjectAdapter(new ProjectServices(workspace, state));
        var reopened = (HostStateStore?)null;
        try
        {
            await state.ChangeAsync([HostStateChange.Setting("language-server:typescript", JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
            async Task<LanguageServerState> Row(IProjectServices? from = null) => (await (from ?? host).LanguageAsync(new(workspace, Operation: "servers"))).Servers.Single(item => item.Id == "typescript");
            async Task<IReadOnlyDictionary<string, string>> Deaths() => (remoteState is null ? state.Current : await remoteState.GetStateAsync()).LanguageDeaths;
            async Task StartDying(object die)
            {
                await host.LanguageAsync(new(workspace, Operation: "restart", Server: "typescript", Configuration: JsonSerializer.Serialize(new { settings = new { fixtureDie = die } })));
                using var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while ((await host.LanguageAsync(new(workspace, Operation: "status", Server: "typescript"), ready.Token)).State != "ready") await Task.Delay(30, ready.Token);
            }
            async Task<LanguageExit> Died(string after)
            {
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                while (true)
                {
                    if ((await Row()).Exit is { } exit && string.CompareOrdinal(exit.Time, after) > 0) return exit;
                    await Task.Delay(50, deadline.Token);
                }
            }
            async Task Trigger(int milliseconds)
            {
                using var request = new CancellationTokenSource(milliseconds);
                try { await host.LanguageAsync(new(workspace, "code.ts", "hover", 0, 3), request.Token); }
                catch (OperationCanceledException) { }
            }
            Require((await Row()).Exit is null, "A fresh workspace claims a server death.");

            // (a) exit 137 after the first request.
            await StartDying(new { code = 137 });
            await Trigger(5000);
            var killed = await Died("");
            var row = await Row();
            Require(killed.Unexpected && killed.ExitCode == 137 && killed.Initiator.Length == 0 && killed.UptimeSeconds >= 0, "Exit 137 was not recorded as an unrequested death with its code.");
            if (!OperatingSystem.IsWindows())
                Require(killed.Signal == "SIGKILL" && killed.Reason.Contains("not confirmed", StringComparison.Ordinal), "Exit 137 was not worded as a suspected SIGKILL: " + killed.Reason);
            Require(killed.Details.Contains("-> request textDocument/hover", StringComparison.Ordinal) && killed.Details.Contains("textDocument/hover #", StringComparison.Ordinal) &&
                killed.Details.Contains("Requests in flight: textDocument/hover", StringComparison.Ordinal) && killed.Details.Contains("fixture stderr", StringComparison.Ordinal),
                "The exit record lost the last request, the request in flight or the stderr tail:\n" + killed.Details);
            Require(row.State == "error" && row.Detail.StartsWith("Stopped unexpectedly: ", StringComparison.Ordinal) && row.Detail.Contains("code 137", StringComparison.Ordinal), "The server row does not say why it stopped: " + row.Detail);
            Require((await Deaths()).TryGetValue(workspace + "\0typescript", out var death) && death.Contains("code 137", StringComparison.Ordinal), "An unexpected death was not published for a notification.");
            var logs = (await host.LanguageAsync(new(workspace, Operation: "logs", Server: "typescript"))).Detail;
            Require(logs.Contains("exit: Exited with code 137", StringComparison.Ordinal), "The log lost the exit line.");

            // (b) a crash with a long stderr: the tail is bounded to the last 200 lines.
            await StartDying(new { code = 134, stderr = 300 });
            await Trigger(5000);
            var crashed = await Died(killed.Time);
            Require(crashed.ExitCode == 134 && crashed.Unexpected && crashed.Reason.Contains("fixture stderr line 299", StringComparison.Ordinal), "The crash did not report its code and last stderr line: " + crashed.Reason);
            if (!OperatingSystem.IsWindows()) Require(crashed.Signal == "SIGABRT", "Exit 134 was not decoded.");
            Require(crashed.Details.Contains("Last 200 stderr lines", StringComparison.Ordinal) && crashed.Details.Contains("fixture stderr line 299", StringComparison.Ordinal) &&
                crashed.Details.Contains("fixture stderr line 100\n", StringComparison.Ordinal) && !crashed.Details.Contains("fixture stderr line 99\n", StringComparison.Ordinal) && crashed.Details.Length < 60_000,
                "The stderr tail is not the last 200 lines.");

            // (c) a hung server killed by SharpRail names who asked and why, and does not notify.
            var deathsBefore = (await Deaths()).Count; var lastDeath = (await Deaths())[workspace + "\0typescript"];
            await StartDying(new { hang = true });
            await Trigger(300);
            await host.LanguageAsync(new(workspace, Operation: "stop", Server: "typescript"));
            var stopped = await Died(crashed.Time);
            Require(!stopped.Unexpected && stopped.Initiator.Contains("a stop was requested", StringComparison.Ordinal) && stopped.Reason.StartsWith("Stopped by SharpRail", StringComparison.Ordinal) &&
                stopped.Details.Contains("cancelled or timed out", StringComparison.Ordinal), "A stop of a hung server was not recorded as requested: " + stopped.Reason);
            Require((await Row()).State == "stopped" && (await Row()).Detail.StartsWith("Last run: ", StringComparison.Ordinal), "A requested stop is described as a failure.");
            await Task.Delay(300);
            Require((await Deaths()).Count == deathsBefore && (await Deaths())[workspace + "\0typescript"] == lastDeath, "Stopping a server notified the user.");

            // (d) a server that exits cleanly on its own.
            await StartDying(new { code = 0 });
            await Trigger(5000);
            var clean = await Died(stopped.Time);
            Require(clean.Unexpected && clean.ExitCode == 0 && clean.Reason.Contains("Exited with code 0 on its own", StringComparison.Ordinal), "A clean exit nobody asked for was not reported: " + clean.Reason);

            // Sampling memory never stops a server by itself (the limits do, see --lsp-limits), and removing the workspace stops it and says so.
            await host.LanguageAsync(new(workspace, Operation: "restart", Server: "typescript", Configuration: "{}"));
            using (var ready = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                while ((await host.LanguageAsync(new(workspace, Operation: "status", Server: "typescript"), ready.Token)).State != "ready") await Task.Delay(30, ready.Token);
            var beforeRemoval = (await Deaths())[workspace + "\0typescript"];
            state.Languages.SampleMemory();
            await Task.Delay(300);
            Require((await Row()).State == "ready", "Sampling memory stopped a server that nobody asked to stop.");
            var removal = new TaskCompletionSource<LanguageExit>(TaskCreationOptions.RunContinuationsAsynchronously);
            state.Languages.ExitObserved = (_, id, exit) => { if (id == "typescript") removal.TrySetResult(exit); };
            await state.Languages.CloseWorkspaceAsync(workspace);
            var idle = await removal.Task.WaitAsync(TimeSpan.FromSeconds(20));
            state.Languages.ExitObserved = null;
            Require(!idle.Unexpected && idle.Reason.Contains("workspace removed", StringComparison.Ordinal) && idle.StartedBy == "human" && (await Row()).State == "idle" &&
                (await Deaths())[workspace + "\0typescript"] == beforeRemoval, "Removing the workspace did not stop its server with that reason, silently: " + idle.Reason);

            // The last unexpected record survives a host restart.
            await StartDying(new { code = 137 });
            await Trigger(5000);
            var final = await Died(idle.Time);
            await state.StopLanguagesAsync();
            reopened = new HostStateStore(storage);
            var again = await Row(new LocalProjectAdapter(new ProjectServices(workspace, reopened)));
            Require(again.Exit is { } persisted && persisted.Time == final.Time && persisted.ExitCode == 137 && persisted.Details == final.Details && again.State == "idle" &&
                again.Detail.StartsWith("Stopped unexpectedly: ", StringComparison.Ordinal), "The exit record did not survive a host restart.");
            Require(new FileInfo(Path.Combine(storage, "language", "exits.json")).Length < 2_000_000, "The exit file is unbounded.");
            Console.WriteLine("PASS language exit records " + (remote ? "remote" : "local"));
        }
        finally
        {
            await state.StopLanguagesAsync();
            if (reopened is not null) await reopened.StopLanguagesAsync();
            Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled);
        }
    }
}