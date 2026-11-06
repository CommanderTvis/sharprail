using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks;

/// <summary>
/// The packaged gate's run of the shipped NativeAOT binaries, which the in-process checks cannot load: the standalone host
/// (<c>SHARPRAIL_PACKAGED_HOST</c>, macOS and Windows) starts and answers RPC, and on Linux the shipped app
/// (<c>SHARPRAIL_PACKAGED_CLIENT</c>) relays a host terminal as every terminal tab runs it. Without the variables the relay
/// half runs this checks program's own relay, so the probe itself is checked wherever the checks run.
/// </summary>
internal static class PackagedBinaries
{
    internal const string HostVariable = "SHARPRAIL_PACKAGED_HOST";
    internal const string ClientVariable = "SHARPRAIL_PACKAGED_CLIENT";
    internal const string ChecksVariable = "SHARPRAIL_PACKAGED_CHECKS";

    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Packaged binaries: " + message); }

    internal static async Task Run(string root)
    {
        var client = Environment.GetEnvironmentVariable(ClientVariable) is { Length: > 0 } shippedClient ? shippedClient : null;
        var host = Environment.GetEnvironmentVariable(HostVariable) is { Length: > 0 } shippedHost ? shippedHost : null;
        var checks = Environment.GetEnvironmentVariable(ChecksVariable) is { Length: > 0 } published ? published : null;
        foreach (var (directory, executable) in new[] { (client, "SharpRail.UI"), (host, "SharpRail.Host.Remote") })
        {
            if (directory is null) continue;
            PackagedApp.RequireNativeAot(directory, executable, PackagedApp.Identity);
            if (checks is not null) RequireShipped(directory, checks, executable == "SharpRail.UI");
            if (!OperatingSystem.IsWindows()) GcSettingsChecks.VerifyCompiledIn(Path.Combine(directory, executable));
            if (OperatingSystem.IsLinux()) RequireGlibcFloor(directory, executable);
            Console.WriteLine($"PASS packaged {executable}: a NativeAOT binary" + (OperatingSystem.IsWindows() ? "" : " with the desktop GC settings compiled in") +
                ", no managed assemblies" + (checks is null ? "" : ", every native library, helper and asset tree it needs"));
        }
        if (host is not null) await Host(root, host);
        if (OperatingSystem.IsWindows()) return;
        // The shipped app is started only on Linux, where nothing else of it runs windowless; elsewhere the checks relay.
        if (client is not null && !OperatingSystem.IsLinux()) return;
        await Relay(root, client is null ? Environment.ProcessPath! : Path.Combine(client, "SharpRail.UI"));
    }

    /// <summary>The AppImages promise glibc 2.31: no ELF file in a Linux payload may need a glibc symbol version newer than that.</summary>
    private static void RequireGlibcFloor(string directory, string executable)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            if (!bytes.AsSpan().StartsWith("\u007fELF"u8)) continue;
            var versions = System.Text.RegularExpressions.Regex.Matches(Encoding.Latin1.GetString(bytes), @"GLIBC_2\.(\d+)")
                .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)).ToList();
            Require(versions.Count > 0 || Path.GetFileName(file) != executable, file + " names no glibc version.");
            Require(versions.Count == 0 || versions.Max() <= 31, $"{file} needs GLIBC_2.{versions.Max()}, newer than the 2.31 the packages promise.");
        }
    }

    /// <summary>
    /// Fails unless a payload carries what its binary loads from beside itself, as the checks' publish of the same sources
    /// lays it out: the plugins and shell-integration trees and the helper programs, and for the app also every native
    /// library but the JIT runtime's (named by the checks' deps.json) and the terminfo tree, which a macOS bundle keeps in
    /// Contents/Resources. The checks' own files (fixtures, the fixture plugin, their native test library) are not asked for.
    /// </summary>
    private static void RequireShipped(string payload, string checks, bool app)
    {
        using var deps = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(checks, "SharpRail.Checks.deps.json")));
        var runtime = deps.RootElement.GetProperty("targets").EnumerateObject().SelectMany(target => target.Value.EnumerateObject())
            .Where(library => library.Name.StartsWith("runtimepack.", StringComparison.Ordinal) && library.Value.TryGetProperty("native", out _))
            .SelectMany(library => library.Value.GetProperty("native").EnumerateObject().Select(native => Path.GetFileName(native.Name)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var resources = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(payload))!, "Resources");
        var missing = new List<string>();
        var required = 0;
        foreach (var file in Directory.EnumerateFiles(checks, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(checks, file).Replace('\\', '/');
            if (!Needed(name, file)) continue;
            required++;
            if (!File.Exists(Path.Combine(payload, name)) && !(OperatingSystem.IsMacOS() && File.Exists(Path.Combine(resources, name)))) missing.Add(name);
        }
        Require(required > 0, "the checks' publish at " + checks + " names nothing a payload needs.");
        Require(missing.Count == 0, $"{payload} lacks {missing.Count} of the {required} files it needs beside its binary: " + string.Join(", ", missing.Take(20)));

        bool Needed(string name, string file)
        {
            if (name.StartsWith("plugins/", StringComparison.Ordinal)) return !name.StartsWith("plugins/fixture/", StringComparison.Ordinal);
            if (name.StartsWith("shell-integration/", StringComparison.Ordinal)) return true;
            if (name.StartsWith("terminfo/", StringComparison.Ordinal)) return app;
            if (name.Contains('/', StringComparison.Ordinal)) return false;
            if (name.StartsWith("sharprail-", StringComparison.Ordinal)) return true;
            if (!app || name is "libTerminalEvents.dylib" || runtime.Contains(name) || Path.GetExtension(name) is ".pdb" or ".xml" or ".json") return false;
            // Not a managed assembly, nor the native launcher of one (the checks' own executable).
            var launched = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            return PackagedApp.Identity(file) is null && !File.Exists(Path.Combine(checks, launched + ".dll"));
        }
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static async Task Host(string root, string directory)
    {
        var executable = Path.Combine(directory, OperatingSystem.IsWindows() ? "SharpRail.Host.Remote.exe" : "SharpRail.Host.Remote");
        Require(File.Exists(executable), "no host binary at " + executable);
        var workspace = Path.Combine(root, "packaged-host λ");
        Directory.CreateDirectory(workspace);
        File.WriteAllText(Path.Combine(workspace, "packaged.txt"), "λ");
        var port = FreePort();
        const string token = "packaged-host-token";
        var start = new ProcessStartInfo(executable) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workspace };
        start.Environment["SHARPRAIL_ROOT"] = workspace;
        start.Environment["SHARPRAIL_STATE_DIR"] = Path.Combine(root, "packaged-host-state");
        start.Environment["SHARPRAIL_BIND"] = "127.0.0.1";
        start.Environment["SHARPRAIL_PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["SHARPRAIL_TOKEN"] = token;
        var output = new StringBuilder();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        void Line(string? line)
        {
            if (line is null) return;
            lock (output) output.AppendLine(line);
            if (line.StartsWith("SHARPRAIL_HOST_READY ", StringComparison.Ordinal)) ready.TrySetResult();
        }
        process.OutputDataReceived += (_, data) => Line(data.Data);
        process.ErrorDataReceived += (_, data) => Line(data.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            var exited = process.WaitForExitAsync();
            if (await Task.WhenAny(ready.Task, exited, Task.Delay(TimeSpan.FromSeconds(60))) != ready.Task)
                throw new InvalidOperationException("Packaged binaries: the host did not start.\n" + Output());
            var started = clock.ElapsedMilliseconds;
            var address = new Uri($"http://127.0.0.1:{port}");
            using (var remote = new RemoteHostAdapter(address, token))
            {
                var info = await remote.GetWorkspaceAsync();
                Require(Path.GetFullPath(info.RootPath) == Path.GetFullPath(workspace), "the host serves " + info.RootPath + " instead of the workspace.");
                Require((await remote.ListRootFilesAsync()).Any(file => file.Name == "packaged.txt" && !file.IsDirectory), "the host did not list the workspace's file.");
            }
            using var refused = new RemoteHostAdapter(address, "wrong-token");
            try
            {
                await refused.GetWorkspaceAsync();
                throw new InvalidOperationException("Packaged binaries: the host accepted a wrong token.");
            }
            catch (RpcException error) when (error.StatusCode == StatusCode.Unauthenticated) { }
            Console.WriteLine($"PASS packaged host: the NativeAOT host started in {started} ms, answered RPC and refused a wrong token");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync();
        }

        string Output() { lock (output) return output.ToString(); }
    }

    [UnsupportedOSPlatform("windows")]
    private static async Task Relay(string root, string executable)
    {
        Require(File.Exists(executable), "no app binary at " + executable);
        var workspace = Path.Combine(root, "packaged-relay λ");
        Directory.CreateDirectory(workspace);
        const string token = "packaged-relay-token";
        await using var terminals = new PtyTerminalService(shell: "/bin/sh");
        await using var app = RemoteServer.Create(workspace, IPAddress.Loopback, 0, token, Path.Combine(root, "packaged-relay-state"), terminals);
        await app.StartAsync();
        try
        {
            var endpoint = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var status = Path.Combine(root, "packaged-relay.status");
            var connection = TerminalRelay.Write("packaged-relay-" + Environment.ProcessId,
                new(endpoint, token, "packaged-relay", "packaged-client", workspace, status));
            var start = new ProcessStartInfo(executable) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(TerminalRelay.Argument);
            start.Environment[TerminalRelay.ConnectionVariable] = connection;
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            // The format string is in the echoed command, so only the shell's own output joins the two halves.
            await process.StandardInput.WriteAsync("printf 'RELAY_%s\\n' 'λ'; exit 7\n");
            await process.StandardInput.FlushAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
            catch (TimeoutException)
            {
                process.Kill(true);
                throw new InvalidOperationException("Packaged binaries: the relay did not end.\n" + await output + await errors);
            }
            var text = await output;
            Require(text.Contains("RELAY_λ", StringComparison.Ordinal), "the relay did not carry the shell's output.\n" + text + await errors);
            Require(process.ExitCode == 7 && File.ReadAllText(status) == TerminalRelay.ExitStatus + "7",
                $"the relay ended with {process.ExitCode} and status '{(File.Exists(status) ? File.ReadAllText(status) : "")}' instead of the shell's 7.\n" + text + await errors);
            Require(!File.Exists(connection), "the relay left its connection file behind.");
        }
        finally { await app.StopAsync(); }
        Console.WriteLine("PASS packaged relay: " + (executable == Environment.ProcessPath ? "the checks' relay" : "the NativeAOT app") +
            " attached a host terminal, carried its Unicode output and its exit code");
    }
}