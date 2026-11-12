using System.Diagnostics;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Language;

internal sealed partial class LanguageServers
{
    internal const string PathWarning = "Unverified binary: it comes from PATH, is not pinned or checksummed, and runs with your permissions.";

    /// <summary>The executable <see cref="ProvisionAsync"/> would use, mirroring its choice. Reads files and, for an allowed PATH or override executable, asks it for <c>--version</c>.</summary>
    internal async Task<ExecutableResolution> ResolveAsync(LanguageServer server, string workspace, CancellationToken token)
    {
        var custom = CustomOptions(server.Id);
        var capable = LanguageExecutables.PathCapable(server);
        var allow = custom?.AllowPath == true && capable;
        var pinning = ManagedArtifacts.Describe(server);
        ExecutableResolution Result(string state, string source, string path, string version, string warning, string message) =>
            new(state, source, path, version, pinning, warning, message, capable, custom?.AllowPath == true);
        if (!string.IsNullOrWhiteSpace(custom?.Command))
        {
            try
            {
                var path = ExplicitExecutable(custom.Command, workspace);
                return Result("ready", "override", path, await QueryVersionAsync(path, token), "Executable override: you chose this binary; it is not pinned or verified.", "Using your executable override.");
            }
            catch (IOException error) { return Result("rejected", "override", custom.Command, "", "", error.Message); }
        }
        // A server with terms declares no managed pin of the host's: its provider installs and launches it.
        if (server.Spec.Pinning == LanguageServerPinning.None && server.Spec.TermsAsync is null || allow)
        {
            if (!allow)
                return Result("needsInstallation", "none", "", "", "", $"{server.Id} needs an installation: SharpRail has no managed installation for it. Install the SDK and allow PATH for this server, then Retry.");
            var rejected = new List<string>();
            var found = await Task.Run(() => LanguageExecutables.FindOnPath(server.Executable, workspace, rejected), token);
            if (found is null)
                return Result("needsInstallation", "path", "", "", PathWarning, $"PATH is allowed but no usable {server.Executable} was found." + (rejected.Count > 0 ? " " + rejected[0] : ""));
            return Result("ready", "path", found, await QueryVersionAsync(found, token), PathWarning, "Using the executable found on PATH.");
        }
        var platform = ManagedArtifacts.Platform();
        var root = Path.Combine(directory, "servers", server.Id, ManagedArtifacts.Directory(server, platform));
        var installed = Directory.Exists(root) && (File.Exists(Path.Combine(root, ".ready")) || File.Exists(Path.Combine(root, ".sharprail-artifact.json")));
        if (server.Spec.Pinning == LanguageServerPinning.Archive && ManagedArtifacts.Find(server, platform) is null)
            return Result("needsInstallation", "managed", "", server.Version, "", $"No verified managed artifact exists for {platform}. Allow PATH for this server or set an executable override.");
        var path2 = installed ? Executable(server, root) : root;
        return Result("ready", "managed", path2, server.Version, "",
            installed ? "Using the pinned managed installation." : "Not installed yet: downloaded, verified and installed on first start.");
    }

    private static string Executable(LanguageServer server, string root)
    {
        if (server.Package.Length > 0) return Path.Combine(root, "node_modules", ".bin", server.Executable);
        var name = InstalledName(server);
        return Directory.EnumerateFiles(root, name + (OperatingSystem.IsWindows() ? ".exe" : ""), SearchOption.AllDirectories).FirstOrDefault() ?? root;
    }

    private async Task<string> QueryVersionAsync(string executable, CancellationToken token)
    {
        try
        {
            var start = new ProcessStartInfo(executable, "--version") { WorkingDirectory = Path.GetTempPath(), UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true, CreateNoWindow = true };
            using var process = Process.Start(start);
            if (process is null) return "version unavailable";
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                process.StandardInput.Close();
                var output = process.StandardOutput.ReadToEndAsync(limit.Token);
                await process.WaitForExitAsync(limit.Token);
                var line = ((await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "").Trim();
                return line.Length == 0 ? "version unavailable" : line[..Math.Min(line.Length, 160)];
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                try { process.Kill(true); } catch (InvalidOperationException) { }
                return "version unavailable";
            }
        }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception or InvalidOperationException) { return "version unavailable"; }
    }
}