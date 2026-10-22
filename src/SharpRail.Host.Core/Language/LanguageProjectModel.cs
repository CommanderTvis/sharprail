namespace SharpRail.Host.Core.Language;

internal sealed record LanguageProjectModel(string Workspace, string Server, string[] Manifests, bool Truncated)
{
    private static readonly HashSet<string> Excluded = [".git", ".tools", ".bench", "node_modules", "bin", "obj", "target", "vendor", ".venv", "venv", "dist", "build", ".gradle", ".idea", ".dart_tool", ".next", ".cache"];

    /// <summary>The provider whose project policy (H21) this model follows.</summary>
    internal LanguageServer? Provider { get; init; }

    internal static LanguageProjectModel Discover(string workspace, LanguageServer server, CancellationToken token, string compilationDatabase = "")
        => DiscoverMany(workspace, [server], token, compilationDatabase)[server.Id];

    /// <summary>Discovery for a builtin server by id.</summary>
    internal static LanguageProjectModel Discover(string workspace, string server, CancellationToken token, string compilationDatabase = "")
        => Discover(workspace, LanguageServers.Catalog.Single(item => item.Id == server), token, compilationDatabase);

    internal static IReadOnlyDictionary<string, LanguageProjectModel> DiscoverMany(string workspace, IReadOnlyList<LanguageServer> servers, CancellationToken token, string compilationDatabase = "")
    {
        var manifests = servers.ToDictionary(server => server.Id, _ => new List<string>());
        var pending = new Stack<string>([workspace]);
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var visited = 0;
        while (pending.Count > 0 && visited++ < 5000)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, "*", options))
            {
                token.ThrowIfCancellationRequested();
                foreach (var server in servers)
                    if (server.Spec.IsProjectManifest(file)) manifests[server.Id].Add(Path.GetRelativePath(workspace, file).Replace(Path.DirectorySeparatorChar, '/'));
            }
            foreach (var child in Directory.EnumerateDirectories(directory, "*", options))
            {
                if (manifests.ContainsKey("swift") && Path.GetExtension(child) is ".xcodeproj" or ".xcworkspace")
                    manifests["swift"].Add(Path.GetRelativePath(workspace, child).Replace(Path.DirectorySeparatorChar, '/'));
                else if (!Excluded.Contains(Path.GetFileName(child))) pending.Push(child);
            }
        }
        if (manifests.ContainsKey("clangd") && File.Exists(Path.Combine(workspace, "build", "compile_commands.json"))) manifests["clangd"].Add("build/compile_commands.json");
        if (manifests.ContainsKey("clangd") && compilationDatabase.Length > 0)
        {
            var file = Path.Combine(Path.GetFullPath(Path.Combine(workspace, compilationDatabase)), "compile_commands.json");
            if (File.Exists(file)) manifests["clangd"].Add(Path.GetRelativePath(workspace, file).Replace(Path.DirectorySeparatorChar, '/'));
        }
        return servers.ToDictionary(server => server.Id, server => new LanguageProjectModel(workspace, server.Id, manifests[server.Id].Distinct().Order(StringComparer.Ordinal).ToArray(), pending.Count > 0) { Provider = server });
    }

    internal string[] WorkspaceFolders => Server == "go" && !Manifests.Any(path => path is "go.work" or "go.mod") && Manifests.Any(path => Path.GetFileName(path) == "go.mod")
        ? Manifests.Where(path => Path.GetFileName(path) == "go.mod").Select(path => Path.GetDirectoryName(Path.Combine(Workspace, path))!).Distinct().ToArray()
        : [Workspace];

    internal void ConfigureInitialization(System.Text.Json.Nodes.JsonObject options)
    {
        if (Server == "rust" && !options.ContainsKey("linkedProjects") && Manifests.Length > 0)
        {
            var projects = Manifests.Any(path => path is "Cargo.toml" or "rust-project.json")
                ? Manifests.Where(path => path is "Cargo.toml" or "rust-project.json") : Manifests;
            options["linkedProjects"] = new System.Text.Json.Nodes.JsonArray(projects.Select(path => (System.Text.Json.Nodes.JsonNode?)System.Text.Json.Nodes.JsonValue.Create(Path.Combine(Workspace, path))).ToArray());
        }
    }

    internal static bool Contains(string directory, string file)
    {
        var relative = Path.GetRelativePath(directory, file);
        return !Path.IsPathFullyQualified(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }

    internal string Resolution => Provider?.Spec.ProjectScope(Manifests) is { Length: > 0 } scope ? scope : "Project resolution is owned by the language server.";

    internal string Description => Resolution + " " + (Manifests.Length == 0 ? "No project/build manifest found; server uses inferred workspace or document context."
        : "Project model: " + string.Join(", ", Manifests.Take(8)) + (Manifests.Length > 8 ? $" (+{Manifests.Length - 8})" : "")) +
        (Truncated ? " Manifest discovery reached its directory limit." : "");
}