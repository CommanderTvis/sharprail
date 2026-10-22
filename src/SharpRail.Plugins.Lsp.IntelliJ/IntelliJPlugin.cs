using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Lsp.IntelliJ;

/// <summary>The Java / Kotlin (IntelliJ) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class IntelliJPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-intellij";

    public static readonly PluginManifest Manifest = new(Id, "Java / Kotlin (IntelliJ)", "code-s-slash-line", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the Java / Kotlin (IntelliJ) language server to code intelligence.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.Lsp.IntelliJ.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "intellij",
        Version = IntelliJVersion,
        Executable = "intellij-server",
        Arguments = ["--stdio"],
        Extensions = [".java", ".kt", ".kts"],
        Implementation = "IntelliJ Java and Kotlin · JetBrains · optional evaluation provider",
        Source = "JetBrains · download.jetbrains.com",
        Website = "https://www.jetbrains.com/help/intellij-vscode/About-instance.html#features",
        Repository = "https://github.com/JetBrains/intellij-lsp.nvim",
        RepositoryLabel = "Official client repository",
        Pinning = LanguageServerPinning.None,
        PinningDescription = "Managed and fully pinned: exact build, SHA-256 per platform, verified before extraction; acceptance of its terms is required.",
        PathCapable = false,
        Settings = [],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name is "pom.xml" or "settings.gradle" or "settings.gradle.kts" or "build.gradle" or "build.gradle.kts" or "workspace.json";
        },
        ProjectScope = _ => "IntelliJ Java/Kotlin uses native Maven, Gradle, Bazel or JPS import. Project-wide results depend on completed project import and indexing.",
        LanguageId = path => Path.GetExtension(path) == ".java" ? "java" : "kotlin",
        ProjectKind = manifest => Path.GetFileName(manifest) switch
        {
            "pom.xml" => "Maven project",
            var name when name.StartsWith("settings.gradle", StringComparison.Ordinal) => "Gradle build",
            var name when name.StartsWith("build.gradle", StringComparison.Ordinal) => "Gradle project",
            ".project" => "Eclipse project",
            _ => null
        }
    };

    /// <summary>The pinned IntelliJ build. Its distribution, terms and launcher stay in the host (<c>IntelliJDistribution</c>), which keys them by this server's id.</summary>
    public const string IntelliJVersion = "263.6379.0";
}
