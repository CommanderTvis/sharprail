namespace SharpRail.Host.Protocol;

/// <summary>Metadata every host service shares.</summary>
public static class HostHeaders
{
    /// <summary>Trailer naming the <c>HostErrorCode</c> of a failure a client handles specifically.</summary>
    public const string ErrorCode = "x-sharprail-error-code";
}