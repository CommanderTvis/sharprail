using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Threading;
using SharpRail.UI.Rendering;

namespace SharpRail.UI.Terminal;

[SupportedOSPlatform("macos")]
internal sealed class GhosttyTerminal : Border, ITerminalBackend
{
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource detached = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TerminalLaunch launch;
    private RemoteTerminalConnection? connection;
    private TerminalHost? terminal;
    private string? statusPath, connectionPath;
    private GCHandle self;
    private bool disposed;

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
        connectionPath = TerminalRelay.Write(name, new(remote.Endpoint.ToString(), remote.Token, launch.SessionId, launch.ClientId, launch.WorkspaceRoot, statusPath));
        var executable = Environment.ProcessPath ?? throw new TerminalStartException("The SharpRail executable path is unknown.");
        var command = "'" + executable.Replace("'", "'\\''") + "' " + TerminalRelay.Argument;
        self = GCHandle.Alloc(this);
        try
        {
            var view = Create(command);
            if (view == 0) throw new TerminalStartException("Ghostty could not create a Metal terminal surface.");
            terminal = new TerminalHost(view) { Focusable = true };
        }
        catch
        {
            self.Free();
            DeleteRelayFiles();
            throw;
        }
        Child = terminal;
        terminal.UpdateColors();
        Ui.ThemeChanged += terminal.UpdateColors;
    }

    private unsafe nint Create(string command) => Native.Create(launch.WorkspaceRoot, launch.ClipboardDirectory, command,
        TerminalRelay.ConnectionVariable, connectionPath, &OnEvent, GCHandle.ToIntPtr(self));

    public ValueTask<bool> IsBusyAsync()
    {
        if (exited.Task.IsCompleted || detached.Task.IsCompleted || connection is null) return ValueTask.FromResult(false);
        return connection.Terminals.IsBusyAsync(launch.SessionId);
    }

    public ValueTask CloseAsync() => connection?.Terminals.CloseAsync(launch.SessionId) ?? ValueTask.CompletedTask;

    [UnmanagedCallersOnly]
    private static void OnEvent(nint context, int kind, int value)
    {
        if (GCHandle.FromIntPtr(context).Target is not GhosttyTerminal owner) return;
        Dispatcher.UIThread.Post(() => owner.Handle(kind, value));
    }

    private void Handle(int kind, int value)
    {
        if (kind == 1)
        {
            var status = statusPath is not null && File.Exists(statusPath) ? File.ReadAllText(statusPath) : null;
            if (statusPath is not null) File.Delete(statusPath);
            if (status == TerminalRelay.DetachedStatus) detached.TrySetResult();
            else if (status is not null && status.StartsWith(TerminalRelay.ExitStatus, StringComparison.Ordinal) &&
                int.TryParse(status[TerminalRelay.ExitStatus.Length..], out var code))
                exited.TrySetResult(code);
            else exited.TrySetException(new TerminalStartException(status ?? $"The terminal relay ended unexpectedly with code {value}."));
        }
        else if (kind == 2)
            RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.J, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift, Source = this });
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        if (!self.IsAllocated) return;
        Ui.ThemeChanged -= terminal!.UpdateColors;
        terminal.Dispose();
        self.Free();
        DeleteRelayFiles();
    }

    private void DeleteRelayFiles()
    {
        foreach (var path in new[] { connectionPath, statusPath })
            if (path is not null) File.Delete(path);
    }

    public void FocusTerminal() => terminal?.FocusTerminal();

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source == this) FocusTerminal();
    }

    private sealed class TerminalHost(nint view) : NativeControlHost, IDisposable
    {
        private nint view = view;

        protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
        {
            ObjectDisposedException.ThrowIf(view == 0, this);
            return new PlatformHandle(view, "NSView");
        }

        // Detaching a hidden tab releases Avalonia's attachment, not the shell session.
        protected override void DestroyNativeControlCore(IPlatformHandle control) { }

        protected override void OnGotFocus(FocusChangedEventArgs e)
        {
            base.OnGotFocus(e);
            if (view != 0) Native.Focus(view);
        }

        internal void FocusTerminal()
        {
            Focus();
            if (view != 0) Native.Focus(view);
        }

        /// <summary>Mirrors the reference's xterm theme: selection composited over the surface, and its contrast floor.</summary>
        internal void UpdateColors()
        {
            if (view == 0) return;
            var theme = Ui.Theme;
            var background = Ui.Surface.Color;
            uint[] colors =
            [
                background.ToUInt32(), Ui.TextBrush.Color.ToUInt32(), Ui.Accent.Color.ToUInt32(),
                Ui.Over(theme["editorSelection"], background).ToUInt32(), theme.Colors["editorSelectionForeground"]?.ToUInt32() ?? 0,
                .. theme.Ansi.Select(color => color.ToUInt32())
            ];
            Native.SetColors(view, colors, theme.IsHighContrast ? 7 : 4.5);
        }

        public void Dispose()
        {
            if (view != 0) { Native.Destroy(view); view = 0; }
        }
    }

    private static unsafe class Native
    {
        private const string Library = "SharpRailGhostty";
        [DllImport(Library, EntryPoint = "sr_terminal_create")]
        internal static extern nint Create([MarshalAs(UnmanagedType.LPUTF8Str)] string directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string clipboardDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? command, [MarshalAs(UnmanagedType.LPUTF8Str)] string? environmentName,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? environmentValue, delegate* unmanaged<nint, int, int, void> callback, nint context);
        [DllImport(Library, EntryPoint = "sr_terminal_set_colors")]
        internal static extern void SetColors(nint view, uint[] colors, double minimumContrast);
        [DllImport(Library, EntryPoint = "sr_terminal_destroy")]
        internal static extern void Destroy(nint view);
        [DllImport(Library, EntryPoint = "sr_terminal_focus")]
        internal static extern void Focus(nint view);
    }
}
