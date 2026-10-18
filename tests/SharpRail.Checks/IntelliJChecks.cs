using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;

namespace SharpRail.Checks;

internal static class IntelliJChecks
{
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    internal static async Task Provider(IProjectServices host, string workspace)
    {
        var catalog = await host.LanguageAsync(new("", Operation: "catalog"));
        Require(catalog.Servers.Single(server => server.Id == "intellij").State == "available", "IntelliJ opt-in provider was not advertised.");
        async Task<LanguageResult> Await(LanguageRequest request)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            LanguageResult result;
            // Retry starts one attempt; poll status without creating further attempts.
            result = await host.LanguageAsync(request, deadline.Token);
            while (result.State is "installing" or "initializing")
            {
                await Task.Delay(20, deadline.Token);
                result = await host.LanguageAsync(request with { Operation = "status" }, deadline.Token);
            }
            return result;
        }
        Require((await host.LanguageAsync(new(workspace, Operation: "retry", Server: "intellij"))).State == "disabled", "An unselected IntelliJ provider could compete with JVM defaults.");
        await host.LanguageAsync(new(workspace, Operation: "setJavaKotlinProvider", Configuration: "intellij"));
        var java = await host.LanguageAsync(new(workspace, Operation: "configuration", Server: "java"));
        var kotlin = await host.LanguageAsync(new(workspace, Operation: "configuration", Server: "kotlin"));
        var saved = await host.LanguageAsync(new(workspace, Operation: "configure", Server: "intellij", Configuration: """{"useForJavaAndKotlin":true,"buildTool":"gradle","settings":{"license":{"active":true}}}"""));
        Require(saved.Configuration.Contains("gradle", StringComparison.Ordinal), "IntelliJ selection/configuration was not persisted.");
        foreach (var path in new[] { "Sample.java", "Sample.kt" })
        {
            var result = await Await(new(workspace, path, "references"));
            Require(result.Server == "intellij" && result.State == "requiresAcceptance" && result.Locations.Count == 0 && result.Freshness == "unavailable", "Java/Kotlin routing bypassed the acceptance gate.");
        }
        foreach (var id in new[] { "java", "kotlin" })
            Require((await host.LanguageAsync(new(workspace, Operation: "retry", Server: id))).State == "disabled", "Competing JVM provider started while IntelliJ was selected.");
        foreach (var operation in new[] { "retry", "restart", "status" })
            Require((await Await(new(workspace, Operation: operation, Server: "intellij"))).State == "requiresAcceptance", "A lifecycle request unlocked IntelliJ.");
        var rows = await host.LanguageAsync(new(workspace, Operation: "servers"));
        Require(rows.Servers.Single(server => server.Id == "java").State == "disabled" && rows.Servers.Single(server => server.Id == "intellij").Configured,
            "Selected provider was not visible in workspace inventory.");
        Require((await host.LanguageAsync(new(workspace, Operation: "configuration", Server: "java"))).Configuration == java.Configuration &&
            (await host.LanguageAsync(new(workspace, Operation: "configuration", Server: "kotlin"))).Configuration == kotlin.Configuration, "Provider selection changed existing JVM settings.");
        if (!OperatingSystem.IsWindows()) await Lifecycle(host, Path.Combine(workspace, ".tools", "intellij-lifecycle"));
        await host.LanguageAsync(new(workspace, Operation: "configure", Server: "intellij", Configuration: "{}"));
        await host.LanguageAsync(new(workspace, Operation: "setJavaKotlinProvider", Configuration: "standard"));
        rows = await host.LanguageAsync(new(workspace, Operation: "servers"));
        Require(rows.Servers.Single(server => server.Id == "java").State == "idle" && rows.Servers.Single(server => server.Id == "kotlin").State == "idle", "Opt-out did not restore existing providers.");
        Console.WriteLine("PASS IntelliJ local/remote exclusive routing, persisted configuration, explicit fallback and explicit acknowledgement gate");
    }

    private static async Task Lifecycle(IProjectServices host, string workspace)
    {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Path.Combine(workspace, ".tools"));
        var root = IntelliJTermsChecks.Installation(Path.Combine(workspace, ".tools", "intellij-native-fixture"));
        var launcher = Path.Combine(root, "bin", "intellij-server");
        static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        var executable = Environment.ProcessPath!;
        var command = Quote(executable) + (Path.GetFileNameWithoutExtension(executable) == "dotnet" ? " " + Quote(typeof(IntelliJChecks).Assembly.Location) : "");
        await File.WriteAllTextAsync(launcher, "#!/bin/sh\necho start >> .tools/intellij-launches\nexec " + command + " --fake-lsp \"$@\"\n");
        File.SetUnixFileMode(launcher, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await host.LanguageAsync(new(workspace, Operation: "configure", Server: "intellij", Configuration: JsonSerializer.Serialize(new { useForJavaAndKotlin = true, installationPath = root })));
        var review = await host.IntelliJTermsAsync(new(workspace));
        Require(!review.Accepted, "A new fixture installation inherited acceptance.");
        await host.IntelliJTermsAsync(new(workspace, true, review.ArtifactId, review.EulaSha256));
        async Task<LanguageResult> Ready(LanguageRequest request)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var result = await host.LanguageAsync(request, timeout.Token);
            while (result.State is "installing" or "initializing")
            {
                await Task.Delay(25, timeout.Token);
                result = await host.LanguageAsync(request with { Operation = "status" }, timeout.Token);
            }
            return result;
        }
        Require((await Ready(new(workspace, Operation: "retry", Server: "intellij"))).State == "ready", "Accepted fixture did not launch through the production lifecycle.");
        foreach (var path in new[] { "Sample.java", "Sample.kt" })
            Require((await host.LanguageAsync(new(workspace, path, "inspect", Text: "😀 broken", DocumentId: path, Version: 2, Dirty: true))).Diagnostics.Count > 0,
                "Joint JVM lifecycle lost a dirty language buffer.");
        await File.WriteAllTextAsync(Path.Combine(workspace, "Unopened.java"), "class Unopened { Library value; }");
        var projectReferences = await host.LanguageAsync(new(workspace, "Sample.java", "references"));
        Require(projectReferences.Total == 2 && projectReferences.Locations.Any(item => item.Path == "Unopened.java" && item.Preview.Contains("Library", StringComparison.Ordinal)), "Workspace references lost unopened source files.");
        var allReferences = await host.LanguageAsync(new(workspace, "Sample.java", "references", Scope: "dependencies"));
        Require(allReferences.Total == 3 && allReferences.Locations.Any(item => item.Path.StartsWith("jar:", StringComparison.Ordinal)), "Dependency scope dropped library references.");
        var dependencyLocations = await host.LanguageAsync(new(workspace, "Sample.java", "definition"));
        Require(dependencyLocations.Locations.Count == 3 && dependencyLocations.Locations[0].Path.StartsWith("jar:", StringComparison.Ordinal), "Dependency URIs were discarded or rewritten.");
        foreach (var location in dependencyLocations.Locations.Where(item => !item.Path.StartsWith("unsupported:", StringComparison.Ordinal)))
        {
            Require(location.Preview.Contains(location.Path, StringComparison.Ordinal), "Dependency preview lost exact URI provenance.");
            var source = await host.LanguageAsync(new(workspace, location.Path, "source", Server: location.Source));
            Require(source.State == "ready" && source.Truncated && source.Locations.Single().Body.Length == 12_000, "Dependency source was not bounded.");
            var rest = await host.LanguageAsync(new(workspace, location.Path, "source", Server: location.Source, Offset: 12_000));
            Require(!rest.Truncated && source.Locations.Single().Body.Length + rest.Locations.Single().Body.Length == source.Total, "Dependency source continuation lost text.");
            var compact = JsonNode.Parse(LanguageMcpResponse.Format(source, "source", 0, 50, true))!;
            Require(compact["nextOffset"]!.GetValue<int>() == 12_000 && compact["totalCharacters"]!.GetValue<int>() == source.Total, "Source response pagination counted locations instead of characters.");
            Require((await host.LanguageAsync(new(Path.GetDirectoryName(workspace)!, location.Path, "source", Server: location.Source))).State == "unavailable", "Dependency source grant escaped its workspace.");
            try
            {
                await host.LanguageAsync(new(workspace, location.Path, "source", Server: location.Source, Text: "edited", DocumentId: "agent"));
                throw new InvalidOperationException("Dependency source accepted a replacement buffer.");
            }
            catch (ArgumentException) { }
            catch (SharpRail.Host.Protocol.RpcException) { }
        }
        Require((await host.LanguageAsync(new(workspace, "unsupported:/Library", "source", Server: "intellij"))).State == "unavailable", "Unsupported source scheme was reported readable.");
        Require((await host.LanguageAsync(new(workspace, "jar:file:///not-returned.jar!/Secret.class", "source", Server: "intellij"))).State == "unavailable", "Arbitrary dependency URI reached the server.");
        var readiness = await host.LanguageAsync(new(workspace, Operation: "status", Server: "intellij"));
        Require(readiness.State == "ready" && readiness.Freshness == "current" && readiness.Coverage.Length > 0, "Readiness omitted project coverage or freshness.");
        Require(File.ReadAllLines(Path.Combine(workspace, ".tools", "intellij-launches")).Length == 1, "Navigation restarted a warm project session.");
        var logs = await host.LanguageAsync(new(workspace, Operation: "logs", Server: "intellij"));
        Require(logs.Detail.Contains("--eula", StringComparison.Ordinal) && logs.Detail.Contains(review.EulaSha256[..16], StringComparison.Ordinal) && logs.Detail.Contains("--system-path", StringComparison.Ordinal), "Acknowledged native launch lost mandatory arguments.");
        await host.LanguageAsync(new(workspace, Operation: "restart", Server: "intellij", Configuration: JsonSerializer.Serialize(new { useForJavaAndKotlin = true, installationPath = root, settings = new { fixtureExit = true } })));
        Require((await host.LanguageAsync(new(workspace, dependencyLocations.Locations[0].Path, "source", Server: "intellij"))).State == "unavailable", "Restart retained dependency access from an old process.");
        using var expiryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        LanguageResult expired;
        do
        {
            await Task.Delay(25, expiryTimeout.Token);
            expired = await host.LanguageAsync(new(workspace, Operation: "status", Server: "intellij"), expiryTimeout.Token);
        } while (expired.State is "installing" or "initializing" or "ready");
        Require(expired.State == "expired" && expired.Diagnostics.Count == 0 && expired.Tokens.Count == 0 && expired.Freshness == "unavailable", "Vendor expiry did not invalidate language results: " + expired.State + " " + expired.Detail);
        var workspaceProblems = await host.LanguageAsync(new(workspace, Operation: "diagnostics"));
        Require(workspaceProblems.State == "expired" && workspaceProblems.Diagnostics.Count == 0 && workspaceProblems.Freshness != "current", "Workspace Problems hid expiry or retained stale diagnostics.");
        var starts = File.ReadAllLines(Path.Combine(workspace, ".tools", "intellij-launches")).Length;
        for (var n = 0; n < 3; n++) Require((await host.LanguageAsync(new(workspace, Operation: "status", Server: "intellij"))).State == "expired", "Expiry was not retained.");
        Require(File.ReadAllLines(Path.Combine(workspace, ".tools", "intellij-launches")).Length == starts && Directory.Exists(root), "Expiry restarted automatically or deleted a user installation.");
        await host.LanguageAsync(new(workspace, Operation: "stop", Server: "intellij"));
        Directory.Delete(workspace, true);
        Console.WriteLine("PASS IntelliJ acknowledged native fixture lifecycle, joint dirty buffers, exit-7 invalidation and no automatic restart/deletion of user installation");
    }

    internal static async Task Distribution(string fixture)
    {
        var root = Path.Combine(fixture, "intellij-distribution"); Directory.CreateDirectory(root);
        try
        {
            foreach (var os in new[] { "macos", "linux", "windows" })
                foreach (var arch in new[] { "aarch64", "x86_64" })
                {
                    var bundle = IntelliJDistribution.Bundle(os, arch);
                    Require(bundle.Url.Host == "download.jetbrains.com" && bundle.Sha256.Length == 64, "IntelliJ pin is incomplete.");
                }
            try { IntelliJDistribution.Bundle("linux", "riscv64"); throw new InvalidOperationException("Unsupported architecture accepted."); }
            catch (PlatformNotSupportedException) { }
            var bytes = Zip();
            var fixtureBundle = IntelliJDistribution.Bundle("macos", "aarch64") with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
            var requests = 0;
            using var http = new HttpClient(new Handler(async token => { Interlocked.Increment(ref requests); await Task.Delay(30, token); return bytes; }));
            var installs = await Task.WhenAll(IntelliJDistribution.InstallAsync(root, fixtureBundle, http, default), IntelliJDistribution.InstallAsync(root, fixtureBundle, http, default));
            Require(requests == 1 && installs[0] == installs[1], "Concurrent IntelliJ installs were not serialized/reused.");
            var install = installs[0];
            var terms = IntelliJDistribution.EulaHash(Encoding.UTF8.GetBytes("Fixture terms, no actual license acceptance."));
            var agreementStore = Path.Combine(root, "fixture-agreements");
            try { await IntelliJDistribution.AcknowledgedLaunchAsync(agreementStore, install, root, default); throw new InvalidOperationException("Launch construction bypassed explicit acknowledgement."); }
            catch (IOException) { }
            var review = await IntelliJTerms.ReviewAsync(agreementStore, install, new(install), default);
            await IntelliJTerms.ReviewAsync(agreementStore, install, new(install, true, review.ArtifactId, review.EulaSha256), default);
            var start = await IntelliJDistribution.AcknowledgedLaunchAsync(agreementStore, install, IntelliJDistribution.SystemPath(root, "/workspace/a", "process-a"), default);
            Require(start.ArgumentList.SequenceEqual(new[] { "--stdio", "--system-path", IntelliJDistribution.SystemPath(root, "/workspace/a", "process-a"), "--eula", terms[..16] }), "Public IntelliJ launch protocol differs.");
            Require(!start.Environment.ContainsKey("IJ_LAUNCHER_DEBUG"), "Inherited launcher debugging corrupts stdio.");
            Require(IntelliJDistribution.SystemPath(root, "/workspace/a", "process-a") != IntelliJDistribution.SystemPath(root, "/workspace/a", "process-b") &&
                IntelliJDistribution.SystemPath(root, "/workspace/a", "process-a") != IntelliJDistribution.SystemPath(root, "/workspace/b", "process-a"), "IntelliJ mutable caches collide.");
            await File.WriteAllTextAsync(Path.Combine(install, "EULA.txt"), "Changed fixture terms");
            try { await IntelliJDistribution.AcknowledgedLaunchAsync(agreementStore, install, root, default); throw new InvalidOperationException("Changed terms reused acknowledgement."); }
            catch (IOException) { }
            foreach (var malicious in new[] { Zip("intellij-server-fixture/../../escape"), Zip(link: "../../../escape") })
            {
                using var badHttp = new HttpClient(new Handler(_ => Task.FromResult(malicious)));
                var bundle = fixtureBundle with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(malicious)) };
                await Refuses(Path.Combine(root, Guid.NewGuid().ToString("N")), bundle, badHttp);
            }
            await Refuses(Path.Combine(root, "bad-checksum"), fixtureBundle with { Sha256 = new('0', 64) }, http);
            var tar = Tar();
            using var tarHttp = new HttpClient(new Handler(_ => Task.FromResult(tar)));
            await IntelliJDistribution.InstallAsync(Path.Combine(root, "tar"), IntelliJDistribution.Bundle("linux", "x86_64") with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(tar)) }, tarHttp, default);
            using var cancelledHttp = new HttpClient(new Handler(async token => { await Task.Delay(Timeout.Infinite, token); return bytes; }));
            using var cancellation = new CancellationTokenSource(100);
            try { await IntelliJDistribution.InstallAsync(Path.Combine(root, "cancel"), fixtureBundle, cancelledHttp, cancellation.Token); throw new InvalidOperationException("Provisioning ignored cancellation."); }
            catch (OperationCanceledException) { }
            Require(!Directory.EnumerateFileSystemEntries(root, "*.partial-*", SearchOption.AllDirectories).Any(), "Provisioning leaked partial files.");
            var server = LanguageServers.Catalog.Single(item => item.Id == "intellij");
            var provisioning = new LanguageServers(Path.Combine(root, "blocked"), () => """{"intellij":{"command":"/must-not-execute","arguments":["--eula","forged"]}}""");
            try { await provisioning.ProvisionAsync(server, root, default); throw new IOException("Override bypassed IntelliJ acceptance."); }
            catch (IntelliJAcceptanceException) { }
            Require(!Directory.Exists(Path.Combine(root, "blocked")), "Unavailable integration wrote installation/cache state.");
            try { WorkspaceLanguageConfiguration.Parse("""{"acceptedEula":true}""", "intellij"); throw new InvalidOperationException("Agent configuration accepted terms."); }
            catch (System.Text.Json.JsonException) { }
            var options = WorkspaceLanguageConfiguration.Initialization(new(BuildTool: "gradle", ProjectJdk: "/host/project-jdk"), "/workspace", "intellij");
            Require(options["projects"] is JsonArray, "IntelliJ projects must serialize as an array, not an object.");
            Require(options["intellijExtensions"]!.ToString() == "false" && options["defaultSdk"]!.ToString() == "/host/project-jdk" && options["buildTools"]!["file:///workspace"]!.ToString() == "gradle", "IntelliJ initialization advertised unimplemented extensions or lost project settings.");
            var expiryStorage = Path.Combine(root, "expired-managed");
            var expiredInstallation = IntelliJDistribution.Installation(expiryStorage, "");
            Directory.CreateDirectory(expiredInstallation);
            await File.WriteAllTextAsync(Path.Combine(expiredInstallation, "fixture"), "expired owned artifact");
            IntelliJDistribution.RecordExpiry(expiryStorage, "");
            Require(!Directory.Exists(expiredInstallation) && File.Exists(expiredInstallation + ".expired"), "Managed expiry did not retain refusal and delete the owned artifact.");
            var beforeExpiry = requests;
            try { await IntelliJDistribution.PrepareAsync(expiryStorage, "", http, default); throw new InvalidOperationException("Expired build was downloaded again."); }
            catch (IntelliJExpiredException) { }
            Require(requests == beforeExpiry, "Expired managed build caused a download loop.");
            ProjectJdk(root);
            await Protocol(Path.Combine(root, "protocol"));
            await Drafts(Path.Combine(root, "drafts"));
            Console.WriteLine("PASS IntelliJ fixture ZIP/tar integrity, traversal/link rejection, concurrency, cancellation, terms identity, cache isolation and blocked override/acceptance");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void ProjectJdk(string root)
    {
        var workspace = Path.Combine(root, "jdk-project");
        var candidates = Path.Combine(root, "sdkman-candidates");
        Directory.CreateDirectory(workspace);
        foreach (var candidate in new[] { "current", "21-fixture" })
        {
            var bin = Path.Combine(candidates, "java", candidate, "bin");
            Directory.CreateDirectory(bin);
            File.WriteAllText(Path.Combine(bin, OperatingSystem.IsWindows() ? "java.exe" : "java"), "fixture only");
        }
        var unwanted = "/opt/homebrew/opt/kotlin-lsp/libexec/jbr";
        Require(ProjectJava.Resolve(workspace, "", candidates, unwanted).Path == Path.Combine(candidates, "java", "current"), "SDKMAN did not take precedence over a bundled Kotlin runtime.");
        File.WriteAllText(Path.Combine(workspace, ".sdkmanrc"), "java=21-fixture\n");
        Require(ProjectJava.Resolve(workspace, "", candidates, unwanted).Path == Path.Combine(candidates, "java", "21-fixture"), "Workspace SDKMAN selection was ignored.");
        Require(ProjectJava.Resolve(workspace, "/explicit/jdk", candidates, unwanted).Path == "/explicit/jdk", "Explicit project JDK lost precedence.");
        File.WriteAllText(Path.Combine(workspace, ".sdkmanrc"), "java=missing\n");
        try { ProjectJava.Resolve(workspace, "", candidates, unwanted); throw new InvalidOperationException("Missing project JDK silently fell back."); }
        catch (IOException) { }
        var raw = WorkspaceLanguageConfiguration.Parse("""{"initializationOptions":{"projects":{},"defaultSdk":"/raw/jdk"}}""", "intellij");
        var initialization = WorkspaceLanguageConfiguration.Initialization(raw, workspace, "intellij");
        Require(initialization["projects"] is JsonArray && initialization["defaultSdk"]!.ToString() == "/raw/jdk", "Legacy empty projects object or explicit raw JDK was not preserved correctly.");
        File.WriteAllText(Path.Combine(workspace, "settings.gradle.kts"), "// fixture");
        initialization = WorkspaceLanguageConfiguration.Initialization(raw, workspace, "intellij");
        Require(initialization["projects"]![0]!["java-home"]!.ToString() == "/raw/jdk" && initialization["projects"]![0]!["type"]!.ToString() == "gradle", "Gradle importer did not receive the project JDK independently of the analysis SDK.");
        foreach (var projects in new[] { "false", "{\"unexpected\":true}" })
        {
            var invalid = WorkspaceLanguageConfiguration.Parse("{\"initializationOptions\":{\"projects\":" + projects + "}}", "intellij");
            try { WorkspaceLanguageConfiguration.Initialization(invalid, workspace, "intellij"); throw new InvalidOperationException("Invalid IntelliJ projects shape reached the server."); }
            catch (ArgumentException) { }
        }
    }

    private static async Task Drafts(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, ".tools"));
        await File.WriteAllTextAsync(Path.Combine(root, "Sample.kt"), "clean");
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(IntelliJChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        var configuration = new Dictionary<(string, string), string>();
        var global = JsonSerializer.Serialize(new { kotlin = new { command = executable, arguments } });
        await using var service = new LanguageService(Path.Combine(root, ".tools", "language"), () => true, () => global,
            (workspace, id) => configuration.GetValueOrDefault((workspace, id)) ?? "{}", (workspace, id, json) => configuration[(workspace, id)] = json);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        async Task<LanguageResult> Ready(LanguageRequest request)
        {
            LanguageResult result;
            do
            {
                result = await service.ExecuteAsync(request, deadline.Token);
                if (result.State is "installing" or "initializing") await Task.Delay(20, deadline.Token);
            } while (result.State is "installing" or "initializing");
            return result;
        }
        var draft = new LanguageRequest(root, "Sample.kt", Text: "😀 broken", DocumentId: "editor", Version: 2, Dirty: true);
        Require((await Ready(draft)).Diagnostics.Count > 0, "Kotlin fixture did not establish a dirty buffer.");
        // Simulate another host saving the shared workspace selection.
        configuration[(root, "intellij")] = """{"useForJavaAndKotlin":true}""";
        Require((await service.ExecuteAsync(new(root, Operation: "servers"))).Servers.Single(item => item.Id == "kotlin").State == "disabled", "External provider selection did not suspend the competing session.");
        var other = Path.Combine(root, "other"); Directory.CreateDirectory(other);
        Require((await service.ExecuteAsync(new(other, Operation: "servers"))).Servers.Single(item => item.Id == "kotlin").State == "idle", "Provider choice leaked to another workspace.");
        await service.ExecuteAsync(new(root, Operation: "configure", Server: "intellij", Configuration: "{}"));
        Require((await Ready(new(root, "Sample.kt"))).Diagnostics.Count > 0, "Provider switching lost a dirty Kotlin draft.");
        await service.ExecuteAsync(new(root, Operation: "configure", Server: "intellij", Configuration: """{"useForJavaAndKotlin":true}"""));
        await service.ExecuteAsync(draft with { Operation = "close" });
        await service.ExecuteAsync(new(root, Operation: "configure", Server: "intellij", Configuration: "{}"));
        Require((await Ready(new(root, "Sample.kt"))).Diagnostics.Count == 0, "Closing a draft while IntelliJ was selected resurrected stale content.");
    }

    private static async Task Protocol(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, ".tools"));
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(IntelliJChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        var server = LanguageServers.Catalog.Single(item => item.Id == "intellij");
        await using var session = await LanguageSession.StartAsync(LanguageServers.Start(executable, arguments), root, server, default, new(BuildTool: "maven", ProjectJdk: "/host/project-jdk"));
        var initialization = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, ".tools", "initialize.json")))!;
        Require(initialization["cwd"]!.ToString() == root && initialization["initializationOptions"]!["intellijExtensions"]!.ToString() == "false", "IntelliJ protocol fixture lost workspace/implemented-capability isolation.");
        foreach (var path in new[] { "Sample.java", "Sample.kt" })
        {
            var request = new LanguageRequest(root, path, Text: "😀 broken", DocumentId: path, Version: 2, Dirty: true);
            var result = await session.ExecuteAsync(request, default);
            Require(result.State == "ready" && result.Diagnostics.Count > 0, "IntelliJ standard-LSP fixture lost Java/Kotlin dirty diagnostics.");
            Require((await session.ExecuteAsync(request with { Version = 1 }, default)).State == "stale", "IntelliJ fixture accepted an older dirty-buffer version.");
            await session.ExecuteAsync(request with { Operation = "close" }, default);
        }
        Require(LanguageServers.LanguageId("A.java", server) == "java" && LanguageServers.LanguageId("A.kt", server) == "kotlin", "Joint IntelliJ provider sent an incorrect document language.");
    }

    private static async Task Refuses(string root, IntelliJBundle bundle, HttpClient http)
    {
        try { await IntelliJDistribution.InstallAsync(root, bundle, http, default); throw new InvalidOperationException("Invalid IntelliJ archive installed."); }
        catch (IOException) { }
        Require(!Directory.EnumerateFiles(root, ".sharprail-artifact.json", SearchOption.AllDirectories).Any(), "Failed install published a completion marker.");
    }

    private static Dictionary<string, string> Files() => new()
    {
        ["bin/" + (OperatingSystem.IsWindows() ? "intellij-server.exe" : "intellij-server")] = "fixture, never executed",
        ["EULA.txt"] = "Fixture terms, no actual license acceptance.",
        ["product-info.json"] = """{"buildNumber":"263.6379.0","productCode":"ILS"}""",
        ["license/retained-notice.txt"] = "Fixture notice"
    };

    private static byte[] Zip(string? invalidPath = null, string? link = null)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
        {
            foreach (var (name, value) in Files())
            {
                var entry = zip.CreateEntry("intellij-server-fixture/" + name);
                entry.ExternalAttributes = 0x81ed << 16;
                using var writer = new StreamWriter(entry.Open()); writer.Write(value);
            }
            if (invalidPath is not null) { using var writer = new StreamWriter(zip.CreateEntry(invalidPath).Open()); writer.Write("escape"); }
            if (link is not null)
            {
                var entry = zip.CreateEntry("intellij-server-fixture/link"); entry.ExternalAttributes = unchecked((int)0xa1ff0000);
                using var writer = new StreamWriter(entry.Open()); writer.Write(link);
            }
        }
        return output.ToArray();
    }

    private static byte[] Tar()
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionMode.Compress, true))
        using (var writer = new TarWriter(gzip, leaveOpen: true))
            foreach (var (name, value) in Files())
            {
                using var data = new MemoryStream(Encoding.UTF8.GetBytes(value));
                writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, "intellij-server-fixture/" + name) { DataStream = data, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute });
            }
        return output.ToArray();
    }

    private sealed class Handler(Func<CancellationToken, Task<byte[]>> response) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            new(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(await response(token)) };
    }
}