using System.Text.Json.Serialization;

using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.Env;

public sealed record EnvSettings;

/// <summary>Names the workspace whose applied environments are wanted.</summary>
public sealed record EnvChipsQuery(string WorkspaceId);

/// <summary>One applied environment: the chip's text, its tooltip and the plugin whose step applied it (null from a host that predates it).</summary>
public sealed record EnvChip(string Label, string? Detail, string? PluginId = null);

/// <summary>The environments applied to a workspace's terminals, in the order the steps ran; empty when none applies.</summary>
public sealed record EnvChipsChanged(string WorkspaceId, IReadOnlyList<EnvChip> Chips);

public static class EnvContract
{
    /// <summary>The order the steps run in: mise, direnv, Python, JavaScript, JVM. A step id not listed runs last.</summary>
    public static readonly IReadOnlyList<string> StepOrder = ["mise", "direnv", "python", "javascript", "jvm"];

    public const string Id = "env";

    public static readonly PluginMethod<EnvChipsQuery, EnvChipsChanged> Get = new("get");
    public static readonly PluginChannel<EnvChipsChanged> Changed = PluginChannel<EnvChipsChanged>.State("changed", Get, "workspaceId");
    public static readonly PluginContract Contract = PluginContract.Create<EnvSettings>(Id, 1, [Get], [Changed], EnvJson.Default);
    public static readonly PluginManifest Manifest = new(Id, "Environment", "stack-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Environment,
        Description = "Orders and merges the workspace environment steps of the mise, direnv, Python, JavaScript and JVM plugins, and shows the environments applied to a workspace's terminals as chips under each terminal.",
        EnabledByDefault = true,
        Headless = HeadlessSupport.No,
        Host = "Tereditor.Plugins.Env.Host.dll",
        Ui = "Tereditor.Plugins.Env.UI.dll"
    };
}

[JsonSerializable(typeof(EnvSettings))]
[JsonSerializable(typeof(EnvChipsQuery))]
[JsonSerializable(typeof(EnvChipsChanged))]
internal sealed partial class EnvJson : JsonSerializerContext;