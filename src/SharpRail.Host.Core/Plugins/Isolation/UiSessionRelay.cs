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

    /// <summary>Why a frame message breaks the surface protocol, or null when it keeps to it.</summary>
    internal static string? Violation(SurfaceFrameHeader header, int bytes, FrameState state)
    {
        if (header.Moves is null || header.Tiles is null || header.Moves.Any(move => move is null) || header.Tiles.Any(tile => tile is null))
            return "The plugin sent a frame with a missing part.";
        if (header.Width is < 0 or > SurfaceLimits.MostSide || header.Height is < 0 or > SurfaceLimits.MostSide || (long)header.Width * header.Height > SurfaceLimits.MostFramePixels)
            return "The plugin sent a frame larger than a client takes.";
        if (!(header.Scale is >= SurfaceLimits.LeastScale and <= SurfaceLimits.MostScale)) return "The plugin sent a frame at a scale out of range.";
        if (!(header.DesiredWidth is >= 0 and <= 100_000) || !(header.DesiredHeight is >= 0 and <= 100_000)) return "The plugin asked for a size out of range.";
        if (state.Open)
        {
            if (header.Sequence != state.Sequence || header.Width != state.Width || header.Height != state.Height) return "The plugin changed a frame while sending it.";
            if (header.Moves.Count > 0) return "The plugin sent moves after a frame's first message.";
        }
        else
        {
            if (state.Unacknowledged) return "The plugin sent a frame before the client took the last one.";
            if (header.Sequence != state.Sequence + 1) return "The plugin sent frames out of order.";
        }
        if (header.Moves.Count > 64) return "The plugin sent too many moves.";
        if (header.Moves.Count > 0 && (header.Width != state.Width || header.Height != state.Height)) return "The plugin moved pixels of a frame of another size.";
        if (header.Moves.Any(move => !SurfacePixels.Fits(move, header.Width, header.Height))) return "The plugin sent a move outside its surface.";
        if (header.Tiles.Count > SurfaceLimits.MostTiles) return "The plugin sent too many tiles.";
        long length = 0, pixels = state.Open ? state.Pixels : 0;
        foreach (var tile in header.Tiles)
        {
            if (tile.Width is < 1 or > SurfaceLimits.MostTileSide || tile.Height is < 1 or > SurfaceLimits.MostTileSide || tile.X < 0 || tile.Y < 0 ||
                tile.X + tile.Width > header.Width || tile.Y + tile.Height > header.Height)
                return "The plugin sent a tile outside its surface.";
            var valid = tile.Codec switch
            {
                TileCodecKind.Solid => tile.Length == 4,
                TileCodecKind.Qoi => tile.Length is > 0 and <= SurfaceLimits.MessageBytes,
                _ => false,
            };
            if (!valid) return "The plugin sent a tile the client cannot read.";
            length += tile.Length;
            pixels += (long)tile.Width * tile.Height;
        }
        if (length != bytes || bytes > 2 * SurfaceLimits.MessageBytes) return "The plugin's tiles do not match their bytes.";
        if (pixels > SurfaceLimits.MostFramePixels) return "The plugin sent a frame larger than a client takes.";
        return null;
    }

    /// <summary>Where a surface's frames stand: the last frame's number and size, and the one being received.</summary>
    internal sealed class FrameState
    {
        public long Sequence { get; set; }
        public int Width { get; set; } = -1;
        public int Height { get; set; } = -1;
        public bool Open { get; set; }
        public bool Unacknowledged { get; set; }
        public long Pixels { get; set; }
    }

    /// <summary>
    /// One surface as the client holds it. The client's messages wait until the plugin's process has shown the
    /// surface, since they travel apart from the call that opens it.
    /// </summary>
    internal sealed class RelaySurface(UiSessionRelay relay, int id) : IPluginSurface
    {
        private readonly Channel<SurfaceEvent> events = Channel.CreateUnbounded<SurfaceEvent>(new() { SingleReader = true });
        private readonly FrameState state = new();
        private readonly Lock gate = new();
        private List<(int Member, string Payload)>? waiting = [];
        private bool failed;
        private int closed;

        public int Id { get; } = id;
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
                if (Volatile.Read(ref closed) != 0) return;
                if (Violation(header, bytes.Length, state) is { } violation)
                {
                    Break(violation);
                    return;
                }
                if (!state.Open) state.Pixels = 0;
                (state.Sequence, state.Width, state.Height, state.Open) = (header.Sequence, header.Width, header.Height, !header.Last);
                state.Pixels += header.Tiles.Sum(tile => (long)tile.Width * tile.Height);
                if (header.Last) state.Unacknowledged = true;
                events.Writer.TryWrite(new SurfaceFrame(header, bytes));
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

        public void Input(SurfaceInput input) => Send(SurfaceProtocol.Input, SurfaceProtocol.Encode(input with { Surface = Id }));

        public void Acknowledge(long sequence)
        {
            lock (gate)
            {
                if (sequence != state.Sequence || state.Open) return;
                state.Unacknowledged = false;
            }
            Send(SurfaceProtocol.Ack, SurfaceProtocol.Encode(new SurfaceAck(Id, sequence)));
        }

        public void Theme(string json) => relay.Theme(json);

        public void Focus(bool enter, bool backward, bool pointer) => Send(SurfaceProtocol.Focus, SurfaceProtocol.Encode(new SurfaceFocus(Id, enter, backward, pointer)));

        /// <summary>A fresh render in the plugin's process, which answers only when the checks started it.</summary>
        internal async Task<SurfaceCapture> CaptureAsync(IReadOnlyList<string> names) =>
            SurfaceProtocol.Decode<SurfaceCapture>((await relay.channel.CallAsync(SurfaceProtocol.Capture, SurfaceProtocol.Encode(new SurfaceCaptureRequest(Id, [.. names]))).ConfigureAwait(false)).Payload);

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