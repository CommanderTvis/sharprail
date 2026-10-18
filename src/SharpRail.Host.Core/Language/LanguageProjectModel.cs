namespace SharpRail.Host.Core.Language;

internal sealed record LanguageProjectModel(string Workspace, string Server, string[] Manifests, bool Truncated)
{
    private static readonly HashSet<string> Excluded = [".git", ".tools", ".bench", "node_modules", "bin", "obj", "target", "vendor", ".venv", "venv", "dist", "build", ".gradle", ".idea", ".dart_tool", ".next", ".cache"];

    internal static LanguageProjectModel Discover(string workspace, string server, CancellationToken token, string compilationDatabase = "")
        => DiscoverMany(workspace, [server], token, compilationDatabase)[server];

    internal static IReadOnlyDictionary<string, LanguageProjectModel> DiscoverMany(string workspace, IReadOnlyList<string> servers, CancellationToken token, string compilationDatabase = "")
    {
        var manifests = servers.ToDictionary(server => server, _ => new List<string>());
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
                    if (Matches(server, file)) manifests[server].Add(Path.GetRelativePath(workspace, file).Replace(Path.DirectorySeparatorChar, '/'));
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
        return manifests.ToDictionary(pair => pair.Key, pair => new LanguageProjectModel(workspace, pair.Key, pair.Value.Distinct().Order(StringComparer.Ordinal).ToArray(), pending.Count > 0));
    }

    internal static bool Matches(string server, string file)
    {
        var name = Path.GetFileName(file);
        var extension = Path.GetExtension(name);
        return server switch
        {
            "csharp" => extension is ".sln" or ".slnx" or ".csproj",
            "typescript" => name is "tsconfig.json" or "jsconfig.json" or "package.json",
            "python" => name is "pyrightconfig.json" or "pyproject.toml" or "setup.py" or "setup.cfg",
            "clangd" => name is "compile_commands.json" or "compile_flags.txt" or ".clangd" or "CMakeLists.txt",
            "java" => name is "pom.xml" or "settings.gradle" or "settings.gradle.kts" or "build.gradle" or "build.gradle.kts" or ".project" or ".classpath",
            "kotlin" or "intellij" => name is "pom.xml" or "settings.gradle" or "settings.gradle.kts" or "build.gradle" or "build.gradle.kts" or "workspace.json",
            "go" => name is "go.mod" or "go.work",
            "rust" => name is "Cargo.toml" or "rust-project.json",
            "scala" => name is "build.sbt" or "build.sc" or "build.mill" or "pom.xml" || Path.GetFileName(Path.GetDirectoryName(file)) == ".bsp" && extension == ".json",
            "ruby" => name is "Gemfile" or "Rakefile" || extension == ".gemspec",
            "php" => name == "composer.json",
            "swift" => name is "Package.swift" or "buildServer.json" || extension == ".xcodeproj",
            "dart" => name == "pubspec.yaml",
            "r" => name == "DESCRIPTION" || extension == ".Rproj",
            "powershell" => name == "PSScriptAnalyzerSettings.psd1" || extension == ".psd1",
            "shell" => name == ".bash-language-serverrc",
            "css" => name == "package.json",
            _ => false
        };
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

    internal string Resolution => Server switch
    {
        "csharp" => "Roslyn loads the selected solution/MSBuild graph; project initialization and requested restores must complete before navigation.",
        "typescript" => "TypeScript resolves tsconfig/jsconfig project references, paths and package imports; files excluded by their configuration are outside that project graph.",
        "python" => "Pyright resolves execution environments, interpreter/import paths and workspace configuration; diagnostic mode does not define reference search scope.",
        "clangd" => Manifests.Any(path => Path.GetFileName(path) == "compile_commands.json")
            ? "clangd background indexing follows the compilation database; only listed translation units and their includes contribute to that index."
            : "No compilation database found. Fallback flags support current-file parsing but do not provide a whole-project background index.",
        "java" => "JDT imports Maven/Gradle/Eclipse project models; standalone files use an inferred project. Dependencies require successful import and a compatible JDK.",
        "intellij" => "IntelliJ Java/Kotlin uses native Maven, Gradle, Bazel or JPS import. Project-wide results depend on completed project import and indexing.",
        "kotlin" => "JetBrains Kotlin imports Gradle/Maven JVM projects; dependencies require successful build import. Other project types depend on server support.",
        "rust" => "rust-analyzer uses Cargo/rust-project metadata, build scripts and proc macros. Discovered manifests are linked unless explicitly configured.",
        "go" => "gopls resolves go.work/go.mod and package build context; independent modules are supplied as separate workspace folders.",
        "scala" => "Metals imports the build through BSP; build imports are enabled by default. Build-tool selection and dependency failures remain server-reported.",
        "ruby" => "Ruby LSP resolves the workspace with its bundle/project index; the project Ruby and installed bundle determine available dependencies.",
        "php" => "Intelephense indexes the workspace; external dependencies use environment.includePaths. Feature availability also depends on the server license.",
        "swift" => "SourceKit-LSP uses SwiftPM/build-server metadata. Cross-module results require an up-to-date native index; older toolchains and Xcode projects may need additional build-server setup.",
        "dart" => "Dart analysis contexts follow pubspec/package_config and SDK resolution; dependencies require successful package resolution.",
        "shell" => "Bash language server follows sourced files and bounded background discovery; scan limits and dynamic source paths can leave references incomplete.",
        "powershell" => "PowerShell Editor Services resolves its workspace/scripts/modules; dynamic imports and external modules can limit cross-file references.",
        "r" => "R languageserver resolves packages and statically sourced dependencies; workspace symbol discovery does not guarantee semantic references across unrelated scripts.",
        "css" => "CSS/SCSS/LESS language service has document-oriented semantics; a workspace folder does not create a general cross-file reference index.",
        _ => "Project resolution is owned by the language server."
    };

    internal string Description => Resolution + " " + (Manifests.Length == 0 ? "No project/build manifest found; server uses inferred workspace or document context."
        : "Project model: " + string.Join(", ", Manifests.Take(8)) + (Manifests.Length > 8 ? $" (+{Manifests.Length - 8})" : "")) +
        (Truncated ? " Manifest discovery reached its directory limit." : "");
}