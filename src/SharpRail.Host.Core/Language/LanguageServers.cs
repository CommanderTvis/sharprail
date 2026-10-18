using System.Collections.Concurrent;
using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
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

internal sealed class LanguageServers(string directory, Func<string>? configuration = null)
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
        var root = Path.Combine(directory, "servers", server.Id, server.Version);
        var cache = Path.Combine(directory, "cache");
        var local = Path.Combine(directory, "workspaces", Key(workspace), server.Id);
        Directory.CreateDirectory(local);
        Directory.CreateDirectory(cache);
        var config = Path.Combine(directory, "servers.json");
        var managed = false;
        var custom = Options(server.Id);
        if (custom is null && File.Exists(config))
        {
            using var json = JsonDocument.Parse(await File.ReadAllTextAsync(config, token));
            if (json.RootElement.TryGetProperty(server.Id, out var entry))
                custom = entry.Deserialize<LanguageServerOptions>(JsonSerializerOptions.Web);
        }
        if (custom is not null)
        {
            managed = custom.Managed;
            if (custom.Disabled) throw new IOException($"{server.Id} is disabled. Enable it in Settings > LSP, then Retry.");
            if (!string.IsNullOrWhiteSpace(custom.Command)) return Start(custom.Command, custom.Arguments ?? server.Arguments);
        }
        var installed = managed ? null : Find(server.Executable);
        if (installed is not null && server.Id == "typescript" && !HasTypeScript(installed, workspace)) installed = null;
        if (server.Id is "swift" or "dart")
        {
            if (installed is null && server.Id == "swift" && OperatingSystem.IsMacOS())
            {
                var found = await RunAsync(Start("/usr/bin/xcrun", ["--find", "sourcekit-lsp"]), workspace, token);
                installed = found.Trim();
            }
            return Start(installed ?? throw new IOException($"Install the {server.Id} SDK on this host; it includes {server.Executable}."), server.Arguments);
        }
        if (installed is not null && server.Id is not ("r" or "powershell")) return Start(installed, server.Arguments);
        var gate = installations.GetOrAdd(root, _ => new(1));
        await gate.WaitAsync(token);
        try
        {
            Directory.CreateDirectory(root);
            // File sharing also serializes provisioning by separate host processes.
            await using var lease = await InstallationLock(root + ".lock", token);
            if (!File.Exists(Path.Combine(root, ".ready")) ||
                (server.Id == "r" && !File.Exists(Path.Combine(root, "languageserver", "DESCRIPTION"))))
            {
                if (server.Package.Length > 0)
                    await RunAsync(Start(Require("npm"), ["install", "--prefix", root, "--no-audit", "--no-fund", "--ignore-scripts", .. server.Package.Split(' ')]), root, token);
                else switch (server.Id)
                    {
                        case "csharp":
                            await RunAsync(Start(FindDotnet(workspace) ?? throw new IOException("Install a .NET SDK on this host, or configure DOTNET_ROOT, then Retry."), ["tool", "install", "roslyn-language-server", "--version", server.Version, "--tool-path", root]), root, token);
                            break;
                        case "go":
                            var go = Start(Require("go"), ["install", "golang.org/x/tools/gopls@" + server.Version]);
                            go.Environment["GOBIN"] = root;
                            go.Environment["GOMODCACHE"] = Path.Combine(cache, "go-modules");
                            await RunAsync(go, root, token);
                            break;
                        case "ruby":
                            await RunAsync(Start(Require("gem"), ["install", "ruby-lsp", "--version", server.Version, "--install-dir", root, "--bindir", Path.Combine(root, "bin"), "--no-document"]), root, token);
                            break;
                        case "scala":
                            await RunAsync(Start(Require("cs"), ["bootstrap", "org.scalameta:metals_2.13:" + server.Version, "--main", "scala.meta.metals.Main", "-o", Path.Combine(root, "metals"), "--force"]), root, token);
                            break;
                        case "r":
                            var r = Start(Require("Rscript"), ["--vanilla", "-e", $"install.packages('remotes', repos='https://cloud.r-project.org', lib=Sys.getenv('R_LIBS_USER')); remotes::install_version('languageserver', version='{server.Version}', repos='https://cloud.r-project.org', lib=Sys.getenv('R_LIBS_USER'), upgrade='never'); stopifnot(requireNamespace('languageserver', quietly=TRUE), as.character(packageVersion('languageserver')) == '{server.Version}')"]);
                            r.Environment["R_LIBS_USER"] = root;
                            await RunAsync(r, root, token);
                            break;
                        case "java":
                            await ArchiveAsync($"https://download.eclipse.org/jdtls/snapshots/jdt-language-server-{server.Version}-202609031315.tar.gz", root, true, token);
                            break;
                        case "powershell":
                            await ArchiveAsync($"https://github.com/PowerShell/PowerShellEditorServices/releases/download/v{server.Version}/PowerShellEditorServices.zip", root, false, token);
                            break;
                        case "clangd":
                            var os = OperatingSystem.IsMacOS() ? "mac" : OperatingSystem.IsWindows() ? "windows" : "linux";
                            await ArchiveAsync($"https://github.com/clangd/clangd/releases/download/{server.Version}/clangd-{os}-{server.Version}.zip", root, false, token);
                            break;
                        case "kotlin":
                            var suffix = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "-aarch64" : "";
                            var extension = OperatingSystem.IsMacOS() ? ".sit" : OperatingSystem.IsWindows() ? ".win.zip" : ".tar.gz";
                            await ArchiveAsync($"https://download.jetbrains.com/language-server/kotlin-server/{server.Version}/kotlin-server-{server.Version}{suffix}{extension}", root, extension == ".tar.gz", token);
                            break;
                        case "rust":
                            var arch = RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";
                            var target = OperatingSystem.IsMacOS() ? "apple-darwin" : OperatingSystem.IsWindows() ? "pc-windows-msvc" : "unknown-linux-gnu";
                            var url = $"https://github.com/rust-lang/rust-analyzer/releases/download/{server.Version}/rust-analyzer-{arch}-{target}";
                            if (OperatingSystem.IsWindows()) await ArchiveAsync(url + ".zip", root, false, token);
                            else
                            {
                                await using var source = await Http.GetStreamAsync(url + ".gz", token);
                                await using var gzip = new GZipStream(source, CompressionMode.Decompress);
                                await using var output = File.Create(Path.Combine(root, "rust-analyzer"));
                                await gzip.CopyToAsync(output, token);
                            }
                            break;
                    }
                await File.WriteAllTextAsync(Path.Combine(root, ".ready"), server.Version, token);
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
            return Start(Require("node"), [Path.Combine(packageRoot, package, file), .. server.Arguments]);
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
        if (server.Id == "powershell") return Start(Require("pwsh"), ["-NoLogo", "-NoProfile", "-File",
            Directory.GetFiles(root, "Start-EditorServices.ps1", SearchOption.AllDirectories).Single(), "-Stdio", "-HostName", "SharpRail", "-HostProfileId", "SharpRail", "-HostVersion", "1.0.0", "-LogLevel", "Normal", "-LogPath", Path.Combine(local, "server.log"), "-SessionDetailsPath", Path.Combine(local, "session.json")]);
        if (server.Id == "r")
        {
            var r = Start(Require("Rscript"), server.Arguments); r.Environment["R_LIBS_USER"] = root; return r;
        }
        var name = server.Id == "kotlin" ? "intellij-server" : server.Executable;
        var executable = Directory.GetFiles(root, name + (OperatingSystem.IsWindows() ? ".exe" : ""), SearchOption.AllDirectories).FirstOrDefault()
            ?? throw new IOException($"{server.Id} installation has no {name}. Remove {root} and Retry.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(executable) | UnixFileMode.UserExecute);
        var start = Start(executable, server.Arguments);
        if (server.Id == "ruby") { start.Environment["GEM_HOME"] = root; start.Environment["GEM_PATH"] = root; }
        return start;
    }

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

    private static string Require(string executable) => Find(executable) ?? throw new IOException($"Install {executable} on the host or configure language/servers.json, then Retry.");
    private static string? FindDotnet(string workspace)
    {
        var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        var project = ProjectServices.KnownCheckout(workspace)?.ProjectRoot ?? workspace;
        return new[]
        {
            Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "", executable),
            Path.Combine(workspace, ".tools", "dotnet", executable), Path.Combine(project, ".tools", "dotnet", executable), Find("dotnet")
        }.FirstOrDefault(path => path is not null && Path.IsPathFullyQualified(path) && File.Exists(path));
    }
    private static string? Find(string executable)
    {
        var extensions = OperatingSystem.IsWindows() ? new[] { ".exe", ".cmd", ".bat", "" } : [""];
        foreach (var path in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(path, executable + extension);
                if (File.Exists(candidate)) return candidate;
            }
        return null;
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

    private static async Task ArchiveAsync(string url, string root, bool tar, CancellationToken token)
    {
        var archive = Path.Combine(root, ".download");
        try
        {
            await using (var source = await Http.GetStreamAsync(url, token))
            await using (var output = File.Create(archive)) await source.CopyToAsync(output, token);
            if (tar)
            {
                await using var input = File.OpenRead(archive);
                await using var gzip = new GZipStream(input, CompressionMode.Decompress);
                await TarFile.ExtractToDirectoryAsync(gzip, root, overwriteFiles: true, cancellationToken: token);
            }
            else
            {
                ZipFile.ExtractToDirectory(archive, root, true);
                if (!OperatingSystem.IsWindows())
                {
                    using var zip = ZipFile.OpenRead(archive);
                    foreach (var entry in zip.Entries)
                    {
                        var mode = (UnixFileMode)((entry.ExternalAttributes >> 16) & 0x1ff);
                        var path = Path.GetFullPath(Path.Combine(root, entry.FullName));
                        if (File.Exists(path) && mode != 0) File.SetUnixFileMode(path, mode);
                    }
                }
            }
        }
        finally { if (File.Exists(archive)) File.Delete(archive); }
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