using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.EnvChips;

public sealed record EnvChipsSettings;

/// <summary>Names the workspace whose applied environments are wanted.</summary>
public sealed record EnvChipsQuery(string WorkspaceId);

/// <summary>One applied environment: the chip's text and its tooltip.</summary>
public sealed record EnvChip(string Label, string? Detail);

/// <summary>The environments applied to a workspace's terminals, in the order the steps ran; empty when none applies.</summary>
public sealed record EnvChipsChanged(string WorkspaceId, IReadOnlyList<EnvChip> Chips);

public static class EnvChipsContract
{
    public const string Id = "env-chips";

    public static readonly PluginMethod<EnvChipsQuery, EnvChipsChanged> Get = new("get");
    public static readonly PluginChannel<EnvChipsChanged> Changed = PluginChannel<EnvChipsChanged>.State("changed", Get, "workspaceId");
    public static readonly PluginContract Contract = PluginContract.Create<EnvChipsSettings>(Id, 1, [Get], [Changed]);
    public static readonly PluginManifest Manifest = new(Id, "Environment Chips", "stack-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Environment,
        Description = "Shows the environments applied to a workspace's terminals (mise, direnv, venv, node, JDK) as chips under each terminal.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.EnvChips.Host.dll",
        Ui = "SharpRail.Plugins.EnvChips.UI.dll"
    };
}
