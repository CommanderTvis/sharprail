using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SharpRail.Plugins.Acp.Host;

/// <summary>
/// Turns a confirmed install plan into an agent entry. Nothing is downloaded or started before the user confirmed exactly this agent,
/// distribution and version; a binary is checked against the registry's SHA-256 before it is unpacked.
/// </summary>
internal sealed class AcpInstaller(string root, Func<string, CancellationToken, Task<byte[]>> download)
{
    private static readonly string[] ArchiveSuffixes = [".zip", ".tar.gz", ".tgz", ".tar.bz2", ".tbz2", ".tar.xz", ".tar"];
    private const long MaxDownload = 600L * 1024 * 1024;

    public static async Task<byte[]> HttpDownload(string url, CancellationToken cancellationToken)
    {
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Only https downloads are accepted.");
        using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxDownload) throw new InvalidOperationException("The download is larger than 600 MB.");
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task<AcpAgentEntry> InstallAsync(JsonObject registry, AcpInstallRequest request, string platform, CancellationToken cancellationToken)
    {
        if (!request.Confirmed) throw new InvalidOperationException("Installing needs your confirmation.");
        var plan = AcpRegistry.Plan(registry, request.AgentId, request.Kind, platform) ?? throw new InvalidOperationException("The registry has no such agent for this machine.");
        if (plan.Version != request.Version || plan.Kind != request.Kind)
            throw new InvalidOperationException($"The registry now offers version {plan.Version}; review it before installing.");
        var agent = (registry["agents"] as JsonArray)!.OfType<JsonObject>().First(candidate => AcpFolder.Str(candidate, "id") == plan.AgentId);
        var node = agent["distribution"]![plan.Kind]!;
        if (plan.Kind != "binary")
        {
            var package = (JsonObject)node;
            var spec = AcpFolder.Str(package, "package");
            var args = plan.Kind == "npx" ? new[] { "-y", spec }.Concat(AcpRegistry.Arguments(package)) : new[] { spec }.Concat(AcpRegistry.Arguments(package));
            return new AcpAgentEntry(plan.AgentId, plan.Name, plan.Kind) { Args = [.. args], Env = AcpRegistry.Environment(package), Origin = "registry", Version = plan.Version, Description = AcpFolder.Str(agent, "description"), Website = Optional(agent, "website"), Repository = plan.Repository, License = plan.License, Source = plan.Source };
        }
        var target = (JsonObject)node[platform]!;
        var archive = AcpFolder.Str(target, "archive");
        var bytes = await download(archive, cancellationToken);
        if (plan.Sha256 is { } expected && !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The download does not match the registry's checksum, so it was discarded.");
        var directory = Path.Combine(root, plan.AgentId, plan.Version);
        var staging = directory + ".partial";
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        var cmd = AcpFolder.Str(target, "cmd").Replace('\\', '/');
        var name = new Uri(archive).AbsolutePath.ToLowerInvariant();
        if (ArchiveSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.Ordinal)))
        {
            var file = Path.Combine(staging, ".download" + Path.GetExtension(name));
            await File.WriteAllBytesAsync(file, bytes, cancellationToken);
            await ExtractAsync(file, staging, cancellationToken);
            File.Delete(file);
        }
        else await File.WriteAllBytesAsync(Path.Combine(staging, Path.GetFileName(cmd)), bytes, cancellationToken);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.Move(staging, directory);
        var program = Path.GetFullPath(Path.Combine(directory, cmd));
        if (!program.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(program))
            throw new InvalidOperationException("The archive does not contain the agent's command.");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(program, File.GetUnixFileMode(program) | UnixFileMode.UserExecute);
        return new AcpAgentEntry(plan.AgentId, plan.Name, program) { Args = AcpRegistry.Arguments(target), Env = AcpRegistry.Environment(target), Origin = "registry", Version = plan.Version, Description = AcpFolder.Str(agent, "description"), Website = Optional(agent, "website"), Repository = plan.Repository, License = plan.License, Source = plan.Source };
    }

    private static string? Optional(JsonObject node, string name) => AcpFolder.Str(node, name) is { Length: > 0 } text ? text : null;

    // The platform's own tar handles .zip, .tar.gz and .tar.bz2 alike (bsdtar on macOS and Windows 10+, GNU tar on Linux).
    private static async Task ExtractAsync(string file, string directory, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo("tar", ["-xf", file, "-C", directory]) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("tar could not be started.");
        var errors = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        if (process.ExitCode != 0) throw new InvalidOperationException("The archive could not be unpacked: " + (await errors).Trim());
    }
}