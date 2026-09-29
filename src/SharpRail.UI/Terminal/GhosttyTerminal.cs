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
internal sealed unsafe class GhosttyTerminal : Border, ITerminalBackend
{
    private readonly TerminalHost terminal;
    private readonly TaskCompletionSource<int> exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<ValueTask<bool>>? remoteBusy;
    private readonly string? statusPath, connectionPath;
    private GCHandle self;

    private GhosttyTerminal(TerminalLaunch launch, string? command, string? connectionPath, string? statusPath, Func<ValueTask<bool>>? remoteBusy)
    {
        this.connectionPath = connectionPath; this.statusPath = statusPath; this.remoteBusy = remoteBusy;
        self = GCHandle.Alloc(this);
        try
        {
            var view = Native.Create(launch.WorkspaceRoot, launch.ClipboardDirectory, command,
                connectionPath is null ? null : TerminalRelay.ConnectionVariable, connectionPath, &OnEvent, GCHandle.ToIntPtr(self));
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
        Focusable = true;
        terminal.UpdateColors();
        Ui.Surface.PropertyChanged += ColorsChanged;
        Ui.TextBrush.PropertyChanged += ColorsChanged;
    }

    internal static GhosttyTerminal Local(TerminalLaunch launch) => new(launch, null, null, null, null);

    internal static GhosttyTerminal Remote(TerminalLaunch launch, RemoteTerminalConnection remote)
    {
        var status = Path.Combine(Path.GetTempPath(), "sharprail-relay-" + Environment.UserName, launch.SessionId + ".status");
        var connection = TerminalRelay.Write(new(remote.Endpoint.ToString(), remote.Token, launch.SessionId, launch.WorkspaceRoot, status));
        var executable = Environment.ProcessPath ?? throw new TerminalStartException("The SharpRail executable path is unknown.");
        var command = "'" + executable.Replace("'", "'\\''") + "' " + TerminalRelay.Argument;
        return new(launch, command, connection, status, () => remote.Terminals.IsBusyAsync(launch.SessionId));
    }

    public Control View => this;
    public Task Started => Task.CompletedTask;
    public Task<int> Exited => exited.Task;

    public ValueTask<bool> IsBusyAsync()
    {
        if (exited.Task.IsCompleted) return ValueTask.FromResult(false);
        return remoteBusy?.Invoke() ?? ValueTask.FromResult(terminal.Busy);
    }

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
            // The relay reports the remote shell's status beside its connection file, because
            // Ghostty's login wrapper does not propagate the child's exit code.
            if (statusPath is not null && File.Exists(statusPath))
            {
                var status = File.ReadAllText(statusPath);
                File.Delete(statusPath);
                if (status.StartsWith(TerminalRelay.ExitStatus, StringComparison.Ordinal) && int.TryParse(status[TerminalRelay.ExitStatus.Length..], out var code))
                    exited.TrySetResult(code);
                else exited.TrySetException(new TerminalStartException(status));
            }
            else exited.TrySetResult(value);
        }
        else if (kind == 2)
            RaiseEvent(new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.J, KeyModifiers = KeyModifiers.Meta | KeyModifiers.Shift, Source = this });
    }

    private void ColorsChanged(object? sender, AvaloniaPropertyChangedEventArgs e) => terminal.UpdateColors();

    public void Dispose()
    {
        if (!self.IsAllocated) return;
        Ui.Surface.PropertyChanged -= ColorsChanged;
        Ui.TextBrush.PropertyChanged -= ColorsChanged;
        terminal.Dispose();
        self.Free();
        DeleteRelayFiles();
    }

    private void DeleteRelayFiles()
    {
        foreach (var path in new[] { connectionPath, statusPath })
            if (path is not null) File.Delete(path);
    }

    public void FocusTerminal() => terminal.FocusTerminal();

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        if (e.Source == this) FocusTerminal();
    }

    private sealed class TerminalHost(nint view) : NativeControlHost, IDisposable
    {
        private nint view = view;

        internal bool Busy => view != 0 && Native.Busy(view);

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

        internal void UpdateColors()
        {
            if (view != 0) Native.SetColors(view, Ui.Surface.Color.ToUInt32(), Ui.TextBrush.Color.ToUInt32());
        }

        public void Dispose()
        {
            if (view != 0) { Native.Destroy(view); view = 0; }
        }
    }

    private static class Native
    {
        private const string Library = "SharpRailGhostty";
        [DllImport(Library, EntryPoint = "sr_terminal_create")]
        internal static extern nint Create([MarshalAs(UnmanagedType.LPUTF8Str)] string directory, [MarshalAs(UnmanagedType.LPUTF8Str)] string clipboardDirectory,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? command, [MarshalAs(UnmanagedType.LPUTF8Str)] string? environmentName,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string? environmentValue, delegate* unmanaged<nint, int, int, void> callback, nint context);
        [DllImport(Library, EntryPoint = "sr_terminal_set_colors")]
        internal static extern void SetColors(nint view, uint background, uint foreground);
        [DllImport(Library, EntryPoint = "sr_terminal_destroy")]
        internal static extern void Destroy(nint view);
        [DllImport(Library, EntryPoint = "sr_terminal_focus")]
        internal static extern void Focus(nint view);
        [DllImport(Library, EntryPoint = "sr_terminal_busy")]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool Busy(nint view);
    }
}
