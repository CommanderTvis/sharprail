namespace SharpRail.Host.Core.Language;

/// <summary>Named limits of the language tools; agents never wait longer than these, whatever a server does.</summary>
internal static class LanguageLimits
{
    /// <summary>The origin an agent's MCP tool call carries; servers it starts are the only ones SharpRail may stop when idle.</summary>
    internal const string AgentOrigin = "agent-tool";
    /// <summary>How long a tool call waits for a server it just started before answering that the server is still initializing.</summary>
    internal static readonly TimeSpan OnDemandStartWait = TimeSpan.FromSeconds(60);
    /// <summary>The deadline of every language tool call; past it the call returns an initializing status instead of hanging.</summary>
    internal static readonly TimeSpan ToolDeadline = TimeSpan.FromSeconds(90);
}