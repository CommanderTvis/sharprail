using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Lsp.Clangd;

/// <summary>The C / C++ (clangd) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class ClangdPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-clangd";

    public static readonly PluginManifest Manifest = new(Id, "C / C++ (clangd)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Cpp,
        Description = "Provides the C / C++ (clangd) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.Clangd.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "clangd",
        Name = "C / C++ (clangd)",
        Icon = LanguageServerIcon.Embedded(typeof(ClangdPlugin).Assembly, "Icon.svg"),
        Version = "23.1.0",
        Executable = "clangd",
        Arguments = ["--background-index"],
        Extensions = [".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".m", ".mm"],
        Aliases = ["c", "c++", "cpp"],
        Implementation = "clangd · LLVM project",
        Source = "GitHub releases · clangd/clangd",
        Website = "https://clangd.llvm.org/",
        Repository = "https://github.com/clangd/clangd",
        Pinning = LanguageServerPinning.Archive,
        Settings = [],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name is "compile_commands.json" or "compile_flags.txt" or ".clangd" or "CMakeLists.txt";
        },
        ProjectScope = manifests => manifests.Any(path => Path.GetFileName(path) == "compile_commands.json")
            ? "clangd background indexing follows the compilation database; only listed translation units and their includes contribute to that index."
            : "No compilation database found. Fallback flags support current-file parsing but do not provide a whole-project background index.",
        LanguageId = path => Path.GetExtension(path) switch { ".c" or ".h" => "c", ".m" => "objective-c", ".mm" => "objective-cpp", _ => "cpp" },
        Artifacts = [
        .. new[]
        {
            ("macos-aarch64", "mac", "1082e6638223b785ca2daf0939f13afcd0bb95c84ee9a4bbaff4745365159253"),
            ("macos-x86_64", "mac", "1082e6638223b785ca2daf0939f13afcd0bb95c84ee9a4bbaff4745365159253"),
            ("linux-x86_64", "linux", "e53b1a96196095faedb7642cf64964f7fb9ad4a0c1f00dd2c172a3d9dcbafdfd"),
            ("windows-x86_64", "windows", "23412a240756a162e7b98a282f36aa2a23a88db5ce16a0cbc4fef7253768c810")
        }.Select(item => new LanguageServerArtifact(item.Item1, $"https://github.com/clangd/clangd/releases/download/23.1.0/clangd-{item.Item2}-23.1.0.zip", item.Item3, "zip",
            "https://api.github.com/repos/clangd/clangd/releases/tags/23.1.0 (asset digest)", GitHub))
        ],
        OptionGroups = LanguageServerOptionGroups.CompilerFlags,
        AdditionalManifests = AdditionalManifests,
        ConfigureInitialization = (start, options) =>
        {
            var flags = start.Options.CompilerFlags.Select(start.Expand).Concat(start.Options.IncludePaths.Select(path => "-I" + start.FullPath(path))).ToArray();
            if (flags.Length > 0) options["fallbackFlags"] = new JsonArray([.. flags.Select(flag => (JsonNode?)JsonValue.Create(flag))]);
            if (start.Options.CompilationDatabasePath.Length > 0) options["compilationDatabasePath"] = start.FullPath(start.Options.CompilationDatabasePath);
        },
        // Reuses the source checkout's background index; see ClangdIndexPrimer.
        PrimeWorkspaceAsync = (prime, cancellationToken) => Task.Run(() => ClangdIndexPrimer.Prime(prime.Source, prime.Target, cancellationToken), cancellationToken),
        ProjectKind = manifest => Path.GetFileName(manifest) switch { "CMakeLists.txt" => "CMake project", "compile_commands.json" => "Compilation database", _ => null }
    };

    private static readonly string[] GitHub = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];

    private static IEnumerable<string> AdditionalManifests(string workspace, LanguageServerWorkspaceOptions options)
    {
        if (File.Exists(Path.Combine(workspace, "build", "compile_commands.json"))) yield return "build/compile_commands.json";
        if (options.CompilationDatabasePath.Length == 0) yield break;
        var file = Path.Combine(Path.GetFullPath(Path.Combine(workspace, options.CompilationDatabasePath)), "compile_commands.json");
        if (File.Exists(file)) yield return Path.GetRelativePath(workspace, file).Replace(Path.DirectorySeparatorChar, '/');
    }
}