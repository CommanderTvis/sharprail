using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

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
        if (server != "clangd" && (options.IncludePaths is { Length: > 0 } || options.CompilerFlags is { Length: > 0 } || options.CompilationDatabasePath.Length > 0))
            throw new ArgumentException("Include paths, compiler flags and compilation database are clangd options. Use server-specific initialization options/settings for other languages.");
        if (options.InstallationPath is null || options.ProjectJdk is null || options.BuildTool is null || options.InstallationPath.Contains('\0') || options.ProjectJdk.Contains('\0') ||
            options.InstallationPath.Length > 0 && !Path.IsPathFullyQualified(options.InstallationPath) ||
            options.ProjectJdk.Length > 0 && !Path.IsPathFullyQualified(options.ProjectJdk))
            throw new ArgumentException("IntelliJ installation and project JDK paths must be absolute host paths.");
        if (server != "intellij" && (options.UseForJavaAndKotlin || options.InstallationPath.Length > 0 || options.ProjectJdk.Length > 0 || options.BuildTool.Length > 0))
            throw new ArgumentException("IntelliJ provider options belong to the intellij server.");
        if (options.BuildTool is not ("" or "maven" or "gradle" or "bazel" or "jps")) throw new ArgumentException("Unsupported IntelliJ build importer.");
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

    internal static JsonObject Initialization(WorkspaceLanguageOptions options, string workspace, string server)
    {
        var result = options.InitializationOptions is { } supplied ? ExpandObject(supplied, workspace) : new JsonObject();
        if (server is "intellij" or "kotlin")
        {
            result["intellijExtensions"] = false;
            result["lazyIntentions"] = false;
            result["runMainCodeLens"] = false;
            if (!result.ContainsKey("projects") || result["projects"] is JsonObject { Count: 0 }) result["projects"] = new JsonArray();
            if (result["projects"] is not JsonArray) throw new ArgumentException("IntelliJ initialization projects must be an array.");
            if (options.BuildTool.Length > 0) result["buildTools"] = new JsonObject { [new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/')] = options.BuildTool };
            if (options.ProjectJdk.Length > 0 || !result.ContainsKey("defaultSdk"))
            {
                var projectJdk = ProjectJava.Resolve(workspace, options.ProjectJdk);
                if (projectJdk.Path.Length > 0) result["defaultSdk"] = projectJdk.Path;
            }
        }
        if (server is "intellij" or "kotlin" && result["defaultSdk"] is JsonValue sdk && sdk.TryGetValue<string>(out var jdk) && jdk.Length > 0)
        {
            // defaultSdk controls analysis; import processes have their own public java-home setting.
            var projects = (JsonArray)result["projects"]!;
            if (projects.Count == 0)
            {
                var gradle = new[] { "settings.gradle.kts", "settings.gradle", "build.gradle.kts", "build.gradle" }.FirstOrDefault(name => File.Exists(Path.Combine(workspace, name)));
                var maven = File.Exists(Path.Combine(workspace, "pom.xml"));
                var importer = options.BuildTool.Length > 0 ? options.BuildTool : gradle is not null ? "gradle" : maven ? "maven" : "";
                if (importer == "gradle" && gradle is not null || importer == "maven" && maven)
                    projects.Add(new JsonObject { ["type"] = importer, ["path"] = new Uri(Path.Combine(workspace, importer == "maven" ? "pom.xml" : gradle ?? "build.gradle.kts")).AbsoluteUri });
            }
            foreach (var project in projects.OfType<JsonObject>())
                if (project["type"]?.ToString() is "gradle" or "maven" && !project.ContainsKey("java-home")) project["java-home"] = jdk;
        }
        LanguageServers.Known(server)?.Spec.DefaultInitialization(result);
        if (server == "clangd")
        {
            var flags = (options.CompilerFlags ?? []).Select(flag => Expand(flag, workspace))
                .Concat((options.IncludePaths ?? []).Select(path => "-I" + FullPath(path, workspace))).ToArray();
            if (flags.Length > 0) result["fallbackFlags"] = new JsonArray(flags.Select(flag => (JsonNode?)JsonValue.Create(flag)).ToArray());
            if (options.CompilationDatabasePath.Length > 0) result["compilationDatabasePath"] = FullPath(options.CompilationDatabasePath, workspace);
        }
        return result;
    }

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