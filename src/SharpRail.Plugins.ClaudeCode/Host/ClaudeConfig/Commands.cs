namespace SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

/// <summary>
/// Claude's own <c>plugin</c> and <c>plugin marketplace</c> subcommands, composed as the exact argv the dialog shows
/// before the host runs it.
/// </summary>
internal static class ClaudeCommands
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(1);

    /// <summary>
    /// The program of the launcher's command line. That line is a whole interactive invocation; a subcommand run takes
    /// the program from it and nothing else, so a user's own flags cannot ride along.
    /// </summary>
    public static string ClaudeBinary(string claudeCommand) =>
        claudeCommand.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "claude";

    public static IReadOnlyList<string> PluginUninstall(string claudeCommand, string name, ClaudeWritableScope scope) =>
        [ClaudeBinary(claudeCommand), "plugin", "uninstall", name, "--scope", scope.Name(), "--yes"];

    private static IReadOnlyList<string> PluginInstall(string claudeCommand, string name, ClaudeWritableScope scope) =>
        [ClaudeBinary(claudeCommand), "plugin", "install", name, "--scope", scope.Name(), "--yes"];

    /// <summary>A move is two of Claude's own commands: installed at the target scope first, then gone from the old.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> PluginMove(string claudeCommand, string name, ClaudeWritableScope from, ClaudeWritableScope to) =>
        [PluginInstall(claudeCommand, name, to), PluginUninstall(claudeCommand, name, from)];

    public static IReadOnlyList<string> Marketplace(string claudeCommand, ClaudeMarketplaceAction action)
    {
        IReadOnlyList<string> command = [ClaudeBinary(claudeCommand), "plugin", "marketplace"];
        return action switch
        {
            AddMarketplace add => [.. command, "add", add.Source, "--scope", add.Scope.Name()],
            RemoveMarketplace remove => [.. command, "remove", remove.Name, "--scope", remove.Scope.Name()],
            UpdateMarketplace update => [.. command, "update", update.Name],
            _ => throw new InvalidOperationException("Unknown marketplace action")
        };
    }

    public static async Task<string> RunAsync(IReadOnlyList<string> command, string cwd, string what)
    {
        var run = await Bounded.RunAsync(command, Timeout, cwd);
        if (run.LaunchFailed) throw new InvalidOperationException($"Could not run {command[0]}: {run.Err.Trim()}");
        if (run.TimedOut) throw new InvalidOperationException($"{command[0]} did not finish within a minute.");
        if (!run.Ok) throw new InvalidOperationException(FirstText(run.Err.Trim(), run.Out.Trim()) ?? $"The {what} failed.");
        return FirstText(run.Out.Trim(), run.Err.Trim()) ?? "";
    }

    private static string? FirstText(params string[] candidates) => candidates.FirstOrDefault(candidate => candidate.Length > 0);

    public static async Task<CommandOutput> UninstallAsync(string claudeCommand, string name, ClaudeWritableScope scope, string cwd) =>
        new(await RunAsync(PluginUninstall(claudeCommand, name, scope), cwd, "uninstall"));

    public static async Task<CommandOutput> MoveAsync(string claudeCommand, string name, ClaudeWritableScope from, ClaudeWritableScope to, string cwd)
    {
        var commands = PluginMove(claudeCommand, name, from, to);
        var installed = await RunAsync(commands[0], cwd, "install");
        string removed;
        try { removed = await RunAsync(commands[1], cwd, "uninstall"); }
        catch (InvalidOperationException error)
        {
            throw new InvalidOperationException(
                $"Installed at {to.Name()}, but removing the {from.Name()} copy failed — the plugin is now in both scopes. {error.Message}");
        }
        return new(string.Join("\n", new[] { installed, removed }.Where(text => text.Length > 0)));
    }

    public static async Task<CommandOutput> RunMarketplaceAsync(string claudeCommand, ClaudeMarketplaceAction action, string cwd) =>
        new(await RunAsync(Marketplace(claudeCommand, action), cwd, "marketplace " + action switch
        {
            AddMarketplace => "add",
            RemoveMarketplace => "remove",
            _ => "update"
        }));
}