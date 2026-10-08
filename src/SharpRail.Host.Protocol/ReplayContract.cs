namespace SharpRail.Host.Protocol;

/// <summary>
/// Metadata that makes a mutation safe to send again after its connection died: the host runs each
/// (client, request) pair once and answers a replay with the first result.
/// </summary>
public static class ReplayHeaders
{
    /// <summary>The client's identity; it spans reconnects but not restarts of the client.</summary>
    public const string Client = "x-sharprail-client";

    /// <summary>The request's id, unique within its client and unchanged across replays.</summary>
    public const string Request = "x-sharprail-request";

    /// <summary>
    /// Every request id of the client still awaiting a reply, comma-separated. The host releases each
    /// settled result not named here, which is both the receipt for replies already read and the
    /// reconciliation after a reconnect.
    /// </summary>
    public const string Resume = "x-sharprail-resume";

    /// <summary>The operations that change something and are therefore replayed under one id rather than retried.</summary>
    public static bool IsReplayable(string method) =>
        method is "SaveFile" or "ApplyGitAction" or "RevertChange" or "UndoChange" or "OpenPr" or "OpenInEditor" or "Change";
}