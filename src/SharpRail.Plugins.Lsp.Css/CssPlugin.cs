using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Css;

/// <summary>The CSS (VS Code) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class CssPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-css";

    public static readonly PluginManifest Manifest = new(Id, "CSS (VS Code)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the CSS (VS Code) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.Css.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "css",
        Name = "CSS (VS Code)",
        Icon = LanguageServerIcon.Embedded(typeof(CssPlugin).Assembly, "Icon.svg"),
        Version = "4.10.0",
        Executable = "vscode-css-language-server",
        Arguments = ["--stdio"],
        Extensions = [".css", ".scss", ".less"],
        Implementation = "VS Code CSS Language Server · Microsoft; vscode-langservers-extracted packaging",
        Website = "https://code.visualstudio.com/docs/languages/css",
        Repository = "https://github.com/microsoft/vscode-css-languageservice",
        Pinning = LanguageServerPinning.Lockfile,
        Dependencies = [new("node", "Node.js", "Install Node.js 18 or newer from https://nodejs.org or your package manager."), new("npm", "npm", "npm ships with Node.js: install it from https://nodejs.org or your package manager.", InstallOnly: true)],
        HostsRuntime = true,
        NpmPackages = "vscode-langservers-extracted@4.10.0",
        LockFiles = Locks("package-lock.json", "package.json"),
        Settings = [Setting("Validate CSS", "css.validate", "boolean"), Setting("Validate SCSS", "scss.validate", "boolean"), Setting("Validate Less", "less.validate", "boolean")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name == "package.json";
        },
        ProjectScope = _ => "CSS/SCSS/LESS language service has document-oriented semantics; a workspace folder does not create a general cross-file reference index.",
        LanguageId = path => Path.GetExtension(path) switch { ".scss" => "scss", ".less" => "less", _ => null }
    };

    private static Dictionary<string, string> Locks(params string[] files) => files.ToDictionary(file => file, file =>
    {
        using var stream = typeof(CssPlugin).Assembly.GetManifestResourceStream("Locks/" + file) ?? throw new InvalidOperationException($"{Id} is built without Locks/{file}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}