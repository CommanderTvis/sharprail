using System.Diagnostics;
using System.Runtime.InteropServices;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.AirGovernance.Host;

/// <summary>
/// The plugin's behaviour apart from the host context: detecting Central and the actions the user triggers on it
/// (sign in and out, start the proxy, update, list and switch the AI access source). The plugin never edits an
/// agent's configuration: governed launches are `central run` in the command line, built by the agent's own plugin.
/// Central runs only from a status check (the read-only `central status` and, with Central chosen and signed in, `central limit --json`, which the pane repeats automatically) or a method the user triggered, and no Central output is stored; the identity and credits reach only the governed agents' Account tabs, on request.
/// </summary>
internal sealed class AirGovernanceRuntime : IDisposable
{
    private readonly AirCentralCli cli;
    private readonly Func<AirSettings> settings;
    private readonly Action<AirStatus> publish;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly SemaphoreSlim probeGate = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly List<Process> logins = [];
    private (string Auth, string Proxy) observed = ("unknown", "unknown");
    private string? detail;
    private AirCredits? credits;
    private (string? Email, string? License) identity;
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
            await probeGate.WaitAsync(cancellationToken);
            try
            {
                observed = await cli.ProbeAsync(central.Path, cancellationToken);
                detail = null;
                await RefreshCreditsAsync(central, cancellationToken);
            }
            finally { probeGate.Release(); }
        }
        return Build(central);
    }

    /// <summary>Reads the chosen source's remaining credits (read-only <c>central limit --json</c>) only with Central chosen and signed in; otherwise forgets them.</summary>
    private async Task RefreshCreditsAsync(AirCentral central, CancellationToken cancellationToken)
    {
        if (settings().Source == AirSources.Central && observed.Auth == "connected" && central is { State: "supported", Path: not null })
        {
            var read = await cli.LimitReadAsync(central.Path, cancellationToken);
            credits = read.Credits is { } fresh ? fresh with { ReadAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() } : null;
            identity = read.Credits is null ? default : (read.Email, read.License);
        }
        else { credits = null; identity = default; }
    }

    /// <summary>
    /// What the agents' panels show for a launcher Central governs: a fresh probe and <c>central limit --json</c> read
    /// (the same read the pane repeats), reported as the account the agent runs under. An unavailable Central is reported
    /// in <see cref="AgentGovernedAccount.Problem"/>. The identity is passed on, never stored or logged.
    /// </summary>
    public async ValueTask<AgentGovernedAccount> AccountAsync(CancellationToken cancellationToken)
    {
        const string Source = "Central";
        try
        {
            var status = await StatusAsync(true, cancellationToken);
            publish(status);
            var central = status.Central;
            if (central.State == "absent") return new(Source, Problem: "Central is not installed. See Settings › Air Governance.");
            if (central.State is not ("supported" or "outdated")) return new(Source, Problem: "Central cannot be used on this host. See Settings › Air Governance.");
            if (central.Auth == "signed-out") return new(Source, Problem: "Not signed in to Central. Sign in under Settings › Air Governance.");
            if (central.Auth != "connected") return new(Source, Problem: "Could not check whether Central is signed in.");
            if (central.State == "outdated") return new(Source, Problem: "Central is outdated. Update it under Settings › Air Governance.");
            if (status.Credits is not { } left) return new(Source, Problem: "Could not read the remaining credits from Central.");
            return new(Source, identity.Email, identity.License, left.Remaining, left.Total, left.TopUp, left.RefillNext);
        }
        catch (Exception error) when (error is not OperationCanceledException) { return new(Source, Problem: "Could not read the remaining credits from Central."); }
    }

    /// <summary>The host-side contribution: <see cref="AirLaunch.Contribution"/> plus the account the governed launcher runs under.</summary>
    public AgentLaunchContribution? Contribution(string launcherId) =>
        AirLaunch.Contribution(settings(), launcherId, cli.Locate(), PiWire(), OperatingSystem.IsWindows()) is { } contribution
            ? contribution with { Account = new("Central", AccountAsync) } : null;

    /// <summary>Central's Pi wire, read (never written) from the Pi integration <c>central add pi</c> installed on this host, or <see langword="null"/> when it is missing or names none.</summary>
    public string? PiWire()
    {
        try
        {
            var file = new FileInfo(AirLaunch.PiExtensionPath(cli.Environment.Home));
            return file is { Exists: true, Length: <= 64 * 1024 } ? AirLaunch.PiWire(File.ReadAllText(file.FullName)) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private AirStatus Build(AirCentral central)
    {
        var current = settings();
        var pi = PiWire();
        var agents = new[] { ("claude", "Claude Code", current.ClaudeCode), ("codex", "Codex", current.Codex), (AirLaunch.OmpLauncher, "omp", current.Omp) }
            .Select(agent => new AirAgent(agent.Item1, agent.Item2, agent.Item3, AirLaunch.Shape(current, agent.Item1, central.Path, pi, OperatingSystem.IsWindows()))).ToArray();
        return new(central with { Auth = observed.Auth, Proxy = observed.Proxy }, current.Source, agents, Volatile.Read(ref busy) > 0, detail, settings().Source == AirSources.Central && observed.Auth == "connected" ? credits : null, pi);
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
        if (running is not null) { lock (logins) logins.Add(running); observed = ("unknown", observed.Proxy); return (true, "Complete sign-in in the browser, it is detected automatically."); }
        if (outcome == "exited" && code == 0) { observed = await cli.ProbeAsync(central.Path!, cancellationToken); return (true, "Signed in."); }
        return (false, outcome == "exited" ? $"Sign-in failed (central exited {code})." : "Sign-in could not be started.");
    });

    public Task<AirOutcome> SignOutAsync() => ExclusiveAsync(async cancellationToken =>
    {
        var central = await cli.InspectAsync(cancellationToken);
        if (central.Path is null) return (false, "Central is not installed.");
        var (ok, message) = await RunAsync(central, "Sign-out", ["logout"], AirCentralCli.ActionTimeout, cancellationToken);
        if (ok) { observed = ("signed-out", "unknown"); credits = null; }
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

    private readonly SemaphoreSlim listing = new(1, 1);
    private readonly object switchLock = new();
    private (string Id, Task<AirOutcome> Task)? latestSwitch;

    /// <summary>
    /// Lists the sources through the signed-in session (see <see cref="AirAccessLister"/>), one listing at a time
    /// so two requests never refresh the same credential concurrently. Runs only from the user's button.
    /// </summary>
    public async Task<AirAccessList> AccessSourcesAsync(CancellationToken cancellationToken)
    {
        var central = await cli.InspectAsync(cancellationToken);
        if (central.State != "supported") return new([], "Central is not available.");
        await listing.WaitAsync(cancellationToken);
        try
        {
            var result = await AirAccessLister.ListAsync(cli, central.Path!, cancellationToken);
            if (result.Sources is not { } sources) return new([], result.Failure);
            return new(sources, sources.Count == 0 ? "Central listed no selectable AI access sources." : null);
        }
        finally { listing.Release(); }
    }

    /// <summary>
    /// Switches serialise behind the gate. A request for the source already queued last shares that switch; any other
    /// source queues its own, so A, B, A runs all three in order and ends on A.
    /// </summary>
    public Task<AirOutcome> SwitchAccessAsync(string id)
    {
        lock (switchLock)
        {
            if (latestSwitch is { } last && last.Id == id && !last.Task.IsCompleted) return last.Task;
            var task = SwitchCoreAsync(id);
            latestSwitch = (id, task);
            return task;
        }
    }

    private Task<AirOutcome> SwitchCoreAsync(string id) => ExclusiveAsync(async cancellationToken =>
    {
        if (!AirCentralCli.IsSelectionId(id)) return (false, "That is not an AI access source id.");
        var central = await cli.InspectAsync(cancellationToken);
        if (central.State != "supported") return (false, "Central is not available to switch sources.");
        var (ok, message) = await RunAsync(central, "Switching the AI access source", ["access", id], AirCentralCli.ActionTimeout, cancellationToken);
        if (!ok) return (false, message);
        var (restarted, _) = await RunAsync(central, "Restarting the proxy", ["proxy", "restart"], AirCentralCli.ActionTimeout, cancellationToken);
        if (observed.Auth == "connected") await RefreshCreditsAsync(central, cancellationToken);
        return (true, restarted ? "Switched. The proxy was restarted." : "Switched, but the proxy could not be restarted. Run `central proxy restart` in a terminal.");
    });

    public void Dispose()
    {
        lifetime.Cancel();
        lock (logins) foreach (var login in logins) { AirCentralCli.Kill(login); login.Dispose(); }
        lifetime.Dispose();
    }
}