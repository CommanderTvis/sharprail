namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

internal enum McpListStatus { Connected, Disabled, Failed, NeedsAuth, Pending, Unknown }

internal sealed record McpListEntry(string Name, string Target, McpListStatus Status, string StatusText);

/// <summary>The servers <c>claude mcp list</c> reports, including the ones no file in the worktree declares.</summary>
internal static class McpList
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    private static readonly (string Glyph, McpListStatus Status)[] StatusGlyphs =
        [("✔", McpListStatus.Connected), ("⊘", McpListStatus.Disabled), ("✘", McpListStatus.Failed), ("!", McpListStatus.NeedsAuth), ("⏸", McpListStatus.Pending)];

    /// <summary>
    /// One server per line as <c>&lt;name&gt;: &lt;target&gt; - &lt;glyph&gt; &lt;status&gt;</c>; a name may contain spaces
    /// ("claude.ai Uber Eats"), so it ends at the first <c>": "</c> and the status starts at the last <c>" - "</c>.
    /// Anything else on stdout (the health-check banner) is skipped.
    /// </summary>
    public static IReadOnlyList<McpListEntry> Parse(string output)
    {
        var entries = new List<McpListEntry>();
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            var dash = line.LastIndexOf(" - ", StringComparison.Ordinal);
            if (colon <= 0 || dash <= colon) continue;
            var statusText = line[(dash + 3)..].Trim();
            var status = StatusGlyphs.FirstOrDefault(glyph => statusText.StartsWith(glyph.Glyph, StringComparison.Ordinal)) is { Glyph: not null } found
                ? found.Status : McpListStatus.Unknown;
            entries.Add(new(line[..colon].Trim(), line[(colon + 2)..dash].Trim(), status, statusText));
        }
        return entries;
    }

    public static async Task<IReadOnlyList<McpListEntry>> ListAsync(string claudeCommand, string cwd, IReadOnlyDictionary<string, string> environment)
    {
        var binary = ClaudeCommands.ClaudeBinary(claudeCommand);
        var run = await Bounded.RunAsync([binary, "mcp", "list"], Timeout, cwd, environment);
        if (run.LaunchFailed) throw new InvalidOperationException($"Could not run {binary}: {run.Err.Trim()}");
        if (run.TimedOut) throw new InvalidOperationException("claude mcp list did not finish within a minute.");
        if (!run.Ok) throw new InvalidOperationException(run.Err.Trim() is { Length: > 0 } error ? error : run.Out.Trim() is { Length: > 0 } output ? output : "claude mcp list failed.");
        return Parse(run.Out);
    }

    /// <summary>
    /// The servers Claude reaches that no file in the worktree declares: claude.ai connectors and plugin servers. They
    /// wear the user scope with no path, which tells the pane there is nothing to edit; one <c>/mcp</c> disabled for this
    /// project says so through <c>DisabledBy</c>, pointing at the list in <c>~/.claude.json</c> that <c>/mcp</c> writes.
    /// </summary>
    public static IReadOnlyList<ClaudeCapability> Capabilities(IReadOnlyList<McpListEntry> entries, IReadOnlySet<string> declared, string root) =>
        [.. entries.Where(entry => !declared.Contains(entry.Name)).Select(entry => new ClaudeCapability(ClaudeCapabilityKind.Mcp, entry.Name,
            new(ClaudeConfigScope.User), entry.Status != McpListStatus.Disabled)
        {
            Detail = $"{entry.Target} · {entry.StatusText}",
            DisabledBy = entry.Status == McpListStatus.Disabled
                ? new(ClaudeConfigScope.Local, ClaudePaths.ClaudeStatePath()) { KeyPath = ["projects", root, "disabledMcpServers"] }
                : null
        })];
}