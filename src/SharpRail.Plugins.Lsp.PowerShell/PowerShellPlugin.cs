using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.PowerShell;

/// <summary>The PowerShell (Editor Services) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class PowerShellPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-powershell";

    public static readonly PluginManifest Manifest = new(Id, "PowerShell (Editor Services)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.PowerShell,
        Description = "Provides the PowerShell (Editor Services) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.PowerShell.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "powershell",
        Name = "PowerShell (Editor Services)",
        Icon = LanguageServerIcon.Embedded(typeof(PowerShellPlugin).Assembly, "Icon.svg"),
        Version = "4.7.0",
        Executable = "pwsh",
        Extensions = [".ps1", ".psm1", ".psd1"],
        Implementation = "PowerShell Editor Services · Microsoft",
        Source = "GitHub releases · PowerShell/PowerShellEditorServices",
        Website = "https://github.com/PowerShell/PowerShellEditorServices#readme",
        Repository = "https://github.com/PowerShell/PowerShellEditorServices",
        Pinning = LanguageServerPinning.Archive,
        Dependencies = [new("pwsh", "PowerShell 7", "Install PowerShell 7 from https://aka.ms/powershell or your package manager.")],
        PathCapable = false,
        HostsRuntime = true,
        Settings = [Setting("Enable script analysis", "powershell.scriptAnalysis.enable", "boolean"), Setting("Script analyzer settings file", "powershell.scriptAnalysis.settingsPath", pathKind: "file")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name);
            return name == "PSScriptAnalyzerSettings.psd1" || extension == ".psd1";
        },
        ProjectScope = _ => "PowerShell Editor Services resolves its workspace/scripts/modules; dynamic imports and external modules can limit cross-file references.",
        Artifacts = [
        new LanguageServerArtifact("any", "https://github.com/PowerShell/PowerShellEditorServices/releases/download/v4.7.0/PowerShellEditorServices.zip",
            "5084f0326cc88539e9d880b08f6c52ce5be4672bb8ecee8921e35f8895a6b9d7", "zip", "https://api.github.com/repos/PowerShell/PowerShellEditorServices/releases/tags/v4.7.0 (asset digest)", GitHub)
        ],
        LaunchAsync = (installation, _) => Task.FromResult(new LanguageServerLaunch(installation.RequireTool("pwsh"), ["-NoLogo", "-NoProfile", "-File",
            Directory.GetFiles(installation.Root, "Start-EditorServices.ps1", SearchOption.AllDirectories).Single(), "-Stdio", "-HostName", "SharpRail", "-HostProfileId", "SharpRail", "-HostVersion", "1.0.0", "-LogLevel", "Normal", "-LogPath", Path.Combine(installation.WorkspaceState, "server.log"), "-SessionDetailsPath", Path.Combine(installation.WorkspaceState, "session.json")]))
    };

    private static readonly string[] GitHub = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];
}