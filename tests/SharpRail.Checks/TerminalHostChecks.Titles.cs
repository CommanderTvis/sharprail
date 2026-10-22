using System.Text;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Plugins.Api;

namespace SharpRail.Checks;

// OSC 0/2 titles: the host follows them in a shell's output and shares them as the weakest tab title, under an agent's
// set_title and a user rename, with local and remote clients alike.
internal static partial class TerminalHostChecks
{
    private static void TitleScanner()
    {
        var scanner = new TerminalTitleScanner();
        Require(scanner.Scan("plain \e]2;Split"u8, out _) is false && scanner.Scan(" title\e\\"u8, out var split) && split == "Split title",
            "An OSC 2 title split across chunks and ended by ST must be read whole.");
        Require(scanner.Scan("\e]0;first\a\e]2;second\a"u8, out var last) && last == "second", "The last title in a chunk wins.");
        Require(!scanner.Scan("\e]1;icon only\a\e]8;;https://example.com\a\e]22;wait\a"u8, out _), "OSC 1, OSC 8 and OSC 22 are not titles.");
        Require(scanner.Scan("\e]2;\a"u8, out var cleared) && cleared is null, "An empty title clears the program's title.");
        Require(scanner.Scan("\e]2;tab\tname\u0085!\a"u8, out var clean) && clean == "tab name !", "Control characters never reach a tab label.");
        Require(!scanner.Scan(Encoding.UTF8.GetBytes("\e]2;" + new string('x', TerminalTitleScanner.MaxBytes + 1) + "\a"), out _) &&
            scanner.Scan("\e]2;after\a"u8, out var after) && after == "after", "An over-long sequence is dropped and scanning recovers.");
        Require(scanner.Scan("\e]2;cancelled\x18\e]2;kept\a"u8, out var kept) && kept == "kept", "CAN abandons a sequence.");
    }

    private static async Task ProgramTitles(ITerminalService terminals, IHostStateService client, HostStateStore store, string workspace, string mode)
    {
        var terminal = new TerminalRef(Path.GetFullPath(workspace), "osc-title-" + mode);
        async Task<TerminalTitle?> Title() => (await client.GetStateAsync()).TerminalTitles.FirstOrDefault(title => title.Terminal == terminal);
        var id = mode + "-osc-title";
        await using var session = await terminals.AttachAsync(new(id, workspace, "client", 100, 30) { TabKey = terminal.TabKey });
        var screen = new Screen(session);
        await screen.Run("printf '\\033]2;Building\\007'; printf '\\033]0;Build logs\\007'; printf 'TITLED_%s\\n' DONE");
        await screen.WaitFor("TITLED_DONE", mode);
        await Until(async () => await Title() is { Title: "Build logs", Source: TerminalTitleSource.Program }, $"{mode}: the program's OSC title must reach the client");
        await screen.Run("printf '\\033]2;\\007'");
        await Until(async () => await Title() is null, $"{mode}: an empty OSC title must clear the program's title");

        Require(store.SetTerminalTitle(terminal, "Agent task", TerminalTitleSource.Agent), "An agent may title a tab the program titled.");
        await screen.Run("printf '\\033]2;vim notes\\007'; printf 'RETITLED_%s\\n' DONE");
        await screen.WaitFor("RETITLED_DONE", mode);
        await Task.Delay(HostedTerminal.TitleMilliseconds * 2);
        Require(await Title() is { Title: "Agent task", Source: TerminalTitleSource.Agent }, $"{mode}: an OSC title replaced the agent's set_title.");

        await client.ChangeAsync([HostStateChange.TitleTerminal(terminal, "Mine")]);
        Require(!store.SetTerminalTitle(terminal, "Agent again", TerminalTitleSource.Agent) && await Title() is { Title: "Mine", Source: TerminalTitleSource.User },
            $"{mode}: an agent's set_title replaced the user's rename.");
        await session.DisposeAsync();
        await terminals.CloseAsync(id);
        store.RemoveTerminalTitles(known => known == terminal);
        Console.WriteLine($"PASS {mode} OSC 0/2 titles reach clients as the weakest tab title: user rename > set_title > program title");
    }
}