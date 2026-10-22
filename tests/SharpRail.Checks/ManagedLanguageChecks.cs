using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;

namespace SharpRail.Checks;

/// <summary>
/// Managed-by-default language servers: the catalog is pinned and checksummed, installers fail closed, PATH is a per-server
/// opt-in and executables are never taken from the workspace. No check downloads anything.
/// </summary>
internal static class ManagedLanguageChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class Stub(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    internal static async Task Run(string fixture)
    {
        Catalog();
        await Tamper(Path.Combine(fixture, "managed-installer"));
        await Policy(Path.Combine(fixture, "managed-policy"));
        Console.WriteLine("PASS managed language servers: pinned catalog, fail-closed installer, PATH opt-in and workspace executable rejection");
    }

    /// <summary>The conformance listing: every managed provider with its version and hash presence; fails when one lacks either.</summary>
    internal static void Catalog()
    {
        var problems = ManagedArtifacts.Audit(LanguageServers.Catalog, out var table);
        foreach (var row in table) Console.WriteLine("MANAGED " + row);
        foreach (var server in LanguageServers.Catalog.Where(server => ManagedArtifacts.PinningOf(server) == Pinning.Partial))
            Console.WriteLine($"MANAGED {server.Id,-11} {server.Version,-20} {ManagedArtifacts.Describe(server)}");
        Require(problems.Count == 0, "Managed catalog is not fully pinned: " + string.Join("; ", problems));
        Require(LanguageServers.Catalog.Where(server => server.Id is not ("swift" or "dart")).All(server => ManagedArtifacts.Describe(server).Length > 0), "A provider lacks a pinning description.");
        var clangd = LanguageServers.Catalog.Single(server => server.Id == "clangd");
        Require(ManagedArtifacts.Directory(clangd, "linux-x86_64") == "23.1.0-e53b1a96", "Installation directory is not keyed by version and hash.");
        Require(ManagedArtifacts.Artifacts(LanguageServers.Catalog.Single(server => server.Id == "kotlin")).Count() == 6, "Kotlin artifacts are missing.");
        Require(JsonSerializer.Deserialize<LanguageServerOptions>("""{"managed":true,"command":""}""", JsonSerializerOptions.Web) is { AllowPath: false }, "The removed managed flag still has an effect.");
    }

    private static byte[] Zip(params (string Name, string Text)[] entries)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var (name, text) in entries)
            {
                var entry = zip.CreateEntry(name);
                entry.ExternalAttributes = 0x81ed << 16;
                using var writer = new StreamWriter(entry.Open()); writer.Write(text);
            }
        return stream.ToArray();
    }

    private static byte[] TarGz(Action<TarWriter> write)
    {
        using var stream = new MemoryStream();
        using (var gzip = new GZipStream(stream, CompressionLevel.Fastest, true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, true)) write(tar);
        return stream.ToArray();
    }

    private static ManagedArtifact Artifact(byte[] bytes, string format = "zip", string? hash = null, string url = "https://fixture.test/server.zip") =>
        new("fixture", "1.0.0", "any", url, hash ?? Convert.ToHexStringLower(SHA256.HashData(bytes)), format, "fixture", ["fixture.test"]);

    private static async Task Tamper(string root)
    {
        Directory.CreateDirectory(root);
        var good = Zip(("bin/server", "#!/bin/sh\n"), ("README", "hello"));
        HttpClient Serve(byte[] bytes) => new(new Stub(_ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
        async Task<string?> Failure(ManagedArtifact artifact, HttpClient http, string name)
        {
            var target = Path.Combine(root, name, "server");
            try { await ArtifactInstaller.InstallAsync(artifact, target, http, default); return null; }
            catch (IOException error)
            {
                Require(!Directory.Exists(target), $"{name}: a failed installation left a directory behind.");
                Require(!Directory.Exists(Path.Combine(root, name)) || !Directory.EnumerateFileSystemEntries(Path.Combine(root, name)).Any(), $"{name}: partial download or staging was not removed.");
                return error.Message;
            }
        }

        var installed = Path.Combine(root, "ok", "server");
        await ArtifactInstaller.InstallAsync(Artifact(good), installed, Serve(good), default);
        Require(File.ReadAllText(Path.Combine(installed, "README")) == "hello" && File.Exists(Path.Combine(installed, ".sharprail-artifact.json")), "A verified artifact was not installed.");
        if (!OperatingSystem.IsWindows())
            Require((File.GetUnixFileMode(Path.Combine(installed, "README")) & (UnixFileMode.UserWrite | UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) == 0, "Installed files are writable.");
        await ArtifactInstaller.InstallAsync(Artifact(good), installed, Serve([]), default); // idempotent: no download for an installed artifact

        Require((await Failure(Artifact(good, hash: new string('0', 64)), Serve(good), "mismatch"))?.Contains("failed verification") == true, "A hash mismatch was not refused.");
        Require((await Failure(Artifact(good, hash: ""), Serve(good), "missing"))?.Contains("no pinned SHA-256") == true, "A missing hash was not refused.");
        Require(await Failure(Artifact(good, url: "http://fixture.test/server.zip"), Serve(good), "http") is not null, "Plain HTTP was accepted.");
        Require(await Failure(Artifact(good, url: "https://other.test/server.zip"), Serve(good), "host") is not null, "A host outside the allow-list was accepted.");
        var redirected = new HttpClient(new Stub(_ => new(HttpStatusCode.Found) { Headers = { Location = new("https://evil.test/server.zip") } }));
        Require((await Failure(Artifact(good), redirected, "redirect"))?.Contains("evil.test") == true, "A redirect to another host was followed.");
        var downgrade = new HttpClient(new Stub(_ => new(HttpStatusCode.Found) { Headers = { Location = new("http://fixture.test/server.zip") } }));
        Require(await Failure(Artifact(good), downgrade, "downgrade") is not null, "A redirect to plain HTTP was followed.");

        var traversal = Zip(("../escaped.txt", "x"));
        Require(await Failure(Artifact(traversal), Serve(traversal), "traversal") is not null, "A path-traversal entry was extracted.");
        Require(!File.Exists(Path.Combine(root, "escaped.txt")) && !File.Exists(Path.Combine(root, "traversal", "escaped.txt")), "A path-traversal entry escaped.");
        var link = TarGz(tar => tar.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "../../outside" }));
        Require(await Failure(Artifact(link, "tar.gz"), Serve(link), "symlink") is not null, "A symbolic link escaping the installation was created.");
        var absolute = TarGz(tar => tar.WriteEntry(new UstarTarEntry(TarEntryType.SymbolicLink, "link") { LinkName = "/etc" }));
        Require(await Failure(Artifact(absolute, "tar.gz"), Serve(absolute), "absolute") is not null, "An absolute symbolic link was created.");
        var hard = TarGz(tar => tar.WriteEntry(new UstarTarEntry(TarEntryType.HardLink, "hard") { LinkName = "../x" }));
        Require(await Failure(Artifact(hard, "tar.gz"), Serve(hard), "hard") is not null, "A hard link entry was accepted.");

        // An installation that no longer matches the pinned artifact is never reused.
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(installed, ".sharprail-artifact.json"), UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.WriteAllText(Path.Combine(installed, ".sharprail-artifact.json"), """{"sha256":"abc"}""");
        var stale = false;
        try { await ArtifactInstaller.InstallAsync(Artifact(good), installed, Serve(good), default); }
        catch (IOException) { stale = true; }
        Require(stale, "A tampered installation was reused.");

        // A NuGet package is kept only when its SHA-512 matches; a mismatch leaves nothing in the feed.
        var feed = Path.Combine(root, "feed");
        Directory.CreateDirectory(feed);
        await ArtifactInstaller.FetchNuGetAsync(Serve(good), new("fixture.tool", "1.0.0", "any", Convert.ToHexStringLower(SHA512.HashData(good))), feed, default);
        Require(File.Exists(Path.Combine(feed, "fixture.tool.1.0.0.nupkg")), "A verified NuGet package was not kept.");
        var rejected = false;
        try { await ArtifactInstaller.FetchNuGetAsync(Serve(good), new("fixture.bad", "1.0.0", "any", new string('0', 128)), feed, default); }
        catch (IOException error) { rejected = error.Message.Contains("failed verification") && !File.Exists(Path.Combine(feed, "fixture.bad.1.0.0.nupkg")); }
        Require(rejected, "A NuGet package with the wrong SHA-512 was kept.");
    }

    private static string Script(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#!/bin/sh\necho fixture-1.2.3\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, (UnixFileMode)0x1ed);
        return path;
    }

    private static async Task Policy(string root)
    {
        if (OperatingSystem.IsWindows()) return; // Shell-script executables and symbolic links need POSIX.
        var workspace = Path.Combine(root, "workspace"); Directory.CreateDirectory(workspace);
        var outside = Directory.CreateTempSubdirectory("sharprail-managed-").FullName;
        var previousPath = Environment.GetEnvironmentVariable("PATH");
        var python = LanguageServers.Catalog.Single(server => server.Id == "python");
        var swift = LanguageServers.Catalog.Single(server => server.Id == "swift");
        string options = "";
        var servers = new LanguageServers(Path.Combine(root, "storage"), () => options);
        static string Json(string id, LanguageServerOptions value) => JsonSerializer.Serialize(new Dictionary<string, LanguageServerOptions> { [id] = value }, JsonSerializerOptions.Web);
        try
        {
            var trusted = Script(Path.Combine(outside, "bin", "pyright-langserver"));
            var planted = Script(Path.Combine(workspace, "node_modules", ".bin", "pyright-langserver"));
            Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, "", "relative-bin", Path.GetDirectoryName(planted)!, Path.GetDirectoryName(trusted)!));

            // Default: managed, even though a matching executable is on PATH.
            var resolution = await servers.ResolveAsync(python, workspace, default);
            Require(resolution is { Source: "managed", State: "ready", PathCapable: true, AllowPath: false } && resolution.Path.Contains("servers", StringComparison.Ordinal) && resolution.Warning.Length == 0,
                $"Default was not the managed installation: {resolution}");

            // Explicit per-server opt-in uses PATH, skips empty, relative and workspace entries, and warns.
            options = Json("python", new(AllowPath: true));
            resolution = await servers.ResolveAsync(python, workspace, default);
            Require(resolution is { Source: "path", State: "ready" } && LanguageExecutables.Real(resolution.Path) == LanguageExecutables.Real(trusted) && resolution.Warning.Contains("Unverified") && resolution.Version.Contains("fixture-1.2.3"),
                $"PATH opt-in did not resolve the trusted executable with its version: {resolution}");
            Require(LanguageExecutables.Real((await servers.ProvisionAsync(python, workspace, default)).FileName) == LanguageExecutables.Real(trusted), "Provisioning did not start the resolved PATH executable.");
            var typescript = LanguageServers.Catalog.Single(server => server.Id == "typescript");
            Require((await servers.ResolveAsync(typescript, workspace, default)).Source == "managed", "Another server's choice leaked from the python opt-in.");

            // Workspace executables are rejected even on PATH; a symbolic link into the workspace is too.
            var rejected = new List<string>();
            var onlyPlanted = LanguageExecutables.FindOnPath("pyright-langserver", workspace, rejected, Path.GetDirectoryName(planted));
            Require(onlyPlanted is null && rejected.Count == 1 && rejected[0].Contains("inside the workspace"), "An executable inside the workspace was accepted from PATH.");
            var link = Path.Combine(outside, "linked", "pyright-langserver");
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            File.CreateSymbolicLink(link, planted);
            Require(LanguageExecutables.FindOnPath("pyright-langserver", workspace, null, Path.GetDirectoryName(link)) is null && LanguageExecutables.Reject(link, workspace) is not null, "A symbolic link into the workspace was accepted.");
            var linkedDirectory = Path.Combine(outside, "dirlink");
            Directory.CreateSymbolicLink(linkedDirectory, Path.GetDirectoryName(planted)!);
            Require(LanguageExecutables.Reject(Path.Combine(linkedDirectory, "pyright-langserver"), workspace) is not null, "A directory symbolic link into the workspace was accepted.");
            // A symbolic link in the workspace to a trusted executable outside is judged by its target.
            var inward = Path.Combine(workspace, "tool-link");
            File.CreateSymbolicLink(inward, trusted);
            Require(LanguageExecutables.Reject(inward, workspace) is null, "A link to an outside executable was refused by its location.");

            // Relative and empty entries are ignored.
            var relative = Path.Combine(Directory.GetCurrentDirectory(), "relative-bin");
            Script(Path.Combine(relative, "only-relative-tool"));
            try { Require(LanguageExecutables.FindOnPath("only-relative-tool", workspace, null, ":relative-bin:") is null, "A relative or empty PATH entry was searched."); }
            finally { Directory.Delete(relative, true); }

            // Regular, executable files only.
            var plain = Path.Combine(outside, "plain"); File.WriteAllText(plain, "x"); File.SetUnixFileMode(plain, (UnixFileMode)0x1a4);
            Require(LanguageExecutables.Reject(plain, workspace) is not null && LanguageExecutables.Reject(outside, workspace) is not null, "A non-executable file or a directory was accepted.");

            // Overrides are the user's explicit choice but never from inside the workspace.
            options = Json("python", new(Command: trusted));
            resolution = await servers.ResolveAsync(python, workspace, default);
            Require(resolution is { Source: "override", State: "ready" }, "A user override outside the workspace was refused: " + resolution);
            options = Json("python", new(Command: planted));
            resolution = await servers.ResolveAsync(python, workspace, default);
            Require(resolution is { Source: "override", State: "rejected" } && resolution.Message.Contains("inside the workspace"), "An override inside the workspace was accepted.");
            var refused = false;
            try { await servers.ProvisionAsync(python, workspace, default); } catch (IOException error) { refused = error.Message.Contains("refused"); }
            Require(refused, "Provisioning ran an override inside the workspace.");

            // Servers without a managed installation need PATH allowed and say so; they never fall back silently.
            options = "";
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(Script(Path.Combine(outside, "swiftbin", "sourcekit-lsp"))));
            resolution = await servers.ResolveAsync(swift, workspace, default);
            Require(resolution is { State: "needsInstallation", Source: "none" } && resolution.Message.Contains("needs an installation"), "A PATH-only server resolved without opt-in: " + resolution);
            var needs = false;
            try { await servers.ProvisionAsync(swift, workspace, default); } catch (IOException error) { needs = error.Message.Contains("needs an installation"); }
            Require(needs, "A PATH-only server started without opt-in.");
            options = Json("swift", new(AllowPath: true));
            resolution = await servers.ResolveAsync(swift, workspace, default);
            Require(resolution is { State: "ready", Source: "path" }, "Allowing PATH did not enable the PATH-only server: " + resolution);
            Environment.SetEnvironmentVariable("PATH", Path.GetDirectoryName(planted));
            resolution = await servers.ResolveAsync(python, workspace, default);
            options = Json("swift", new(AllowPath: true));
            Require((await servers.ResolveAsync(swift, workspace, default)).State == "needsInstallation", "A PATH-only server resolved from a workspace directory.");

            // Servers whose executable is only a runtime cannot be redirected to PATH.
            Require(!LanguageExecutables.PathCapable(Server("r")) && !LanguageExecutables.PathCapable(Server("powershell")) && LanguageExecutables.PathCapable(Server("go")), "PATH capability is wrong.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", previousPath);
            Directory.Delete(outside, true);
        }
    }

    private static LanguageServer Server(string id) => LanguageServers.Catalog.Single(server => server.Id == id);
}
