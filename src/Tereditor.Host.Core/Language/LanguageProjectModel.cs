namespace SharpRail.Host.Core.Language;

internal sealed record LanguageProjectModel(string Workspace, string Server, string[] Manifests, bool Truncated)
{
    private static readonly HashSet<string> Excluded = [".git", ".tools", ".bench", "node_modules", "bin", "obj", "target", "vendor", ".venv", "venv", "dist", "build", ".gradle", ".idea", ".dart_tool", ".next", ".cache"];

    /// <summary>The provider whose project policy (H21) this model follows.</summary>
    internal LanguageServer? Provider { get; init; }

    internal static LanguageProjectModel Discover(string workspace, LanguageServer server, CancellationToken token, WorkspaceLanguageOptions? options = null)
        => DiscoverMany(workspace, [server], token, options)[server.Id];

    /// <summary>Discovery for a builtin server by id.</summary>
    internal static LanguageProjectModel Discover(string workspace, string server, CancellationToken token, WorkspaceLanguageOptions? options = null)
        => Discover(workspace, LanguageServers.Catalog.Single(item => item.Id == server), token, options);

    internal static IReadOnlyDictionary<string, LanguageProjectModel> DiscoverMany(string workspace, IReadOnlyList<LanguageServer> servers, CancellationToken token, WorkspaceLanguageOptions? options = null)
    {
        var manifests = servers.ToDictionary(server => server.Id, _ => new List<string>());
        var pending = new Stack<string>([workspace]);
        var enumeration = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var visited = 0;
        while (pending.Count > 0 && visited++ < 5000)
        {
            token.ThrowIfCancellationRequested();
            var directory = pending.Pop();
            foreach (var file in Directory.EnumerateFiles(directory, "*", enumeration))
            {
                token.ThrowIfCancellationRequested();
                foreach (var server in servers)
                    if (server.Spec.IsProjectManifest(file)) manifests[server.Id].Add(Path.GetRelativePath(workspace, file).Replace(Path.DirectorySeparatorChar, '/'));
            }
            foreach (var child in Directory.EnumerateDirectories(directory, "*", enumeration))
            {
                var owners = servers.Where(server => server.Spec.IsProjectDirectory(child)).ToArray();
                foreach (var server in owners) manifests[server.Id].Add(Path.GetRelativePath(workspace, child).Replace(Path.DirectorySeparatorChar, '/'));
                if (owners.Length == 0 && !Excluded.Contains(Path.GetFileName(child))) pending.Push(child);
            }
        }
        var typed = WorkspaceLanguageConfiguration.Typed(options ?? new());
        foreach (var server in servers) manifests[server.Id].AddRange(server.Spec.AdditionalManifests(workspace, typed));
        return servers.ToDictionary(server => server.Id, server => new LanguageProjectModel(workspace, server.Id, manifests[server.Id].Distinct().Order(StringComparer.Ordinal).ToArray(), pending.Count > 0) { Provider = server });
    }

    internal IReadOnlyList<string> WorkspaceFolders => Provider?.Spec.WorkspaceFolders(Workspace, Manifests) ?? [Workspace];

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