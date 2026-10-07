using System.Reflection;

namespace SharpRail.Host.Abstractions;

/// <summary>
/// The wire-compatibility counter of this build. It rises whenever a host operation or message changes
/// in a way an independently shipped client must know about. Each feature that an older host cannot serve
/// records the version that introduced it here, so a newer client can hide that feature.
/// </summary>
public static class HostProtocol
{
    public const int Current = 2;

    /// <summary>Revert and undo of a change, and the SHA-256 on both diff sides they depend on.</summary>
    public const int ChangeWritePath = 2;

    /// <summary>The build's own version, reported beside the protocol number.</summary>
    public static string BuildVersion => typeof(HostProtocol).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "";
}

/// <summary>What a host reports about itself; version 0 means the host predates the handshake.</summary>
public sealed record HostHandshake(int ProtocolVersion, string HostVersion)
{
    /// <summary>Whether this host serves a feature introduced at <paramref name="introducedAt"/>.</summary>
    public bool Supports(int introducedAt) => ProtocolVersion >= introducedAt;
}