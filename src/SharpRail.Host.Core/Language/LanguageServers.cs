using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal sealed record LanguageServer(string Id, string Version, string Executable, string[] Arguments, string[] Extensions, string Package = "")
{
    internal string Implementation => Id switch
    {
        "typescript" => "TypeScript Language Server · typescript-language-server project; Microsoft TypeScript engine",
        "python" => "Pyright · Microsoft",
        "php" => "Intelephense · Ben Mewburn",
        "css" => "VS Code CSS Language Server · Microsoft; vscode-langservers-extracted packaging",
        "shell" => "Bash Language Server · bash-lsp project",
        "csharp" => "Roslyn Language Server · Microsoft",
        "clangd" => "clangd · LLVM project",
        "java" => "Eclipse JDT Language Server · Eclipse Foundation",
        "kotlin" => "Kotlin Language Server · JetBrains",
        "intellij" => "IntelliJ Java and Kotlin · JetBrains · optional evaluation provider",
        "ruby" => "Ruby LSP · Shopify",
        "go" => "gopls · Go project",
        "rust" => "rust-analyzer · Rust project",
        "scala" => "Metals · Scalameta",
        "r" => "languageserver · REditorSupport",
        "powershell" => "PowerShell Editor Services · Microsoft",
        "swift" => "SourceKit-LSP · Swift project",
        "dart" => "Dart Analysis Server · Dart SDK",
        _ => Id
    };
}

internal sealed partial class LanguageServers(string directory, Func<string>? configuration = null, HttpClient? managedDownloads = null)
{
    internal string DirectoryPath => directory;

    internal string Storage => directory;
    internal LanguageServerOptions? Options(string id)
    {
        var settings = configuration?.Invoke();
        return string.IsNullOrWhiteSpace(settings) ? null :
            JsonSerializer.Deserialize<Dictionary<string, LanguageServerOptions>>(settings, JsonSerializerOptions.Web)?.GetValueOrDefault(id);
    }
    internal static readonly LanguageServer[] Catalog =
    [
        new("typescript", "6.0.2", "typescript-language-server", ["--stdio"], [".js", ".jsx", ".mjs", ".cjs", ".ts", ".tsx", ".mts", ".cts"], "typescript-language-server@6.0.2 typescript@5.9.3"),
        new("python", "1.1.414", "pyright-langserver", ["--stdio"], [".py", ".pyi"], "pyright@1.1.414"),
        new("php", "1.18.5", "intelephense", ["--stdio"], [".php"], "intelephense@1.18.5"),
        new("css", "4.10.0", "vscode-css-language-server", ["--stdio"], [".css", ".scss", ".less"], "vscode-langservers-extracted@4.10.0"),
        new("shell", "5.8.1", "bash-language-server", ["start"], [".sh", ".bash", ".zsh"], "bash-language-server@5.8.1"),
        new("csharp", "5.13.0-1.26507.4", "roslyn-language-server", ["--stdio", "--autoLoadProjects"], [".cs"]),
        new("clangd", "23.1.0", "clangd", ["--background-index"], [".c", ".h", ".cc", ".cpp", ".cxx", ".hpp", ".m", ".mm"]),
        new("java", "1.61.0", "jdtls", [], [".java"]),
        new("kotlin", "263.6379.0", "kotlin-lsp", ["--stdio"], [".kt", ".kts"]),
        new("intellij", IntelliJDistribution.Version, "intellij-server", ["--stdio"], [".java", ".kt", ".kts"]),
        new("ruby", "0.26.11", "ruby-lsp", [], [".rb", ".rake", ".gemspec"]),
        new("go", "v0.23.0", "gopls", [], [".go"]),
        new("rust", "2026-10-12", "rust-analyzer", [], [".rs"]),
        new("scala", "1.6.9", "metals", [], [".scala", ".sc", ".sbt"]),
        new("r", "0.3.17", "Rscript", ["--vanilla", "-e", "languageserver::run()"], [".r", ".R"]),
        new("powershell", "4.7.0", "pwsh", [], [".ps1", ".psm1", ".psd1"]),
        new("swift", "toolchain", "sourcekit-lsp", [], [".swift"]),
        new("dart", "toolchain", "dart", ["language-server", "--protocol=lsp"], [".dart"])
    ];
    internal static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };
    private readonly ConcurrentDictionary<string, SemaphoreSlim> installations = new();
    private readonly HttpClient managedHttp = managedDownloads ?? ArtifactInstaller.CreateClient();

    internal static LanguageServer? Detect(string path) => Catalog.FirstOrDefault(server => server.Extensions.Contains(Path.GetExtension(path)))
        ?? (Path.GetFileName(path) is "Gemfile" or "Rakefile" ? Catalog.First(server => server.Id == "ruby")
            : Path.GetFileName(path) is ".bashrc" or ".zshrc" ? Catalog.First(server => server.Id == "shell") : null);

    internal static string LanguageId(string path, LanguageServer server) => Path.GetExtension(path) switch
    {
        ".js" or ".mjs" or ".cjs" => "javascript",
        ".jsx" => "javascriptreact",
        ".tsx" => "typescriptreact",
        ".c" or ".h" => "c",
        ".m" => "objective-c",
        ".mm" => "objective-cpp",
        ".scss" => "scss",
        ".less" => "less",
        _ => server.Id == "intellij" ? (Path.GetExtension(path) == ".java" ? "java" : "kotlin") : server.Id == "clangd" ? "cpp" : server.Id == "shell" ? "shellscript" : server.Id
    };

    internal async Task<ProcessStartInfo> ProvisionAsync(LanguageServer server, string workspace, CancellationToken token, WorkspaceLanguageOptions? options = null)
    {
        if (server.Id == "intellij")
        {
            var installation = IntelliJDistribution.Installation(directory, options?.InstallationPath ?? "");
            var cache = IntelliJDistribution.SystemPath(directory, workspace, Guid.NewGuid().ToString("N"));
            var launch = await IntelliJDistribution.AcknowledgedLaunchAsync(directory, installation, cache, token);
            // Overrides cannot replace the acknowledged launcher or its mandatory protocol arguments.
            if (Options(server.Id) is { Command.Length: > 0 }) throw new IOException("Use IntelliJ workspace installation directory instead of an executable override.");
            foreach (var argument in options?.Arguments ?? [])
            {
                if (argument is "--stdio" or "--eula" or "--system-path" || argument.StartsWith("--eula=", StringComparison.Ordinal) || argument.StartsWith("--system-path=", StringComparison.Ordinal))
                    throw new IOException("IntelliJ protocol and acceptance arguments are managed by SharpRail.");
                launch.ArgumentList.Add(WorkspaceLanguageConfiguration.Expand(argument, workspace));
            }
            Directory.CreateDirectory(cache);
            return launch;
        }
        var start = await ProvisionCoreAsync(server, workspace, token);
        ConfigureCaches(start, server, workspace);
        return start;
    }

    internal void ConfigureCaches(ProcessStartInfo start, LanguageServer server, string workspace)
    {
        var shared = Path.Combine(directory, "cache", server.Id, server.Version);
        Directory.CreateDirectory(shared);
        if (server.Id == "go") start.Environment["GOPLSCACHE"] = shared;
        if (server.Id == "scala") start.Environment["COURSIER_CACHE"] = shared;
        if (server.Id == "csharp" && FindDotnet(workspace) is { } dotnet)
        {
            var sdk = Path.GetDirectoryName(dotnet)!;
            start.Environment["DOTNET_ROOT"] = sdk;
            start.Environment["PATH"] = sdk + Path.PathSeparator + (start.Environment.TryGetValue("PATH", out var path) ? path : "");
        }
        if (server.Id == "java")
        {
            if (!start.ArgumentList.Contains("-data"))
            {
                start.ArgumentList.Add("-data");
                start.ArgumentList.Add(Path.Combine(directory, "workspaces", Key(workspace), "java"));
            }
        }
    }

    private async Task<ProcessStartInfo> ProvisionCoreAsync(LanguageServer server, string workspace, CancellationToken token)
    {
        var platform = ManagedArtifacts.Platform();
        var root = Path.Combine(directory, "servers", server.Id, ManagedArtifacts.Directory(server, platform));
        var cache = Path.Combine(directory, "cache");
        var local = Path.Combine(directory, "workspaces", Key(workspace), server.Id);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(cache);
        var custom = CustomOptions(server.Id);
        if (custom?.Disabled == true) throw new IOException($"{server.Id} is disabled. Enable it in Settings > Code Intelligence, then Retry.");
        if (!string.IsNullOrWhiteSpace(custom?.Command))
        {
            var explicitPath = ExplicitExecutable(custom.Command, workspace);
            return Start(explicitPath, custom.Arguments ?? server.Arguments);
        }
        if (server.Id is "swift" or "dart" || custom?.AllowPath == true && LanguageExecutables.PathCapable(server.Id))
        {
            if (custom?.AllowPath != true)
                throw new IOException($"{server.Id} needs an installation: SharpRail has no managed installation for it. Install the {server.Id} SDK and allow PATH for this server in Settings > Code Intelligence, then Retry.");
            var rejected = new List<string>();
            var installed = LanguageExecutables.FindOnPath(server.Executable, workspace, rejected);
            if (installed is not null && server.Id == "typescript" && !HasTypeScript(installed, workspace)) installed = null;
            if (installed is null && server.Id == "swift" && OperatingSystem.IsMacOS())
            {
                var found = (await RunAsync(Start("/usr/bin/xcrun", ["--find", "sourcekit-lsp"]), Path.GetTempPath(), token)).Trim();
                if (LanguageExecutables.Reject(found, workspace) is null) installed = found;
            }
            return Start(installed ?? throw new IOException($"PATH is allowed for {server.Id} but no usable {server.Executable} was found on it." + (rejected.Count > 0 ? " " + rejected[0] : "")), server.Arguments);
        }
        var gate = installations.GetOrAdd(root, _ => new(1));
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(root)!);
            // File sharing also serializes provisioning by separate host processes.
            await using var lease = await InstallationLock(root + ".lock", token);
            if (ManagedArtifacts.ArchiveServers.Contains(server.Id))
            {
                var artifact = ManagedArtifacts.Find(server.Id, platform)
                    ?? throw new IOException($"{server.Id} needs an installation: there is no verified managed artifact for {platform}. Allow PATH for this server in Settings > Code Intelligence, or configure an executable.");
                await ArtifactInstaller.InstallAsync(artifact, root, managedHttp, token);
            }
            else
            {
                Directory.CreateDirectory(root);
                if (!File.Exists(Path.Combine(root, ".ready")) ||
                    (server.Id == "r" && !File.Exists(Path.Combine(root, "languageserver", "DESCRIPTION"))))
                {
                    if (server.Package.Length > 0)
                    {
                        // The embedded lockfile pins every package by version and integrity hash; npm ci refuses anything else.
                        var lockfile = ManagedArtifacts.Lock(server.Id) ?? throw new IOException($"{server.Id} has no embedded package-lock.json; it will not be installed.");
                        await File.WriteAllTextAsync(Path.Combine(root, "package.json"), ManagedArtifacts.Lock(server.Id, "package.json")!, token);
                        await File.WriteAllTextAsync(Path.Combine(root, "package-lock.json"), lockfile, token);
                        await RunAsync(Start(Require("npm", workspace), ["ci", "--prefix", root, "--no-audit", "--no-fund", "--ignore-scripts", "--registry", "https://registry.npmjs.org/"]), root, token);
                    }
                    else switch (server.Id)
                        {
                            case "csharp":
                                var dotnet = FindDotnet(workspace) ?? throw new IOException("Install a .NET SDK on this host, or configure DOTNET_ROOT, then Retry.");
                                var packages = ManagedArtifacts.NuGetFor(platform)
                                    ?? throw new IOException($"csharp needs an installation: there is no verified NuGet runtime pack for {platform}. Allow PATH for this server in Settings > Code Intelligence, or configure an executable.");
                                // Only packages whose SHA-512 matches the pinned value reach dotnet: it reads a local feed and a private package cache, never nuget.org.
                                var feed = root + ".feed";
                                try
                                {
                                    if (Directory.Exists(feed)) Directory.Delete(feed, true);
                                    Directory.CreateDirectory(feed);
                                    foreach (var package in packages) await ArtifactInstaller.FetchNuGetAsync(managedHttp, package, feed, token);
                                    var install = Start(dotnet, ["tool", "install", "roslyn-language-server", "--version", server.Version, "--tool-path", root, "--source", feed]);
                                    install.Environment["NUGET_PACKAGES"] = Path.Combine(feed, "packages");
                                    await RunAsync(install, root, token);
                                }
                                finally { if (Directory.Exists(feed)) Directory.Delete(feed, true); }
                                break;
                            case "go":
                                // The embedded go.mod/go.sum pin gopls and its whole module graph by h1 hash; -mod=readonly refuses anything not in go.sum.
                                var module = Path.Combine(root, "module");
                                Directory.CreateDirectory(module);
                                await File.WriteAllTextAsync(Path.Combine(module, "go.mod"), ManagedArtifacts.Lock("go", "go.mod") ?? throw new IOException("go has no embedded go.mod; it will not be installed."), token);
                                await File.WriteAllTextAsync(Path.Combine(module, "go.sum"), ManagedArtifacts.Lock("go", "go.sum") ?? throw new IOException("go has no embedded go.sum; it will not be installed."), token);
                                var go = Start(Require("go", workspace), ["install", "golang.org/x/tools/gopls"]);
                                go.Environment["GOBIN"] = root;
                                go.Environment["GOMODCACHE"] = Path.Combine(cache, "go-modules");
                                go.Environment["GOFLAGS"] = "-mod=readonly";
                                go.Environment["GOSUMDB"] = "sum.golang.org";
                                go.Environment["GOPROXY"] = "https://proxy.golang.org";
                                go.Environment["GOTOOLCHAIN"] = "local";
                                // The user's own Go environment must not switch verification off (GONOSUMDB, GOPRIVATE, GOINSECURE, go.env files, workspaces).
                                foreach (var variable in new[] { "GONOSUMDB", "GONOSUMCHECK", "GOPRIVATE", "GOINSECURE", "GONOPROXY" }) go.Environment[variable] = "";
                                go.Environment["GOENV"] = "off";
                                go.Environment["GOWORK"] = "off";
                                await RunAsync(go, module, token);
                                break;
                            case "ruby":
                                await RunAsync(Start(Require("gem", workspace), ["install", "ruby-lsp", "--version", server.Version, "--install-dir", root, "--bindir", Path.Combine(root, "bin"), "--no-document"]), root, token);
                                break;
                            case "scala":
                                await RunAsync(Start(Require("cs", workspace), ["bootstrap", "org.scalameta:metals_2.13:" + server.Version, "--main", "scala.meta.metals.Main", "-o", Path.Combine(root, "metals"), "--force"]), root, token);
                                break;
                            case "r":
                                var r = Start(Require("Rscript", workspace), ["--vanilla", "-e", $"install.packages('remotes', repos='https://cloud.r-project.org', lib=Sys.getenv('R_LIBS_USER')); remotes::install_version('languageserver', version='{server.Version}', repos='https://cloud.r-project.org', lib=Sys.getenv('R_LIBS_USER'), upgrade='never'); stopifnot(requireNamespace('languageserver', quietly=TRUE), as.character(packageVersion('languageserver')) == '{server.Version}')"]);
                                r.Environment["R_LIBS_USER"] = root;
                                await RunAsync(r, root, token);
                                break;
                        }
                    await File.WriteAllTextAsync(Path.Combine(root, ".ready"), server.Version, token);
                }
            }
        }
        finally { gate.Release(); }

        if (server.Package.Length > 0)
        {
            var packageRoot = Path.Combine(root, "node_modules");
            var package = server.Package.Split(' ')[0].Split('@')[0];
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(packageRoot, package, "package.json"), token));
            var bin = manifest.RootElement.GetProperty("bin");
            var file = bin.ValueKind == JsonValueKind.String ? bin.GetString()! : bin.GetProperty(server.Executable).GetString()!;
            return Start(Require("node", workspace), [Path.Combine(packageRoot, package, file), .. server.Arguments]);
        }
        if (server.Id == "java")
        {
            var java = Environment.GetEnvironmentVariable("JAVA_HOME");
            if (string.IsNullOrEmpty(java)) throw new IOException("Set JAVA_HOME to a JDK 21 or newer (sdkman on macOS), then Retry.");
            var launcher = Directory.GetFiles(Path.Combine(root, "plugins"), "org.eclipse.equinox.launcher_*.jar").Single();
            var configOs = OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsWindows() ? "win" : "linux";
            var configuration = Path.Combine(local, "configuration");
            Directory.CreateDirectory(configuration);
            var template = Path.Combine(root, "config_" + configOs);
            foreach (var file in Directory.EnumerateFiles(template, "*", SearchOption.AllDirectories))
            {
                var destination = Path.Combine(configuration, Path.GetRelativePath(template, file));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                if (!File.Exists(destination)) File.Copy(file, destination);
            }
            return Start(Path.Combine(java, "bin", "java" + (OperatingSystem.IsWindows() ? ".exe" : "")),
                ["-Declipse.application=org.eclipse.jdt.ls.core.id1", "-Dosgi.bundles.defaultStartLevel=4", "-Declipse.product=org.eclipse.jdt.ls.core.product", "-Xmx1G", "--add-modules=ALL-SYSTEM", "--add-opens", "java.base/java.util=ALL-UNNAMED", "--add-opens", "java.base/java.lang=ALL-UNNAMED", "-jar", launcher, "-configuration", configuration, "-data", local]);
        }
        if (server.Id == "powershell") return Start(Require("pwsh", workspace), ["-NoLogo", "-NoProfile", "-File",
            Directory.GetFiles(root, "Start-EditorServices.ps1", SearchOption.AllDirectories).Single(), "-Stdio", "-HostName", "SharpRail", "-HostProfileId", "SharpRail", "-HostVersion", "1.0.0", "-LogLevel", "Normal", "-LogPath", Path.Combine(local, "server.log"), "-SessionDetailsPath", Path.Combine(local, "session.json")]);
        if (server.Id == "r")
        {
            var r = Start(Require("Rscript", workspace), server.Arguments); r.Environment["R_LIBS_USER"] = root; return r;
        }
        var name = server.Id == "kotlin" ? "intellij-server" : server.Executable;
        var executable = Directory.GetFiles(root, name + (OperatingSystem.IsWindows() ? ".exe" : ""), SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new IOException($"{server.Id} installation has no {name}. Remove {root} and Retry.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        var start = Start(executable, server.Arguments);
        if (server.Id == "ruby") { start.Environment["GEM_HOME"] = root; start.Environment["GEM_PATH"] = root; }
        return start;
    }

    /// <summary>Whether the process is a general runtime whose first arguments choose the program, so configured arguments must never replace them.</summary>
    internal static bool HostsRuntime(LanguageServer server) => server.Package.Length > 0 || server.Id is "java" or "powershell" or "r";

    internal static string Key(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24];
    internal static ProcessStartInfo Start(string executable, string[] args)
    {
        var start = new ProcessStartInfo(executable);
        foreach (var argument in args) start.ArgumentList.Add(argument);
        return start;
    }

    internal static bool HasTypeScript(string executable, string workspace)
    {
        var resolved = File.ResolveLinkTarget(executable, returnFinalTarget: true)?.FullName ?? executable;
        foreach (var origin in new[] { workspace, Path.GetDirectoryName(resolved)! })
            for (var parent = new DirectoryInfo(origin); parent is not null; parent = parent.Parent)
            {
                var library = Path.Combine(parent.FullName, "node_modules", "typescript", "lib");
                if (File.Exists(Path.Combine(library, "tsserver.js")) && File.Exists(Path.Combine(library, "typescript.js")) &&
                    File.Exists(Path.Combine(library, "..", "package.json"))) return true;
            }
        return false;
    }

    /// <summary>A tool the installers need (npm, go, gem...). Found with the hardened PATH search, never inside the workspace.</summary>
    private static string Require(string executable, string workspace) => LanguageExecutables.FindOnPath(executable, workspace)
        ?? throw new IOException($"Install {executable} on the host (outside the workspace), then Retry.");
    private static string? FindDotnet(string workspace)
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", executable), LanguageExecutables.FindOnPath("dotnet", workspace)
        }.FirstOrDefault(path => path is not null && Path.IsPathFullyQualified(path) && LanguageExecutables.Reject(path, workspace) is null);
    }

    /// <summary>The user's own executable override from host state: a bare name is searched on the hardened PATH, a path is used as given; both are refused inside the workspace.</summary>
    internal static string ExplicitExecutable(string command, string workspace)
    {
        var resolved = command.IndexOfAny(['/', '\\']) < 0 ? LanguageExecutables.FindOnPath(command, workspace) : command;
        if (resolved is null) throw new IOException($"The executable override '{command}' was not found on PATH (empty, relative and workspace entries are ignored).");
        return LanguageExecutables.Reject(resolved, workspace) is { } problem ? throw new IOException("Executable override refused: " + problem) : resolved;
    }

    /// <summary>The host's choices for one server: host state, or the legacy <c>servers.json</c> file beside the installations.</summary>
    internal LanguageServerOptions? CustomOptions(string id)
    {
        if (Options(id) is { } stored) return stored;
        var config = Path.Combine(directory, "servers.json");
        if (!File.Exists(config)) return null;
        using var json = JsonDocument.Parse(File.ReadAllText(config));
        return json.RootElement.TryGetProperty(id, out var entry) ? entry.Deserialize<LanguageServerOptions>(JsonSerializerOptions.Web) : null;
    }

    private static async Task<FileStream> InstallationLock(string path, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, token); }
        }
    }

    private static async Task<string> RunAsync(ProcessStartInfo start, string root, CancellationToken token)
    {
        start.WorkingDirectory = root; start.UseShellExecute = false;
        start.RedirectStandardOutput = start.RedirectStandardError = true; start.CreateNoWindow = true;
        using var process = Process.Start(start) ?? throw new IOException($"Could not start {start.FileName}.");
        using var stop = token.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var output = process.StandardOutput.ReadToEndAsync(token);
        var error = process.StandardError.ReadToEndAsync(token);
        await process.WaitForExitAsync(token);
        var result = await output; var problem = await error;
        if (process.ExitCode != 0) throw new IOException($"{Path.GetFileName(start.FileName)} failed: {problem[..Math.Min(problem.Length, 3000)]}");
        return result;
    }
}