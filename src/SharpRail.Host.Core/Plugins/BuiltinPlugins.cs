using SharpRail.Plugins.AirContext;
using SharpRail.Plugins.AirContext.Host;
using SharpRail.Plugins.AirGovernance;
using SharpRail.Plugins.AirGovernance.Host;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.Plugins.Blueprint;
using SharpRail.Plugins.Blueprint.Host;
using SharpRail.Plugins.BranchGraph;
using SharpRail.Plugins.BranchGraph.Host;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.Host;
using SharpRail.Plugins.Codex;
using SharpRail.Plugins.Codex.Host;
using SharpRail.Plugins.Discord;
using SharpRail.Plugins.Discord.Host;
using SharpRail.Plugins.Env;
using SharpRail.Plugins.Env.Host;
using SharpRail.Plugins.EnvDirenv;
using SharpRail.Plugins.EnvDirenv.Host;
using SharpRail.Plugins.EnvJavaScript;
using SharpRail.Plugins.EnvJavaScript.Host;
using SharpRail.Plugins.EnvJvm;
using SharpRail.Plugins.EnvJvm.Host;
using SharpRail.Plugins.EnvMise;
using SharpRail.Plugins.EnvMise.Host;
using SharpRail.Plugins.EnvPython;
using SharpRail.Plugins.EnvPython.Host;
using SharpRail.Plugins.Epub;
using SharpRail.Plugins.Epub.Host;
using SharpRail.Plugins.GitHub;
using SharpRail.Plugins.GitHub.Host;
using SharpRail.Plugins.Lsp.Clangd;
using SharpRail.Plugins.Lsp.CSharp;
using SharpRail.Plugins.Lsp.Css;
using SharpRail.Plugins.Lsp.Dart;
using SharpRail.Plugins.Lsp.Go;
using SharpRail.Plugins.Lsp.IntelliJ;
using SharpRail.Plugins.Lsp.Java;
using SharpRail.Plugins.Lsp.Kotlin;
using SharpRail.Plugins.Lsp.Php;
using SharpRail.Plugins.Lsp.PowerShell;
using SharpRail.Plugins.Lsp.Python;
using SharpRail.Plugins.Lsp.R;
using SharpRail.Plugins.Lsp.Ruby;
using SharpRail.Plugins.Lsp.Rust;
using SharpRail.Plugins.Lsp.Scala;
using SharpRail.Plugins.Lsp.Shell;
using SharpRail.Plugins.Lsp.Swift;
using SharpRail.Plugins.Lsp.TypeScript;
using SharpRail.Plugins.Omp;
using SharpRail.Plugins.Omp.Host;
using SharpRail.Plugins.SpecDialect;
using SharpRail.Plugins.Tailscale;
using SharpRail.Plugins.Tailscale.Host;
using SharpRail.Plugins.Visualize;
using SharpRail.Plugins.Visualize.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>The plugins that ship with the host, each a manifest and its host half (null for a manifest-only plugin).</summary>
public static class BuiltinPlugins
{
    public static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> All => Create(null);

    /// <summary>The language server plugins (H21), one per server, in the order file ownership is decided: Java and Kotlin before IntelliJ.</summary>
    public static IReadOnlyList<LanguageServerPlugin> LanguageServers =>
    [
        new TypeScriptPlugin(),
        new PythonPlugin(),
        new PhpPlugin(),
        new CssPlugin(),
        new ShellPlugin(),
        new CSharpPlugin(),
        new ClangdPlugin(),
        new JavaPlugin(),
        new KotlinPlugin(),
        new IntelliJPlugin(),
        new RubyPlugin(),
        new GoPlugin(),
        new RustPlugin(),
        new ScalaPlugin(),
        new RPlugin(),
        new PowerShellPlugin(),
        new SwiftPlugin(),
        new DartPlugin()
    ];

    /// <summary>The language server plugins as roster entries, for a host that runs only these builtins.</summary>
    public static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> LanguageServerEntries =>
        [.. LanguageServers.Select(plugin => (Manifest(plugin), (PluginHostModule?)plugin))];

    private static PluginManifest Manifest(LanguageServerPlugin plugin) => plugin switch
    {
        TypeScriptPlugin => TypeScriptPlugin.Manifest,
        PythonPlugin => PythonPlugin.Manifest,
        PhpPlugin => PhpPlugin.Manifest,
        CssPlugin => CssPlugin.Manifest,
        ShellPlugin => ShellPlugin.Manifest,
        CSharpPlugin => CSharpPlugin.Manifest,
        ClangdPlugin => ClangdPlugin.Manifest,
        JavaPlugin => JavaPlugin.Manifest,
        KotlinPlugin => KotlinPlugin.Manifest,
        IntelliJPlugin => IntelliJPlugin.Manifest,
        RubyPlugin => RubyPlugin.Manifest,
        GoPlugin => GoPlugin.Manifest,
        RustPlugin => RustPlugin.Manifest,
        ScalaPlugin => ScalaPlugin.Manifest,
        RPlugin => RPlugin.Manifest,
        PowerShellPlugin => PowerShellPlugin.Manifest,
        SwiftPlugin => SwiftPlugin.Manifest,
        DartPlugin => DartPlugin.Manifest,
        _ => throw new ArgumentOutOfRangeException(nameof(plugin))
    };

    public static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> Create(TailscaleServing? tailscale) =>
    [
        (SpecDialectManifest.Manifest, new SpecDialectHost()),
        (BlueprintContract.Manifest, new BlueprintHost()),
        (ClaudeCodeManifest.Manifest, new ClaudeCodeHost()),
        (DiscordPlugin.Manifest, new DiscordHost()),
        (BranchGraphPlugin.Manifest, new BranchGraphHost()),
        (VisualizeContract.Manifest, new VisualizeHost()),
        (CodexManifest.Manifest, new CodexHost()),
        (OmpManifest.Manifest, new OmpHost()),
        (AirGovernanceContract.Manifest, new AirGovernanceHost()),
        (AirContextContract.Manifest, new AirContextHost()),
        (TailscaleContract.Manifest, new TailscaleHost(tailscale)),
        (EpubContract.Manifest, new EpubHost()),
        (GitHubContract.Manifest, new GitHubHost()),
        (EnvContract.Manifest, new EnvHost()),
        (EnvMiseContract.Manifest, new EnvMiseHost()),
        (EnvDirenvContract.Manifest, new EnvDirenvHost()),
        (EnvPythonContract.Manifest, new EnvPythonHost()),
        (EnvJavaScriptContract.Manifest, new EnvJavaScriptHost()),
        (EnvJvmContract.Manifest, new EnvJvmHost()),
        .. LanguageServerEntries
    ];
}