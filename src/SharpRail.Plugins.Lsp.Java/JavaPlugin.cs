using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Java;

/// <summary>The Java (Eclipse JDT) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class JavaPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-java";

    public static readonly PluginManifest Manifest = new(Id, "Java (Eclipse JDT)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Jvm,
        Description = "Provides the Java (Eclipse JDT) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.Java.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "java",
        Name = "Java (Eclipse JDT)",
        Icon = LanguageServerIcon.Embedded(typeof(JavaPlugin).Assembly, "Icon.svg"),
        Version = "1.61.0",
        Executable = "jdtls",
        Extensions = [".java"],
        Implementation = "Eclipse JDT Language Server · Eclipse Foundation",
        Source = "Eclipse · download.eclipse.org",
        Website = "https://projects.eclipse.org/projects/eclipse.jdt.ls",
        Repository = "https://github.com/eclipse-jdtls/eclipse.jdt.ls",
        Pinning = LanguageServerPinning.Archive,
        Dependencies = [new("java", "a JDK 21 or newer", "Install a JDK 21 or newer (sdkman on macOS) and set JAVA_HOME.", HomeVariable: "JAVA_HOME")],
        HostsRuntime = true,
        Settings = [Setting("Project JDK home", "java.home", pathKind: "folder"), Setting("Gradle importer JDK", "java.import.gradle.java.home", pathKind: "folder"), Setting("Additional library JARs", "java.project.referencedLibraries", "lines", pathKind: "file", help: "JAR paths and glob patterns. Advanced JSON also accepts an include/exclude/sources object."), Setting("Build configuration updates", "java.configuration.updateBuildConfiguration", choices: ["automatic", "interactive", "disabled"]), Setting("Import Maven projects", "java.import.maven.enabled", "boolean"), Setting("Import Gradle projects", "java.import.gradle.enabled", "boolean")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name is "pom.xml" or "settings.gradle" or "settings.gradle.kts" or "build.gradle" or "build.gradle.kts" or ".project" or ".classpath";
        },
        ProjectScope = _ => "JDT imports Maven/Gradle/Eclipse project models; standalone files use an inferred project. Dependencies require successful import and a compatible JDK.",
        Artifacts = [
        new LanguageServerArtifact("any", "https://download.eclipse.org/jdtls/milestones/1.61.0/jdt-language-server-1.61.0-202609031315.tar.gz",
            "338e7e73d61836651ba2453919a0d34fa763eb4e7c03342092309bffb8934c64", "tar.gz", "https://download.eclipse.org/jdtls/milestones/1.61.0/jdt-language-server-1.61.0-202609031315.tar.gz.sha256", Eclipse)
        ],
        LaunchAsync = LaunchAsync,
        ConfigureLaunch = (installation, launch) =>
        {
            if (!launch.Arguments.Contains("-data")) { launch.Arguments.Add("-data"); launch.Arguments.Add(installation.WorkspaceState); }
        },
        SourceRootsAsync = SourceRootsAsync,
        ProjectKind = manifest => Path.GetFileName(manifest) switch
        {
            "pom.xml" => "Maven project",
            var name when name.StartsWith("settings.gradle", StringComparison.Ordinal) => "Gradle build",
            var name when name.StartsWith("build.gradle", StringComparison.Ordinal) => "Gradle project",
            ".project" => "Eclipse project",
            _ => null
        }
    };

    private static readonly string[] Eclipse = ["download.eclipse.org"];

    private static Task<LanguageServerLaunch> LaunchAsync(ILanguageServerInstallation installation, CancellationToken token)
    {
        var java = installation.RequireTool("java");
        var launcher = Directory.GetFiles(Path.Combine(installation.Root, "plugins"), "org.eclipse.equinox.launcher_*.jar").Single();
        var configOs = OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsWindows() ? "win" : "linux";
        var configuration = Path.Combine(installation.WorkspaceState, "configuration");
        Directory.CreateDirectory(configuration);
        var template = Path.Combine(installation.Root, "config_" + configOs);
        foreach (var file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(configuration, Path.GetRelativePath(template, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            if (!File.Exists(destination)) File.Copy(file, destination);
        }
        return Task.FromResult(new LanguageServerLaunch(java,
            ["-Declipse.application=org.eclipse.jdt.ls.core.id1", "-Dosgi.bundles.defaultStartLevel=4", "-Declipse.product=org.eclipse.jdt.ls.core.product", "-Xmx1G", "--add-modules=ALL-SYSTEM", "--add-opens", "java.base/java.util=ALL-UNNAMED", "--add-opens", "java.base/java.lang=ALL-UNNAMED", "-jar", launcher, "-configuration", configuration, "-data", installation.WorkspaceState]));
    }

    private static async Task<IReadOnlyList<string>> SourceRootsAsync(ILanguageServerSession session, CancellationToken token)
    {
        const string command = "java.project.listSourcePaths";
        if (!session.SupportsCommand(command)) return [];
        var response = await session.RequestAsync("workspace/executeCommand", new { command, arguments = Array.Empty<string>() }, token);
        if (response?["status"]?.GetValue<bool>() != true || response["data"] is not System.Text.Json.Nodes.JsonArray paths) return [];
        return [.. paths.Take(500).Select(item => item?["path"]?.ToString() ?? "").Where(Path.IsPathFullyQualified)];
    }
}