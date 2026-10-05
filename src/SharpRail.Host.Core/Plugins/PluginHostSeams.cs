using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>What the terminal service lends the plugin runtime: identity tokens, host-side writes, the process table and its hooks.</summary>
public interface IPluginTerminalSeams
{
    string Token(TerminalRef terminal);
    TerminalRef? ForToken(string token);
    void Write(TerminalRef terminal, string data);
    IReadOnlyList<TerminalProcess> List();
    string? WorkspaceForProcess(int pid);
    /// <summary>Environment for a shell starting for a tab, after core's own variables.</summary>
    Func<TerminalRef, IReadOnlyDictionary<string, string>>? EnvironmentContributor { get; set; }
    /// <summary>Spawn (with the shell's pid) and exit of a shell whose tab is known.</summary>
    Action<TerminalEvent>? Lifecycle { get; set; }
    /// <summary>Offered when a fresh shell starts for a tab; the prefill rides the creating attachment.</summary>
    Func<TerminalRef, TerminalPrefill?>? RevivePrefill { get; set; }
    /// <summary>A session was closed: its id, and its tab when an attach named it.</summary>
    Action<string, TerminalRef?>? SessionClosed { get; set; }
}

/// <summary>
/// Every core capability the plugin runtime composes against, built by the host's composition (the app's
/// <c>App.cs</c> or <c>RemoteServer.Create</c>) from the real services. Nothing in <c>Plugins/</c> reaches a
/// sibling service another way.
/// </summary>
public sealed record PluginHostSeams
{
    /// <summary>The host state directory: <c>plugins/</c> and <c>plugin-state/</c> live under it. Null keeps no files.</summary>
    public required string? StateDirectory { get; init; }

    /// <summary>Settings namespaces, plugin roots, the roster and agent records.</summary>
    public required HostStateStore State { get; init; }

    /// <summary>The loopback HTTP/1.1 server's base URL, starting it when needed.</summary>
    public Func<string> PublicBaseUrl { get; init; } = () => throw new InvalidOperationException("This host serves no loopback routes.");

    public IPluginTerminalSeams? Terminals { get; init; }

    /// <summary>The worktrees of a project root; empty for a folder without Git.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<WorktreeInfo>>> Worktrees { get; init; } = async (root, ct) =>
    {
        try { return await GitRepository.ListWorktreesAsync(root, ct); }
        catch (Exception error) when (error is IOException or System.ComponentModel.Win32Exception) { return []; }
    };

    public Func<string, IReadOnlyList<string>, GitRunOptions?, CancellationToken, Task<GitRunResult>> Git { get; init; } = GitRepository.RunBoundedAsync;

    /// <summary>Writes one log line: the plugin id (or "plugins"), a level and the message.</summary>
    public Action<string, string, string> Log { get; init; } = (scope, level, message) => Console.Error.WriteLine($"[{level}] {scope}: {message}");

    /// <summary>Where builtin plugins' assets are staged, as <c>&lt;id&gt;/&lt;assets&gt;</c>.</summary>
    public string BuiltinDirectory { get; init; } = Path.Combine(AppContext.BaseDirectory, "plugins");

    public IReadOnlyList<(PluginManifest Manifest, PluginHostModule? Host)> Builtins { get; init; } = BuiltinPlugins.All;

    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan DisposeTimeout { get; init; } = TimeSpan.FromSeconds(5);
}