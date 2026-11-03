using System.Text.Json.Serialization;

using SharpRail.Plugins.Api;

namespace SharpRail.Plugins.Providers;

/// <summary>The agents whose model providers are managed here. Each reads its own credential store.</summary>
public enum ProviderAgent
{
    [JsonStringEnumMemberName("pi")] Pi,
    [JsonStringEnumMemberName("omp")] Omp
}

/// <summary>Where a provider's credential comes from, as far as the host can tell without reading the secret.</summary>
public enum ProviderSource
{
    /// <summary>No credential is stored by the agent's own files.</summary>
    [JsonStringEnumMemberName("none")] None,
    /// <summary>An API key SharpRail or the agent stored in the agent's file.</summary>
    [JsonStringEnumMemberName("key")] Key,
    /// <summary>A sign-in managed by the agent itself (an OAuth login); not editable here.</summary>
    [JsonStringEnumMemberName("sign-in")] SignIn,
    /// <summary>The provider's environment variable is set in the host's environment.</summary>
    [JsonStringEnumMemberName("environment")] Environment
}

/// <summary>One provider row. Nothing here carries a key.</summary>
/// <param name="Id">The id the agent uses for the provider (<c>anthropic</c>).</param>
/// <param name="EnvVar">The variable the agent reads the key from; omp stores a key as this variable.</param>
public sealed record ProviderEntry(string Id, string Name, string? EnvVar, ProviderSource Source)
{
    /// <summary>Added by the user rather than listed in the agent's documentation.</summary>
    public bool Custom { get; init; }
    /// <summary>Why a key cannot be entered for this provider, such as a subscription sign-in; null when one can.</summary>
    public string? Note { get; init; }
    public bool Configured => Source != ProviderSource.None;
}

/// <summary>An agent's providers and the file their keys live in.</summary>
public sealed record ProvidersReport(ProviderAgent Agent, string Location, IReadOnlyList<ProviderEntry> Providers)
{
    /// <summary>What the agent needs for these keys to apply, such as a launch with SharpRail's profile.</summary>
    public string? Note { get; init; }
}

public sealed record ProvidersParams(ProviderAgent Agent);

/// <summary>Stores <paramref name="Key"/> for a provider. <paramref name="EnvVar"/> names the variable of a provider the catalog does not list.</summary>
public sealed record SetProviderKeyParams(ProviderAgent Agent, string ProviderId, string Key)
{
    public string? Name { get; init; }
    public string? EnvVar { get; init; }
}

public sealed record RemoveProviderKeyParams(ProviderAgent Agent, string ProviderId);

public sealed record ProvidersAck(bool Ok = true);

public static class ProvidersContract
{
    public const int WireVersion = 1;

    public static readonly PluginMethod<ProvidersParams, ProvidersReport> List = new("list");
    public static readonly PluginMethod<SetProviderKeyParams, ProvidersReport> SetKey = new("setKey");
    public static readonly PluginMethod<RemoveProviderKeyParams, ProvidersReport> RemoveKey = new("removeKey");

    public static readonly PluginContract Contract = PluginContract.Create<ProvidersSettings>(ProvidersManifest.Id, WireVersion, [List, SetKey, RemoveKey], []);
}

public sealed record ProvidersSettings;