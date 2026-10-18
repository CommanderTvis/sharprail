using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

public sealed partial class LanguageService
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Time, IReadOnlyDictionary<string, LanguageProjectModel> Models)> projectRoots = new();

    private async Task<LanguageResult> ProjectRootsAsync(LanguageRequest request, CancellationToken token)
    {
        var detected = await Task.Run(() => ProjectRoots(request with { Offset = 0, Limit = int.MaxValue }, token), token);
        var marks = new List<LanguageProject>(detected.Projects);
        foreach (var entry in entries.Where(pair => pair.Key.StartsWith(request.Workspace + "\0", StringComparison.Ordinal)).Select(pair => pair.Value))
        {
            if (!entry.Session.IsValueCreated || !entry.Session.Value.IsCompletedSuccessfully) continue;
            var session = entry.Session.Value.Result;
            if (session.Exited) continue;
            var reported = await session.DirectoryMarksAsync(request.Query == "refresh", token);
            marks.AddRange(reported.Where(mark => (request.Server.Length == 0 || mark.Server == request.Server) &&
                (!UsesIntelliJ(request.Workspace) || mark.Server is not ("java" or "kotlin"))));
        }
        var all = marks.Distinct().OrderBy(mark => mark.Root, StringComparer.Ordinal).ThenBy(mark => mark.Server, StringComparer.Ordinal)
            .ThenBy(mark => mark.Manifest, StringComparer.Ordinal).ToArray();
        return detected with
        {
            Detail = "Detected projects and source roots reported by running language servers. Listing starts no servers.",
            Projects = all.Skip(request.Offset).Take(request.Limit).ToArray(),
            Total = all.Length,
            Truncated = detected.Truncated || request.Offset + request.Limit < all.Length || marks.Count - detected.Projects.Count >= 500,
            Coverage = "Build-file detection and supported provider-reported source roots; package ownership and complete import are not implied."
        };
    }

    private LanguageResult ProjectRoots(LanguageRequest request, CancellationToken token)
    {
        if (!projectRoots.TryGetValue(request.Workspace, out var cached) || DateTime.UtcNow - cached.Time > TimeSpan.FromMinutes(1) || request.Query == "refresh")
        {
            cached = (DateTime.UtcNow, LanguageProjectModel.DiscoverMany(request.Workspace, LanguageServers.Catalog.Select(server => server.Id).ToArray(), token));
            projectRoots[request.Workspace] = cached;
        }
        var intellij = UsesIntelliJ(request.Workspace);
        var projects = cached.Models.Where(pair => request.Server.Length == 0 || pair.Key == request.Server)
            .Where(pair => intellij ? pair.Key is not ("java" or "kotlin") : pair.Key != "intellij")
            .SelectMany(pair => pair.Value.Manifests.Select(manifest => (Server: pair.Key, Manifest: manifest)))
            .Select(item => (item.Server, item.Manifest, Kind: Kind(item.Server, item.Manifest)))
            .Select(item => item.Kind is not { } kind ? null : new LanguageProject(Path.GetDirectoryName(item.Manifest)?.Replace('\\', '/') ?? "", item.Manifest, item.Server,
                kind, "detected", "Detected build definition; imported module membership is not confirmed by the language server."))
            .OfType<LanguageProject>()
            .OrderBy(item => item.Root, StringComparer.Ordinal).ThenBy(item => item.Server, StringComparer.Ordinal).ThenBy(item => item.Manifest, StringComparer.Ordinal).ToArray();
        return new("ready", "Detected project roots. Listing starts no servers.")
        {
            Projects = projects.Skip(request.Offset).Take(request.Limit).ToArray(),
            Total = projects.Length,
            Truncated = request.Offset + request.Limit < projects.Length || cached.Models.Values.Any(model => model.Truncated),
            Freshness = "partial",
            Coverage = "Build definitions only; no provider-confirmed import or module ownership."
        };
    }

    private static string? Kind(string server, string manifest)
    {
        var name = Path.GetFileName(manifest);
        return server switch
        {
            "csharp" => Path.GetExtension(manifest) == ".csproj" ? "MSBuild project" : "MSBuild solution",
            "java" or "kotlin" or "intellij" => name == "pom.xml" ? "Maven project" : name.StartsWith("settings.gradle", StringComparison.Ordinal) ? "Gradle build" : name.StartsWith("build.gradle", StringComparison.Ordinal) ? "Gradle project" : name == ".project" ? "Eclipse project" : null,
            "rust" => "Rust project",
            "go" => name == "go.work" ? "Go workspace" : "Go module",
            "typescript" => name == "package.json" ? "Node package" : "TypeScript project",
            "python" => "Python project",
            "scala" => "Scala build",
            "ruby" => "Ruby project",
            "php" => "Composer package",
            "swift" => "Swift project",
            "dart" => "Dart package",
            "r" => "R project",
            "clangd" => name == "CMakeLists.txt" ? "CMake project" : name == "compile_commands.json" ? "Compilation database" : null,
            _ => null
        };
    }
}