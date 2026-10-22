using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Kotlin;

/// <summary>The Kotlin (Kotlin LS) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class KotlinPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-kotlin";

    public static readonly PluginManifest Manifest = new(Id, "Kotlin (Kotlin LS)", "code-s-slash-line", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the Kotlin (Kotlin LS) language server to code intelligence.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.Lsp.Kotlin.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "kotlin",
        Version = "263.6379.0",
        Executable = "kotlin-lsp",
        Arguments = ["--stdio"],
        Extensions = [".kt", ".kts"],
        Aliases = ["kt"],
        Implementation = "Kotlin Language Server · JetBrains",
        Source = "JetBrains · download.jetbrains.com",
        Website = "https://github.com/Kotlin/kotlin-lsp#readme",
        Repository = "https://github.com/Kotlin/kotlin-lsp",
        Pinning = LanguageServerPinning.Archive,
        Settings = [Init("Project JDK home", "defaultSdk", pathKind: "folder", help: "JDK for analysis and Maven/Gradle import. Blank follows the workspace .sdkmanrc or SDKMAN current; the bundled server runtime is separate.")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name is "pom.xml" or "settings.gradle" or "settings.gradle.kts" or "build.gradle" or "build.gradle.kts" or "workspace.json";
        },
        ProjectScope = _ => "JetBrains Kotlin imports Gradle/Maven JVM projects; dependencies require successful build import. Other project types depend on server support.",
        InstalledExecutable = "intellij-server",
        Artifacts = [
        .. new[]
        {
            ("macos-x86_64", "", ".sit", "zip", "e69e0c9d27b915b2db9ee692ec08d097df1a394d9f21190457ef199f2905f77e"),
            ("macos-aarch64", "-aarch64", ".sit", "zip", "ebef2e13cd4adc4ec9e04084b848000a3ec7a9d2917c64f269574ce2efe9ecad"),
            ("linux-x86_64", "", ".tar.gz", "tar.gz", "ab8ca4455dc2fc5fe1a24db2bccc46c104254d2c465155c4251ee65df8f3f7cc"),
            ("linux-aarch64", "-aarch64", ".tar.gz", "tar.gz", "50999901ef8bcfa1e58561b6a8d782a72dea5620fcf92a64130807f8924a56fc"),
            ("windows-x86_64", "", ".win.zip", "zip", "72148fa0832c2a45a4b26c55f56197d8d8886bcdb732b910607efc5e2693b9df"),
            ("windows-aarch64", "-aarch64", ".win.zip", "zip", "290f48cb564295239bb678bb66b1ebbd06755b14fd4890f3cdf822f41af011ef")
        }.Select(item => new LanguageServerArtifact(item.Item1,
            $"https://download.jetbrains.com/language-server/kotlin-server/263.6379.0/kotlin-server-263.6379.0{item.Item2}{item.Item3}", item.Item5, item.Item4,
            $"https://download.jetbrains.com/language-server/kotlin-server/263.6379.0/kotlin-server-263.6379.0{item.Item2}{item.Item3}.sha256", JetBrains))
        ],
        ProjectKind = manifest => Path.GetFileName(manifest) switch
        {
            "pom.xml" => "Maven project",
            var name when name.StartsWith("settings.gradle", StringComparison.Ordinal) => "Gradle build",
            var name when name.StartsWith("build.gradle", StringComparison.Ordinal) => "Gradle project",
            ".project" => "Eclipse project",
            _ => null
        }
    };

    private static readonly string[] JetBrains = ["download.jetbrains.com", "download-cdn.jetbrains.com"];
}
