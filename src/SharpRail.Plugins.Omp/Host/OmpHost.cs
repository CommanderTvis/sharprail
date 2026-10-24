using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Omp.Host;

/// <summary>
/// The omp plugin's host half: the version probe, the SharpRail profile's MCP file, the per-launch session folders,
/// the context-file resolution, the process poll that writes the agent record, and the revive prefill.
/// </summary>
public sealed partial class OmpHost : PluginHostModule
{
    public override PluginContract Contract => OmpContract.Contract;

    /// <summary>The variable that overrides the home directory omp's files are resolved under, for checks.</summary>
    public const string HomeVariable = "SHARPRAIL_OMP_HOME";

    private static string HomeDirectory => Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>The profile's <c>mcp.json</c>: SharpRail's server only, its URL taken from each terminal's environment.</summary>
    public static string ProfileMcpPath(string home) => Path.Combine(OmpContext.UserDirectory(home, true), "mcp.json");

    /// <summary>
    /// Writes the profile's MCP file atomically. The file is SharpRail's own, so it is replaced whole; omp expands
    /// <c>${VAR}</c> when it loads it (https://omp.sh/docs/mcp), so one file serves every terminal's own MCP URL.
    /// </summary>
    public static void WriteProfileMcp(string home)
    {
        var path = ProfileMcpPath(home);
        var text = new JsonObject
        {
            ["mcpServers"] = new JsonObject
            {
                ["sharprail"] = new JsonObject { ["type"] = "http", ["url"] = "${THINKRAIL_MCP_URL}" }
            }
        }.ToJsonString(new() { WriteIndented = true }) + "\n";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && File.ReadAllText(path) == text) return;
        var temporary = path + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temporary, text);
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>The newest session file in a launch's session folder, which a revive resumes; null when it has none.</summary>
    public static string? NewestSession(string directory)
    {
        try
        {
            return Directory.Exists(directory)
                ? new DirectoryInfo(directory).EnumerateFiles("*.jsonl", SearchOption.AllDirectories).OrderByDescending(file => file.LastWriteTimeUtc).FirstOrDefault()?.FullName
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    internal static async Task<OmpStatus> ReadStatusAsync(string command, string home, bool profile)
    {
        var executable = OmpLaunch.Executable(command);
        var status = new OmpStatus(false, executable) { ProfileMcpPath = profile ? ProfileMcpPath(home) : null };
        try
        {
            using var process = Process.Start(new ProcessStartInfo(executable, ["--version"]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            if (process is null) return status with { Detail = "omp could not be started." };
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
            var errors = process.StandardError.ReadToEndAsync(cancel.Token);
            await process.WaitForExitAsync(cancel.Token);
            var text = (await output).Trim();
            if (process.ExitCode != 0) return status with { Detail = (await errors).Trim() is { Length: > 0 } detail ? detail : $"omp --version exited {process.ExitCode}." };
            return status with { Installed = true, Version = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).LastOrDefault() };
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return status with { Detail = $"{executable} was not found on PATH." };
        }
        catch (OperationCanceledException) { return status with { Detail = "omp --version timed out." }; }
    }

    private static ProcessStartInfo OmpProcess(string executable, IEnumerable<string> arguments, string? home)
    {
        var start = new ProcessStartInfo(executable, arguments) { RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        // The profile is chosen by the arguments alone; an inherited OMP_PROFILE would redirect the export.
        start.Environment.Remove("OMP_PROFILE");
        if (home is not null)
        {
            start.Environment["HOME"] = home;
            start.Environment["USERPROFILE"] = home;
        }
        return start;
    }

    /// <summary>
    /// Copies the default profile's stored credentials into SharpRail's profile: <c>omp auth export</c> piped straight
    /// into <c>omp --profile sharprail auth import -</c> (syntax from <c>omp auth --help</c>, omp 18.12.6). The
    /// credentials stream process to process: SharpRail never writes, logs or returns them. Error output is reduced to
    /// a short line with long tokens masked. <paramref name="home"/> overrides omp's home directory (checks only).
    /// </summary>
    internal static async Task<OmpSignInResult> ImportSignInAsync(string command, string? home, CancellationToken cancellationToken)
    {
        var executable = OmpLaunch.Executable(command);
        Process? export = null, import = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            var exportStart = OmpProcess(executable, ["auth", "export"], home);
            exportStart.RedirectStandardOutput = true;
            var importStart = OmpProcess(executable, ["--profile", OmpLaunch.Profile, "auth", "import", "-"], home);
            importStart.RedirectStandardInput = true;
            importStart.RedirectStandardOutput = true;
            export = Process.Start(exportStart);
            if (export is null) return new(false, "omp could not be started.");
            import = Process.Start(importStart);
            if (import is null) return new(false, "omp could not be started.");
            var exportErrors = export.StandardError.ReadToEndAsync(timeout.Token);
            var importErrors = import.StandardError.ReadToEndAsync(timeout.Token);
            var importOutput = import.StandardOutput.ReadToEndAsync(timeout.Token);
            try
            {
                await export.StandardOutput.BaseStream.CopyToAsync(import.StandardInput.BaseStream, timeout.Token);
            }
            catch (IOException) { } // The import exited early; its exit code says why.
            finally
            {
                try { import.StandardInput.Close(); } catch (IOException) { }
            }
            await export.WaitForExitAsync(timeout.Token);
            await import.WaitForExitAsync(timeout.Token);
            await importOutput;
            if (export.ExitCode != 0) return new(false, $"omp auth export failed (exit {export.ExitCode}){Sanitize(await exportErrors)}");
            if (import.ExitCode != 0) return new(false, $"omp auth import failed (exit {import.ExitCode}){Sanitize(await importErrors)}");
            return new(true, $"Your omp sign-in now works in SharpRail's \"{OmpLaunch.Profile}\" profile.");
        }
        catch (OperationCanceledException) { return new(false, cancellationToken.IsCancellationRequested ? "Cancelled." : "omp auth timed out."); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(false, $"{executable} could not be run.");
        }
        finally
        {
            foreach (var process in new[] { export, import })
            {
                if (process is null) continue;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                process.Dispose();
            }
        }
    }

    /// <summary>The first non-empty error line, at most 160 characters, with anything token-like masked.</summary>
    internal static string Sanitize(string errors)
    {
        var line = errors.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
        if (line is null) return ".";
        line = TokenLike().Replace(line, "***");
        return ": " + (line.Length > 160 ? line[..160] + "…" : line);
    }

    [GeneratedRegex(@"[A-Za-z0-9_\-.+/=]{20,}")]
    private static partial Regex TokenLike();

    public override async ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context)
    {
        var settings = () => context.Settings<OmpSettings>();
        var sessions = context.StateDirectory is { } state ? Path.Combine(state, "sessions") : Path.Combine(Path.GetTempPath(), "sharprail-omp-sessions");

        async ValueTask<string> RootOf(string workspaceId, CancellationToken cancellationToken) =>
            (await context.WorkspaceAsync(workspaceId, cancellationToken))?.Path ?? throw new InvalidOperationException($"Unknown workspace: {workspaceId}");

        void EnsureProfile()
        {
            if (!settings().Mcp) return;
            try { WriteProfileMcp(HomeDirectory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { context.Log.Warn("omp profile MCP file was not written: " + error.Message); }
        }

        context.Method(OmpContract.Status, async (_, _, _) =>
        {
            var current = settings();
            return await Task.Run(() => ReadStatusAsync(current.Command, HomeDirectory, current.Mcp));
        });
        context.Method(OmpContract.Context, async (p, _, ct) =>
        {
            var root = await RootOf(p.WorkspaceId, ct);
            return await Task.Run(() => OmpContext.Resolve(root, HomeDirectory, settings().Mcp), ct);
        });
        context.Method(OmpContract.CreateContextFile, async (p, _, ct) =>
        {
            var root = await RootOf(p.WorkspaceId, ct);
            return await Task.Run(() =>
            {
                OmpContext.Create(root, p.Target);
                return OmpContext.Resolve(root, HomeDirectory, settings().Mcp);
            }, ct);
        });

        var importing = new SemaphoreSlim(1, 1);
        // Only ever on an explicit request; host calls come from authenticated clients only.
        context.Method(OmpContract.ImportSignIn, async (_, _, ct) =>
        {
            if (!await importing.WaitAsync(0, ct)) return new OmpSignInResult(false, "An import is already running.");
            try
            {
                var command = settings().Command;
                var home = Environment.GetEnvironmentVariable(HomeVariable) is { Length: > 0 } overridden ? overridden : null;
                return await Task.Run(() => ImportSignInAsync(command, home, ct), ct);
            }
            finally { importing.Release(); }
        });

        context.TerminalEnvironment(_ =>
        {
            EnsureProfile();
            return new Dictionary<string, string> { [OmpLaunch.SessionsVariable] = sessions };
        });
        context.OnSettings<OmpSettings>(_ => EnsureProfile());
        await Task.Run(EnsureProfile);

        var watch = new AgentWatch(
            () => [.. context.Terminals().Where(item => item.Pid is not null).Select(item => new AgentWatchTarget(item.Terminal.WorkspaceId, item.Terminal.TabKey, item.Pid!.Value))],
            (workspaceId, tabKey, pid) =>
            {
                var terminal = new TerminalRef(workspaceId, tabKey);
                var existing = context.AgentRecord(terminal);
                if (existing is not null && existing.Kind != OmpManifest.Id) return;
                var arguments = ProcessSnapshot.CaptureCommand(pid) ?? "";
                context.SetAgentRecord(terminal, new(OmpManifest.Id, existing?.Command ?? OmpLaunch.Executable(settings().Command))
                {
                    SessionId = OmpLaunch.SessionIdOf(arguments) ?? existing?.SessionId,
                    Model = existing?.Model,
                    LaunchedByUi = ProcessSnapshot.CaptureUiLaunch(pid)
                });
            },
            (workspaceId, tabKey) =>
            {
                var terminal = new TerminalRef(workspaceId, tabKey);
                if (context.AgentRecord(terminal)?.Kind == OmpManifest.Id) context.SetAgentRecord(terminal, null);
            });
        context.OnTerminal(change =>
        {
            switch (change)
            {
                case TerminalSpawned: watch.Poke(); break;
                case TerminalClosed closed: watch.Forget(closed.Terminal.WorkspaceId, closed.Terminal.TabKey); break;
            }
        });

        // A revived omp tab resumes its own session folder's newest session, or continues in it when none was saved yet.
        context.RevivePrefill((_, record) =>
        {
            if (record.Kind != OmpManifest.Id) return null;
            var current = settings();
            var directory = record.SessionId is { Length: > 0 } id ? Path.Combine(sessions, id) : null;
            var newest = directory is null ? null : NewestSession(directory);
            return new RevivePrefill(OmpLaunch.Line(current.Command, new()
            {
                Mcp = current.Mcp,
                Windows = OperatingSystem.IsWindows(),
                UiLaunch = true,
                Model = current.Model,
                ApprovalMode = current.ApprovalMode,
                Advisor = current.Advisor,
                OwnCodeIntelligence = current.OwnCodeIntelligence,
                SessionDirectory = directory,
                ResumePath = newest,
                Continue = newest is null && directory is null,
                Contributions = context.AgentLaunchContributions(OmpManifest.Id)
            }))
            { Program = true };
        });

        return () =>
        {
            watch.Dispose();
            return ValueTask.CompletedTask;
        };
    }
}