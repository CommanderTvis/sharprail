using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.EnvChips.Host;

/// <summary>Keeps what the last environment resolution of each workspace reported and pushes every change to the terminals' chip rows.</summary>
public sealed class EnvChipsHost : PluginHostModule
{
    public override PluginContract Contract => EnvChipsContract.Contract;

    public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var known = new Dictionary<string, EnvChipsChanged>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var gate = new object();
        context.Method(EnvChipsContract.Get, (query, _, _) =>
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
            context.Publish(EnvChipsContract.Changed, next);
        });
        return ValueTask.FromResult<PluginDisposer?>(null);
    }
}
