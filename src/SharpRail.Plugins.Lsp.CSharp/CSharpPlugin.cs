using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

using static SharpRail.Plugins.Api.Host.LanguageSettingField;

namespace SharpRail.Plugins.Lsp.CSharp;

/// <summary>The C# (Roslyn) language server, as a builtin plugin that is enabled by default.</summary>
public sealed class CSharpPlugin() : LanguageServerPlugin(Id)
{
    public const string Id = "lsp-csharp";

    public static readonly PluginManifest Manifest = new(Id, "C# (Roslyn)", "asset:Icon.png", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.CSharp,
        Description = "Provides the C# (Roslyn) language server to code intelligence.",
        EnabledByDefault = true,
        Assets = "assets",
        Host = "SharpRail.Plugins.Lsp.CSharp.dll"
    };

    public override LanguageServerSpec Spec { get; } = new()
    {
        Id = "csharp",
        Name = "C# (Roslyn)",
        Icon = LanguageServerIcon.Embedded(typeof(CSharpPlugin).Assembly, "Icon.png"),
        Version = "5.13.0-1.26507.4",
        Executable = "roslyn-language-server",
        Arguments = ["--stdio", "--autoLoadProjects"],
        Extensions = [".cs"],
        Aliases = ["c#", "cs", "dotnet"],
        Implementation = "Roslyn Language Server · Microsoft",
        Source = "NuGet · roslyn-language-server (.NET tool)",
        Website = "https://www.nuget.org/packages/roslyn-language-server",
        Repository = "https://github.com/dotnet/roslyn",
        Pinning = LanguageServerPinning.NuGet,
        Dependencies = [new("dotnet", ".NET SDK", "Install a .NET SDK from https://dot.net, or set DOTNET_ROOT.", HomeVariable: "DOTNET_ROOT")],
        PinningDescription = "Managed and pinned: exact version and a SHA-512 per NuGet package (tool and runtime pack) verified before a local-feed install; NuGet signature verification also applies.",
        Settings = [Init("Automatic project loading limit", "autoLoadProjects", "integer", minimum: 0, help: "Maximum projects to load automatically; 0 disables automatic loading. SharpRail defaults to 500.")],
        IsProjectManifest = file =>
        {
            var name = Path.GetFileName(file);
            var extension = Path.GetExtension(name);
            return extension is ".sln" or ".slnx" or ".csproj";
        },
        ProjectScope = _ => "Roslyn loads the selected solution/MSBuild graph; project initialization and requested restores must complete before navigation.",
        NuGetPackages = Packages,
        InstallAsync = InstallAsync,
        ConfigureLaunch = (installation, launch) =>
        {
            // Roslyn ships with server GC, which sizes heaps per core; workstation GC with conserve-memory cut one SharpRail.slnx server from
            // ~830 to ~515 MiB physical footprint without changing answers. A value the user exported is kept.
            launch.Environment.TryAdd("DOTNET_gcServer", "0");
            launch.Environment.TryAdd("DOTNET_GCConserveMemory", "7");
            if (installation.FindDotnet() is not { } dotnet) return;
            var sdk = Path.GetDirectoryName(dotnet)!;
            launch.Environment["DOTNET_ROOT"] = sdk;
            launch.Environment["PATH"] = sdk + Path.PathSeparator + (launch.Environment.TryGetValue("PATH", out var path) ? path : "");
        },
        LoadsProjects = options => options["autoLoadProjects"]?.GetValue<int>() != 0,
        ProjectLoadedNotification = "workspace/projectInitializationComplete",
        // Roslyn logs this at the end of every project (re)load batch, including those that watched project files or restore outputs start.
        ProjectReloadedMessage = "[LanguageServerProjectSystem] Completed (re)load of",
        IsProjectInput = path => Path.GetFileName(path) == "project.assets.json" || Path.GetExtension(path) is ".csproj" or ".props" or ".targets" or ".sln" or ".slnx",
        // Avalonia views are AdditionalFiles from a glob; Roslyn watches only C#, Razor and project inputs, so a new view reloads its project.
        ReloadingExtensions = [".axaml"],
        OnMessage = OnMessage,
        ProjectKind = manifest => Path.GetExtension(manifest) == ".csproj" ? "MSBuild project" : "MSBuild solution",
        DefaultInitialization = options => options.TryAdd("autoLoadProjects", 500)
    };

    private const string RoslynVersion = "5.13.0-1.26507.4";

    /// <summary>The roslyn-language-server packages: the pointer package for every platform, then each platform's runtime pack. SHA-512 values are the NuGet catalog <c>packageHash</c> of each package.</summary>
    private static readonly LanguageServerNuGetPackage[] Packages =
    [
        new("roslyn-language-server", RoslynVersion, "any", "2a8d6137b84bacd70c1ac7aedfcd07d824fd6c9b534a6df11205561cd09233516d1506caa50d61c01bbbcb8d86b951af1464b6a3ce8c33bea68e007f4c8b14a2"),
        new("roslyn-language-server.osx-arm64", RoslynVersion, "macos-aarch64", "bce48bacb85855e5514f08264e8d0fa80fc70b4ae5ad032ab5528982f3c7ac24185e65935d44cba55e16918ae5927257b9afa3eead752f8f849d5f7c9c4151c8"),
        new("roslyn-language-server.osx-x64", RoslynVersion, "macos-x86_64", "c42e3763f10a410bb8010a46a18815fcc08adf1c654dbb5d208bf1ea133bb2331b05fafadb86aa477b0b479e6c036530b099288646840607079bf19e3c4ff787"),
        new("roslyn-language-server.linux-x64", RoslynVersion, "linux-x86_64", "3254f932367167bf4c523e33a86b1430d68c6defce2a8643c5ffd30d642a3268167a9d58619bfe8ff2b01be3c6d2def1c98d007fddb5b8e25fc61db7ccd21d59"),
        new("roslyn-language-server.linux-arm64", RoslynVersion, "linux-aarch64", "3c12c729d46b8edfa8ff0f07a2fc0714633c61b3bce44fc34b371ae64c753bbda01312468816524ad6172331f53b31ea3b0a43dcccd74935200b00b94499587a"),
        new("roslyn-language-server.win-x64", RoslynVersion, "windows-x86_64", "78c65815370a89cf0fa5c2293f45b16e9523e1debf0a9ad6963d7baf4a1711867c5e6d4e38d9d58a973bfcd386d3742dddd636f569bf542da2881f5609e8e3ad"),
        new("roslyn-language-server.win-arm64", RoslynVersion, "windows-aarch64", "2c4693fba6baa715ab7295a5c7400b283a320fbb9cbcd9f1c2b3181eb965e91a4c9dc30c60bc5cfda5e5a94f2d2b5ca18bc5ee86a81a467bb7d2a10ac4953b1d")
    ];

    private static async Task InstallAsync(ILanguageServerInstallation installation, CancellationToken token)
    {
        var dotnet = installation.RequireTool("dotnet");
        if (Packages.All(package => package.Platform != installation.Platform))
            throw new IOException($"csharp needs an installation: there is no verified NuGet runtime pack for {installation.Platform}. Allow PATH for this server in Settings > Code Intelligence, or configure an executable.");
        // Only packages whose SHA-512 matches the pinned value reach dotnet: it reads a local feed and a private package cache, never nuget.org.
        var feed = installation.Root + ".feed";
        try
        {
            if (Directory.Exists(feed)) Directory.Delete(feed, true);
            Directory.CreateDirectory(feed);
            foreach (var package in Packages.Where(package => package.Platform is "any" || package.Platform == installation.Platform))
                await installation.FetchNuGetAsync(package, feed, token);
            await installation.RunAsync(dotnet, ["tool", "install", "roslyn-language-server", "--version", RoslynVersion, "--tool-path", installation.Root, "--source", feed],
                installation.Root, new Dictionary<string, string?> { ["NUGET_PACKAGES"] = Path.Combine(feed, "packages") }, token);
        }
        finally { if (Directory.Exists(feed)) Directory.Delete(feed, true); }
    }

    /// <summary>Roslyn asks for a restore of projects whose assets are missing; each distinct set of workspace projects is restored once.</summary>
    private static void OnMessage(ILanguageServerSession session, string method, JsonNode? parameters)
    {
        if (method != "workspace/_roslyn_projectNeedsRestore") return;
        var paths = (parameters?["projectFilePaths"] as JsonArray ?? []).Select(value => value!.GetValue<string>())
            .Where(path => Path.IsPathFullyQualified(path) && session.Contains(path)).Distinct().ToArray();
        if (paths.Length == 0) return;
        var key = string.Join("\n", paths.Order(StringComparer.Ordinal));
        session.Track(key, "project restore", token => session.RequestAsync("workspace/_roslyn_restore", new { projectFilePaths = paths }, token, TimeSpan.FromMinutes(3)));
    }
}