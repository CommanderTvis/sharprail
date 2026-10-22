using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Php;

/// <summary>The PHP (Intelephense) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class PhpPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-php";

    public static readonly PluginManifest Manifest = new(Id, "PHP (Intelephense)", "code-s-slash-line", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the PHP (Intelephense) language server to code intelligence.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.Lsp.Php.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "php",
        Version = "1.18.5",
        Executable = "intelephense",
        Arguments = ["--stdio"],
        Extensions = [".php"],
        Implementation = "Intelephense · Ben Mewburn",
        Website = "https://intelephense.com/",
        Repository = "https://github.com/bmewburn/intelephense-docs",
        RepositoryLabel = "Documentation repository",
        Pinning = LanguageServerPinning.Lockfile,
        HostsRuntime = true,
        NpmPackages = "intelephense@1.18.5",
        LockFiles = Locks("package-lock.json", "package.json"),
        Settings = [Setting("Library include directories", "intelephense.environment.includePaths", "lines", pathKind: "folder"), Setting("PHP language version", "intelephense.environment.phpVersion", help: "Semantic version, for example 8.3.0; provider supports PHP 5.3.0 and later."), Setting("Excluded file patterns", "intelephense.files.exclude", "lines"), Setting("Enable diagnostics", "intelephense.diagnostics.enable", "boolean")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name == "composer.json";
        },
        ProjectScope = _ => "Intelephense indexes the workspace; external dependencies use environment.includePaths. Feature availability also depends on the server license.",
        ProjectKind = _ => "Composer package"
    };

    private static Dictionary<string, string> Locks(params string[] files) => files.ToDictionary(file => file, file =>
    {
        using var stream = typeof(PhpPlugin).Assembly.GetManifestResourceStream("Locks/" + file) ?? throw new InvalidOperationException($"{Id} is built without Locks/{file}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}
