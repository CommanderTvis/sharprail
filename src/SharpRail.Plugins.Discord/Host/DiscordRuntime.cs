namespace SharpRail.Plugins.Discord.Host;

/// <summary>
/// Owns one Discord IPC connection for an activation's lifetime: connecting on demand, a retry floor after a
/// failure, the elapsed-time anchor per project, and announcing every status change.
/// </summary>
internal sealed class DiscordRuntime(Func<DiscordSettings> settings, Action<DiscordStatus> announce)
{
    private const long RetryFloorMilliseconds = 5_000;
    private readonly Lock gate = new();
    private DiscordIpc? ipc;
    private DiscordPresence? latest;
    private long projectStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private string? startedForProjectId;
    private string? failure;
    private long lastAttempt = long.MinValue / 2;
    private bool connecting;
    private bool stopped;
    private int connectionGeneration;

    private bool IsConnected => ipc?.Connected == true;

    private DiscordStatus Current()
    {
        lock (gate) return PresenceDecision.Status(PresenceDecision.For(latest, settings(), projectStartedAt), IsConnected, failure);
    }

    private DiscordStatus Report()
    {
        var next = Current();
        lock (gate)
            if (!stopped) announce(next);
        return next;
    }

    private void Disconnect()
    {
        DiscordIpc? closing;
        lock (gate) { closing = ipc; ipc = null; failure = null; connectionGeneration++; }
        closing?.Close();
    }

    private void TakeLastError()
    {
        lock (gate)
            if (ipc?.LastError is { } error) { failure = error; ipc.LastError = null; }
    }

    private async Task EnsureConnectedAsync(string applicationId)
    {
        int generation;
        lock (gate)
        {
            if (stopped || IsConnected || connecting) return;
            if (failure is not null && Environment.TickCount64 - lastAttempt < RetryFloorMilliseconds) return;
            connecting = true;
            lastAttempt = Environment.TickCount64;
            generation = connectionGeneration;
        }
        var client = new DiscordIpc();
        try
        {
            await client.ConnectAsync(applicationId, () =>
            {
                lock (gate)
                {
                    if (!ReferenceEquals(ipc, client)) return;
                    ipc = null;
                    failure = "Discord closed the connection.";
                }
                Report();
            });
            lock (gate)
            {
                if (stopped || generation != connectionGeneration) client.Close();
                else { ipc = client; failure = null; }
            }
        }
        catch (InvalidOperationException error)
        {
            client.Close();
            lock (gate)
                if (!stopped && generation == connectionGeneration) failure = error.Message;
        }
        finally
        {
            lock (gate) connecting = false;
        }
    }

    public async Task<DiscordStatus> PublishPresenceAsync(DiscordPresence? presence)
    {
        long startedAt;
        lock (gate)
        {
            if (stopped) return Current();
            // The elapsed time follows the project, not the file.
            if (presence?.ProjectId != startedForProjectId)
            {
                startedForProjectId = presence?.ProjectId;
                projectStartedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }
            latest = presence;
            startedAt = projectStartedAt;
        }
        var current = settings();
        var decision = PresenceDecision.For(presence, current, startedAt);
        if (decision is PresenceDecision.Silent)
        {
            Disconnect();
            return Report();
        }
        await EnsureConnectedAsync(current.ApplicationId);
        lock (gate)
        {
            if (stopped) return Current();
            ipc?.SetActivity(decision is PresenceDecision.Publish publish ? publish.Activity : null);
        }
        TakeLastError();
        return Report();
    }

    /// <summary>Answers truthfully: it retries a connection rather than reporting a cached failure.</summary>
    public async Task<DiscordStatus> GetStatusAsync()
    {
        var current = settings();
        DiscordPresence? presence;
        long startedAt;
        lock (gate) (presence, startedAt) = (latest, projectStartedAt);
        if (PresenceDecision.For(presence, current, startedAt) is not PresenceDecision.Silent && !IsConnected)
            await EnsureConnectedAsync(current.ApplicationId);
        TakeLastError();
        return Current();
    }

    /// <summary>A settings change clears the retry floor, so the next attempt is immediate.</summary>
    public void ApplySettingsChange()
    {
        DiscordPresence? presence;
        lock (gate) { failure = null; lastAttempt = long.MinValue / 2; presence = latest; }
        _ = PublishPresenceAsync(presence);
    }

    public void Stop()
    {
        DiscordIpc? closing;
        lock (gate) { stopped = true; closing = ipc; }
        closing?.SetActivity(null);
        Disconnect();
        lock (gate) { latest = null; startedForProjectId = null; }
    }
}