using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

internal static class WorkspaceLanguageConfiguration
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    internal static WorkspaceLanguageOptions Parse(string json, string server)
    {
        if (json.Length > 16 * 1024) throw new ArgumentException("Workspace language configuration is limited to 16 KiB.");
        var options = JsonSerializer.Deserialize<WorkspaceLanguageOptions>(json, Json) ?? throw new ArgumentException("Configuration must be an object.");
        foreach (var values in new[] { options.Arguments, options.IncludePaths, options.CompilerFlags })
            if (values?.Any(value => value is null || value.Contains('\0')) == true) throw new ArgumentException("Configuration arguments cannot contain null or NUL.");
        if (options.CompilationDatabasePath is null || options.CompilationDatabasePath.Contains('\0')) throw new ArgumentException("Invalid compilation database path.");
        foreach (var value in new[] { options.InitializationOptions, options.Settings })
            if (value is { ValueKind: not JsonValueKind.Object }) throw new ArgumentException("Initialization options and settings must be JSON objects.");
        var groups = LanguageServers.Known(server)?.Spec.OptionGroups ?? LanguageServerOptionGroups.None;
        if (!groups.HasFlag(LanguageServerOptionGroups.CompilerFlags) && (options.IncludePaths is { Length: > 0 } || options.CompilerFlags is { Length: > 0 } || options.CompilationDatabasePath.Length > 0))
            throw new ArgumentException("Include paths, compiler flags and a compilation database are not options of this server. Use server-specific initialization options/settings.");
        if (options.InstallationPath is null || options.ProjectJdk is null || options.BuildTool is null || options.InstallationPath.Contains('\0') || options.ProjectJdk.Contains('\0') ||
            options.InstallationPath.Length > 0 && !Path.IsPathFullyQualified(options.InstallationPath) ||
            options.ProjectJdk.Length > 0 && !Path.IsPathFullyQualified(options.ProjectJdk))
            throw new ArgumentException("Installation and project JDK paths must be absolute host paths.");
        if (!groups.HasFlag(LanguageServerOptionGroups.Installation) && options.InstallationPath.Length > 0)
            throw new ArgumentException("An installation directory is not an option of this server.");
        if (!groups.HasFlag(LanguageServerOptionGroups.JvmProject) && (options.ProjectJdk.Length > 0 || options.BuildTool.Length > 0))
            throw new ArgumentException("A project JDK and build importer are not options of this server.");
        if (options.UseForJavaAndKotlin && LanguageServers.Known(server)?.Spec.Replaces is not { Count: > 0 })
            throw new ArgumentException("Only a server that replaces others can be chosen for their languages.");
        if (options.BuildTool is not ("" or "maven" or "gradle" or "bazel" or "jps")) throw new ArgumentException("Unsupported build importer.");
        return options;
    }

    internal static string Serialize(WorkspaceLanguageOptions options) => JsonSerializer.Serialize(options, Json);
    internal static string Expand(string value, string workspace) => value.Replace("${workspaceFolder}", workspace, StringComparison.Ordinal);
    private static string FullPath(string value, string workspace) => Path.GetFullPath(Expand(value, workspace), workspace);

    private static JsonObject ExpandObject(JsonElement supplied, string workspace)
    {
        JsonNode? Visit(JsonNode? node) => node switch
        {
            JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(Expand(text, workspace)),
            JsonObject obj => new JsonObject(obj.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, Visit(pair.Value)))),
            JsonArray array => new JsonArray(array.Select(Visit).ToArray()),
            _ => node?.DeepClone()
        };
        return (JsonObject)Visit(JsonNode.Parse(supplied.GetRawText()))!;
    }

    internal static JsonObject Initialization(WorkspaceLanguageOptions options, string workspace, string server, IReadOnlyList<string>? manifests = null, string storage = "", LanguageLog? log = null, string sessionDirectory = "")
    {
        var result = options.InitializationOptions is { } supplied ? ExpandObject(supplied, workspace) : new JsonObject();
        if (LanguageServers.Known(server) is not { } known) return result;
        known.Spec.DefaultInitialization(result);
        known.Spec.ConfigureInitialization(new Start(options, workspace, known, manifests ?? [], storage, log, sessionDirectory), result);
        return result;
    }

    /// <summary>What a provider's initialization hook reads (H21).</summary>
    private sealed class Start(WorkspaceLanguageOptions options, string workspace, LanguageServer server, IReadOnlyList<string> manifests, string storage, LanguageLog? log, string sessionDirectory) : ILanguageServerStart
    {
        public string Workspace => workspace;
        public LanguageServerWorkspaceOptions Options { get; } = Typed(options);
        public IReadOnlyList<string> Manifests => manifests;
        public string WorkspaceState { get; } = storage.Length == 0 ? "" : Path.Combine(storage, "workspaces", LanguageServers.Key(workspace), server.Id);
        public string ServerCache { get; } = storage.Length == 0 ? "" : Path.Combine(storage, "cache", server.Id, server.Version);
        public string SessionDirectory => sessionDirectory;
        public string FullPath(string value) => WorkspaceLanguageConfiguration.FullPath(value, workspace);
        public string Expand(string value) => WorkspaceLanguageConfiguration.Expand(value, workspace);
        public (string Path, string Source) ResolveProjectJdk(string configured) => ProjectJava.Resolve(workspace, configured);
        public void Log(string category, string text) => log?.Add(category, text);
    }

    internal static LanguageServerWorkspaceOptions Typed(WorkspaceLanguageOptions options) => new(options.IncludePaths ?? [], options.CompilerFlags ?? [], options.CompilationDatabasePath,
        options.InstallationPath, options.ProjectJdk, options.BuildTool,
        options.InitializationOptions is { ValueKind: JsonValueKind.Object } supplied ? [.. supplied.EnumerateObject().Select(property => property.Name)] : []);

    internal static JsonObject Settings(WorkspaceLanguageOptions options, string workspace, string server)
    {
        var result = options.Settings is { } supplied ? ExpandObject(supplied, workspace) : new JsonObject();
        LanguageServers.Known(server)?.Spec.DefaultSettings(result);
        return result;
    }
}

/// <summary>Independent workspace configuration, copied from Default when a workspace is created.</summary>
internal sealed record WorkspaceLanguageOptions(bool AutoStart = false, string[]? Arguments = null,
    System.Text.Json.JsonElement? InitializationOptions = null, System.Text.Json.JsonElement? Settings = null,
    string[]? IncludePaths = null, string[]? CompilerFlags = null, string CompilationDatabasePath = "",
    bool UseForJavaAndKotlin = false, string InstallationPath = "", string ProjectJdk = "", string BuildTool = "");