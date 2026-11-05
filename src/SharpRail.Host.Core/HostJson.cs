using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;
using SharpRail.Plugins.Api;

namespace SharpRail.Host.Core;

/// <summary>
/// Source-generated JSON metadata for everything the host core persists or answers outside plugin payloads. Its
/// <see cref="JsonSerializerContext.Default"/> carries the serializer's default options; a site with other options
/// keeps them and resolves its types from this context (<see cref="Resolving"/>), so the bytes stay those the reflection
/// serializer wrote.
/// </summary>
[JsonSerializable(typeof(HostStateStore.Stored), TypeInfoPropertyName = "HostStateStored")]
[JsonSerializable(typeof(HostStateStore.LanguageMetadata))]
[JsonSerializable(typeof(TerminalCatalogStore.Stored), TypeInfoPropertyName = "TerminalCatalogStored")]
[JsonSerializable(typeof(OwnedProcesses.Stored), TypeInfoPropertyName = "OwnedProcessesStored")]
[JsonSerializable(typeof(OwnedProcesses.Entry), TypeInfoPropertyName = "OwnedProcessEntry")]
[JsonSerializable(typeof(TerminalRef))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(Collection<string>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(SortedDictionary<string, string>))]
[JsonSerializable(typeof(LanguageServerOptions))]
[JsonSerializable(typeof(Dictionary<string, LanguageServerOptions>))]
[JsonSerializable(typeof(LanguageExitStore.Entry), TypeInfoPropertyName = "LanguageExitEntry")]
[JsonSerializable(typeof(List<LanguageExitStore.Entry>), TypeInfoPropertyName = "ListLanguageExitEntry")]
[JsonSerializable(typeof(LanguageUsageStore.Entry), TypeInfoPropertyName = "LanguageUsageEntry")]
[JsonSerializable(typeof(List<LanguageUsageStore.Entry>), TypeInfoPropertyName = "ListLanguageUsageEntry")]
[JsonSerializable(typeof(LanguageTerms.Acceptance))]
[JsonSerializable(typeof(ExecutableResolution))]
[JsonSerializable(typeof(LanguageConfigurationCatalog.Description))]
[JsonSerializable(typeof(ProjectLanguageProfile))]
[JsonSerializable(typeof(WorkspaceLanguageOptions))]
[JsonSerializable(typeof(IReadOnlyList<WorkspaceWatches.WatchInstance>))]
[JsonSerializable(typeof(IReadOnlyList<WorkspaceWatches.WatchLease>))]
internal sealed partial class HostJson : JsonSerializerContext
{
    /// <summary>The web defaults (camelCase, case-insensitive reading, numbers from strings), as <see cref="JsonSerializerOptions.Web"/>.</summary>
    internal static JsonSerializerOptions Web => field ??= Resolving(new(JsonSerializerDefaults.Web));

    /// <summary>Makes <paramref name="options"/> resolve every type from this context and read-only.</summary>
    internal static JsonSerializerOptions Resolving(JsonSerializerOptions options)
    {
        options.TypeInfoResolver = Default;
        options.MakeReadOnly();
        return options;
    }
}

internal static class JsonTypeInfos
{
    /// <summary>The metadata <paramref name="options"/> hold for <typeparamref name="T"/>, from the context they resolve from.</summary>
    internal static JsonTypeInfo<T> Info<T>(this JsonSerializerOptions options) => (JsonTypeInfo<T>)options.GetTypeInfo(typeof(T));
}