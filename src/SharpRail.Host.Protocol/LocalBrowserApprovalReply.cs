namespace SharpRail.Host.Protocol;

/// <summary>A temporary, origin-bound browser credential issued after approval in the local app.</summary>
public sealed record LocalBrowserApprovalReply(string Token);