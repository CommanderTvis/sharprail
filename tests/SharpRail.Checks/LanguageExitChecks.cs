using System.Net;
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
        foreach (var remote in new[] { false, true }) await Exercise(Path.Combine(fixture, "language-exit", remote ? "remote" : "local"), remote);
        Console.WriteLine("PASS language server exit records: exit code and signal, bounded stderr, stop reasons, workspace removal, persistence, transport and notifications");
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
        await using var server = remote ? RemoteServer.Create(workspace, IPAddress.Loopback, 0, "exit-test", storage, plugins: seams => seams with { Builtins = [] }) : null;
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

            // Servers run for the lifetime of their workspace: nothing evicts one for being unused, and removing the workspace stops it and says so.
            Require(typeof(LanguageService).GetMembers(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
                .All(member => !member.Name.Contains("Idle", StringComparison.Ordinal)), "The language service still has idle eviction.");
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