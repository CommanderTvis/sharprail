namespace SharpRail.Plugins.ClaudeCode.Host;

internal sealed record AgentWatchTarget(string WorkspaceId, string TabKey, int Pid);

/// <summary>A repeating timer the watches run on; checks substitute one they step by hand.</summary>
internal interface IPollTimer
{
    IDisposable Every(TimeSpan interval, Action tick);
}

internal sealed class SystemPollTimer : IPollTimer
{
    public static readonly SystemPollTimer Instance = new();
    public IDisposable Every(TimeSpan interval, Action tick) => new Timer(_ => tick(), null, interval, interval);
}

/// <summary>
/// Polls the process table while terminals exist, and reports when a tab's shell gains or loses a <c>claude</c>
/// descendant. It runs only while the plugin is active, and only while some terminal exists: off means no sweep at all.
/// </summary>
internal sealed class AgentWatch(
    Func<IReadOnlyList<AgentWatchTarget>> listTargets,
    Action<string> onWorkspaceChanged,
    Action<string, string>? onAgentCleared = null,
    Action<string, string, int>? onAgentDetected = null,
    Func<IProcessSnapshot?>? capture = null,
    IPollTimer? timer = null)
{
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(2500);
    private static readonly string[] DetectedAgents = ["claude"];

    private readonly Lock gate = new();
    private readonly Dictionary<(string Workspace, string Tab), (int Pid, string Name)> agents = [];
    private readonly Func<IProcessSnapshot?> capture = capture ?? ProcessTree.Capture;
    private readonly IPollTimer timer = timer ?? SystemPollTimer.Instance;
    private IDisposable? running;

    public string? AgentFor(string workspaceId, string tabKey)
    {
        lock (gate) return agents.GetValueOrDefault((workspaceId, tabKey)).Name;
    }

    private void Idle()
    {
        running?.Dispose();
        running = null;
    }

    public void Sweep()
    {
        var changed = new HashSet<string>();
        lock (gate)
        {
            var targets = listTargets();
            if (targets.Count == 0)
            {
                Idle();
                foreach (var key in agents.Keys) changed.Add(key.Workspace);
                agents.Clear();
            }
            else
            {
                if (capture() is not { } snapshot) return;
                var live = new HashSet<(string, string)>();
                foreach (var target in targets)
                {
                    var key = (target.WorkspaceId, target.TabKey);
                    live.Add(key);
                    var previous = agents.GetValueOrDefault(key);
                    var found = ProcessTree.FindDescendant(snapshot, target.Pid, DetectedAgents);
                    var next = found ?? default;
                    if (previous == next) continue;
                    if (found is null)
                    {
                        agents.Remove(key);
                        if (previous.Name is not null) onAgentCleared?.Invoke(target.WorkspaceId, target.TabKey);
                    }
                    else
                    {
                        agents[key] = next;
                        onAgentDetected?.Invoke(target.WorkspaceId, target.TabKey, found!.Value.Pid);
                    }
                    changed.Add(target.WorkspaceId);
                }
                foreach (var key in agents.Keys.Where(key => !live.Contains(key)).ToArray())
                {
                    agents.Remove(key);
                    changed.Add(key.Workspace);
                }
            }
        }
        foreach (var workspace in changed) onWorkspaceChanged(workspace);
    }

    /// <summary>Arms the timer only; sweeping inline would block the terminal attach that pokes it.</summary>
    public void Poke()
    {
        lock (gate)
            if (running is null && listTargets().Count > 0) running = timer.Every(PollInterval, Sweep);
    }

    public void Forget(string workspaceId, string tabKey)
    {
        lock (gate) agents.Remove((workspaceId, tabKey));
    }

    public void Stop()
    {
        lock (gate)
        {
            Idle();
            agents.Clear();
        }
    }
}