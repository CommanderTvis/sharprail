using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

using SharpRail.Plugins.Isolation;

namespace SharpRail.Host.Core.Plugins.Isolation;

/// <summary>
/// One client's surfaces in an external plugin's process (Security.SPEC.md, UI halves). The host numbers each surface,
/// and every frame the plugin sends is held to the surface protocol's caps before the client sees it; a frame that
/// breaks them ends that surface with the reason.
/// </summary>
internal sealed class UiSessionRelay(IsolationPeer peer, long context, Func<int> nextSurface) : IPluginSurfaceService
{
    private readonly ConcurrentDictionary<int, RelaySurface> surfaces = new();
    private readonly IsolationPeer channel = peer;
    private string theme = "";

    public IPluginSurface Open(int member, long handle, string payload, string theme)
    {
        var surface = new RelaySurface(this, nextSurface());
        if (surfaces.Count >= SurfaceLimits.MostSurfaces)
        {
            surface.End(new InvalidOperationException("This plugin has too many views open."));
            return surface;
        }
        surfaces[surface.Id] = surface;
        this.theme = theme;
        _ = surface.OpenAsync(new SurfaceOpen(context, surface.Id, member, handle, payload, theme));
        return surface;
    }

    internal RelaySurface? Find(int id) => surfaces.GetValueOrDefault(id);

    private void Theme(string json)
    {
        if (json == theme) return;
        theme = json;
        Send(SurfaceProtocol.Theme, SurfaceProtocol.Encode(new SurfaceTheme(json)));
    }

    /// <summary>
    /// A frame message the plugin sent on this context. One the client cannot read ends the surface it names, or every
    /// surface of this client when it names none.
    /// </summary>
    public void Frame(string payload, byte[] bytes)
    {
        var (named, header, error) = Read(payload);
        if (header is not null)
        {
            if (surfaces.TryGetValue(header.Surface, out var surface)) surface.Accept(header, bytes);
            return;
        }
        var reason = "The plugin sent a frame the client cannot read: " + error;
        if (named is not { } id) foreach (var broken in surfaces.Values) broken.Break(reason);
        else if (surfaces.TryGetValue(id, out var broken)) broken.Break(reason);
    }

    /// <summary>A frame message's header, or why it cannot be read and the surface it names, if any.</summary>
    internal static (int? Surface, SurfaceFrameHeader? Header, string? Error) Read(string payload)
    {
        try { return (null, SurfaceProtocol.Decode<SurfaceFrameHeader>(payload), null); }
        catch (IsoCallException error)
        {
            int? named = null;
            try
            {
                using var json = JsonDocument.Parse(payload);
                if (json.RootElement.ValueKind == JsonValueKind.Object && json.RootElement.TryGetProperty(nameof(SurfaceFrameHeader.Surface), out var surface) &&
                    surface.TryGetInt32(out var id)) named = id;
            }
            catch (JsonException) { }
            return (named, null, error.Message);
        }
    }

    public void FocusLeave(string payload)
    {
        var left = SurfaceProtocol.Decode<SurfaceFocusLeave>(payload);
        if (surfaces.TryGetValue(left.Surface, out var surface)) surface.Left(left.Backward);
    }

    /// <summary>A popup of a surface opened, moved or closed; one that breaks the surface protocol ends that surface.</summary>
    public void Popup(string payload)
    {
        var popup = SurfaceProtocol.Decode<SurfacePopup>(payload);
        if (surfaces.TryGetValue(popup.Surface, out var surface)) surface.Place(popup);
    }

    /// <summary>The cursor over a surface or its popup changed; one that breaks the surface protocol ends that surface.</summary>
    public void Cursor(string payload)
    {
        var cursor = SurfaceProtocol.Decode<SurfaceCursor>(payload);
        if (surfaces.TryGetValue(cursor.Surface, out var surface)) surface.Report(state => state.Cursor(cursor), new SurfaceCursorChanged(cursor));
    }

    /// <summary>A text field's focus or caret changed; one that breaks the surface protocol ends that surface.</summary>
    public void Ime(string payload)
    {
        var ime = SurfaceProtocol.Decode<SurfaceIme>(payload);
        if (surfaces.TryGetValue(ime.Surface, out var surface)) surface.Report(state => state.Ime(ime), new SurfaceImeChanged(ime));
    }

    /// <summary>The accessibility tree of a surface or its popup changed; one that breaks the surface protocol ends that surface.</summary>
    public void Automation(string payload)
    {
        var automation = SurfaceProtocol.Decode<SurfaceAutomation>(payload);
        if (surfaces.TryGetValue(automation.Surface, out var surface)) surface.Report(state => state.Automation(automation), new SurfaceAutomationChanged(automation));
    }

    /// <summary>Ends every surface of this client, as the session does.</summary>
    public void End(Exception? reason)
    {
        foreach (var surface in surfaces.Values) surface.End(reason);
        surfaces.Clear();
    }

    private void Send(int member, string payload)
    {
        try { channel.OneWay(member, payload); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { }
    }

    /// <summary>
    /// One surface as the client holds it. The client's messages wait until the plugin's process has shown the
    /// surface, since they travel apart from the call that opens it.
    /// </summary>
    internal sealed class RelaySurface(UiSessionRelay relay, int id) : IPluginSurface
    {
        private readonly Channel<SurfaceEvent> events = Channel.CreateUnbounded<SurfaceEvent>(new() { SingleReader = true });
        private readonly SurfaceState state = new();
        private readonly Lock gate = new();
        private List<(int Member, string Payload)>? waiting = [];
        private bool failed;
        private int closed;

        public int Id { get; } = id;
        public int Number => Id;
        public ChannelReader<SurfaceEvent> Events => events.Reader;

        public async Task OpenAsync(SurfaceOpen open)
        {
            try { await relay.channel.CallAsync(SurfaceProtocol.Open, SurfaceProtocol.Encode(open)).ConfigureAwait(false); }
            catch (Exception error)
            {
                lock (gate)
                {
                    waiting = null;
                    failed = true;
                }
                End(new IOException("The plugin could not show this view: " + error.Message, error));
                return;
            }
            lock (gate)
            {
                foreach (var (member, payload) in waiting!) relay.Send(member, payload);
                waiting = null;
            }
            // A theme the client pushed while the surface opened may have been applied before the one it opened under.
            var theme = relay.theme;
            if (theme != open.Theme) relay.Send(SurfaceProtocol.Theme, SurfaceProtocol.Encode(new SurfaceTheme(theme)));
        }

        private void Send(int member, string payload)
        {
            lock (gate)
            {
                if (failed) return;
                if (waiting is not null)
                {
                    waiting.Add((member, payload));
                    return;
                }
                relay.Send(member, payload);
            }
        }

        public void Accept(SurfaceFrameHeader header, byte[] bytes)
        {
            lock (gate)
            {
                if (Volatile.Read(ref closed) != 0 || state.Stale(header)) return;
                if (state.Violation(header, bytes.Length) is { } violation)
                {
                    Break(violation);
                    return;
                }
                state.Frames(header.Popup)!.Accept(header);
                events.Writer.TryWrite(new SurfaceFrame(header, bytes));
            }
        }

        public void Place(SurfacePopup popup)
        {
            lock (gate)
            {
                if (Volatile.Read(ref closed) != 0) return;
                if (state.Place(popup) is { } violation)
                {
                    Break(violation);
                    return;
                }
                events.Writer.TryWrite(new SurfacePopupChanged(popup));
            }
        }

        /// <summary>Passes on what the plugin reported about the surface when it keeps to the surface protocol.</summary>
        public void Report(Func<SurfaceState, string?> violation, SurfaceEvent report)
        {
            lock (gate)
            {
                if (Volatile.Read(ref closed) != 0) return;
                if (violation(state) is { } broken)
                {
                    Break(broken);
                    return;
                }
                events.Writer.TryWrite(report);
            }
        }

        /// <summary>The plugin broke the surface protocol: its process closes the surface and the client shows why.</summary>
        public void Break(string reason)
        {
            Send(SurfaceProtocol.Close, SurfaceProtocol.Encode(new SurfaceClose(Id)));
            End(new InvalidDataException(reason));
        }

        public void Left(bool backward) => events.Writer.TryWrite(new SurfaceLeft(backward));

        public void Resize(double width, double height, double scale) => Send(SurfaceProtocol.Resize, SurfaceProtocol.Encode(new SurfaceResize(Id, width, height, scale)));

        public void Input(SurfaceInput input)
        {
            // An accessibility action on a node the plugin no longer describes is stale: the tree moved on.
            lock (gate)
                if (!state.Admits(input)) return;
            Send(SurfaceProtocol.Input, SurfaceProtocol.Encode(input with { Surface = Id }));
        }

        public void Acknowledge(long sequence, int popup)
        {
            lock (gate)
                if (state.Frames(popup)?.Acknowledge(sequence) != true) return;
            Send(SurfaceProtocol.Ack, SurfaceProtocol.Encode(new SurfaceAck(Id, sequence, popup)));
        }

        public void Keyframe()
        {
            lock (gate) state.AwaitKeyframe(lost: false);
            Send(SurfaceProtocol.Keyframe, SurfaceProtocol.Encode(new SurfaceKeyframe(Id)));
        }

        public void Dismiss() => Send(SurfaceProtocol.Dismiss, SurfaceProtocol.Encode(new SurfaceDismiss(Id)));

        public void Theme(string json) => relay.Theme(json);

        public void Focus(bool enter, bool backward, bool pointer) => Send(SurfaceProtocol.Focus, SurfaceProtocol.Encode(new SurfaceFocus(Id, enter, backward, pointer)));

        /// <summary>A fresh render in the plugin's process, which answers only when the checks started it.</summary>
        internal async Task<SurfaceCapture> CaptureAsync(IReadOnlyList<string> names, int popup = 0) =>
            SurfaceProtocol.Decode<SurfaceCapture>((await relay.channel.CallAsync(SurfaceProtocol.Capture, SurfaceProtocol.Encode(new SurfaceCaptureRequest(Id, [.. names], popup))).ConfigureAwait(false)).Payload);

        public void End(Exception? reason)
        {
            if (Interlocked.Exchange(ref closed, 1) != 0) return;
            events.Writer.TryComplete(reason);
        }

        public ValueTask DisposeAsync()
        {
            if (relay.surfaces.TryRemove(Id, out _)) Send(SurfaceProtocol.Close, SurfaceProtocol.Encode(new SurfaceClose(Id)));
            End(null);
            return ValueTask.CompletedTask;
        }
    }
}