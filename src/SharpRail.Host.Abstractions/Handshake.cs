using System.Reflection;

namespace SharpRail.Host.Abstractions;

/// <summary>
/// The wire-compatibility counter of this build. It rises whenever a host operation or message changes
/// in a way an independently shipped client must know about. Each feature that an older host cannot serve
/// records the version that introduced it here, so a newer client can hide that feature.
/// </summary>
public static class HostProtocol
{
    public const int Current = 8;

    /// <summary>
    /// Air Context is a plugin: its visibility switch and enabled workspaces left the host state and its operations left the
    /// typed project services, so a client below this version still reads and calls them where they were.
    /// </summary>
    public const int AirContextPlugin = 8;

    /// <summary>The core file-type icon setting, shared independently of the plugin roster.</summary>
    public const int FileTypeIcons = 7;

    /// <summary>All remote platforms use the multiplexed protobuf WebSocket protocol.</summary>
    public const int WebSocketTransport = 5;

    /// <summary>
    /// Workspace activity stamps, Settled/Keep active overrides with their actions, and the idle-day setting. Below
    /// it a client keeps the host's flat workspace list and offers no shelf.
    /// </summary>
    public const int WorkspaceSettle = 6;

    /// <summary>Revert and undo of a change, and the SHA-256 on both diff sides they depend on.</summary>
    public const int ChangeWritePath = 2;

    /// <summary>
    /// A mutation replayed under its request id runs once. A client replays only against a host that reports
    /// at least this version; against an older one a call lost with its connection still fails to the caller.
    /// </summary>
    public const int RequestReplay = 3;

    /// <summary>The build's own version, reported beside the protocol number.</summary>
    public static string BuildVersion => typeof(HostProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
}

/// <summary>What a host reports about itself; version 0 means the host predates the handshake.</summary>
public sealed record HostHandshake(int ProtocolVersion, string HostVersion)
{
    /// <summary>Whether this host serves a feature introduced at <paramref name="introducedAt"/>.</summary>
    public bool Supports(int introducedAt) => ProtocolVersion >= introducedAt;
}