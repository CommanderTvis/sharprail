using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.Go;

/// <summary>The Go (gopls) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class GoPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-go";

    public static readonly PluginManifest Manifest = new(Id, "Go (gopls)", "asset:Icon.svg", "0.1.0", PluginApi.Generation, 1)
    {
        Description = "Provides the Go (gopls) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.Go.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "go",
        Name = "Go (gopls)",
        Icon = LanguageServerIcon.Embedded(typeof(GoPlugin).Assembly, "Icon.svg"),
        Version = "v0.23.0",
        Executable = "gopls",
        Extensions = [".go"],
        Aliases = ["golang"],
        Implementation = "gopls · Go project",
        Source = "Go modules · golang.org/x/tools/gopls",
        Website = "https://go.dev/gopls/",
        Repository = "https://github.com/golang/tools/tree/master/gopls",
        Pinning = LanguageServerPinning.GoModule,
        Dependencies = [new("go", "Go", "Install Go from https://go.dev/dl/ or your package manager.", InstallOnly: true)],
        PinningDescription = "Managed and pinned: exact gopls version with an embedded go.mod/go.sum (h1 hashes of the whole module graph), built read-only; Go's checksum database stays on.",
        LockFiles = Locks("go.mod", "go.sum"),
        Settings = [Setting("Build flags", "gopls.buildFlags", "lines", help: "One flag per line, for example -tags=integration."), Setting("Directory filters", "gopls.directoryFilters", "lines", help: "Ordered +include / -exclude directory patterns."), Setting("Staticcheck diagnostics", "gopls.staticcheck", "boolean"), Setting("Semantic tokens", "gopls.semanticTokens", "boolean")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            return name is "go.mod" or "go.work";
        },
        ProjectScope = _ => "gopls resolves go.work/go.mod and package build context; independent modules are supplied as separate workspace folders.",
        InstallAsync = InstallAsync,
        ConfigureLaunch = (installation, launch) => launch.Environment["GOPLSCACHE"] = installation.ServerCache,
        WorkspaceFolders = (workspace, manifests) => !manifests.Any(path => path is "go.work" or "go.mod") && manifests.Any(path => Path.GetFileName(path) == "go.mod")
            ? [.. manifests.Where(path => Path.GetFileName(path) == "go.mod").Select(path => Path.GetDirectoryName(Path.Combine(workspace, path))!).Distinct()]
            : [workspace],
        ProjectKind = manifest => Path.GetFileName(manifest) == "go.work" ? "Go workspace" : "Go module",
        DefaultInitialization = options => options.TryAdd("semanticTokens", true),
        DefaultSettings = settings =>
        {
            if (settings["gopls"] is not JsonObject gopls) settings["gopls"] = gopls = new JsonObject();
            gopls.TryAdd("semanticTokens", true);
        }
    };

    private static Dictionary<string, string> Locks(params string[] files) => files.ToDictionary(file => file, file =>
    {
        using var stream = typeof(GoPlugin).Assembly.GetManifestResourceStream("Locks/" + file) ?? throw new InvalidOperationException($"{Id} is built without Locks/{file}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static async Task InstallAsync(ILanguageServerInstallation installation, CancellationToken token)
    {
        // The embedded go.mod/go.sum pin gopls and its whole module graph by h1 hash; -mod=readonly refuses anything not in go.sum.
        var module = Path.Combine(installation.Root, "module");
        Directory.CreateDirectory(module);
        foreach (var (file, text) in Locks("go.mod", "go.sum")) await File.WriteAllTextAsync(Path.Combine(module, file), text, token);
        var environment = new Dictionary<string, string?>
        {
            ["GOBIN"] = installation.Root,
            ["GOMODCACHE"] = Path.Combine(installation.SharedCache, "go-modules"),
            ["GOFLAGS"] = "-mod=readonly",
            ["GOSUMDB"] = "sum.golang.org",
            ["GOPROXY"] = "https://proxy.golang.org",
            ["GOTOOLCHAIN"] = "local",
            ["GOENV"] = "off",
            ["GOWORK"] = "off"
        };
        // The user's own Go environment must not switch verification off (GONOSUMDB, GOPRIVATE, GOINSECURE, go.env files, workspaces).
        foreach (var variable in new[] { "GONOSUMDB", "GONOSUMCHECK", "GOPRIVATE", "GOINSECURE", "GONOPROXY" }) environment[variable] = "";
        await installation.RunAsync(installation.RequireTool("go"), ["install", "golang.org/x/tools/gopls"], module, environment, token);
    }
}