using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Dart;

/// <summary>The Dart (Analysis Server) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class DartPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-dart";

    public static readonly PluginManifest Manifest = new(Id, "Dart (Analysis Server)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the Dart (Analysis Server) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.Dart.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "dart",
        Name = "Dart (Analysis Server)",
        Icon = LanguageServerIcon.Embedded(typeof(DartPlugin).Assembly, "Icon.svg"),
        Version = "toolchain",
        Executable = "dart",
        Arguments = ["language-server", "--protocol=lsp"],
        Extensions = [".dart"],
        Implementation = "Dart Analysis Server · Dart SDK",
        Source = "Installed Dart SDK · analysis server",
        Website = "https://dart.dev/tools/analysis",
        Repository = "https://github.com/dart-lang/sdk/tree/main/pkg/analysis_server",
        Pinning = LanguageServerPinning.None,
        PinningDescription = "No managed installation: runs the host SDK's executable only when allowed for this server.",
        Settings = [Init("Analyze only projects with open files", "onlyAnalyzeProjectsWithOpenFiles", "boolean"), Setting("Excluded analysis folders", "dart.analysisExcludedFolders", "lines", pathKind: "folder"), Setting("Include dependencies in symbol searches", "dart.includeDependenciesInWorkspaceSymbols", "boolean"), Setting("Report TODO comments", "dart.showTodos", "boolean")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name == "pubspec.yaml";
        },
        ProjectScope = _ => "Dart analysis contexts follow pubspec/package_config and SDK resolution; dependencies require successful package resolution.",
        OnMessage = (session, method, parameters) =>
        {
            if (method == "$/analyzerStatus") session.SetProgress("dart-analysis", parameters?["isAnalyzing"]?.GetValue<bool>() == true ? "Resolving Dart analysis contexts…" : null);
        },
        ProjectKind = _ => "Dart package"
    };
}