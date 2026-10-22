using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Rust;

/// <summary>The Rust (rust-analyzer) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class RustPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-rust";

    public static readonly PluginManifest Manifest = new(Id, "Rust (rust-analyzer)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Rust,
        Description = "Provides the Rust (rust-analyzer) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.Rust.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "rust",
        Name = "Rust (rust-analyzer)",
        Icon = LanguageServerIcon.Embedded(typeof(RustPlugin).Assembly, "Icon.svg"),
        Version = "2026-10-12",
        Executable = "rust-analyzer",
        Extensions = [".rs"],
        Aliases = ["rs"],
        Implementation = "rust-analyzer · Rust project",
        Source = "GitHub releases · rust-lang/rust-analyzer",
        Website = "https://rust-analyzer.github.io/",
        Repository = "https://github.com/rust-lang/rust-analyzer",
        Pinning = LanguageServerPinning.Archive,
        Settings = [Setting("Cargo features", "rust-analyzer.cargo.features", "features", help: "Select all features or list individual Cargo feature names."), Setting("Check command", "rust-analyzer.check.command", choices: ["check", "clippy"], allowCustomChoices: true, help: "Cargo subcommand; check and clippy are common, but custom commands are supported."), Setting("Run checks on save", "rust-analyzer.checkOnSave", "boolean"), Setting("Enable procedural macros", "rust-analyzer.procMacro.enable", "boolean"), Setting("Run build scripts", "rust-analyzer.cargo.buildScripts.enable", "boolean")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name is "Cargo.toml" or "rust-project.json";
        },
        ProjectScope = _ => "rust-analyzer uses Cargo/rust-project metadata, build scripts and proc macros. Discovered manifests are linked unless explicitly configured.",
        Artifacts = [
        .. new[]
        {
            ("macos-aarch64", "aarch64-apple-darwin", "gz", "a2e45e828b37bfc4b507c2ca456e8b61096e0bdfbae225f3d33295546d1eeacf"),
            ("macos-x86_64", "x86_64-apple-darwin", "gz", "56a5698a2e0d495c3ec28155416db310c879f21a70f6f38db549166cf157cfab"),
            ("linux-aarch64", "aarch64-unknown-linux-gnu", "gz", "d7219d5d8f5537bb227b521157aebddfe7e4a90f4a85a74f31e53722f5691025"),
            ("linux-x86_64", "x86_64-unknown-linux-gnu", "gz", "040cab4e2abc490fadd95c3f6cbc60b941a78761dbc3acf0ec66fd7a97f31db3"),
            ("windows-aarch64", "aarch64-pc-windows-msvc", "zip", "b053585692c2848acfa8e9ddf5f85bdc3741f3b23c51b72717ba0b391910b5fe"),
            ("windows-x86_64", "x86_64-pc-windows-msvc", "zip", "77f7a907f0e6f179281bac38e2d08435c974db9a5b99de52c5c963aff52883ec")
        }.Select(item => new LanguageServerArtifact(item.Item1, $"https://github.com/rust-lang/rust-analyzer/releases/download/2026-10-12/rust-analyzer-{item.Item2}.{item.Item3}", item.Item4, item.Item3,
            "https://api.github.com/repos/rust-lang/rust-analyzer/releases/tags/2026-10-12 (asset digest)", GitHub))
        ],
        OnMessage = (session, method, parameters) =>
        {
            if (method == "experimental/serverStatus")
                session.SetProgress("rust-project", parameters?["quiescent"]?.GetValue<bool>() == true ? null : parameters?["message"]?.ToString() ?? "Loading Cargo project graph…");
        },
        ConfigureInitialization = (start, options) =>
        {
            if (options.ContainsKey("linkedProjects") || start.Manifests.Count == 0) return;
            var projects = start.Manifests.Any(path => path is "Cargo.toml" or "rust-project.json") ? start.Manifests.Where(path => path is "Cargo.toml" or "rust-project.json") : start.Manifests;
            options["linkedProjects"] = new JsonArray([.. projects.Select(path => (JsonNode?)JsonValue.Create(Path.Combine(start.Workspace, path)))]);
        },
        ProjectKind = _ => "Rust project"
    };

    private static readonly string[] GitHub = ["github.com", "objects.githubusercontent.com", "release-assets.githubusercontent.com"];
}