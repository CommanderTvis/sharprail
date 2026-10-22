using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.TypeScript;

/// <summary>The JavaScript / TypeScript (TypeScript LS) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class TypeScriptPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-typescript";

    public static readonly PluginManifest Manifest = new(Id, "JavaScript / TypeScript (TypeScript LS)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.JavaScript,
        Description = "Provides the JavaScript / TypeScript (TypeScript LS) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.TypeScript.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "typescript",
        Name = "JavaScript / TypeScript (TypeScript LS)",
        Icon = LanguageServerIcon.Embedded(typeof(TypeScriptPlugin).Assembly, "Icon.svg"),
        Version = "6.0.2",
        Executable = "typescript-language-server",
        Arguments = ["--stdio"],
        Extensions = [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts"],
        Aliases = ["javascript", "js", "ts"],
        Implementation = "TypeScript Language Server · typescript-language-server project; Microsoft TypeScript engine",
        Website = "https://github.com/typescript-language-server/typescript-language-server#readme",
        Repository = "https://github.com/typescript-language-server/typescript-language-server",
        Pinning = LanguageServerPinning.Lockfile,
        Dependencies = [new("node", "Node.js", "Install Node.js 18 or newer from https://nodejs.org or your package manager."), new("npm", "npm", "npm ships with Node.js: install it from https://nodejs.org or your package manager.", InstallOnly: true)],
        HostsRuntime = true,
        NpmPackages = "typescript-language-server@6.0.2 typescript@5.9.3",
        LockFiles = Locks("package-lock.json", "package.json"),
        Settings = [Init("TypeScript server memory (MiB)", "maxTsServerMemory", "integer", minimum: 1), Init("TypeScript engine path", "tsserver.path", pathKind: "fileOrFolder", help: "Path to tsserver.js on the workspace host; blank uses the managed engine or project installation."), Init("Server log level", "tsserver.logVerbosity", choices: ["off", "terse", "normal", "requestTime", "verbose"])],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name is "tsconfig.json" or "jsconfig.json" or "package.json";
        },
        ProjectScope = _ => "TypeScript resolves tsconfig/jsconfig project references, paths and package imports; files excluded by their configuration are outside that project graph.",
        LanguageId = path => Path.GetExtension(path) switch { ".js" or ".mjs" or ".cjs" => "javascript", ".jsx" => "javascriptreact", ".tsx" => "typescriptreact", _ => null },
        AcceptsPathExecutable = HasTypeScript,
        ProjectKind = manifest => Path.GetFileName(manifest) == "package.json" ? "Node package" : "TypeScript project"
    };

    private static Dictionary<string, string> Locks(params string[] files) => files.ToDictionary(file => file, file =>
    {
        using var stream = typeof(TypeScriptPlugin).Assembly.GetManifestResourceStream("Locks/" + file) ?? throw new InvalidOperationException($"{Id} is built without Locks/{file}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    /// <summary>Whether a PATH server can reach a TypeScript engine: the workspace's own, or one installed beside the executable.</summary>
    internal static bool HasTypeScript(string executable, string workspace)
    {
        var resolved = File.ResolveLinkTarget(executable, returnFinalTarget: true)?.FullName ?? executable;
        foreach (var origin in new[] { workspace, Path.GetDirectoryName(resolved)! })
            for (var parent = new DirectoryInfo(origin); parent is not null; parent = parent.Parent)
            {
                var library = Path.Combine(parent.FullName, "node_modules", "typescript", "lib");
                if (File.Exists(Path.Combine(library, "tsserver.js")) && File.Exists(Path.Combine(library, "typescript.js")) &&
                    File.Exists(Path.Combine(library, "..", "package.json"))) return true;
            }
        return false;
    }
}