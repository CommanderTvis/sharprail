using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Env;

public sealed record EnvSettings;

/// <summary>Names the workspace whose applied environments are wanted.</summary>
public sealed record EnvChipsQuery(string WorkspaceId);

/// <summary>One applied environment: the chip's text and its tooltip.</summary>
public sealed record EnvChip(string Label, string? Detail);

/// <summary>The environments applied to a workspace's terminals, in the order the steps ran; empty when none applies.</summary>
public sealed record EnvChipsChanged(string WorkspaceId, IReadOnlyList<EnvChip> Chips);

public static class EnvContract
{
    /// <summary>The order the steps run in: mise, direnv, Python, JavaScript, JVM. A step id not listed runs last.</summary>
    public static readonly IReadOnlyList<string> StepOrder = ["mise", "direnv", "python", "javascript", "jvm"];

    public const string Id = "env";

    public static readonly PluginMethod<EnvChipsQuery, EnvChipsChanged> Get = new("get");
    public static readonly PluginChannel<EnvChipsChanged> Changed = PluginChannel<EnvChipsChanged>.State("changed", Get, "workspaceId");
    public static readonly PluginContract Contract = PluginContract.Create<EnvSettings>(Id, 1, [Get], [Changed]);
    public static readonly PluginManifest Manifest = new(Id, "Environment", "stack-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Environment,
        Description = "Orders and merges the workspace environment steps of the mise, direnv, Python, JavaScript and JVM plugins, and shows the environments applied to a workspace's terminals as chips under each terminal.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.Env.Host.dll",
        Ui = "SharpRail.Plugins.Env.UI.dll"
    };
}