using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Python;

/// <summary>The Python (Pyright) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class PythonPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-python";

    public static readonly PluginManifest Manifest = new(Id, "Python (Pyright)", "asset:Icon.png", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the Python (Pyright) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.Python.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "python",
        Name = "Python (Pyright)",
        Icon = LanguageServerIcon.Embedded(typeof(PythonPlugin).Assembly, "Icon.png"),
        Version = "1.1.414",
        Executable = "pyright-langserver",
        Arguments = ["--stdio"],
        Extensions = [".py", ".pyi"],
        Aliases = ["py"],
        Implementation = "Pyright · Microsoft",
        Website = "https://microsoft.github.io/pyright/",
        Repository = "https://github.com/microsoft/pyright",
        Pinning = LanguageServerPinning.Lockfile,
        Dependencies = [new("node", "Node.js", "Install Node.js 18 or newer from https://nodejs.org or your package manager."), new("npm", "npm", "npm ships with Node.js: install it from https://nodejs.org or your package manager.", InstallOnly: true)],
        HostsRuntime = true,
        NpmPackages = "pyright@1.1.414",
        LockFiles = Locks("package-lock.json", "package.json"),
        Settings = [Setting("Python interpreter", "python.pythonPath", pathKind: "file"), Setting("Additional import directories", "python.analysis.extraPaths", "lines", pathKind: "folder"), Setting("Type checking", "python.analysis.typeCheckingMode", choices: ["off", "basic", "standard", "strict"]), Setting("Diagnostic scope", "python.analysis.diagnosticMode", choices: ["openFilesOnly", "workspace"]), Setting("Read library code for types", "python.analysis.useLibraryCodeForTypes", "boolean"), Setting("Custom type stubs", "python.analysis.stubPath", pathKind: "folder")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name is "pyrightconfig.json" or "pyproject.toml" or "setup.py" or "setup.cfg";
        },
        ProjectScope = _ => "Pyright resolves execution environments, interpreter/import paths and workspace configuration; diagnostic mode does not define reference search scope.",
        ProjectKind = _ => "Python project"
    };

    private static Dictionary<string, string> Locks(params string[] files) => files.ToDictionary(file => file, file =>
    {
        using var stream = typeof(PythonPlugin).Assembly.GetManifestResourceStream("Locks/" + file) ?? throw new InvalidOperationException($"{Id} is built without Locks/{file}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}