using Avalonia.Controls;


namespace SharpRail.Plugins.Chat.UI;

/// <summary>Who a chat view talks to: the words it uses for the agent, the document it lives in and the permission modes the agent offers.</summary>
internal sealed record ChatAgent(string Name, string Short, string Scheme, string Icon)
{
    /// <summary>The agent's kind in a terminal tab's agent record (<c>claude</c>), so a session a terminal already runs is not resumed here.</summary>
    public string TerminalKind { get; init; } = "";
    /// <summary>Where the "View help docs" action goes; null omits it.</summary>
    public string? HelpUrl { get; init; }
    /// <summary>The end of the message shown when the host has no chat handler; the first sentence is fixed.</summary>
    public string UnavailableHint { get; init; } = ".";
    /// <summary>The effort (thinking) levels offered when the current model reports none of its own.</summary>
    public IReadOnlyList<string> EffortLevels { get; init; } = [];
    /// <summary>The modes to offer when the host sent none, with the machine's own list when it has one.</summary>
    public Func<Task<IReadOnlyList<ChatMode>>> Modes { get; init; } = () => Task.FromResult<IReadOnlyList<ChatMode>>([]);
    /// <summary>The words for one mode id; an unknown id keeps itself as its label.</summary>
    public Func<string, ChatMode> DescribeMode { get; init; } = id => new ChatMode(id, id, "");
    /// <summary>The label of a mode in a menu or on a chip.</summary>
    public string ModeLabel(ChatMode mode) => mode.Dangerous ? mode.Label + " (dangerous)" : mode.Label;
}

internal static class ChatErrors
{
    public static string Text(Exception error, string fallback = "The request failed.") => error.Message is { Length: > 0 } message ? message : fallback;
}

internal static class ChatButtons
{
    public static Button Primary(Button button)
    {
        button.Classes.Add("primary");
        return button;
    }
}