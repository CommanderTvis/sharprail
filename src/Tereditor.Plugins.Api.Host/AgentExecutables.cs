namespace Tereditor.Plugins.Api.Host;

/// <summary>
/// The coding agents tereditor knows, by the executable they run as: Claude Code, Codex, omp, Pi and goose (the ACP registry's agent that
/// ships its own binary). The terminals' memory guard spares them and the Processes pane lists them; an agent started through a generic
/// runtime (<c>node</c>, <c>bun</c>, <c>python</c>) runs under that runtime's name and is not among these.
/// </summary>
public static class AgentExecutables
{
    /// <summary>The executables' names, lower case, without an extension.</summary>
    public static IReadOnlyList<string> Names { get; } = ["claude", "codex", "omp", "pi", "goose"];
}