using Tereditor.Plugins.Acp;
using Tereditor.Plugins.Acp.Host;
using Tereditor.Plugins.AirContext;
using Tereditor.Plugins.AirContext.Host;
using Tereditor.Plugins.AirGateway;
using Tereditor.Plugins.AirGateway.Host;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;
using Tereditor.Plugins.Blueprint;
using Tereditor.Plugins.Blueprint.Host;
using Tereditor.Plugins.BranchGraph;
using Tereditor.Plugins.BranchGraph.Host;
using Tereditor.Plugins.ClaudeCode;
using Tereditor.Plugins.ClaudeCode.Host;
using Tereditor.Plugins.CodeIntelligence;
using Tereditor.Plugins.CodeIntelligence.Host;
using Tereditor.Plugins.Codex;
using Tereditor.Plugins.Codex.Host;
using Tereditor.Plugins.Discord;
using Tereditor.Plugins.Discord.Host;
using Tereditor.Plugins.Env;
using Tereditor.Plugins.Env.Host;
using Tereditor.Plugins.EnvDirenv;
using Tereditor.Plugins.EnvDirenv.Host;
using Tereditor.Plugins.EnvJavaScript;
using Tereditor.Plugins.EnvJavaScript.Host;
using Tereditor.Plugins.EnvJvm;
using Tereditor.Plugins.EnvJvm.Host;
using Tereditor.Plugins.EnvMise;
using Tereditor.Plugins.EnvMise.Host;
using Tereditor.Plugins.EnvPython;
using Tereditor.Plugins.EnvPython.Host;
using Tereditor.Plugins.Epub;
using Tereditor.Plugins.Epub.Host;
using Tereditor.Plugins.GitHub;
using Tereditor.Plugins.GitHub.Host;
using Tereditor.Plugins.Lsp.Clangd;
using Tereditor.Plugins.Lsp.CSharp;
using Tereditor.Plugins.Lsp.Css;
using Tereditor.Plugins.Lsp.Dart;
using Tereditor.Plugins.Lsp.Go;
using Tereditor.Plugins.Lsp.IntelliJ;
using Tereditor.Plugins.Lsp.Java;
using Tereditor.Plugins.Lsp.Kotlin;
using Tereditor.Plugins.Lsp.Php;
using Tereditor.Plugins.Lsp.PowerShell;
using Tereditor.Plugins.Lsp.Python;
using Tereditor.Plugins.Lsp.R;
using Tereditor.Plugins.Lsp.Ruby;
using Tereditor.Plugins.Lsp.Rust;
using Tereditor.Plugins.Lsp.Scala;
using Tereditor.Plugins.Lsp.Shell;
using Tereditor.Plugins.Lsp.Swift;
using Tereditor.Plugins.Lsp.TypeScript;
using Tereditor.Plugins.Omp;
using Tereditor.Plugins.Omp.Host;
using Tereditor.Plugins.Pi;
using Tereditor.Plugins.Pi.Host;
using Tereditor.Plugins.Processes;
using Tereditor.Plugins.Processes.Host;
using Tereditor.Plugins.Providers;
using Tereditor.Plugins.Providers.Host;
using Tereditor.Plugins.Review;
using Tereditor.Plugins.Review.Host;
using Tereditor.Plugins.SpecDialect;
using Tereditor.Plugins.Tailscale;
using Tereditor.Plugins.Tailscale.Host;
using Tereditor.Plugins.Visualize;
using Tereditor.Plugins.Visualize.Host;

namespace Tereditor.Host.Core.Plugins;

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

    /// <summary>The Code Intelligence plugin and the language server plugins that require it, as roster entries, for a host that runs only these builtins.</summary>
    public static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> LanguageServerEntries =>
        [(CodeIntelligenceContract.Manifest, new CodeIntelligenceHost()), .. LanguageServers.Select(plugin => (Manifest(plugin), (PluginHostModule?)plugin))];

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

    /// <summary>The plugins a host with no window, terminal or agent runs: the builtins whose manifest declares <see cref="HeadlessSupport.Yes"/>.
    /// The roster follows the declaration, and the runtime of a headless host refuses any other plugin too.</summary>
    public static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> Headless =>
        [.. Create(null).Where(plugin => plugin.Manifest.Headless == HeadlessSupport.Yes)];

    public static IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> Create(TailscaleServing? tailscale) =>
    [
        (SpecDialectManifest.Manifest, new SpecDialectHost()),
        (BlueprintContract.Manifest, new BlueprintHost()),
        (ClaudeCodeManifest.Manifest, new ClaudeCodeHost()),
        (DiscordPlugin.Manifest, new DiscordHost()),
        (BranchGraphPlugin.Manifest, new BranchGraphHost()),
        (ReviewPlugin.Manifest, new ReviewHost()),
        (ProcessesPlugin.Manifest, new ProcessesHost()),
        (VisualizeContract.Manifest, new VisualizeHost()),
        (CodexManifest.Manifest, new CodexHost()),
        (OmpManifest.Manifest, new OmpHost()),
        (PiManifest.Manifest, new PiHost()),
        (AcpManifest.Manifest, new AcpHost()),
        (ProvidersManifest.Manifest, new ProvidersHost()),
        (AirGatewayContract.Manifest, new AirGatewayHost()),
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