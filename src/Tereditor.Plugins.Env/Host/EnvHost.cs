using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Env.Host;

/// <summary>Owns the workspace environment policy (step order) and keeps what the last environment resolution of each workspace reported and pushes every change to the terminals' chip rows.</summary>
public sealed class EnvHost : PluginHostModule
{
    public override PluginContract Contract => EnvContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        context.WorkspaceEnvironment(new StepPolicy());
        var known = new Dictionary<string, EnvChipsChanged>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var gate = new object();
        context.Method(EnvContract.Get, (query, _, _) =>
        {
            lock (gate) return ValueTask.FromResult(known.GetValueOrDefault(query.WorkspaceId) ?? new EnvChipsChanged(query.WorkspaceId, []));
        });
        context.OnWorkspaceEnvironment((workspace, facts) =>
        {
            var next = new EnvChipsChanged(workspace, [.. facts.Select(fact => new EnvChip(fact.Label, fact.Detail))]);
            lock (gate)
            {
                if (known.TryGetValue(workspace, out var previous) && previous.Chips.SequenceEqual(next.Chips)) return;
                known[workspace] = next;
            }
            context.Publish(EnvContract.Changed, next);
        });
        return ValueTask.FromResult<PluginDisposer?>(null);
    }

    /// <summary>Runs the steps in <see cref="EnvContract.StepOrder"/>; an unknown step id runs last.</summary>
    internal sealed class StepPolicy : IWorkspaceEnvironmentPolicy
    {
        public int Rank(string stepId)
        {
            var index = EnvContract.StepOrder.ToList().IndexOf(stepId);
            return index < 0 ? int.MaxValue : index;
        }
    }
}