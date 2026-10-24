using System.Diagnostics;
using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Plugins.Omp.Host;

/// <summary>
/// The omp plugin's host half: the version probe, the SharpRail profile's MCP file, the per-launch session folders,
/// the context-file resolution, the process poll that writes the agent record, and the revive prefill.
/// </summary>
public sealed class OmpHost : PluginHostModule
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