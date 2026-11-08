namespace SharpRail.Host.Protocol;

[ProtoContract]
public sealed class PluginRosterRequest { }

[ProtoContract]
public sealed class PluginRetryRequest
{
    [ProtoMember(1)] public string Id { get; set; } = "";
}

[ProtoContract]
public sealed class PluginSideToolMessage
{
    [ProtoMember(1)] public string Tool { get; set; } = "";
    [ProtoMember(2)] public string Label { get; set; } = "";
    [ProtoMember(3)] public string Icon { get; set; } = "";
    [ProtoMember(4)] public string DefaultSide { get; set; } = "";
    [ProtoMember(5)] public bool RequiresGit { get; set; }
}

[ProtoContract]
public sealed class PluginFileViewerMessage
{
    [ProtoMember(1)] public List<string> Extensions { get; set; } = [];
    [ProtoMember(2)] public List<string> Names { get; set; } = [];
    [ProtoMember(3)] public string Read { get; set; } = "";
}

[ProtoContract]
public sealed class PluginChannelMessage
{
    [ProtoMember(1)] public string Name { get; set; } = "";
    [ProtoMember(2)] public string Kind { get; set; } = "";
    [ProtoMember(3)] public string Snapshot { get; set; } = "";
    [ProtoMember(4)] public List<string> Key { get; set; } = [];
}

// Enums travel as PluginJson's camelCase names; an absent string is empty.
[ProtoContract]
public sealed class PluginRosterMessage
{
    [ProtoMember(1)] public string Id { get; set; } = "";
    [ProtoMember(2)] public string Label { get; set; } = "";
    [ProtoMember(3)] public string Description { get; set; } = "";
    [ProtoMember(4)] public string Icon { get; set; } = "";
    [ProtoMember(5)] public string Version { get; set; } = "";
    [ProtoMember(6)] public int WireVersion { get; set; }
    [ProtoMember(7)] public string Origin { get; set; } = "";
    [ProtoMember(8)] public string Status { get; set; } = "";
    [ProtoMember(9)] public string Reason { get; set; } = "";
    [ProtoMember(10)] public List<string> DependsOn { get; set; } = [];
    [ProtoMember(11)] public List<PluginSideToolMessage> SideTools { get; set; } = [];
    [ProtoMember(12)] public List<PluginFileViewerMessage> FileViewers { get; set; } = [];
    [ProtoMember(13)] public List<PluginChannelMessage> Channels { get; set; } = [];
    [ProtoMember(15)] public string Assets { get; set; } = "";
    [ProtoMember(16)] public string Category { get; set; } = "";
    [ProtoMember(17)] public string Support { get; set; } = "";
}

[ProtoContract]
public sealed class PluginRosterReply
{
    [ProtoMember(1)] public List<PluginRosterMessage> Plugins { get; set; } = [];
}

// Params, results and payloads are JSON written with PluginJson.Options.
[ProtoContract]
public sealed class PluginCallMessage
{
    [ProtoMember(1)] public string PluginId { get; set; } = "";
    [ProtoMember(2)] public string Method { get; set; } = "";
    [ProtoMember(3)] public byte[] ParamsJson { get; set; } = [];
    [ProtoMember(4)] public string ClientKey { get; set; } = "";
}

[ProtoContract]
public sealed class PluginCallReply
{
    // JSON null for a null result.
    [ProtoMember(1)] public byte[] ResultJson { get; set; } = [];
}

[ProtoContract]
public sealed class PluginSubscribeMessage
{
    [ProtoMember(1)] public string PluginId { get; set; } = "";
    [ProtoMember(2)] public string Channel { get; set; } = "";
    // Empty for a subscription without a key.
    [ProtoMember(3)] public byte[] KeyJson { get; set; } = [];
    [ProtoMember(4)] public string ClientKey { get; set; } = "";
}

[ProtoContract]
public sealed class PluginPushMessage
{
    [ProtoMember(1)] public byte[] PayloadJson { get; set; } = [];
}

[ProtoContract]
public sealed class PluginFileRequest
{
    [ProtoMember(1)] public string PluginId { get; set; } = "";
    [ProtoMember(2)] public string Path { get; set; } = "";
}

[ProtoContract]
public sealed class PluginFileReply
{
    [ProtoMember(1)] public bool Found { get; set; }
    [ProtoMember(2)] public byte[] Data { get; set; } = [];
}

// One client's session with an external plugin's process and the surfaces it shows (Security.SPEC.md, UI halves).
// A message with a Seq above 0 is reliable: each side numbers its own from 1, acknowledges the other's in Ack, and
// after a resume sends again every message the other has not acknowledged. Frames and frame acknowledgements are not
// numbered; a resumed client asks for a keyframe instead. Payload and Json are the plugin channel's JSON; Error is its
// IsoError value.
public enum PluginSurfaceUpKind { None = 0, Open = 1, Resume = 2, Ack = 3, Answer = 4, Invoke = 5, OneWay = 6, Mirror = 7, SurfaceOpen = 8, Resize = 9, Input = 10, FrameAck = 11, Focus = 12, Theme = 13, SurfaceClose = 14, Keyframe = 15, Dismiss = 16 }

public enum PluginSurfaceDownKind { None = 0, Opened = 1, Resumed = 2, Ack = 3, UiCall = 4, Result = 5, Shown = 6, Frame = 7, FocusLeave = 8, SurfaceEnded = 9, Ended = 10, Popup = 11, Cursor = 12, Ime = 13 }

// Why a surface ended: the client closed it, the plugin broke the surface protocol, or the plugin could not show it.
public enum PluginSurfaceEndKind { Closed = 0, Broken = 1, Failed = 2 }

public static class PluginSurfaceWire
{
    // No Frame message of a surface session passes 1 MiB, so a frame is split into several.
    public const int MessageBytes = 1024 * 1024;
    // Tile bytes in one Frame message, leaving room for the tile list of up to 4,096 tiles.
    public const int FrameDataBytes = 896 * 1024;
}

// Client to host. The first message is Open (PluginId, ClientKey) or Resume (Session, Ack); later ones drive the session:
// Answer (Call, Payload or Error and Detail) answers a UiCall; Invoke and Mirror (Call, Member, Handle, Payload, Json)
// await a Result; OneWay; SurfaceOpen (Surface, Member, Handle, Payload, theme Json); Resize (Width, Height, Scale);
// Input (on the surface, or on its open Popup, a dialog's close request among them); FrameAck (Sequence, of the surface or of its Popup); Focus (Enter,
// Backward, Pointer); Theme (Json); SurfaceClose; Keyframe (of the surface and its popups); Dismiss (its popups).
[ProtoContract]
public sealed class PluginSurfaceUp
{
    [ProtoMember(1)] public PluginSurfaceUpKind Kind { get; set; }
    [ProtoMember(2)] public long Seq { get; set; }
    [ProtoMember(3)] public long Ack { get; set; }
    [ProtoMember(4)] public string PluginId { get; set; } = "";
    [ProtoMember(5)] public string ClientKey { get; set; } = "";
    [ProtoMember(6)] public string Session { get; set; } = "";
    [ProtoMember(7)] public long Call { get; set; }
    [ProtoMember(8)] public int Member { get; set; }
    [ProtoMember(9)] public long Handle { get; set; }
    [ProtoMember(10)] public string Payload { get; set; } = "";
    [ProtoMember(11)] public string Json { get; set; } = "";
    [ProtoMember(12)] public int Error { get; set; }
    [ProtoMember(13)] public string Detail { get; set; } = "";
    [ProtoMember(14)] public int Surface { get; set; }
    [ProtoMember(15)] public double Width { get; set; }
    [ProtoMember(16)] public double Height { get; set; }
    [ProtoMember(17)] public double Scale { get; set; }
    [ProtoMember(18)] public long Sequence { get; set; }
    [ProtoMember(19)] public PluginSurfaceInputMessage? Input { get; set; }
    [ProtoMember(20)] public bool Enter { get; set; }
    [ProtoMember(21)] public bool Backward { get; set; }
    [ProtoMember(22)] public bool Pointer { get; set; }
    [ProtoMember(23)] public int Popup { get; set; }
}

// Kind is SurfaceInputKind's value; Button, Modifiers, Key and PhysicalKey are Avalonia's; Caret is a composition's
// caret in Text.
[ProtoContract]
public sealed class PluginSurfaceInputMessage
{
    [ProtoMember(1)] public int Kind { get; set; }
    [ProtoMember(2)] public double X { get; set; }
    [ProtoMember(3)] public double Y { get; set; }
    [ProtoMember(4)] public int Button { get; set; }
    [ProtoMember(5)] public int Modifiers { get; set; }
    [ProtoMember(6)] public double DeltaX { get; set; }
    [ProtoMember(7)] public double DeltaY { get; set; }
    [ProtoMember(8)] public int Key { get; set; }
    [ProtoMember(9)] public int PhysicalKey { get; set; }
    [ProtoMember(10)] public string Text { get; set; } = "";
    [ProtoMember(11)] public int Caret { get; set; }
}

// Host to client. Opened (Session, ApiTableHash, Registrations) or Resumed (Ack) answers the first message; then
// UiCall (Call, Member, Payload, Answered); Result (Call, Payload or Error and Detail); Shown (Surface, the host's
// Number for it); Frame; FocusLeave (Backward); SurfaceEnded (End, Detail); Ended (Detail) as the session ends; Popup
// (PopupPlacement) as a popup or dialog of the surface opens, moves or closes; Cursor (Popup, Cursor: Avalonia's
// StandardCursorType value) as the cursor over the surface or its popup changes; Ime (Popup, Ime) as a text field there
// gains or loses focus or its caret moves.
[ProtoContract]
public sealed class PluginSurfaceDown
{
    [ProtoMember(1)] public PluginSurfaceDownKind Kind { get; set; }
    [ProtoMember(2)] public long Seq { get; set; }
    [ProtoMember(3)] public long Ack { get; set; }
    [ProtoMember(4)] public string Session { get; set; } = "";
    [ProtoMember(5)] public string ApiTableHash { get; set; } = "";
    [ProtoMember(6)] public List<PluginSurfaceRegistrationMessage> Registrations { get; set; } = [];
    [ProtoMember(7)] public long Call { get; set; }
    [ProtoMember(8)] public int Member { get; set; }
    [ProtoMember(9)] public string Payload { get; set; } = "";
    [ProtoMember(10)] public bool Answered { get; set; }
    [ProtoMember(11)] public int Error { get; set; }
    [ProtoMember(12)] public string Detail { get; set; } = "";
    [ProtoMember(13)] public int Surface { get; set; }
    [ProtoMember(14)] public int Number { get; set; }
    [ProtoMember(15)] public PluginSurfaceFrameMessage? Frame { get; set; }
    [ProtoMember(16)] public bool Backward { get; set; }
    [ProtoMember(17)] public PluginSurfaceEndKind End { get; set; }
    [ProtoMember(18)] public PluginSurfacePopupMessage? PopupPlacement { get; set; }
    [ProtoMember(19)] public int Popup { get; set; }
    [ProtoMember(20)] public int Cursor { get; set; }
    [ProtoMember(21)] public PluginSurfaceImeMessage? Ime { get; set; }
}

// Whether a text field that composes with an input method has focus, and its caret from the top-level's corner in
// device-independent pixels.
[ProtoContract]
public sealed class PluginSurfaceImeMessage
{
    [ProtoMember(1)] public bool Active { get; set; }
    [ProtoMember(2)] public double X { get; set; }
    [ProtoMember(3)] public double Y { get; set; }
    [ProtoMember(4)] public double Width { get; set; }
    [ProtoMember(5)] public double Height { get; set; }
}

// A popup of a surface, numbered within it from 1: its place from the surface's top-left corner and its size in
// device-independent pixels, whether it is open, and whether it takes pointer input (a tooltip does not). A Dialog is
// a window the plugin opened, which the client places itself in a frame showing its Title.
[ProtoContract]
public sealed class PluginSurfacePopupMessage
{
    [ProtoMember(1)] public int Popup { get; set; }
    [ProtoMember(2)] public double X { get; set; }
    [ProtoMember(3)] public double Y { get; set; }
    [ProtoMember(4)] public double Width { get; set; }
    [ProtoMember(5)] public double Height { get; set; }
    [ProtoMember(6)] public bool Open { get; set; }
    [ProtoMember(7)] public bool Input { get; set; }
    [ProtoMember(8)] public bool Dialog { get; set; }
    [ProtoMember(9)] public string Title { get; set; } = "";
}

[ProtoContract]
public sealed class PluginSurfaceRegistrationMessage
{
    [ProtoMember(1)] public int Member { get; set; }
    [ProtoMember(2)] public string Payload { get; set; } = "";
}

// One message of a frame the host checked, of the surface or of its open Popup: moves only in a frame's first message,
// Last only on its last, the tiles' bytes one after another in Data. A keyframe repaints everything from transparent.
[ProtoContract]
public sealed class PluginSurfaceFrameMessage
{
    [ProtoMember(1)] public long Sequence { get; set; }
    [ProtoMember(2)] public int Width { get; set; }
    [ProtoMember(3)] public int Height { get; set; }
    [ProtoMember(4)] public double Scale { get; set; }
    [ProtoMember(5)] public double DesiredWidth { get; set; }
    [ProtoMember(6)] public double DesiredHeight { get; set; }
    [ProtoMember(7)] public bool Last { get; set; }
    [ProtoMember(8)] public bool Keyframe { get; set; }
    [ProtoMember(9)] public List<PluginSurfaceMoveMessage> Moves { get; set; } = [];
    [ProtoMember(10)] public List<PluginSurfaceTileMessage> Tiles { get; set; } = [];
    [ProtoMember(11)] public byte[] Data { get; set; } = [];
    [ProtoMember(12)] public int Popup { get; set; }
}

[ProtoContract]
public sealed class PluginSurfaceMoveMessage
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Width { get; set; }
    [ProtoMember(4)] public int Height { get; set; }
    [ProtoMember(5)] public int Dx { get; set; }
    [ProtoMember(6)] public int Dy { get; set; }
}

// Codec is TileCodecKind's value.
[ProtoContract]
public sealed class PluginSurfaceTileMessage
{
    [ProtoMember(1)] public int X { get; set; }
    [ProtoMember(2)] public int Y { get; set; }
    [ProtoMember(3)] public int Width { get; set; }
    [ProtoMember(4)] public int Height { get; set; }
    [ProtoMember(5)] public int Codec { get; set; }
    [ProtoMember(6)] public int Length { get; set; }
}

// A failed call is an RpcException: Unknown is NotFound, Disabled is FailedPrecondition, InvalidParams is
// InvalidArgument and Failed is Unknown, with the message as the status detail.
public interface IPluginRpc
{
    ValueTask<PluginRosterReply> ListAsync(PluginRosterRequest request, CallContext context = default);
    ValueTask<PluginRosterReply> RescanAsync(PluginRosterRequest request, CallContext context = default);
    ValueTask<PluginRosterReply> RetryAsync(PluginRetryRequest request, CallContext context = default);
    ValueTask<PluginCallReply> CallAsync(PluginCallMessage request, CallContext context = default);
    /// <summary>Server streaming: every later publish that reaches the subscriber; never a replay.</summary>
    IAsyncEnumerable<PluginPushMessage> SubscribeAsync(PluginSubscribeMessage request, CallContext context = default);
    ValueTask<PluginFileReply> ReadFileAsync(PluginFileRequest request, CallContext context = default);
    /// <summary>Duplex: one client's session with an external plugin's process and its surfaces (protocol 19).</summary>
    IAsyncEnumerable<PluginSurfaceDown> SurfaceAsync(IAsyncEnumerable<PluginSurfaceUp> up, CallContext context = default);
}