namespace SharpRail.Plugins.Agent.UI;

/// <summary>Why an agent's terminal wants the user while they are away.</summary>
public enum AgentAttentionReason { Blocked, Done, Failed }

/// <summary>The wording of an agent's away notification, shared so every agent's reads alike.</summary>
public static class AgentAttention
{
    /// <summary>The headline: who wants the user and why, scoped to a project when one is known.</summary>
    public static string Title(string agent, AgentAttentionReason reason, string? project = null)
    {
        var scope = project is { Length: > 0 } ? " — " + project : "";
        return reason switch
        {
            AgentAttentionReason.Blocked => $"{agent} needs you{scope}",
            AgentAttentionReason.Done => $"{agent} finished{scope}",
            _ => $"{agent} hit an error{scope}"
        };
    }

    /// <summary>The first detail the agent reported, or a pointer to the terminal when it reported none.</summary>
    public static string Body(params string?[] details) =>
        details.FirstOrDefault(detail => detail is { Length: > 0 }) ?? "Open the terminal for details.";
}