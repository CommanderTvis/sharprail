using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Blueprint.UI;

internal sealed class BlueprintOpener(IPluginUIContext context, BlueprintStore store)
{
    public const string AuthorTab = "blueprint-author";
    private readonly Dictionary<string, Task> opening = [];

    public Task Open(string workspace)
    {
        if (opening.TryGetValue(workspace, out var pending)) return pending;
        var next = OpenOnce(workspace);
        opening[workspace] = next;
        _ = Forget(workspace, next);
        return next;
    }

    private async Task Forget(string workspace, Task operation)
    {
        try { await operation; }
        catch (Exception) { }
        finally { opening.Remove(workspace); }
    }

    private async Task OpenOnce(string workspace)
    {
        var state = store.Get(workspace);
        if (state is null)
        {
            state = (await context.RequestAsync(BlueprintContract.Get, new(workspace))).State;
            store.Set(workspace, state);
        }
        if (state?.Author is BlueprintTerminalAuthor author)
        {
            if (context.Host().Terminals.GetValueOrDefault(workspace)?.Any(tab => tab.TabKey == author.TabKey) == true)
            {
                await context.OpenTerminalAsync(workspace, new() { TabKey = author.TabKey });
                context.FocusCompanion(new(workspace, author.TabKey), "blueprint");
                return;
            }
            if (author.AgentSessionId is { Length: > 0 } sessionId && context.Launchers().FirstOrDefault(launcher => launcher.Id == "claude") is { } launcher)
            {
                await context.OpenTerminalAsync(workspace, new() { TabKey = author.TabKey, Command = launcher.TerminalCommand(new() { ResumeSessionId = sessionId }) });
                context.FocusCompanion(new(workspace, author.TabKey), "blueprint");
                return;
            }
        }
        await Start(workspace, state?.Source ?? new BlueprintProduct());
    }

    public async Task Start(string workspace, BlueprintSource source)
    {
        var launcher = context.Launchers().FirstOrDefault(candidate => candidate.Id == "claude")
            ?? throw new InvalidOperationException("Enable the Claude Code plugin to author a blueprint in a terminal.");
        var availability = launcher.Availability();
        if (!availability.Available) throw new InvalidOperationException(availability.Reason ?? "Claude Code is unavailable on this host.");
        var opened = await context.RequestAsync(BlueprintContract.Open, new(workspace, source, BlueprintAgentId.Claude));
        store.Set(workspace, opened.State);
        var command = launcher.TerminalCommand(new() { SystemPrompt = opened.SystemPrompt, InitialPrompt = opened.Opening });
        await context.OpenTerminalAsync(workspace, new() { TabKey = AuthorTab, Command = command });
        await context.RequestAsync(BlueprintContract.SetAuthor, new(workspace, new BlueprintTerminalAuthor(AuthorTab)));
        store.Follow(workspace);
        context.FocusCompanion(new(workspace, AuthorTab), "blueprint");
    }
}