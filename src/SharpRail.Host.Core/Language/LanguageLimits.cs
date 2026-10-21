namespace SharpRail.Host.Core.Language;

/// <summary>Named limits of the language tools; agents never wait longer than these, whatever a server does.</summary>
internal static class LanguageLimits
{
    /// <summary>The origin an agent's MCP tool call carries.</summary>
    internal const string AgentOrigin = LanguageOrigins.AgentTool;
    /// <summary>How long a tool call waits for a server it just started before answering that the server is still initializing.</summary>
    internal static readonly TimeSpan OnDemandStartWait = TimeSpan.FromSeconds(60);
    /// <summary>The deadline of every language tool call; past it the call returns an initializing status instead of hanging.</summary>
    internal static readonly TimeSpan ToolDeadline = TimeSpan.FromSeconds(90);
}

/// <summary>Request origins and the labels they show; origin is information about who started a server, never a policy.</summary>
internal static class LanguageOrigins
{
    internal const string Human = "human";
    internal const string Config = "config";
    internal const string Workspace = "workspace-creation";
    internal const string AgentTool = "agent-tool";

    /// <summary>The label stored and shown for a request origin; empty means a person using the editor or panes.</summary>
    internal static string Normalize(string origin) => origin switch
    {
        Config => "config",
        Workspace => "workspace creation",
        AgentTool => "agent",
        _ => Human
    };
}