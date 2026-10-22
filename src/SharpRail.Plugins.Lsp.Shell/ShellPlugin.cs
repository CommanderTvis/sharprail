using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Shell;

/// <summary>The Shell (Bash LS) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class ShellPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-shell";

    public static readonly PluginManifest Manifest = new(Id, "Shell (Bash LS)", "code-s-slash-line", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the Shell (Bash LS) language server to code intelligence.",
        EnabledByDefault = true,
        Host = "SharpRail.Plugins.Lsp.Shell.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "shell",
        Name = "Shell (Bash LS)",
        Icon = LanguageServerIcon.Embedded(typeof(ShellPlugin).Assembly, "Icon.png"),
        Version = "5.8.1",
        Executable = "bash-language-server",
        Arguments = ["start"],
        Extensions = [".sh", ".bash", ".zsh"],
        FileNames = [".bashrc", ".zshrc"],
        Aliases = ["bash", "sh"],
        Implementation = "Bash Language Server · bash-lsp project",
        Website = "https://github.com/bash-lsp/bash-language-server#readme",
        Repository = "https://github.com/bash-lsp/bash-language-server",
        Pinning = LanguageServerPinning.Lockfile,
        HostsRuntime = true,
        NpmPackages = "bash-language-server@5.8.1",
        LockFiles = Locks("package-lock.json", "package.json"),
        Settings = [Setting("Workspace scan pattern", "bashIde.globPattern"), Setting("Background analysis file limit", "bashIde.backgroundAnalysisMaxFiles", "integer", minimum: 0), Setting("Include workspace symbols", "bashIde.includeAllWorkspaceSymbols", "boolean"), Setting("ShellCheck executable", "bashIde.shellcheckPath", pathKind: "file")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name == ".bash-language-serverrc";
        },
        ProjectScope = _ => "Bash language server follows sourced files and bounded background discovery; scan limits and dynamic source paths can leave references incomplete.",
        LanguageId = _ => "shellscript"
    };

    private static Dictionary<string, string> Locks(params string[] files) => files.ToDictionary(file => file, file =>
    {
        using var stream = typeof(ShellPlugin).Assembly.GetManifestResourceStream("Locks/" + file) ?? throw new InvalidOperationException($"{Id} is built without Locks/{file}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });
}