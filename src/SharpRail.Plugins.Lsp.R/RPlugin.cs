using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.R;

/// <summary>The R (languageserver) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class RPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-r";

    public static readonly PluginManifest Manifest = new(Id, "R (languageserver)", "code-s-slash-line", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the R (languageserver) language server to code intelligence.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.Lsp.R.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "r",
        Name = "R (languageserver)",
        Icon = LanguageServerIcon.Embedded(typeof(RPlugin).Assembly, "Icon.svg"),
        Version = "0.3.17",
        Executable = "Rscript",
        Arguments = ["--vanilla", "-e", "languageserver::run()"],
        Extensions = [".r", ".R"],
        Implementation = "languageserver · REditorSupport",
        Source = "CRAN · languageserver",
        Website = "https://cran.r-project.org/package=languageserver",
        Repository = "https://github.com/REditorSupport/languageserver",
        Pinning = LanguageServerPinning.Partial,
        Dependencies = [new("Rscript", "R", "Install R from https://cran.r-project.org or your package manager.")],
        PinningDescription = "Managed, partially pinned: exact CRAN package version; CRAN publishes no verified hash and dependencies are resolved at install.",
        PathCapable = false,
        HostsRuntime = true,
        Settings = [Setting("Enable diagnostics", "r.lsp.diagnostics", "boolean"), Setting("Debug logging", "r.lsp.debug", "boolean"), Setting("Rich documentation", "r.lsp.rich_documentation", "boolean")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name);
            return name == "DESCRIPTION" || extension == ".Rproj";
        },
        ProjectScope = _ => "R languageserver resolves packages and statically sourced dependencies; workspace symbol discovery does not guarantee semantic references across unrelated scripts.",
        InstallAsync = (installation, token) => installation.RunAsync(installation.RequireTool("Rscript"),
            ["--vanilla", "-e", $"install.packages('remotes', repos='https://cloud.r-project.org', lib=Sys.getenv('R_LIBS_USER')); remotes::install_version('languageserver', version='{PackageVersion}', repos='https://cloud.r-project.org', lib=Sys.getenv('R_LIBS_USER'), upgrade='never'); stopifnot(requireNamespace('languageserver', quietly=TRUE), as.character(packageVersion('languageserver')) == '{PackageVersion}')"],
            installation.Root, new Dictionary<string, string?> { ["R_LIBS_USER"] = installation.Root }, token),
        Installed = root => File.Exists(Path.Combine(root, "languageserver", "DESCRIPTION")),
        LaunchAsync = (installation, _) => Task.FromResult(new LanguageServerLaunch(installation.RequireTool("Rscript"), ["--vanilla", "-e", "languageserver::run()"])
        {
            Environment = new Dictionary<string, string?> { ["R_LIBS_USER"] = installation.Root }
        }),
        ProjectKind = _ => "R project"
    };

    private const string PackageVersion = "0.3.17";
}