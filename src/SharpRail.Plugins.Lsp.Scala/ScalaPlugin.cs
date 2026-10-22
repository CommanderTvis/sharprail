using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Scala;

/// <summary>The Scala (Metals) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class ScalaPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-scala";

    public static readonly PluginManifest Manifest = new(Id, "Scala (Metals)", "asset:Icon.png", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the Scala (Metals) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.Scala.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "scala",
        Name = "Scala (Metals)",
        Icon = LanguageServerIcon.Embedded(typeof(ScalaPlugin).Assembly, "Icon.png"),
        Version = "1.6.9",
        Executable = "metals",
        Extensions = [".scala", ".sc", ".sbt"],
        Implementation = "Metals · Scalameta",
        Source = "Maven repositories · org.scalameta:metals (Coursier)",
        Website = "https://scalameta.org/metals/",
        Repository = "https://github.com/scalameta/metals",
        Pinning = LanguageServerPinning.Partial,
        Dependencies = [new("cs", "Coursier", "Install Coursier (cs) from https://get-coursier.io.", InstallOnly: true)],
        PinningDescription = "Managed, partially pinned: exact Metals version; Coursier resolves dependencies at install without a stored hash.",
        Settings = [Setting("Build import", "metals.autoImportBuilds", choices: ["off", "initial", "all"]), Setting("Excluded symbol packages", "metals.excludedPackages", "lines")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name);
            return name is "build.sbt" or "build.sc" or "build.mill" or "pom.xml" || Path.GetFileName(Path.GetDirectoryName(file)) == ".bsp" && extension == ".json";
        },
        ProjectScope = _ => "Metals imports the build through BSP; build imports are enabled by default. Build-tool selection and dependency failures remain server-reported.",
        InstallAsync = (installation, token) => installation.RunAsync(installation.RequireTool("cs"),
            ["bootstrap", "org.scalameta:metals_2.13:" + MetalsVersion, "--main", "scala.meta.metals.Main", "-o", Path.Combine(installation.Root, "metals"), "--force"], installation.Root, null, token),
        ConfigureLaunch = (installation, launch) => launch.Environment["COURSIER_CACHE"] = installation.ServerCache,
        ProjectKind = _ => "Scala build",
        DefaultSettings = settings =>
        {
            if (settings["metals"] is not JsonObject metals) settings["metals"] = metals = new JsonObject();
            metals.TryAdd("autoImportBuilds", "all");
        }
    };

    private const string MetalsVersion = "1.6.9";
}