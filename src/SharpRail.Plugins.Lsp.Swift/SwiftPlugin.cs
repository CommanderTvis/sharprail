using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Swift;

/// <summary>The Swift (SourceKit-LS) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class SwiftPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-swift";

    public static readonly PluginManifest Manifest = new(Id, "Swift (SourceKit-LS)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Swift,
        Description = "Provides the Swift (SourceKit-LS) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.Swift.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "swift",
        Name = "Swift (SourceKit-LS)",
        Icon = LanguageServerIcon.Embedded(typeof(SwiftPlugin).Assembly, "Icon.svg"),
        Version = "toolchain",
        Executable = "sourcekit-lsp",
        Extensions = [".swift"],
        Implementation = "SourceKit-LSP · Swift project",
        Source = "Installed Swift toolchain · sourcekit-lsp",
        Website = "https://www.swift.org/documentation/",
        Repository = "https://github.com/swiftlang/sourcekit-lsp",
        Pinning = LanguageServerPinning.None,
        PinningDescription = "No managed installation: runs the host SDK's executable only when allowed for this server.",
        Settings = [Init("Build configuration", "swiftPM.configuration", choices: ["debug", "release"]), Init("Swift compiler flags", "swiftPM.swiftCompilerFlags", "lines"), Init("Compilation database search paths", "compilationDatabase.searchPaths", "lines", pathKind: "folder")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name);
            return name is "Package.swift" or "buildServer.json" || extension == ".xcodeproj";
        },
        ProjectScope = _ => "SourceKit-LSP uses SwiftPM/build-server metadata. Cross-module results require an up-to-date native index; older toolchains and Xcode projects may need additional build-server setup.",
        FindFallbackAsync = async (installation, token) => OperatingSystem.IsMacOS()
            ? (await installation.RunAsync("/usr/bin/xcrun", ["--find", "sourcekit-lsp"], Path.GetTempPath(), null, token)).Trim()
            : null,
        IsProjectDirectory = directory => Path.GetExtension(directory) is ".xcodeproj" or ".xcworkspace",
        ProjectKind = _ => "Swift project"
    };
}