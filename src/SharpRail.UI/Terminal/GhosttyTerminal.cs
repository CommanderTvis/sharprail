using System.Runtime.Versioning;

using Avalonia.Controls;
using Avalonia.Input;

using Ghostty.Avalonia;


namespace SharpRail.UI.Terminal;

// Ghostty's Metal renderer, composed as a texture; its child is the relay.
[SupportedOSPlatform("macos")]
internal sealed class GhosttyTerminal : Border, ITerminalBackend
{
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TerminalLaunch launch;
    private RemoteTerminalConnection? connection;
    private GhosttyTextureView? texture;
    private SkiaTerminal? fallback;
    private Task? fallbackStart;
    private bool switching;
    private string? statusPath, connectionPath;
    private bool disposed;
    private bool agentNewline;

    // The relay attaches to the host session; exit status and takeover come back through its status file,
    // because Ghostty's login wrapper does not propagate the child's exit code.
    internal GhosttyTerminal(TerminalLaunch launch, Task<RemoteTerminalConnection> connection)
    {
        this.launch = launch;
        Focusable = true;
        Started = StartAsync(connection);
    }

    public Control View => this;
    public Task Started { get; }
    public Task<int> Exited => exited.Task;
    public Task Detached => detached.Task;

    private async Task StartAsync(Task<RemoteTerminalConnection> pending)
    {
        var remote = await pending;
        ObjectDisposedException.ThrowIf(disposed, this);
        connection = remote;
        // Each attachment gets its own relay files, so a take-back never races the files of the one it replaces.
        var name = launch.SessionId + "-" + Guid.NewGuid().ToString("N");
        statusPath = Path.Combine(TerminalRelay.Directory, name + ".status");
        connectionPath = TerminalRelay.Write(name, new(remote.Endpoint.ToString(), remote.Token, launch.SessionId, launch.ClientId, launch.WorkspaceRoot, statusPath, launch.TabKey));
        var executable = Environment.ProcessPath ?? throw new TerminalStartException("The SharpRail executable path is unknown.");
        var command = "'" + executable.Replace("'", "'\\''") + "' " + TerminalRelay.Argument;
        try
        {
            var child = new GhosttyLaunch(launch.WorkspaceRoot, command,
                new(TerminalRelay.ConnectionVariable, connectionPath), launch.ClipboardDirectory);

            texture = new GhosttyTextureView(child) { Colors = TerminalTheme.Colors(), AgentNewline = agentNewline };
            texture.Exited += (_, code) => Ended(code);
            texture.OperationFailed += TextureFailed;
            Child = texture;
            Ui.ThemeChanged += UpdateColors;
            await texture.Ready;
        }
        catch (Exception error) when (!disposed && error is InvalidOperationException or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            await UseSkia();
        }
    }

    private async void TextureFailed(object? sender, Exception error)
    {
        try { await UseSkia(); }
        catch (Exception failure) { if (!disposed) exited.TrySetException(failure); }
    }

    private Task UseSkia() => fallbackStart ??= StartSkia();

    private async Task StartSkia()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        switching = true;
        var focused = IsKeyboardFocusWithin;
        Ui.ThemeChanged -= UpdateColors;
        if (texture is not null) texture.OperationFailed -= TextureFailed;
        texture?.Dispose();
        texture = null;
        DeleteRelayFiles();
        fallback = new SkiaTerminal(launch, connection!.Terminals);
        fallback.SetAgentNewline(agentNewline);
        Child = fallback.View;
        await fallback.Started;
        if (disposed) return;
        if (focused) fallback.FocusTerminal();
        _ = ObserveSkia();
    }

    private async Task ObserveSkia()
    {
        try
        {
            await Task.WhenAny(fallback!.Exited, fallback.Detached);
            if (disposed) return;
            if (fallback.Detached.IsCompleted) detached.TrySetResult();
            else exited.TrySetResult(await fallback.Exited);
        }
        catch (Exception error) { if (!disposed) exited.TrySetException(error); }
    }

    private void UpdateColors()
    {
        if (texture is not null) texture.Colors = TerminalTheme.Colors();
    }

    public ValueTask<bool> IsBusyAsync()
    {
        if (exited.Task.IsCompleted || detached.Task.IsCompleted || connection is null) return ValueTask.FromResult(false);
        return connection.Terminals.IsBusyAsync(launch.SessionId);
    }

    public ValueTask CloseAsync() => connection?.Terminals.CloseAsync(launch.SessionId) ?? ValueTask.CompletedTask;

    private void Ended(int value)
    {
        if (disposed || switching) return;
        var status = statusPath is not null && File.Exists(statusPath) ? File.ReadAllText(statusPath) : null;
        if (statusPath is not null) File.Delete(statusPath);
        if (status == TerminalRelay.DetachedStatus) detached.TrySetResult();
        else if (status is not null && status.StartsWith(TerminalRelay.ExitStatus, StringComparison.Ordinal) &&
            int.TryParse(status[TerminalRelay.ExitStatus.Length..], out var code))
            exited.TrySetResult(code);
        else exited.TrySetException(new TerminalStartException(status ?? $"The terminal relay ended unexpectedly with code {value}."));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Ui.ThemeChanged -= UpdateColors;
        texture?.Dispose();
        fallback?.Dispose();
        DeleteRelayFiles();
    }

    private void DeleteRelayFiles()
    {
        foreach (var path in new[] { connectionPath, statusPath })
            if (path is not null) File.Delete(path);
    }

    public void FocusTerminal()
    {
        texture?.FocusTerminal();
        fallback?.FocusTerminal();
    }

    public void Write(string data)
    {
        if (texture is not null) texture.Type(data);
        else fallback?.Write(data);
    }

    public string ReadScreen() => texture?.ReadScreen() ?? fallback?.ReadScreen() ?? "";

    public void SetAgentNewline(bool enabled)
    {
        agentNewline = enabled;
        if (texture is not null) texture.AgentNewline = enabled;
        fallback?.SetAgentNewline(enabled);
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source == this) FocusTerminal();
    }
}