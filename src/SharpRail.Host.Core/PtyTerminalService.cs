using System.Collections;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core;

// Owns every terminal session for the lifetime of the host. Clients attach and detach; a shell ends only
// when its tab is closed, it exits, or the host stops.
public sealed class PtyTerminalService : ITerminalService, IAsyncDisposable
{
    private readonly Dictionary<string, HostedTerminal> sessions = [];
    private readonly Lock gate = new();
    private readonly Dictionary<string, byte[]> pending = [];
    private readonly string? shell;
    private readonly TerminalRecordingStore? store;
    private bool disposed;

    // A null shell runs the user's login shell. With a recordings directory, the last screen of every session is
    // saved when the service is disposed, and a tab attached again after a restart starts a new shell showing it.
    public PtyTerminalService(string? shell = null, string? recordingsDirectory = null)
    {
        Posix.EnsureSupported();
        this.shell = shell;
        if (recordingsDirectory is null) return;
        store = new TerminalRecordingStore(recordingsDirectory);
        foreach (var (id, bytes) in store.LoadAll()) pending[id] = bytes;
    }

    public ValueTask<ITerminalSession> AttachAsync(TerminalAttachRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.SessionId)) throw new ArgumentException("A terminal session id is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.ClientId)) throw new ArgumentException("A terminal client id is required.", nameof(request));
        // Lookup and start happen under one lock so concurrent attaches never start two shells for a session.
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (sessions.TryGetValue(request.SessionId, out var existing)) return ValueTask.FromResult<ITerminalSession>(existing.Attach(request, created: false));
            if (request.Resume) throw new IOException("The terminal session no longer exists.");
            var directory = Path.GetFullPath(request.WorkspaceRoot);
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException($"The workspace folder {directory} does not exist.");
            var process = PtySession.Start(request.SessionId, shell ?? LoginShell(), directory, request.Columns, request.Rows);
            // A recording is consumed only once the shell runs, so a failed start keeps it for the retry.
            pending.Remove(request.SessionId, out var restored);
            var terminal = new HostedTerminal(process, request.Columns, request.Rows, restored);
            sessions.Add(request.SessionId, terminal);
            return ValueTask.FromResult<ITerminalSession>(terminal.Attach(request, created: true));
        }
    }

    public ValueTask<bool> IsBusyAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate) return ValueTask.FromResult(sessions.TryGetValue(sessionId, out var terminal) && terminal.Process.IsBusy);
    }

    public async ValueTask CloseAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        HostedTerminal? terminal;
        lock (gate)
        {
            sessions.Remove(sessionId, out terminal);
            pending.Remove(sessionId);
        }
        store?.Delete(sessionId);
        if (terminal is not null) await terminal.Process.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        KeyValuePair<string, HostedTerminal>[] all;
        lock (gate)
        {
            disposed = true; all = [.. sessions]; sessions.Clear();
            if (store is not null)
            {
                foreach (var (id, bytes) in pending) store.Save(id, bytes);
                foreach (var (id, terminal) in all) store.Save(id, terminal.Recording());
            }
        }
        foreach (var (_, terminal) in all) await terminal.Process.DisposeAsync();
    }

    private static string LoginShell()
    {
        var configured = Environment.GetEnvironmentVariable("SHELL");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return File.Exists("/bin/zsh") && Posix.Mac ? "/bin/zsh" : "/bin/sh";
    }

    internal static string?[] ShellEnvironment()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            variables[(string)entry.Key] = (string?)entry.Value ?? "";
        // The host's session token authorizes this process; it must not reach user shells.
        variables.Remove("SHARPRAIL_TOKEN");
        variables["TERM"] = "xterm-256color";
        variables["COLORTERM"] = "truecolor";
        var locale = variables.GetValueOrDefault("LC_ALL") is { Length: > 0 } all ? all
            : variables.GetValueOrDefault("LC_CTYPE") is { Length: > 0 } ctype ? ctype : variables.GetValueOrDefault("LANG") ?? "";
        if (!locale.Contains("UTF-8", StringComparison.OrdinalIgnoreCase) && !locale.Contains("utf8", StringComparison.OrdinalIgnoreCase))
        {
            variables.Remove("LC_ALL");
            variables["LANG"] = "en_US.UTF-8";
            variables["LC_CTYPE"] = "en_US.UTF-8";
        }
        return [.. variables.Select(pair => pair.Key + "=" + pair.Value), null];
    }
}

// The shell process behind a hosted terminal.
internal sealed class PtySession : IAsyncDisposable
{
    private readonly int master;
    // Held open so macOS keeps output the shell wrote just before exiting.
    private readonly int slave;
    private readonly int pid;
    private readonly Channel<ReadOnlyMemory<byte>> output = Channel.CreateUnbounded<ReadOnlyMemory<byte>>(new() { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<int> exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock writing = new();
    private int reading, disposed;

    public string Id { get; }
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

    private PtySession(string id, int master, int slave, int pid)
    {
        Id = id; this.master = master; this.slave = slave; this.pid = pid;
        new Thread(Wait) { IsBackground = true, Name = "SharpRail PTY wait " + pid }.Start();
        new Thread(Pump) { IsBackground = true, Name = "SharpRail PTY read " + pid }.Start();
    }

    internal static PtySession Start(string id, string shell, string directory, int columns, int rows)
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
            var pid = Spawn(shell, directory, device);
            return new PtySession(id, master, slave, pid);
        }
        catch
        {
            if (slave >= 0) Posix.Close(slave);
            Posix.Close(master);
            throw;
        }
    }

    private static int Spawn(string shell, string directory, string device)
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
            var (program, arguments) = Posix.Mac
                ? ("/bin/sh", new[] { "sh", "-c", "exec 3<>\"$1\" 3>&-; shift; exec \"$@\"", "sh", device, shell, "-l", null })
                : (shell, new[] { "-" + Path.GetFileName(shell), null });
            var result = Posix.Spawn(out var pid, program, actions, attributes, arguments, PtyTerminalService.ShellEnvironment());
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
                output.Writer.TryWrite(buffer.AsSpan(0, (int)count).ToArray());
            }
        }
        finally
        {
            output.Writer.TryComplete();
            exited.Task.ContinueWith(task => exit.TrySetResult(task.Result), TaskScheduler.Default);
        }
    }

    public async IAsyncEnumerable<ReadOnlyMemory<byte>> ReadAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
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
        // The shell leads its own session and process group.
        Posix.Kill(-pid, Posix.Sighup);
        Posix.Kill(pid, Posix.Sighup);
        try { await exited.Task.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); }
        catch (TimeoutException)
        {
            Posix.Kill(-pid, Posix.Sigkill);
            Posix.Kill(pid, Posix.Sigkill);
            await exited.Task.WaitAsync(cancellationToken);
        }
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