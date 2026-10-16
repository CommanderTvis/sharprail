using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

namespace SharpRail.Performance;

internal sealed partial class Runner(string root, string dotnet)
{
    public string BuildDirectory => Path.Combine(root, "artifacts/performance/build");
    private string Ui => Path.Combine(BuildDirectory, "SharpRail.app/Contents/MacOS/SharpRail.UI");
    private string Host => Path.Combine(BuildDirectory, "host/SharpRail.Host.Remote");
    private string Probe => Path.Combine(root, "artifacts/performance/probe");
    private static string Hash(string path) { using var file = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(file)); }
    private static Dictionary<string, string> Hashes(string directory) => Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
        .Where(path => Path.GetFileName(path) != "manifest.json").Order().ToDictionary(path => Path.GetRelativePath(directory, path), Hash);

    public async Task Publish()
    {
        foreach (var name in new[] { "SharpRail.UI", "SharpRail.Host.Remote" })
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                    if (process.MainModule?.FileName?.StartsWith(BuildDirectory + Path.DirectorySeparatorChar, StringComparison.Ordinal) == true)
                        throw new IOException("A profiling target is still running; stop it before republishing.");
            }
        if (Directory.Exists(BuildDirectory)) Directory.Delete(BuildDirectory, true);
        Directory.CreateDirectory(BuildDirectory);
        var rid = "osx-" + (System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? "arm64" : "x64");
        foreach (var (project, output) in new[] { ("src/SharpRail.UI", "SharpRail.app/Contents/MacOS"), ("src/SharpRail.Host.Remote", "host") })
        {
            Console.WriteLine("Publishing " + project);
            var outputText = await Commands.Run(dotnet, ["publish", project, "-c", "Release", "-r", rid, "--self-contained", "true", "-p:PublishReadyToRun=true", "-p:PublishReadyToRunComposite=false", "-o", Path.Combine(BuildDirectory, output)], root);
            File.WriteAllText(Path.Combine(BuildDirectory, Path.GetFileName(project) + ".publish.log"), outputText);
        }
        File.WriteAllText(Path.Combine(BuildDirectory, "SharpRail.app/Contents/Info.plist"), """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0"><dict><key>CFBundleExecutable</key><string>SharpRail.UI</string><key>CFBundleIdentifier</key><string>dev.sharprail.performance</string><key>CFBundleName</key><string>SharpRail Performance</string><key>CFBundlePackageType</key><string>APPL</string><key>NSHighResolutionCapable</key><true/></dict></plist>
            """);
        var resources = Path.Combine(BuildDirectory, "SharpRail.app/Contents/Resources");
        Directory.CreateDirectory(resources);
        foreach (var name in new[] { "plugins", "ghostty", "terminfo" })
        {
            var source = Path.Combine(Path.GetDirectoryName(Ui)!, name);
            if (!Directory.Exists(source)) continue;
            Directory.Move(source, Path.Combine(resources, name));
            if (name == "plugins") Directory.CreateSymbolicLink(source, "../Resources/plugins");
        }
        await Commands.Run("codesign", ["--force", "--deep", "--sign", "-", Path.Combine(BuildDirectory, "SharpRail.app")]);
        RequireReadyToRun();
        var artifact = new Artifact(await Commands.Run("git", ["rev-parse", "HEAD"], root), await Commands.Run("git", ["status", "--porcelain=v1"], root),
            rid, await Commands.Run(dotnet, ["--version"], root), DateTimeOffset.UtcNow.ToString("O"), Hashes(BuildDirectory), PublishedRuntime());
        Data.Save(Path.Combine(BuildDirectory, "manifest.json"), artifact);
    }

    private void RequireReadyToRun()
    {
        foreach (var dll in new[] { Path.Combine(Path.GetDirectoryName(Ui)!, "SharpRail.UI.dll"), Path.Combine(Path.GetDirectoryName(Host)!, "SharpRail.Host.Remote.dll") })
        {
            using var file = File.OpenRead(dll);
            using var pe = new PEReader(file);
            if (pe.PEHeaders.CorHeader?.ManagedNativeHeaderDirectory.Size is not > 0) throw new InvalidDataException("Not ReadyToRun: " + dll);
        }
    }
    private string PublishedRuntime()
    {
        var runtimes = new List<string>();
        foreach (var deps in new[] { Ui + ".deps.json", Host + ".deps.json" })
        {
            using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(deps));
            var pack = json.RootElement.GetProperty("libraries").EnumerateObject().Select(property => property.Name)
                .Single(name => name.StartsWith("runtimepack.Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal));
            runtimes.Add(pack);
        }
        if (runtimes.Distinct().Count() != 1) throw new InvalidDataException("Published app and host runtime packs differ.");
        return runtimes[0];
    }
    private static void FreshOutput(string path)
    {
        if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any()) throw new IOException("Output directory is not empty: " + path);
        Directory.CreateDirectory(path);
    }
    private Artifact Validate()
    {
        var manifest = Data.Read<Artifact>(Path.Combine(BuildDirectory, "manifest.json"));
        var hashes = Hashes(BuildDirectory);
        if (hashes.Count != manifest.Sha256.Count || hashes.Any(pair => !manifest.Sha256.TryGetValue(pair.Key, out var expected) || expected != pair.Value))
            throw new InvalidDataException("Published files changed since the manifest was recorded; publish again.");
        RequireReadyToRun();
        var runtime = PublishedRuntime();
        if (manifest.Runtime.Length > 0 && manifest.Runtime != runtime) throw new InvalidDataException("Runtime provenance differs from published deps.json.");
        return manifest with { Runtime = runtime };
    }
    private async Task PrepareProbe()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Probe)!);
        await Commands.Run("clang", ["-O2", "-Wall", "-Wextra", "-Werror", Path.Combine(root, "performance/native/probe.m"), "-framework", "CoreGraphics", "-framework", "CoreFoundation", "-o", Probe]);
        var memory = System.Text.Json.JsonSerializer.Deserialize<MemorySnapshot>(await Commands.Run(Probe, ["memory", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)]), Data.Json)!;
        var difference = Math.Abs(memory.Timestamp * 1000.0 / memory.Frequency - Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        if (difference > 1000) throw new InvalidDataException("Native and managed monotonic clocks disagree.");
        if (!memory.Processes.Any(process => process.Pid == Environment.ProcessId && process.Error == 0)) throw new IOException("Cannot read macOS physical footprint.");
    }
    private static async Task<string> Power()
    {
        var reading = await Commands.Run("pmset", ["-g", "batt"]);
        if (!reading.Contains("'AC Power'", StringComparison.Ordinal)) throw new InvalidOperationException("Measurement requires AC power: " + reading);
        return reading;
    }
    internal static string[] RejectRuntimeOverrides()
    {
        var forbidden = Environment.GetEnvironmentVariables().Keys.Cast<string>().Where(key =>
            key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("DYLD_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase) && !(key.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("DOTNET_CLI_", StringComparison.OrdinalIgnoreCase) || key is "DOTNET_HOST_PATH" or "DOTNET_NOLOGO" or "DOTNET_SKIP_FIRST_TIME_EXPERIENCE" or "DOTNET_MULTILEVEL_LOOKUP"))
            .Where(key => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key))).Order().ToArray();
        if (forbidden.Length > 0) throw new InvalidOperationException("Remove runtime/profiler injection environment variables before profiling (values withheld): " + string.Join(", ", forbidden));
        return forbidden;
    }
    public async Task<Result> Measure(string output, int warmups, int repetitions, int memoryDelay, string[] modes)
    {
        RejectRuntimeOverrides();
        var artifact = Validate();
        await PrepareProbe();
        FreshOutput(output);
        var scratch = Path.Combine(output, "scratch");
        if (Directory.Exists(scratch)) throw new IOException("Scratch already exists; choose a fresh output directory.");
        Directory.CreateDirectory(scratch);
        var samples = new List<Sample>();
        try
        {
            var fixture = Path.Combine(scratch, "workspace");
            var fixtureHash = await Fixture(fixture);
            foreach (var mode in modes)
                for (var run = 0; run < warmups + repetitions; run++)
                {
                    Console.WriteLine($"{mode} {(run < warmups ? "warm-up" : "sample")} {run + 1}/{warmups + repetitions}");
                    var sample = await One(mode, run, run < warmups, memoryDelay, fixture, scratch, output);
                    samples.Add(sample);
                    Data.Save(Path.Combine(output, "samples.partial.json"), samples);
                }
            var machine = new Machine(await Commands.Run("sysctl", ["-n", "hw.model"]), await Commands.Run("sw_vers", ["-productVersion"]),
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(), await Commands.Run("sysctl", ["-n", "machdep.cpu.brand_string"]), await Commands.Run("sysctl", ["-n", "hw.memsize"]));
            var result = new Result(1, Data.Contract, DateTimeOffset.UtcNow.ToString("O"), machine, artifact, fixtureHash, warmups, repetitions, memoryDelay, [.. samples]);
            Data.Save(Path.Combine(output, "results.json"), result);
            Report.Write(result, Path.Combine(output, "report.html"));
            File.Delete(Path.Combine(output, "samples.partial.json"));
            return result;
        }
        finally { Directory.Delete(scratch, true); }
    }

    internal async Task<string> Fixture(string destination)
    {
        var manifest = Data.Read<FixtureManifest>(Path.Combine(root, "performance/fixture/manifest.json"));
        var bundle = Path.Combine(root, "performance/fixture/workspace.bundle");
        if (Hash(bundle) != manifest.BundleSha256) throw new InvalidDataException("Fixture bundle hash differs.");
        await Commands.Run("git", ["clone", "--quiet", "--branch", "main", bundle, destination]);
        if (await Commands.Run("git", ["rev-parse", "HEAD"], destination) != manifest.Commit ||
            await Commands.Run("git", ["rev-parse", "HEAD^{tree}"], destination) != manifest.Tree ||
            (await Commands.Run("git", ["ls-files"], destination)).Split('\n').Order().SequenceEqual(manifest.Files.Keys.Order()) == false)
            throw new InvalidDataException("Unexpected fixture history or tracked files.");
        foreach (var pair in manifest.Files) if (Hash(Path.Combine(destination, pair.Key)) != pair.Value) throw new InvalidDataException("Fixture content differs: " + pair.Key);
        return manifest.Commit + ":" + manifest.Tree;
    }

    private async Task<Sample> One(string mode, int run, bool warmup, int memoryDelay, string fixture, string scratch, string output)
    {
        var name = mode + "-" + run;
        var profile = Path.Combine(scratch, name);
        Directory.CreateDirectory(profile);
        var env = new Dictionary<string, string>
        {
            ["SHARPRAIL_ROOT"] = fixture,
            ["SHARPRAIL_PROFILE"] = Path.Combine(profile, "profile"),
            ["SHARPRAIL_STATE_DIR"] = Path.Combine(profile, "host"),
            ["SHARPRAIL_REMOTE"] = "",
            ["SHARPRAIL_PERFORMANCE"] = "1",
            ["SHARPRAIL_TOKEN"] = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)),
            ["DOTNET_DiagnosticPorts"] = "",
            ["DOTNET_DefaultDiagnosticPortSuspend"] = "0"
        };
        Child? host = null, ui = null;
        Task<string>? windowTask = null;
        var uiPid = 0;
        int? hostPid = null;
        var times = new Dictionary<string, double>();
        MemorySnapshot? memory = null;
        string? error = null;
        var powerBefore = "";
        var powerAfter = "";
        double? sampledAfterReady = null;
        Milestone[] observed = [];
        var origin = Stopwatch.GetTimestamp();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            powerBefore = await Power();
            origin = Stopwatch.GetTimestamp();
            if (mode == "remote")
            {
                using var listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                var port = ((IPEndPoint)listener.LocalEndpoint).Port;
                listener.Stop();
                env["SHARPRAIL_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
                env["SHARPRAIL_BIND"] = "127.0.0.1";
                host = new Child(Host, [], fixture, env, Path.Combine(output, name + ".host.log"), Probe);
                hostPid = host.Process.Id;
                await host.Wait("host-ready", timeout.Token);
                env["SHARPRAIL_REMOTE"] = "http://127.0.0.1:" + port;
            }
            ui = new Child(Ui, [], fixture, env, Path.Combine(output, name + ".ui.log"), Probe);
            uiPid = ui.Process.Id;
            windowTask = Commands.Run(Probe, ["window", ui.Process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)], token: timeout.Token);
            var ready = await ui.Wait("workspace-ready", timeout.Token);
            if (ready.Status is "error" or "failed") throw new IOException("Workspace readiness failed: " + ready.Detail);
            var readyAt = ready.Timestamp * 1000.0 / ready.Frequency;
            var remaining = readyAt + memoryDelay - Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
            if (remaining > 0) await Task.Delay(TimeSpan.FromMilliseconds(remaining), timeout.Token);
            if (ui.Process.HasExited || host?.Process.HasExited == true) throw new IOException("A target exited before the RAM snapshot.");
            var ids = new List<string> { "memory", ui.Process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            if (host is not null) ids.Add(host.Process.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
            memory = System.Text.Json.JsonSerializer.Deserialize<MemorySnapshot>(await Commands.Run(Probe, ids, token: timeout.Token), Data.Json) ?? throw new InvalidDataException("Empty memory snapshot.");
            sampledAfterReady = memory.Timestamp * 1000.0 / memory.Frequency - readyAt;
            if (!Data.OnTime(sampledAfterReady.Value, memoryDelay)) throw new IOException("Missed RAM sampling deadline (500 ms tolerance): " + sampledAfterReady);
            if (memory.EnumerationError != 0) throw new IOException("Cannot enumerate a target process tree: errno " + memory.EnumerationError);
            if (!memory.Processes.Any(process => process.Pid == ui.Process.Id) || host is not null && !memory.Processes.Any(process => process.Pid == host.Process.Id) || memory.Processes.Any(process => process.Error != 0))
                throw new IOException("A process was absent or unreadable in the RAM snapshot; see raw memory rows.");
            var windowAt = long.Parse(await windowTask, System.Globalization.CultureInfo.InvariantCulture);
            times = ui.Times(origin);
            times["ui-launch"] = (ui.Launched - origin) * 1000.0 / Stopwatch.Frequency;
            times["first-window"] = windowAt / 1_000_000.0 - origin * 1000.0 / Stopwatch.Frequency;
            if (host is not null) foreach (var pair in host.Times(origin)) times[pair.Key] = pair.Value;
            powerAfter = await Power();
        }
        catch (Exception failure) { error = failure.ToString(); times = ui?.Times(origin) ?? []; }
        finally
        {
            observed = [.. ui?.Observed() ?? [], .. host?.Observed() ?? []];
            await timeout.CancelAsync();
            if (windowTask is not null) { try { await windowTask; } catch (Exception failure) when (failure is IOException or OperationCanceledException) { } }
            if (ui is not null) await ui.DisposeAsync();
            if (host is not null) await host.DisposeAsync();
        }
        return new(run + 1, mode, warmup, error is null, error, times, sampledAfterReady, memory, uiPid, hostPid, powerBefore, powerAfter, observed);
    }
}