using System.Collections;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core;

// Owns every terminal session for the lifetime of the host, and the catalog of terminal tabs its clients
// share. Clients attach and detach; a shell ends only when its tab is closed, its workspace is removed, it
// exits, or the host stops.
public sealed class PtyTerminalService : ITerminalService, ITerminalCatalogService, IAsyncDisposable, IPluginTerminalSeams
{
    private readonly Dictionary<string, HostedTerminal> sessions = [];
    // The workspace folder each running session was started in.
    private readonly Dictionary<string, string> roots = [];
    private readonly ConcurrentDictionary<(string Workspace, string Path), string> linkedFiles = new();
    public bool AllowsFileLink(string workspace, string path) => linkedFiles.ContainsKey((workspace, path));
    private readonly TerminalCatalogStore catalog;
    private readonly TerminalSignalHub signals = new();
    // The tab each session shows, known once a client attaches with its tab key.
    private readonly Dictionary<string, TerminalRef> terminals = [];
    private readonly Dictionary<string, string> directories = [];
    // One MCP identity per session: its token names the workspace an agent in that shell works in.
    private readonly Dictionary<string, string> tokens = [];
    private readonly ConcurrentDictionary<string, (string Workspace, TerminalRef? Terminal)> owners = new(StringComparer.Ordinal);
    private readonly Lock gate = new();
    private readonly Dictionary<string, byte[]> pending = [];
    private readonly string? shell;
    private readonly TerminalRecordingStore? store;
    private readonly Func<int>? replayBytes;
    private readonly Func<bool>? keepAfterExit;
    private readonly Func<int>? undoCloseSeconds;
    // Sessions whose tab the user closed, kept for undo until their expiry fires or the tab is reserved again.
    private readonly Dictionary<string, CancellationTokenSource> closing = [];
    private readonly Func<bool>? shellIntegration;
    private bool disposed;

    /// <summary>The directory holding the <c>shell-integration</c> scripts; by default the one shipped with the app.</summary>
    public string ShellIntegrationResources { get; set; } = ShellIntegration.DefaultResources;

    /// <summary>
    /// Ghostty's default <c>abnormal-command-exit-runtime</c>: a shell that ends this soon after starting failed to launch,
    /// so its tab stays to show why. On macOS any exit code counts, elsewhere only a non-zero one, as in Ghostty.
    /// </summary>
    public static readonly TimeSpan AbnormalExitRuntime = TimeSpan.FromMilliseconds(250);

    /// <summary>Whether a shell that exited after running this long with this code exited abnormally, by Ghostty's rule.</summary>
    public static bool ExitedAbnormally(TimeSpan runtime, int code) =>
        runtime <= AbnormalExitRuntime && (OperatingSystem.IsMacOS() || code != 0);

    /// <summary>The loopback base URL of the host MCP route.</summary>
    public string? McpEndpoint { get; set; }

    public Func<TerminalRef, IReadOnlyDictionary<string, string>>? EnvironmentContributor { get; set; }
    public Func<TerminalRef, CancellationToken, ValueTask>? EnvironmentPreparer { get; set; }
    public Action<TerminalEvent>? Lifecycle { get; set; }
    public Func<TerminalRef, TerminalPrefill?>? RevivePrefill { get; set; }
    public Func<TerminalRef, TerminalPrefill, bool>? ReviveGate { get; set; }
    internal Func<string, CancellationToken, ValueTask>? PrepareRevival { get; set; }
    public Action<string, TerminalRef?>? SessionClosed { get; set; }

    /// <summary>The processes agents reported as their own; stopped with their terminal and when the service is disposed.</summary>
    public OwnedProcesses Owned { get; }

    // A null shell runs the user's login shell. With a recordings directory, the catalog is kept there, the last
    // screen of every session is saved when the service is disposed, and a tab attached again after a restart
    // starts a new shell showing it. replayBytes is read when a shell starts and sizes its replay snapshot; none
    // means the default 64 KiB. keepAfterExit is read when a catalogued shell exits: false closes its tab for every client,
    // like Ghostty's default (wait-after-command = false), unless it exited abnormally; none keeps every tab. shellIntegration is read when a POSIX shell starts and says whether
    // the shell-integration scripts are injected into it; none injects nothing.
    private readonly string clipboardDirectory;

    public PtyTerminalService(string? shell = null, string? recordingsDirectory = null, Func<int>? replayBytes = null, Func<bool>? keepAfterExit = null, Func<int>? undoCloseSeconds = null,
        Func<bool>? shellIntegration = null)
    {
        if (!OperatingSystem.IsWindows()) Posix.EnsureSupported();
        this.shell = shell; this.replayBytes = replayBytes; this.keepAfterExit = keepAfterExit; this.undoCloseSeconds = undoCloseSeconds; this.shellIntegration = shellIntegration;
        clipboardDirectory = recordingsDirectory is null ? Path.Combine(Path.GetTempPath(), "sharprail-clipboard", Guid.NewGuid().ToString("N")) : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(recordingsDirectory))!, "clipboard");
        catalog = new TerminalCatalogStore(recordingsDirectory);
        Owned = new OwnedProcesses(recordingsDirectory is null ? null : Path.GetDirectoryName(Path.GetFullPath(recordingsDirectory)));
        if (recordingsDirectory is null) return;
        _ = Task.Run(Owned.SweepAsync);
        store = new TerminalRecordingStore(recordingsDirectory);
        // Once a catalog exists, a recording revives only a tab that is still in it. A host that never kept one
        // keeps every recording until its clients have brought their tabs.
        var catalogued = catalog.Loaded ? catalog.Sessions() : null;
        foreach (var (id, bytes) in store.LoadAll())
            if (catalogued?.Contains(id) == false) store.Delete(id);
            else pending[id] = bytes;
    }

    public async ValueTask<string> SaveClipboardImageAsync(string sessionId, ReadOnlyMemory<byte> png, CancellationToken cancellationToken = default)
    {
        lock (gate) if (!sessions.ContainsKey(sessionId)) throw new IOException("The terminal session is no longer attached.");
        if (png.Length is < 8 or > 16 * 1024 * 1024 || !png.Span[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
            throw new ArgumentException("A clipboard image must be a PNG smaller than 16 MiB.", nameof(png));
        Directory.CreateDirectory(clipboardDirectory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(clipboardDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(clipboardDirectory, Guid.NewGuid().ToString("N") + ".png");
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None, Options = FileOptions.Asynchronous };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var file = new FileStream(path, options);
            await file.WriteAsync(png, cancellationToken);
        }
        catch
        {
            File.Delete(path);
            throw;
        }
        return path;
    }

    public TerminalCatalog Catalog => catalog.Current;

    public ValueTask<TerminalCatalog> OpenWorkspaceAsync(string workspaceRoot, IReadOnlyList<TerminalTab> tabs, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(catalog.OpenWorkspace(workspaceRoot, tabs));
    }

    public ValueTask<TerminalCatalog> ReserveAsync(string workspaceRoot, TerminalTab tab, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = catalog.Reserve(workspaceRoot, tab);
        // Reserving a tab closed for undo restores it: its session stays and the next attach finds its screen.
        lock (gate) if (closing.Remove(TerminalTab.SessionFor(workspaceRoot, tab.Key), out var expiry)) expiry.Cancel();
        return ValueTask.FromResult(result);
    }

    /// <summary>Sessions closed for undo and not yet ended.</summary>
    public int ClosingCount { get { lock (gate) return closing.Count; } }

    public async ValueTask<TerminalCatalog> CloseTabForUndoAsync(string workspaceRoot, string key, CancellationToken cancellationToken = default)
    {
        var keep = Math.Clamp(undoCloseSeconds?.Invoke() ?? 0, 0, HostSettings.MaxTerminalUndoCloseSeconds);
        var session = TerminalTab.SessionFor(workspaceRoot, key);
        bool live;
        lock (gate) live = !disposed && sessions.TryGetValue(session, out var terminal) && !terminal.Process.Exit.IsCompleted;
        // An exited shell has nothing to keep; zero turns undo off.
        if (keep == 0 || !live) return await CloseTabAsync(workspaceRoot, key, cancellationToken);
        var expiry = new CancellationTokenSource();
        // Marked before the tab leaves the catalog, which publishes: a watcher that sees the tab gone must find its session already waiting for undo.
        lock (gate)
        {
            if (closing.Remove(session, out var previous)) previous.Cancel();
            closing[session] = expiry;
        }
        TerminalCatalog result;
        try { result = catalog.Remove(workspaceRoot, key); }
        catch
        {
            lock (gate) if (closing.TryGetValue(session, out var current) && current == expiry) closing.Remove(session);
            expiry.Cancel();
            throw;
        }
        _ = ExpireAsync(session, expiry, TimeSpan.FromSeconds(keep));
        return result;
    }

    private async Task ExpireAsync(string session, CancellationTokenSource expiry, TimeSpan after)
    {
        try { await Task.Delay(after, expiry.Token); }
        catch (OperationCanceledException) { return; }
        lock (gate)
        {
            if (!closing.TryGetValue(session, out var current) || current != expiry) return;
            closing.Remove(session);
            if (catalog.Sessions().Contains(session)) return;
        }
        await EndAsync(session);
    }

    public async ValueTask<TerminalCatalog> CloseTabAsync(string workspaceRoot, string key, CancellationToken cancellationToken = default)
    {
        var result = catalog.Remove(workspaceRoot, key);
        await EndAsync(TerminalTab.SessionFor(workspaceRoot, key));
        return result;
    }

    public async ValueTask<TerminalCatalog> CloseWorkspaceAsync(string workspaceRoot, CancellationToken cancellationToken = default)
    {
        var ended = catalog.Forget(workspaceRoot).Select(tab => TerminalTab.SessionFor(workspaceRoot, tab.Key)).ToHashSet();
        // Sessions no tab names are rooted there too, and must not outlive the folder.
        var folder = Path.GetFullPath(workspaceRoot);
        lock (gate) ended.UnionWith(roots.Where(entry => entry.Value == folder).Select(entry => entry.Key));
        foreach (var session in ended) await EndAsync(session);
        return catalog.Current;
    }

    public IAsyncEnumerable<TerminalCatalog> WatchAsync(CancellationToken cancellationToken = default) => catalog.WatchAsync(cancellationToken);
    public IAsyncEnumerable<TerminalSignal> WatchSignalsAsync(CancellationToken cancellationToken = default) => signals.WatchAsync(cancellationToken);
    /// <summary>The session id a client derives for a tab: the hash of workspace root and tab key the app uses.</summary>
    public static string SessionFor(TerminalRef terminal) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(terminal.WorkspaceId + "\n" + terminal.TabKey)).AsSpan(0, 16));

    public async ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.SessionId)) throw new ArgumentException("A terminal session id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ClientId)) throw new ArgumentException("A terminal client id is required.", nameof(request));
        TerminalGrid.Require(request.Columns, request.Rows);
        var directory = Path.GetFullPath(request.WorkspaceRoot);
        ITerminalProcess process;
        HostedTerminal hosted;
        Attachment attachment;
        var started = Stopwatch.GetTimestamp();
        TerminalRef? terminal;
        // A plugin resolves the shell's environment here, awaited and outside the lock, so the contributor only reads it.
        if (EnvironmentPreparer is { } preparer)
        {
            TerminalRef? starting;
            lock (gate) starting = sessions.ContainsKey(request.SessionId) ? null
                : !string.IsNullOrEmpty(request.TabKey) ? new(directory, request.TabKey) : terminals.GetValueOrDefault(request.SessionId);
            if (starting is not null) await preparer(starting, cancellationToken);
        }
        var revived = false;
        // Lookup and start happen under one lock so concurrent attaches never start two shells for a session.
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!string.IsNullOrEmpty(request.TabKey)) terminals.TryAdd(request.SessionId, new(directory, request.TabKey));
            if (sessions.TryGetValue(request.SessionId, out var existing)) return existing.Attach(request, created: false);
            if (request.Resume) throw new IOException("The terminal session no longer exists.");
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"The workspace folder {directory} does not exist.");
            terminal = terminals.GetValueOrDefault(request.SessionId);
            var mcp = McpEndpoint is { } endpoint ? $"{endpoint}/mcp/{Mint(request.SessionId, directory, terminal)}" : null;
            var contributed = terminal is not null ? EnvironmentContributor?.Invoke(terminal) : null;
            // A revive hook's program, or the one the attach asked for, runs as the shell's program; on Windows it is
            // typed into the shell instead, as before.
            var prefill = terminal is not null ? RevivePrefill?.Invoke(terminal) : null;
            revived = prefill is not null;
            // A revival nobody asked for waits for the user: the gate offers it and the shell starts alone.
            if (prefill is not null && request.Program is not { Length: > 0 } && ReviveGate?.Invoke(terminal!, prefill) == true) prefill = null;
            var program = request.Program is { Length: > 0 } asked ? asked : prefill is { Program: true } ? prefill.Text : null;
            if (program is not null && OperatingSystem.IsWindows())
            {
                prefill = request.Program is { Length: > 0 } ? new TerminalPrefill(program, true) : prefill! with { Program = false };
                program = null;
            }
            else if (program is not null) prefill = null;
            process = Start(request, StartDirectory(request, directory), mcp, contributed, program);
            pending.Remove(request.SessionId, out var restored);
            var replay = Math.Clamp(replayBytes?.Invoke() ?? TerminalRecorder.SnapshotBytes, 0, TerminalRecorder.MaxSnapshotBytes);
            var known = terminal;
            hosted = new HostedTerminal(process, request.Columns, request.Rows, restored, replay)
            {
                InputAccepted = () => InputAccepted?.Invoke(directory),
                SessionId = request.SessionId,
                Signaled = signals.Publish,
                TitleChanged = known is null ? null : title => Retitled(request.SessionId, known, title),
                PromptChanged = (status, foreground) =>
                {
                    if (known is not null && status.Phase != TerminalShellPhase.Unknown)
                        Lifecycle?.Invoke(new TerminalPromptChanged(known, status.Phase != TerminalShellPhase.Running, status.LastExitCode, foreground));
                }
            };
            sessions.Add(request.SessionId, hosted);
            directories[request.SessionId] = directory;
            roots[request.SessionId] = directory;
            attachment = hosted.Attach(request, created: true, prefill);
        }
        if (terminal is not null && keepAfterExit is not null) _ = CloseAfterExitAsync(request.SessionId, hosted, started);
        if (terminal is not null && Lifecycle is { } lifecycle)
        {
            lifecycle(new TerminalSpawned(terminal, process.Pid));
            _ = process.Exit.ContinueWith(exit => lifecycle(new TerminalExited(terminal, exit.Result)),
                CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
        }
        if (revived && PrepareRevival is { } prepare) await prepare(directory, cancellationToken);
        return attachment;
    }

    // A tab whose shell exits closes for every client, as a user close would, unless the setting keeps it or the shell
    // failed to launch; its recording goes with it. A session closed for undo has no tab left to keep: its exit ends
    // it at once, so undo never brings back a finished program, and an exit close is never itself undoable.
    private async Task CloseAfterExitAsync(string sessionId, HostedTerminal hosted, long started)
    {
        int code;
        try { code = await hosted.Process.Exit; }
        catch (Exception) { return; }
        bool closedForUndo;
        lock (gate) closedForUndo = closing.ContainsKey(sessionId);
        if (!closedForUndo && (ExitedAbnormally(Stopwatch.GetElapsedTime(started), code) || keepAfterExit?.Invoke() != false)) return;
        lock (gate) if (disposed || sessions.GetValueOrDefault(sessionId) != hosted) return;
        try { await CloseAsync(sessionId); }
        catch (Exception) { }
    }

    // A terminal opened from another one starts where that one's shell last reported being (OSC 7), as Ghostty's
    // window-inherit-working-directory does; only within the same workspace, and only a directory that still exists.
    private string StartDirectory(TerminalAttachRequest request, string workspace)
    {
        if (request.DirectoryFrom is not { Length: > 0 } from || roots.GetValueOrDefault(from) != workspace ||
            sessions.GetValueOrDefault(from)?.Shell.Directory is not { } reported) return workspace;
        try { return Path.IsPathFullyQualified(reported) && Directory.Exists(reported) ? reported : workspace; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return workspace; }
    }

    public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // At a prompt (OSC 133 A or B after the last D) nothing runs, as Ghostty skips its close confirmation there.
        lock (gate) return ValueTask.FromResult(sessions.TryGetValue(sessionId, out var terminal) && terminal.Process.IsBusy &&
            terminal.Shell is not { Marked: true, Phase: TerminalShellPhase.Prompt or TerminalShellPhase.Input });
    }

    public ValueTask<TerminalShellStatus?> ShellStatusAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return ValueTask.FromResult(sessions.GetValueOrDefault(sessionId)?.Shell);
    }

    public ValueTask<bool> IsPasswordInputAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ITerminalProcess? process;
        lock (gate) process = sessions.GetValueOrDefault(sessionId)?.Process;
        return ValueTask.FromResult(process?.IsPasswordInput == true);
    }

    public async ValueTask<IReadOnlyList<TerminalFileLink>> FindFileLinksAsync(string sessionId, string text, CancellationToken cancellationToken = default)
    {
        ITerminalProcess? process;
        string? workspace, reported;
        lock (gate)
        {
            var hosted = sessions.GetValueOrDefault(sessionId);
            process = hosted?.Process; workspace = roots.GetValueOrDefault(sessionId); reported = hosted?.Shell.Directory;
        }
        if (process is null || text.Length > TerminalFileLinks.MaxLineLength) return [];
        // The directory the shell last reported (OSC 7) comes first; then the shell process's own, then the launch directory. A
        // POSIX directory probe fails only transiently. ConPTY never knows the directory, and guessing there would misreport
        // it after a cd, so Windows without a report links absolute paths only.
        var directory = reported is not null && Directory.Exists(reported) ? reported : process.CurrentDirectory ?? (OperatingSystem.IsWindows() ? null : workspace);
        var links = await Task.Run(() => TerminalFileLinks.Find(text, directory,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), cancellationToken), cancellationToken);
        lock (gate)
            if (workspace is not null && sessions.GetValueOrDefault(sessionId)?.Process == process)
                foreach (var link in links.Where(link => !link.IsDirectory)) linkedFiles[(workspace, link.Path)] = sessionId;
        return links;
    }

    public async ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        catalog.RemoveSession(sessionId);
        await EndAsync(sessionId);
    }

    private async ValueTask EndAsync(string sessionId)
    {
        HostedTerminal? terminal;
        TerminalRef? closed;
        lock (gate)
        {
            sessions.Remove(sessionId, out terminal);
            roots.Remove(sessionId);
            foreach (var link in linkedFiles.Where(link => link.Value == sessionId)) linkedFiles.TryRemove(link.Key, out _);
            pending.Remove(sessionId);
            if (closing.Remove(sessionId, out var expiry)) expiry.Cancel();
            terminals.Remove(sessionId, out closed);
            directories.Remove(sessionId);
            if (tokens.Remove(sessionId, out var token)) owners.TryRemove(token, out _);
        }
        store?.Delete(sessionId);
        signals.End(sessionId);
        if (terminal is not null) await terminal.DisposeAsync();
        if (closed is not null) await Owned.StopTerminalAsync(closed);
        SessionClosed?.Invoke(sessionId, closed);
    }

    /// <summary>The workspace root and tab of the terminal a token was minted for, or null for an unknown token.</summary>
    public (string Workspace, TerminalRef? Terminal)? McpOwner(string token) => owners.TryGetValue(token, out var owner) ? owner : null;

    /// <summary>Ends all sessions and MCP identities owned by a closing project, including detached worktrees.</summary>
    public async ValueTask CloseProjectAsync(string project, IReadOnlyList<string> workspaces)
    {
        var projectRoot = Path.GetFullPath(project);
        var roots = workspaces.Append(projectRoot).Select(Path.GetFullPath).ToHashSet(StringComparer.Ordinal);
        KeyValuePair<string, string>[] known;
        lock (gate)
            known = directories.Concat(tokens.Where(entry => owners.ContainsKey(entry.Value)).Select(entry =>
                new KeyValuePair<string, string>(entry.Key, owners[entry.Value].Workspace))).ToArray();
        var ids = known.Where(entry => roots.Contains(entry.Value) || ProjectServices.KnownCheckout(entry.Value)?.ProjectRoot == projectRoot)
            .Select(entry => entry.Key).Distinct().ToArray();
        foreach (var id in ids) await CloseAsync(id);
    }

    /// <summary>
    /// Raised with the workspace root of a shell that accepted a client's input. A watcher's ignored write, output and
    /// merely attaching are not input.
    /// </summary>
    public event Action<string>? InputAccepted;

    /// <summary>Raised with a tab's terminal and the title its program set with OSC 0/2 (null when cleared), coalesced.</summary>
    public event Action<TerminalRef, string?>? TitleChanged;

    // A title that arrives after its tab closed would outlive the tab's other state.
    private void Retitled(string sessionId, TerminalRef terminal, string? title)
    {
        lock (gate) if (!sessions.ContainsKey(sessionId)) return;
        TitleChanged?.Invoke(terminal, title);
    }

    public string Token(TerminalRef terminal)
    {
        lock (gate) return Mint(SessionOf(terminal), Path.GetFullPath(terminal.WorkspaceId), terminal);
    }

    public TerminalRef? ForToken(string token) => owners.TryGetValue(token, out var owner) ? owner.Terminal : null;

    public void Write(TerminalRef terminal, string data)
    {
        HostedTerminal? target;
        lock (gate) target = sessions.GetValueOrDefault(SessionOf(terminal));
        if (target is null) return;
        try { target.Process.WriteAsync(Encoding.UTF8.GetBytes(data)).AsTask().GetAwaiter().GetResult(); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    public IReadOnlyList<TerminalProcess> List()
    {
        lock (gate)
            return [.. terminals.Where(entry => sessions.ContainsKey(entry.Key)).Select(entry =>
                new TerminalProcess(entry.Value, sessions[entry.Key].Process.Exit.IsCompleted ? null : sessions[entry.Key].Process.Pid))];
    }

    public string? WorkspaceForProcess(int pid)
    {
        Dictionary<int, string> shells;
        lock (gate) shells = sessions.Where(entry => !entry.Value.Process.Exit.IsCompleted)
            .ToDictionary(entry => entry.Value.Process.Pid, entry => directories[entry.Key]);
        if (shells.Count == 0) return null;
        var parents = ProcessParents();
        var seen = new HashSet<int>();
        for (var current = pid; current > 1 && seen.Add(current); current = parents.GetValueOrDefault(current))
            if (shells.TryGetValue(current, out var workspace)) return workspace;
        return null;
    }

    // Linux reads /proc; macOS asks ps once rather than once per ancestor.
    private static Dictionary<int, int> ProcessParents()
    {
        if (OperatingSystem.IsWindows()) return WindowsProcesses.Parents();
        var parents = new Dictionary<int, int>();
        try
        {
            if (Directory.Exists("/proc/self"))
            {
                foreach (var directory in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(directory), out var id)) continue;
                    try
                    {
                        var stat = File.ReadAllText(Path.Combine(directory, "stat"));
                        var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ');
                        if (fields.Length > 1 && int.TryParse(fields[1], out var parent)) parents[id] = parent;
                    }
                    catch (IOException) { }
                }
                return parents;
            }
            var start = new System.Diagnostics.ProcessStartInfo("/bin/ps", "-A -o pid= -o ppid=") { RedirectStandardOutput = true, UseShellExecute = false };
            using var ps = System.Diagnostics.Process.Start(start);
            if (ps is null) return parents;
            foreach (var line in ps.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length == 2 && int.TryParse(fields[0], out var id) && int.TryParse(fields[1], out var parent)) parents[id] = parent;
            }
            ps.WaitForExit(2000);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { }
        return parents;
    }

    private string SessionOf(TerminalRef terminal)
    {
        foreach (var (session, known) in terminals)
            if (known == terminal) return session;
        return SessionFor(terminal);
    }

    private string Mint(string sessionId, string workspace, TerminalRef? terminal)
    {
        if (!tokens.TryGetValue(sessionId, out var token))
            tokens[sessionId] = token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        owners[token] = (workspace, terminal ?? (owners.TryGetValue(token, out var known) ? known.Terminal : null));
        return token;
    }

    public async ValueTask DisposeAsync()
    {
        KeyValuePair<string, HostedTerminal>[] all;
        lock (gate)
        {
            disposed = true; all = [.. sessions]; sessions.Clear(); roots.Clear(); linkedFiles.Clear();
            // A tab still waiting for undo was closed: stopping the host ends it for good.
            foreach (var (id, expiry) in closing) { expiry.Cancel(); store?.Delete(id); }
            if (store is not null)
            {
                foreach (var (id, bytes) in pending) store.Save(id, bytes);
                foreach (var (id, terminal) in all.Where(entry => !closing.ContainsKey(entry.Key))) store.Save(id, terminal.Recording());
            }
            closing.Clear();
        }
        await Owned.StopAllAsync();
        foreach (var (_, terminal) in all) await terminal.DisposeAsync();
        signals.Dispose();
    }

    // The native reason, the executable path and the value of SHELL stay on the host: a client is told only
    // which choice failed and what to do about it.
    private ITerminalProcess Start(TerminalAttachRequest request, string directory, string? mcpUrl, IReadOnlyDictionary<string, string>? contributed, string? program)
    {
        var fromEnvironment = shell is null && ConfiguredShell() is not null;
        var shellPath = shell ?? LoginShell();
        try
        {
            // macOS execs the shell from a trampoline, so a shell that cannot run would otherwise look like one that exited.
            if (!Runnable(shellPath)) throw new IOException("The shell is not an executable file.");
            if (OperatingSystem.IsWindows()) return ConPtySession.Start(request.SessionId, shellPath, directory, request.Columns, request.Rows, mcpUrl, contributed);
            var integration = shellIntegration?.Invoke() == true ? ShellIntegrationResources : null;
            return PtySession.Start(request.SessionId, shellPath, directory, request.Columns, request.Rows, mcpUrl, contributed, program, integration);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new IOException(fromEnvironment
                ? "Couldn’t start the shell configured by SHELL. Fix or clear SHELL in the host environment, restart SharpRail, then retry."
                : "Couldn’t start the configured shell. Check the host’s shell installation, then retry.", error);
        }
    }

    private static bool Runnable(string program) =>
        File.Exists(program) && (OperatingSystem.IsWindows() || (File.GetUnixFileMode(program) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0);

    private static string? ConfiguredShell() =>
        Environment.GetEnvironmentVariable("SHELL") is { } configured && !string.IsNullOrWhiteSpace(configured) && File.Exists(configured) ? configured : null;

    private static string LoginShell() => ConfiguredShell() ?? (OperatingSystem.IsWindows() ? WindowsProcesses.DefaultShell() : File.Exists("/bin/zsh") && Posix.Mac ? "/bin/zsh" : "/bin/sh");

    private static readonly string[] ClaudeSessionMarkers =
    [
        "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_SESSION_ID",
        "CLAUDE_CODE_BRIDGE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID", "CLAUDE_EFFORT"
    ];

    // Plugins name per-terminal status endpoints SHARPRAIL_*_STATUS_URL or THINKRAIL_*_STATUS_URL, so a plugin's variable is covered without listing it here.
    internal static bool IsTerminalIdentityVariable(string name) =>
        name.EndsWith("_STATUS_URL", StringComparison.Ordinal) &&
        (name.StartsWith("SHARPRAIL_", StringComparison.Ordinal) || name.StartsWith("THINKRAIL_", StringComparison.Ordinal));

    internal static string?[] ShellEnvironment(string? mcpUrl = null, IReadOnlyDictionary<string, string>? contributed = null, string shell = "") =>
        ShellEnvironment(mcpUrl, contributed, shell, null, out _);

    // The environment plugins' hooks apply first; with integration (the scripts' directory), the shell-integration
    // injection is applied last, to the environment the shell would otherwise get, and its extra shell arguments are returned.
    // zsh's injection keeps the hooks' ZDOTDIR as the one it restores, so both run.
    internal static string?[] ShellEnvironment(string? mcpUrl, IReadOnlyDictionary<string, string>? contributed, string shell,
        string? integration, out string[] arguments)
    {
        arguments = [];
        var variables = new SortedDictionary<string, string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value ?? "";
        // The host's session token authorizes this process; it must not reach user shells.
        variables.Remove("SHARPRAIL_TOKEN");
        // An app launched from inside a Claude Code session inherits that session's identity; a Claude started in a
        // new terminal would take itself for its child and stop saving transcripts. Configuration variables stay.
        foreach (var marker in ClaudeSessionMarkers) variables.Remove(marker);
        // An app started from a Ghostty or SharpRail terminal inherits integration variables; only the host's own injection sets them.
        foreach (var key in variables.Keys.Where(key => key.StartsWith("GHOSTTY_", StringComparison.Ordinal) ||
            key.StartsWith(ShellIntegration.VariablePrefix, StringComparison.Ordinal)).ToArray()) variables.Remove(key);
        // An outer SharpRail's per-terminal identity (status URLs carry the terminal's token) must not reach this host's
        // terminals: an inner agent's hooks would report to the outer tab. This host's own contributions are applied below.
        foreach (var key in variables.Keys.Where(IsTerminalIdentityVariable).ToArray()) variables.Remove(key);
        variables["TERM"] = "xterm-256color";
        variables["COLORTERM"] = "truecolor";
        // The PTY registers no utmpx record, so a login shell without LOGNAME asks getlogin(), which reads
        // whatever stale record the reused device carries. The name comes from the uid instead.
        variables["USER"] = variables["LOGNAME"] = Environment.UserName;
        // The name the ThinkRail Claude Code plugin's .mcp.json expands, so it works unchanged here.
        if (mcpUrl is null) variables.Remove("THINKRAIL_MCP_URL");
        else variables["THINKRAIL_MCP_URL"] = mcpUrl;
        var locale = variables.GetValueOrDefault("LC_ALL") is { Length: > 0 } all ? all
            : variables.GetValueOrDefault("LC_CTYPE") is { Length: > 0 } ctype ? ctype : variables.GetValueOrDefault("LANG") ?? "";
        if (!locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) && !locale.Contains("utf8", StringComparison.OrdinalIgnoreCase))
        {
            variables.Remove("LC_ALL");
            variables["LANG"] = "en_US.UTF-8";
            variables["LC_CTYPE"] = "en_US.UTF-8";
        }
        // Plugin contributions come after core's own variables, and never re-expose the host's token.
        foreach (var (key, value) in contributed ?? new Dictionary<string, string>())
            if (key.Length > 0 && !key.Equals("SHARPRAIL_TOKEN", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) && !key.Contains('=') && !key.Contains('\0') && !value.Contains('\0')) variables[key] = value;
        ShellEnvironmentHooks.Apply(variables, shell);
        if (integration is not null && ShellIntegration.For(shell, integration, variables) is { } injected)
        {
            foreach (var (key, value) in injected.Variables) variables[key] = value;
            arguments = injected.Arguments;
        }
        return [.. variables.Select(pair => pair.Key + "=" + pair.Value), null];
    }
}

// The shell process behind a hosted terminal.
internal sealed class PtySession : ITerminalProcess
{
    private readonly int master;
    // Held open so macOS keeps output the shell wrote just before exiting.
    private readonly int slave;
    private readonly int pid;
    // Bounded, so a consumer that stalls blocks the read pump and the kernel holds the program back.
    private readonly Channel<TerminalChunk> output = Channel.CreateBounded<TerminalChunk>(new BoundedChannelOptions(16) { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock writing = new();
    private int reading, disposed;

    public string Id { get; }
    public int Pid => pid;
    public string? CurrentDirectory => exited.Task.IsCompleted || Volatile.Read(ref disposed) != 0 ? null : Posix.ProcessDirectory(pid);
    public Task<int> Exit => exit.Task;
    public bool IsBusy
    {
        get
        {
            if (exited.Task.IsCompleted || Volatile.Read(ref disposed) != 0) return false;
            var group = Posix.ForegroundGroup(master);
            return group > 0 && group != pid;
        }
    }
    // The slave side carries the line discipline the reading program set.
    public bool IsPasswordInput => !exited.Task.IsCompleted && Volatile.Read(ref disposed) == 0 && Posix.IsPasswordInput(slave);

    private PtySession(string id, int master, int slave, int pid)
    {
        Id = id; this.master = master; this.slave = slave; this.pid = pid;
        new Thread(Wait) { IsBackground = true, Name = "SharpRail PTY wait " + pid }.Start();
        new Thread(Pump) { IsBackground = true, Name = "SharpRail PTY read " + pid }.Start();
    }

    internal static PtySession Start(string id, string shell, string directory, int columns, int rows, string? mcpUrl = null,
        IReadOnlyDictionary<string, string>? contributed = null, string? program = null, string? shellIntegration = null)
    {
        var master = Posix.OpenPt(Posix.ORdwr | Posix.ONoctty);
        if (master < 0) throw new IOException("Couldn't open a pseudo-terminal: " + Posix.LastError());
        var slave = -1;
        try
        {
            if (Posix.GrantPt(master) != 0 || Posix.UnlockPt(master) != 0)
                throw new IOException("Couldn't prepare the pseudo-terminal: " + Posix.LastError());
            var name = new byte[256];
            var failure = Posix.PtsName(master, name, name.Length);
            if (failure != 0) throw new IOException("Couldn't name the pseudo-terminal: " + Posix.Error(failure));
            var device = Encoding.UTF8.GetString(name, 0, Array.IndexOf(name, (byte)0));
            slave = Posix.Open(device, Posix.ORdwr | Posix.ONoctty);
            if (slave < 0) throw new IOException("Couldn't open the pseudo-terminal: " + Posix.LastError());
            Posix.SetWindowSize(master, columns, rows);
            var environment = PtyTerminalService.ShellEnvironment(mcpUrl, contributed, shell, shellIntegration, out var options);
            var pid = Spawn(shell, directory, device, environment, program, options);
            return new PtySession(id, master, slave, pid);
        }
        catch
        {
            if (slave >= 0) Posix.Close(slave);
            Posix.Close(master);
            throw;
        }
    }

    private static int Spawn(string shell, string directory, string device, string?[] environment, string? program, string[] options)
    {
        // posix_spawnattr_t and posix_spawn_file_actions_t are pointers on macOS and structs on Linux.
        var attributes = Marshal.AllocHGlobal(1024);
        var actions = Marshal.AllocHGlobal(1024);
        var mask = Marshal.AllocHGlobal(256);
        var defaults = Marshal.AllocHGlobal(256);
        var attributesReady = false; var actionsReady = false;
        try
        {
            Check(Posix.SpawnAttrInit(attributes)); attributesReady = true;
            Check(Posix.FileActionsInit(actions)); actionsReady = true;
            Posix.SigEmptySet(mask); Posix.SigFillSet(defaults);
            Check(Posix.SpawnAttrSetMask(attributes, mask));
            Check(Posix.SpawnAttrSetDefault(attributes, defaults));
            // The new session leader acquires the slave as its controlling terminal when it opens it.
            Check(Posix.SpawnAttrSetFlags(attributes, Posix.SpawnFlags));
            Check(Posix.FileActionsOpen(actions, 0, device, Posix.ORdwr, 0));
            Check(Posix.FileActionsDup(actions, 0, 1));
            Check(Posix.FileActionsDup(actions, 0, 2));
            Check(Posix.FileActionsChdir(actions, directory));
            // macOS applies file actions before POSIX_SPAWN_SETSID, so the slave opened above cannot become
            // the controlling terminal. A trampoline reopens it as the new session leader, then becomes the shell.
            // A program runs through the same login shell, interactive so its rc files apply, then leaves an
            // interactive shell behind (TerminalProgram). Shell integration's options (bash's --posix) come before the login option.
            string[] tail = program is null ? [] : ["-i", "-c", TerminalProgram.Script(shell, program)];
            string?[] arguments = Posix.Mac
                ? ["sh", "-c", "exec 3<>\"$1\" 3>&-; shift; exec \"$@\"", "sh", device, shell, .. options, "-l", .. tail, null]
                : ["-" + Path.GetFileName(shell), .. options, .. tail, null];
            var executable = Posix.Mac ? "/bin/sh" : shell;
            var result = Posix.Spawn(out var pid, executable, actions, attributes, arguments, environment);
            if (result != 0) throw new IOException($"Couldn't start {shell}: {Posix.Error(result)}.");
            return pid;
        }
        finally
        {
            if (actionsReady) Posix.FileActionsDestroy(actions);
            if (attributesReady) Posix.SpawnAttrDestroy(attributes);
            Marshal.FreeHGlobal(attributes); Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(mask); Marshal.FreeHGlobal(defaults);
        }

        static void Check(int result)
        {
            if (result != 0) throw new IOException("Couldn't configure the shell process: " + Posix.Error(result));
        }
    }

    private void Wait()
    {
        int status;
        while (Posix.WaitPid(pid, out status, 0) < 0)
            if (Marshal.GetLastPInvokeError() != Posix.Eintr) { exited.TrySetResult(-1); return; }
        var signal = status & 0x7f;
        exited.TrySetResult(signal == 0 ? (status >> 8) & 0xff : 128 + signal);
    }

    private void Pump()
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (true)
            {
                var poll = new Posix.PollFd { Fd = master, Events = Posix.PollIn };
                var ready = Posix.Poll(ref poll, 1, 50);
                if (ready < 0 && Marshal.GetLastPInvokeError() == Posix.Eintr) continue;
                // Background jobs can keep the slave open; after the shell exits, drain what is buffered.
                if (ready == 0) { if (exited.Task.IsCompleted) break; continue; }
                if (ready < 0) break;
                var count = Posix.Read(master, buffer, buffer.Length);
                if (count < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is Posix.Eintr or Posix.Eagain or Posix.EagainLinux) continue;
                    break;
                }
                if (count == 0) break;
                // The read buffer is reused; each chunk is copied once into a right-sized pooled array.
                var chunk = TerminalChunk.Copy(buffer.AsSpan(0, (int)count));
                if (!output.Writer.TryWrite(chunk)) output.Writer.WriteAsync(chunk).AsTask().GetAwaiter().GetResult();
            }
        }
        finally
        {
            output.Writer.TryComplete();
            exited.Task.ContinueWith(task => exit.TrySetResult(task.Result), TaskScheduler.Default);
        }
    }

    public async IAsyncEnumerable<TerminalChunk> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref reading, 1) != 0) throw new InvalidOperationException("A terminal session has a single reader.");
        await foreach (var chunk in output.Reader.ReadAllAsync(cancellationToken)) yield return chunk;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (exited.Task.IsCompleted) return ValueTask.CompletedTask;
        lock (writing)
        {
            var span = data.Span;
            while (span.Length > 0)
            {
                var count = Posix.Write(master, ref MemoryMarshal.GetReference(span), span.Length);
                if (count < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error is Posix.Eintr or Posix.Eagain or Posix.EagainLinux) { Thread.Sleep(1); continue; }
                    throw new IOException("Couldn't write to the terminal: " + Posix.Error(error));
                }
                span = span[(int)count..];
            }
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask ResizeAsync(int columns, int rows, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Volatile.Read(ref disposed) == 0 && !exited.Task.IsCompleted && Posix.SetWindowSize(master, columns, rows) != 0)
            throw new IOException("Couldn't resize the terminal: " + Posix.LastError());
        return ValueTask.CompletedTask;
    }

    public async ValueTask KillAsync(CancellationToken cancellationToken = default)
    {
        if (exited.Task.IsCompleted) return;
        // Job control puts an agent in a process group of its own, so the shell's group misses it: the descendants and the
        // terminal's foreground group are captured first, hung up with the shell, and killed if they outlast it.
        var foreground = Posix.ForegroundGroup(master);
        var rest = await Task.Run(() => ProcessTable.Capture(pid, foreground), cancellationToken);
        rest.Remove(pid);
        // The shell leads its own session and process group.
        Posix.Kill(-pid, Posix.Sighup);
        Posix.Kill(pid, Posix.Sighup);
        ProcessTable.Signal(rest.Keys, Posix.Sighup);
        try { await exited.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
        catch (TimeoutException)
        {
            Posix.Kill(-pid, Posix.Sigkill);
            Posix.Kill(pid, Posix.Sigkill);
            await exited.Task.WaitAsync(cancellationToken);
        }
        // Stragglers get the hang-up's grace, then SIGKILL.
        var until = Environment.TickCount64 + 2000;
        while (Environment.TickCount64 < until && await Task.Run(() => ProcessTable.Alive(rest).Count, cancellationToken) > 0) await Task.Delay(100, cancellationToken);
        await ProcessTable.EndAsync(rest, TimeSpan.FromSeconds(1));
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        await KillAsync();
        await exit.Task;
        Posix.Close(slave);
        Posix.Close(master);
    }
}