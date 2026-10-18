using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SharpRail.Plugins.AirGovernance.Host;

/// <summary>
/// The plugin's behaviour apart from the host context: detecting Central and the actions the user triggers on it
/// (sign in and out, start the proxy, update, list and switch the AI access source). The plugin never edits an
/// agent's configuration: governed launches are `central run` in the command line, built by the agent's own plugin.
/// Nothing here runs except from a method the user triggered, and no Central output is stored or forwarded.
/// </summary>
internal sealed class AirGovernanceRuntime : IDisposable
{
    private readonly AirCentralCli cli;
    private readonly Func<AirSettings> settings;
    private readonly Action<AirStatus> publish;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Process> logins = [];
    private (string Auth, string Proxy) observed = ("unknown", "unknown");
    private string? detail;
    private int busy;

    public AirGovernanceRuntime(AirEnvironment environment, Func<AirSettings> settings, Action<AirStatus> publish)
    {
        cli = new(environment);
        this.settings = settings; this.publish = publish;
    }

    /// <summary>Where <c>central</c> is on this host, read from the file system only.</summary>
    public string? CentralPath() => cli.Locate();

    public async Task<AirStatus> StatusAsync(bool probe, CancellationToken cancellationToken)
    {
        var central = await cli.InspectAsync(cancellationToken);
        if (probe && central.Path is not null && central.State is "supported" or "outdated")
        {
            observed = await cli.ProbeAsync(central.Path, cancellationToken);
            detail = null;
        }
        return Build(central);
    }

    private AirStatus Build(AirCentral central)
    {
        var current = settings();
        var agents = new[] { ("claude", "Claude Code", current.ClaudeCode), ("codex", "Codex", current.Codex) }
            .Select(agent => new AirAgent(agent.Item1, agent.Item2, agent.Item3, AirLaunch.Shape(current, agent.Item1, central.Path))).ToArray();
        return new(central with { Auth = observed.Auth, Proxy = observed.Proxy }, current.Source, agents, Volatile.Read(ref busy) > 0, detail);
    }

    private async Task<AirOutcome> ExclusiveAsync(Func<CancellationToken, Task<(bool Ok, string Message)>> work)
    {
        await gate.WaitAsync(lifetime.Token);
        (bool Ok, string Message) result;
        try
        {
            Interlocked.Increment(ref busy);
            publish(Build(await cli.InspectAsync(lifetime.Token)));
            result = await work(lifetime.Token);
            detail = result.Message;
        }
        finally { Interlocked.Decrement(ref busy); gate.Release(); }
        var status = Build(await cli.InspectAsync(lifetime.Token));
        publish(status);
        return new(result.Ok, result.Message, status);
    }

    private async Task<(bool, string)> RunAsync(AirCentral central, string label, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var result = await cli.RunAsync(central.Path!, arguments, timeout, false, cancellationToken);
        return result.Outcome switch
        {
            "exited" when result.ExitCode == 0 => (true, ""),
            "exited" => (false, $"{label} failed (central exited {result.ExitCode})."),
            "timed-out" => (false, $"{label} timed out."),
            _ => (false, $"{label} could not be started.")
        };
    }

    public Task<AirOutcome> SignInAsync() => ExclusiveAsync(async cancellationToken =>
    {
        var central = await cli.InspectAsync(cancellationToken);
        if (central.State != "supported" && central.State != "outdated") return (false, "Central is not available to sign in.");
        var (outcome, code, running) = await cli.LaunchAsync(central.Path!, ["login"], TimeSpan.FromMilliseconds(1500), cancellationToken);
        if (running is not null) { lock (logins) logins.Add(running); observed = ("unknown", observed.Proxy); return (true, "Complete sign-in in the browser, then check the sign-in state."); }
        if (outcome == "exited" && code == 0) { observed = await cli.ProbeAsync(central.Path!, cancellationToken); return (true, "Signed in."); }
        return (false, outcome == "exited" ? $"Sign-in failed (central exited {code})." : "Sign-in could not be started.");
    });

    public Task<AirOutcome> SignOutAsync() => ExclusiveAsync(async cancellationToken =>
    {
        var central = await cli.InspectAsync(cancellationToken);
        if (central.Path is null) return (false, "Central is not installed.");
        var (ok, message) = await RunAsync(central, "Sign-out", ["logout"], AirCentralCli.ActionTimeout, cancellationToken);
        if (ok) observed = ("signed-out", "unknown");
        return (ok, ok ? "Signed out of Central." : message);
    });

    public Task<AirOutcome> StartProxyAsync() => ExclusiveAsync(async cancellationToken =>
    {
        var central = await cli.InspectAsync(cancellationToken);
        if (central.State != "supported") return (false, "Central is not available to start its proxy.");
        var (ok, message) = await RunAsync(central, "Starting the proxy", ["proxy", "start"], AirCentralCli.ActionTimeout, cancellationToken);
        if (ok) observed = await cli.ProbeAsync(central.Path!, cancellationToken);
        return ok && observed.Proxy == "stopped" ? (false, "The proxy did not stay running.") : (ok, ok ? "The proxy is running." : message);
    });

    public Task<AirOutcome> UpdateAsync() => ExclusiveAsync(async cancellationToken =>
    {
        var central = await cli.InspectAsync(cancellationToken);
        if (central.Path is null) return (false, "Central is not installed.");
        var (ok, message) = await RunAsync(central, "Update", ["update", "--install"], AirCentralCli.UpdateTimeout, cancellationToken);
        return (ok, ok ? "Central was updated." : message);
    });

    public async Task<AirAccessList> AccessSourcesAsync(CancellationToken cancellationToken)
    {
        var central = await cli.InspectAsync(cancellationToken);
        if (central.State != "supported") return new([], "Central is not available.");
        var result = await cli.RunAsync(central.Path!, ["access"], AirCentralCli.StatusTimeout, true, cancellationToken);
        if (result is not { Outcome: "exited", ExitCode: 0 }) return new([], "Central could not list AI access sources. If it asks for terms, run `central access` once in a terminal.");
        var sources = AirCentralCli.ParseAccess(result.Stdout);
        return new(sources, sources.Count == 0 ? "Central listed no selectable AI access sources." : null);
    }

    public Task<AirOutcome> SwitchAccessAsync(string id) => ExclusiveAsync(async cancellationToken =>
    {
        if (!AirCentralCli.IsSelectionId(id)) return (false, "That is not an AI access source id.");
        var central = await cli.InspectAsync(cancellationToken);
        if (central.State != "supported") return (false, "Central is not available to switch sources.");
        var (ok, message) = await RunAsync(central, "Switching the AI access source", ["access", id], AirCentralCli.ActionTimeout, cancellationToken);
        if (!ok) return (false, message);
        var (restarted, _) = await RunAsync(central, "Restarting the proxy", ["proxy", "restart"], AirCentralCli.ActionTimeout, cancellationToken);
        return (true, restarted ? "Switched. The proxy was restarted." : "Switched, but the proxy could not be restarted. Run `central proxy restart` in a terminal.");
    });

    public void Dispose()
    {
        lifetime.Cancel();
        lock (logins) foreach (var login in logins) { AirCentralCli.Kill(login); login.Dispose(); }
        lifetime.Dispose();
    }
}
