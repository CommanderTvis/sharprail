using System.Text.Json.Nodes;

using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Lsp;

/// <summary>The initialization the JetBrains JVM servers share; the IntelliJ plugin keeps the same copy, since plugins never reference each other.</summary>
internal static class JvmInitialization
{
    internal static void Configure(ILanguageServerStart start, JsonObject result)
    {
        var options = start.Options;
        var workspace = start.Workspace;
        result["intellijExtensions"] = false;
        result["lazyIntentions"] = false;
        result["runMainCodeLens"] = false;
        if (!result.ContainsKey("projects") || result["projects"] is JsonObject { Count: 0 }) result["projects"] = new JsonArray();
        if (result["projects"] is not JsonArray) throw new ArgumentException("IntelliJ initialization projects must be an array.");
        if (options.BuildTool.Length > 0) result["buildTools"] = new JsonObject { [new Uri(workspace + Path.DirectorySeparatorChar).AbsoluteUri.TrimEnd('/')] = options.BuildTool };
        if (options.ProjectJdk.Length > 0 || !result.ContainsKey("defaultSdk"))
        {
            var projectJdk = start.ResolveProjectJdk(options.ProjectJdk);
            if (projectJdk.Path.Length > 0) result["defaultSdk"] = projectJdk.Path;
        }
        if (result["defaultSdk"] is JsonValue sdk && sdk.TryGetValue<string>(out var jdk) && jdk.Length > 0)
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
        var used = options.ProjectJdk.Length == 0 && options.InitializationSupplied.Contains("defaultSdk")
            ? (Path: result["defaultSdk"]?.ToString() ?? "", Source: "workspace initialization defaultSdk")
            : start.ResolveProjectJdk(options.ProjectJdk);
        start.Log("project JDK", used.Source + (used.Path.Length > 0 ? ": " + used.Path : "") + ". This is separate from the server runtime and is not an import-failure diagnosis.");
    }
}