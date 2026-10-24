using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Lsp.IntelliJ;

/// <summary>The Java / Kotlin (IntelliJ) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class IntelliJPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-intellij";

    public static readonly PluginManifest Manifest = new(Id, "Java / Kotlin (IntelliJ)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Jvm,
        Description = "Provides the Java / Kotlin (IntelliJ) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.IntelliJ.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "intellij",
        Name = "Java / Kotlin (IntelliJ)",
        Icon = LanguageServerIcon.Embedded(typeof(IntelliJPlugin).Assembly, "Icon.svg"),
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
        HostsRuntime = true,
        AllowsOverride = false,
        ReservedArguments = ["--stdio", "--eula", "--system-path"],
        OptionGroups = LanguageServerOptionGroups.Installation | LanguageServerOptionGroups.JvmProject,
        Replaces = ["java", "kotlin"],
        ReplacesLabel = "Java / Kotlin",
        TermsAsync = async (installation, prepare, token) => await IntelliJTerms.ReadAsync(
            prepare ? await IntelliJDistribution.PrepareAsync(installation, token) : IntelliJDistribution.Existing(installation), token),
        LaunchAsync = (installation, _) => Task.FromResult(IntelliJDistribution.Launch(installation)),
        TermsNote = "Uses JetBrains’ standalone server for Java and Kotlin. Download or select an installation, then review and accept its EULA before starting. " +
            "The current build is evaluation-only EAP; JetBrains enforces its validity. Terms: JetBrains LSP Extension Public EAP Agreement, version 1.0 (July 31, 2026). " +
            "Internal evaluation only; ends at the earliest vendor-defined EAP termination. Terms acceptance is not an activation credential and cannot extend a build’s lifetime.",
        ExpiredExitCode = 7,
        ExpiredDetail = IntelliJDistribution.Expired,
        OnExpired = IntelliJDistribution.RecordExpiry,
        ConfigureInitialization = JvmInitialization.Configure,
        PrimeWorkspaceAsync = (prime, cancellationToken) => Task.Run(() => IntelliJIndexPrimer.Prime(prime.SourceState, prime.TargetState, cancellationToken), cancellationToken),
        InitializeTimeout = TimeSpan.FromMinutes(15),
        InitializeProgressToken = "sharprail-intellij-init",
        SourceSchemes = ["jar:", "jrt:"],
        ReadSourceAsync = ReadSourceAsync,
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

    /// <summary>The pinned IntelliJ build.</summary>
    public const string IntelliJVersion = IntelliJDistribution.Version;

    /// <summary>Public IntelliJ client protocol: the URI is preserved exactly, including the JAR entry and JDK module.</summary>
    private static async Task<LanguageServerSource> ReadSourceAsync(ILanguageServerSession session, string uri, CancellationToken token)
    {
        if (!session.SupportsCommand("decompile")) throw new IOException("The server does not advertise dependency source retrieval.");
        var response = await session.RequestAsync("workspace/executeCommand", new { command = "decompile", arguments = new[] { uri } }, token);
        if (response?["code"] is not System.Text.Json.Nodes.JsonValue code || !code.TryGetValue<string>(out var text)) throw new IOException("The server returned no dependency source.");
        if (text.Length > 1_048_576) throw new IOException("Dependency source exceeds the 1 Mi character limit.");
        return new(text, response["language"]?.ToString() ?? "java");
    }
}