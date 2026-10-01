namespace SharpRail.Plugins.Api;

/// <summary>Identifies one terminal tab: the workspace it belongs to and its tab key within that workspace.</summary>
/// <param name="WorkspaceId">The workspace's id, its worktree root path.</param>
/// <param name="TabKey">The terminal tab's layout id, stable across windows and restarts.</param>
public sealed record TerminalRef(string WorkspaceId, string TabKey);

/// <summary>Describes the call site a tool ran from.</summary>
/// <param name="Cwd">The resolved working directory for the call.</param>
/// <param name="WorkspaceId">The workspace the call ran in, or <see langword="null"/> outside any workspace.</param>
/// <param name="Terminal">The terminal whose MCP token made the call.</param>
public sealed record PluginToolContext(string Cwd, string? WorkspaceId, TerminalRef? Terminal);

/// <summary>The value a tool returns.</summary>
/// <param name="Text">The textual result shown to the MCP caller.</param>
public sealed record PluginToolResult(string Text)
{
    /// <summary>Marks <see cref="Text"/> as an error message rather than a normal result.</summary>
    public bool IsError { get; init; }

    /// <summary>Structured detail alongside <see cref="Text"/>, serialized with <see cref="PluginJson.Options"/>.</summary>
    public object? Details { get; init; }
}

/// <summary>
/// One tool, registered by a host half and served on the per-terminal MCP surface: an agent running in a
/// SharpRail terminal lists and calls it through the terminal's MCP token. Declare tools as
/// <see cref="PluginTool{TParams}"/>; the runtime derives the MCP input schema from
/// <see cref="ParametersType"/> and validates arguments by strict deserialization before running the tool.
/// </summary>
public abstract class PluginToolDefinition
{
    private protected PluginToolDefinition(string name, string label, string description, Type parametersType)
    {
        Name = name; Label = label; Description = description; ParametersType = parametersType;
    }

    /// <summary>The tool's name, unique across the host's MCP table.</summary>
    public string Name { get; }

    /// <summary>The human-readable title MCP clients show.</summary>
    public string Label { get; }

    /// <summary>Shown to the agent alongside the tool's schema.</summary>
    public string Description { get; }

    /// <summary>The record type the call's arguments deserialize into.</summary>
    public Type ParametersType { get; }

    /// <summary>Runs the tool.</summary>
    /// <param name="parameters">The validated arguments: an instance of <see cref="ParametersType"/>, or a JSON element convertible to it.</param>
    /// <param name="context">The call site the tool ran from.</param>
    /// <param name="cancellationToken">Cancelled when the caller goes away or the host stops.</param>
    /// <returns>The tool's result.</returns>
    public abstract ValueTask<PluginToolResult> RunAsync(object parameters, PluginToolContext context, CancellationToken cancellationToken);
}

/// <summary>A typed tool whose arguments are <typeparamref name="TParams"/>.</summary>
/// <typeparam name="TParams">The arguments record.</typeparam>
/// <param name="name">The tool's name.</param>
/// <param name="label">The human-readable title.</param>
/// <param name="description">Shown to the agent.</param>
/// <param name="run">Runs the tool with validated arguments.</param>
/// <example>
/// <code>
/// context.Tool(new PluginTool&lt;AddTodo&gt;("add_todo", "Add Todo", "Adds a todo item",
///     (args, call, token) =&gt; ValueTask.FromResult(new PluginToolResult($"added: {args.Text}"))));
/// </code>
/// </example>
public sealed class PluginTool<TParams>(string name, string label, string description, Func<TParams, PluginToolContext, CancellationToken, ValueTask<PluginToolResult>> run)
    : PluginToolDefinition(name, label, description, typeof(TParams))
{
    /// <inheritdoc />
    public override ValueTask<PluginToolResult> RunAsync(object parameters, PluginToolContext context, CancellationToken cancellationToken) =>
        run(PluginJson.Convert<TParams>(parameters), context, cancellationToken);
}