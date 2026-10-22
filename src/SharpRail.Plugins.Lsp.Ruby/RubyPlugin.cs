using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Ruby;

/// <summary>The Ruby (Ruby LS) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class RubyPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-ruby";

    public static readonly PluginManifest Manifest = new(Id, "Ruby (Ruby LS)", "asset:Icon.png", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the Ruby (Ruby LS) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.Ruby.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "ruby",
        Name = "Ruby (Ruby LS)",
        Icon = LanguageServerIcon.Embedded(typeof(RubyPlugin).Assembly, "Icon.png"),
        Version = "0.26.11",
        Executable = "ruby-lsp",
        Extensions = [".rb", ".rake", ".gemspec"],
        FileNames = ["Gemfile", "Rakefile"],
        Aliases = ["rb"],
        Implementation = "Ruby LSP · Shopify",
        Source = "RubyGems · ruby-lsp",
        Website = "https://shopify.github.io/ruby-lsp/",
        Repository = "https://github.com/Shopify/ruby-lsp",
        Pinning = LanguageServerPinning.Partial,
        Dependencies = [new("gem", "RubyGems", "Install Ruby, which includes gem, from https://www.ruby-lang.org or your package manager.", InstallOnly: true)],
        PinningDescription = "Managed, partially pinned: exact gem version; RubyGems does not verify a stored hash and dependencies are resolved at install.",
        Settings = [Init("Formatter", "formatter", choices: ["auto", "rubocop", "rubocop_internal", "syntax_tree", "none"], allowCustomChoices: true, help: "Formatter name; installed add-ons may supply additional formatters.")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name);
            return name is "Gemfile" or "Rakefile" || extension == ".gemspec";
        },
        ProjectScope = _ => "Ruby LSP resolves the workspace with its bundle/project index; the project Ruby and installed bundle determine available dependencies.",
        InstallAsync = (installation, token) => installation.RunAsync(installation.RequireTool("gem"),
            ["install", "ruby-lsp", "--version", GemVersion, "--install-dir", installation.Root, "--bindir", Path.Combine(installation.Root, "bin"), "--no-document"], installation.Root, null, token),
        LaunchAsync = (installation, _) => Task.FromResult(new LanguageServerLaunch(installation.FindInstalled("ruby-lsp"), [])
        {
            Environment = new Dictionary<string, string?> { ["GEM_HOME"] = installation.Root, ["GEM_PATH"] = installation.Root }
        }),
        InitializeTimeout = TimeSpan.FromMinutes(15),
        ProjectKind = _ => "Ruby project"
    };

    private const string GemVersion = "0.26.11";
}