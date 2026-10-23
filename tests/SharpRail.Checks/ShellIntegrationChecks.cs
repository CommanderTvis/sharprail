using System.Net;
using System.Text;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

// SharpRail's shell integration in shells the host starts: real zsh in a fixture HOME with no user startup files, locally
// and through a real WebSocket RPC host, with the host setting on and off.
internal static partial class ShellIntegrationChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Shell integration: " + message); }

    internal static async Task RunHost(string root)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) { Console.WriteLine("SKIP shell integration: PTYs require macOS or Linux."); return; }
        var resources = ShellIntegration.DefaultResources;
        Require(File.Exists(Path.Combine(resources, "zsh", ".zshenv")) && File.Exists(Path.Combine(resources, "bash", "sharprail.bash")),
            "the app must ship SharpRail's zsh and bash scripts in " + resources);
        Plans(resources);
        ShellModel();
        var fixture = Fixture(root);
        await WithFixture(fixture, async () =>
        {
            var workspace = Path.Combine(fixture, "workspace");
            await using (var on = new PtyTerminalService("/bin/zsh", shellIntegration: () => true))
            {
                await Marked(new LocalTerminalAdapter(on), workspace, "local");
                await UserStartup(new LocalTerminalAdapter(on), fixture, workspace);
            }
            await LinksFollowReports(workspace);
            await using (var off = new PtyTerminalService("/bin/zsh", shellIntegration: () => false))
                await Unmarked(new LocalTerminalAdapter(off), workspace, "local");
            if (OperatingSystem.IsMacOS())
                await using (var bash = new PtyTerminalService("/bin/bash", shellIntegration: () => true))
                    await AppleBash(new LocalTerminalAdapter(bash), workspace);

            await using var app = RemoteServer.Create(root, IPAddress.Loopback, 0, "integration-token");
            await app.StartAsync();
            try
            {
                var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
                using var remote = new RemoteTerminalAdapter(address, "integration-token");
                using var state = new RemoteStateAdapter(address, "integration-token");
                Require((await state.GetStateAsync()).Settings.ShellIntegrationEnabled, "a remote host must integrate by default.");
                await Marked(remote, workspace, "remote");
                Require(!(await state.ChangeAsync([HostStateChange.Setting("shell-integration", "false")])).Settings.ShellIntegrationEnabled,
                    "turning integration off must survive the wire.");
                await Unmarked(remote, workspace, "remote");
            }
            finally { await app.StopAsync(); }
        });
        Console.WriteLine("PASS shell integration: SharpRail's scripts ship with the app; zsh marks prompts (OSC 133 A/B/C/D) and reports its directory (OSC 7) locally and remotely; the host's model gives every client the phase, each command's exit code and busy only while one runs; new terminals start in the reported directory and relative links resolve there; keeps the user's ZDOTDIR and re-marks a prompt the user's precmd rewrites; bash gets the ENV injection and fish none; Apple's /bin/bash and the setting off inject nothing");
    }

    // The injection for shells this machine may not have.
    private static void Plans(string resources)
    {
        var empty = new Dictionary<string, string>();
        var zsh = ShellIntegration.For("/bin/zsh", resources, new Dictionary<string, string> { ["ZDOTDIR"] = "/home/me/.config/zsh" })
            ?? throw new InvalidOperationException("Shell integration: zsh must be integrated.");
        Require(zsh.Variables["ZDOTDIR"] == Path.Combine(resources, "zsh") && zsh.Variables["SHARPRAIL_SHELL_ZDOTDIR"] == "/home/me/.config/zsh" &&
            zsh.Arguments.Length == 0 && zsh.Variables.Count == 2,
            "zsh must start from SharpRail's ZDOTDIR and keep the user's in SHARPRAIL_SHELL_ZDOTDIR.");
        Require(!(ShellIntegration.For("/bin/zsh", resources, empty)?.Variables.ContainsKey("SHARPRAIL_SHELL_ZDOTDIR") ?? true), "an unset ZDOTDIR must stay unset.");
        var bash = ShellIntegration.For("/usr/local/bin/bash", resources, new Dictionary<string, string> { ["ENV"] = "/home/me/.envrc" })
            ?? throw new InvalidOperationException("Shell integration: a non-Apple bash must be integrated.");
        Require(bash.Arguments.SequenceEqual(["--posix"]) && bash.Variables["ENV"] == Path.Combine(resources, "bash", "sharprail.bash") &&
            bash.Variables["SHARPRAIL_SHELL_INJECT"] == "1" && bash.Variables["SHARPRAIL_SHELL_BASH_ENV"] == "/home/me/.envrc" &&
            bash.Variables["HISTFILE"].EndsWith("/.bash_history", StringComparison.Ordinal) && bash.Variables["SHARPRAIL_SHELL_HISTFILE_UNEXPORT"] == "1",
            "bash must start in POSIX mode reading SharpRail's script through ENV, keeping ENV and the history file.");
        Require(!ShellIntegration.For("/usr/local/bin/bash", resources, new Dictionary<string, string> { ["HISTFILE"] = "/h" })!.Value.Variables.ContainsKey("HISTFILE"),
            "a user's HISTFILE must be left alone.");
        Require(ShellIntegration.For("/usr/bin/fish", resources, new Dictionary<string, string> { ["XDG_DATA_DIRS"] = "/opt/share" }) is null &&
            ShellIntegration.For("/usr/bin/elvish", resources, empty) is null, "fish marks its own prompts and elvish is not integrated: neither gets anything.");
        Require(ShellIntegration.For("/bin/sh", resources, empty) is null && ShellIntegration.For("/usr/local/bin/bash", Path.Combine(resources, "missing"), empty) is null,
            "an unknown shell or missing scripts must inject nothing.");
        Require(ShellIntegration.For("/bin/bash", resources, empty) is null == OperatingSystem.IsMacOS(), "only Apple's /bin/bash, which ignores ENV, is left alone.");
    }

    // The host's reading of shell integration from raw output, split anywhere.
    private static void ShellModel()
    {
        var bytes = Encoding.UTF8.GetBytes("\u001b]133;A;cl=line\u0007% \u001b]133;B\u0007ls\r\n\u001b]133;C\u0007out\r\n\u001b]133;D;2\u0007" +
            "\u001b]7;file://localhost/tmp/a%20b\u001b\\\u001b]133;A\u0007\u001b]133;D\u0007\u001b]133;P;k=s\u0007");
        for (var split = 0; split <= bytes.Length; split++)
        {
            var state = new TerminalShellState();
            state.Push(bytes.AsSpan(0, split)); state.Push(bytes.AsSpan(split));
            var status = state.Status;
            Require(status.Phase == TerminalShellPhase.Prompt && status.LastExitCode == 2 && status.Directory == "/tmp/a b" && status.Marked &&
                status.Commands.Count == 1 && status.Commands[0].ExitCode == 2 && status.Commands[0].End > status.Commands[0].Start,
                $"split at {split}: {status} {string.Join(",", status.Commands)}");
        }
        Require(TerminalShellState.DirectoryOf("kitty-shell-cwd://" + Environment.MachineName + "/raw%20path") == "/raw%20path", "kitty-shell-cwd paths are raw.");
        Require(TerminalShellState.DirectoryOf("file://elsewhere.example/tmp") is null, "another machine's directory must be ignored.");
        // An agent run by the shell draws its own prompt: its marks are told from the shell's.
        var busy = false;
        var nested = new TerminalShellState { ForegroundBusy = () => busy };
        var seen = new List<(TerminalShellPhase, bool)>();
        nested.Changed = () => seen.Add((nested.Phase, nested.Foreground));
        nested.Push("\u001b]133;A\u0007\u001b]133;B\u0007claude\r\n\u001b]133;C\u0007"u8);
        busy = true;
        nested.Push("\u001b]133;A\u0007> \u001b]133;B\u0007hi\u001b]133;C\u0007working\u001b]133;D;0\u0007\u001b]133;A\u0007"u8);
        busy = false;
        nested.Push("\u001b]133;D;0\u0007\u001b]133;A\u0007"u8);
        Require(seen.SequenceEqual([(TerminalShellPhase.Prompt, false), (TerminalShellPhase.Input, false), (TerminalShellPhase.Running, false),
            (TerminalShellPhase.Prompt, true), (TerminalShellPhase.Input, true), (TerminalShellPhase.Running, true), (TerminalShellPhase.Prompt, true),
            (TerminalShellPhase.Prompt, false)]) && nested.Status.Commands.Count == 1,
            "an agent's marks inside a shell command: " + string.Join(" ", seen));
        var running = new TerminalShellState();
        running.Push("\u001b]133;C;cwd=/srv/x\u0007"u8);
        Require(running.Phase == TerminalShellPhase.Running && running.Directory == "/srv/x", "a running command and an OSC 133 cwd option.");
    }

    private static string Fixture(string root)
    {
        var fixture = Path.Combine(root, "shell-integration");
        if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        Directory.CreateDirectory(Path.Combine(fixture, "home"));
        Directory.CreateDirectory(Path.Combine(fixture, "workspace", "nested dir"));
        return fixture;
    }

    // A HOME with no startup files, so only the system's and SharpRail's run, and no inherited ZDOTDIR.
    private static async Task WithFixture(string fixture, Func<Task> body)
    {
        string[] names = ["HOME", "ZDOTDIR", "SHELL", "LOGNAME", "ENV", "HISTFILE", "TERM_PROGRAM"];
        var saved = names.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        Environment.SetEnvironmentVariable("HOME", Path.Combine(fixture, "home"));
        Environment.SetEnvironmentVariable("ZDOTDIR", null);
        Environment.SetEnvironmentVariable("SHELL", "/bin/zsh");
        Environment.SetEnvironmentVariable("LOGNAME", null);
        Environment.SetEnvironmentVariable("ENV", null);
        Environment.SetEnvironmentVariable("HISTFILE", null);
        // Apple's /etc/zshrc reports the directory itself for Terminal.app.
        Environment.SetEnvironmentVariable("TERM_PROGRAM", null);
        try { await body(); }
        finally { foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value); }
    }

    private const string PromptStart = "\u001b]133;A", InputStart = "\u001b]133;B", OutputStart = "\u001b]133;C", CommandEnd = "\u001b]133;D";

    private static async Task Marked(ITerminalService terminals, string workspace, string mode)
    {
        await using var session = await terminals.AttachAsync(new("integration-" + Guid.NewGuid().ToString("N"), workspace, "client", 100, 30));
        var screen = new Output(session);
        try
        {
            await screen.WaitFor(PromptStart, mode + " prompt start");
            await screen.WaitFor(InputStart, mode + " input start");
            await screen.WaitFor("\u001b]7;file://", mode + " working directory");
            await screen.WaitFor("/workspace\u0007", mode + " OSC 7 naming the workspace");
            await screen.Run("echo INTEGRATED_$((6*7))");
            await screen.WaitFor(OutputStart, mode + " output start");
            await screen.WaitFor("INTEGRATED_42", mode + " command output");
            await screen.WaitFor(CommandEnd + ";0", mode + " command end with status");
            await Status(terminals, session, screen, mode);
            await screen.Run("cd 'nested dir'");
            await screen.WaitFor("nested%20dir\u0007", mode + " OSC 7 after cd");
            await Inherits(terminals, session.Id, workspace, mode);
            await LinksFollowCd(terminals, session, screen, workspace, mode);
            await screen.Run("env | grep -c '^SHARPRAIL_SHELL_\\|^ZDOTDIR=' ; echo ZDOT_$?");
            await screen.WaitFor("ZDOT_1", mode + " ZDOTDIR restored after startup");
        }
        finally { await session.KillAsync(); await terminals.CloseAsync(session.Id); }
    }

    // The host's model of the shell, as every client reads it: the phase, each command's exit code, busy only while one runs.
    private static async Task Status(ITerminalService terminals, ITerminalSession session, Output screen, string mode)
    {
        async Task<TerminalShellStatus> Until(Func<TerminalShellStatus, bool> condition, string what)
        {
            var deadline = Awake.Now.AddSeconds(20);
            while (true)
            {
                if (await terminals.ShellStatusAsync(session.Id) is { } status && condition(status)) return status;
                if (Awake.Now > deadline) throw new InvalidOperationException($"Shell integration: {mode} shell status never showed {what}: {await terminals.ShellStatusAsync(session.Id)}");
                await Task.Delay(50);
            }
        }
        var idle = await Until(status => status is { Marked: true, Phase: TerminalShellPhase.Input, LastExitCode: 0 } && status.Commands.Count >= 1, "a finished command at the prompt");
        Require(!await terminals.IsBusyAsync(session.Id), mode + " a shell at its prompt is not busy.");
        var count = idle.Commands.Count;
        await screen.Run("sleep 2; (exit 3)");
        await Until(status => status.Phase == TerminalShellPhase.Running, "a running command");
        Require(await terminals.IsBusyAsync(session.Id), mode + " a running command makes the terminal busy.");
        var failed = await Until(status => status.Phase == TerminalShellPhase.Input && status.Commands.Count == count + 1, "the failed command");
        Require(failed.LastExitCode == 3 && failed.Commands[^1].ExitCode == 3 && failed.Commands[^1].Start < failed.Commands[^1].End,
            mode + " the failed command's exit code: " + failed);
        Require(failed.Directory?.EndsWith("/workspace", StringComparison.Ordinal) == true, mode + " the reported directory: " + failed.Directory);
        Require(await terminals.ShellStatusAsync("no-such-session") is null, mode + " an unknown session has no status.");
    }

    // A terminal opened from this one starts in the directory its shell reported; one from another workspace does not.
    private static async Task Inherits(ITerminalService terminals, string source, string workspace, string mode)
    {
        await using var child = await terminals.AttachAsync(new("inherit-" + Guid.NewGuid().ToString("N"), workspace, "client", 100, 30) { DirectoryFrom = source });
        var screen = new Output(child);
        try
        {
            await screen.Run("printf 'CHILD_%s_\\n' \"$(basename \"$(pwd)\")\"");
            await screen.WaitFor("CHILD_nested dir_", mode + " new terminal in the reported directory");
        }
        finally { await child.KillAsync(); await terminals.CloseAsync(child.Id); }
        var elsewhere = Path.Combine(Path.GetDirectoryName(workspace)!, "home");
        await using var other = await terminals.AttachAsync(new("other-" + Guid.NewGuid().ToString("N"), elsewhere, "client", 100, 30) { DirectoryFrom = source });
        var otherScreen = new Output(other);
        try
        {
            await otherScreen.Run("printf 'OTHER_%s_\\n' \"$(basename \"$(pwd)\")\"");
            await otherScreen.WaitFor("OTHER_home_", mode + " another workspace keeps its root");
        }
        finally { await other.KillAsync(); await terminals.CloseAsync(other.Id); }
    }

    // Relative file links resolve in the directory the shell reports after a cd, on the host that runs it.
    private static async Task LinksFollowCd(ITerminalService terminals, ITerminalSession session, Output screen, string workspace, string mode)
    {
        var reported = Path.Combine(workspace, "reported-" + mode);
        Directory.CreateDirectory(reported);
        await File.WriteAllTextAsync(Path.Combine(reported, "only-here.txt"), "x");
        Require((await terminals.FindFileLinksAsync(session.Id, "see only-here.txt")).Count == 0, mode + " a file outside the shell's directory must not link.");
        await screen.Run($"cd '{reported}'");
        await screen.WaitFor("reported-" + mode + "\u0007", mode + " OSC 7 after cd");
        var links = await terminals.FindFileLinksAsync(session.Id, "see only-here.txt");
        Require(links.Count == 1 && links[0].Path.EndsWith("/reported-" + mode + "/only-here.txt", StringComparison.Ordinal),
            mode + " a relative link must resolve in the reported directory: " + string.Join(",", links.Select(link => link.Path)));
    }

    // A program without shell integration that reports a directory (OSC 7) moves link resolution there, though the
    // process itself never left the workspace: the report wins over the process probe.
    private static async Task LinksFollowReports(string workspace)
    {
        await using var host = new PtyTerminalService("/bin/sh");
        var terminals = new LocalTerminalAdapter(host);
        await using var session = await terminals.AttachAsync(new("report-" + Guid.NewGuid().ToString("N"), workspace, "client", 100, 30));
        var screen = new Output(session);
        var reported = Path.Combine(workspace, "reported dir");
        Directory.CreateDirectory(reported);
        await File.WriteAllTextAsync(Path.Combine(reported, "report-only.txt"), "x");
        await screen.Run($"printf '\\033]7;file://localhost%s\\007MOVED\\n' '{reported.Replace(" ", "%20")}'");
        await screen.WaitFor("MOVED", "a reported directory");
        var links = await terminals.FindFileLinksAsync(session.Id, "see report-only.txt");
        Require(links.Count == 1 && links[0].Path == Path.Combine(reported, "report-only.txt"),
            "a relative link must resolve in the OSC 7 directory: " + string.Join(",", links.Select(link => link.Path)));
        await session.KillAsync();
    }

    private static async Task Unmarked(ITerminalService terminals, string workspace, string mode)
    {
        await using var session = await terminals.AttachAsync(new("plain-" + Guid.NewGuid().ToString("N"), workspace, "client", 100, 30));
        var screen = new Output(session);
        try
        {
            await screen.Run("echo PLAIN_$((6*7)); env | grep -c '^GHOSTTY_\\|^SHARPRAIL_SHELL_'");
            await screen.WaitFor("PLAIN_42", mode + " plain output");
            await Task.Delay(300);
            Require(!screen.Text.Contains("\u001b]133;", StringComparison.Ordinal) && !screen.Text.Contains("\u001b]7;", StringComparison.Ordinal),
                mode + " with integration off a shell must emit no prompt marks or directory reports: " + screen.Text);
            await screen.WaitFor("\n0", mode + " no integration variables");
        }
        finally { await session.KillAsync(); await terminals.CloseAsync(session.Id); }
    }

    // The user's ZDOTDIR still runs: SharpRail's .zshenv restores it before zsh reads .zshrc from it. A prompt the user's
    // own precmd rebuilds is marked again, and an empty Enter reports no command.
    private static async Task UserStartup(ITerminalService terminals, string fixture, string workspace)
    {
        var dot = Path.Combine(fixture, "zdotdir");
        Directory.CreateDirectory(dot);
        await File.WriteAllTextAsync(Path.Combine(dot, ".zshrc"), "echo USER_RC_LOADED\nprecmd() { PROMPT='custom> '; return 7; }\n");
        Environment.SetEnvironmentVariable("ZDOTDIR", dot);
        try
        {
            await using var session = await terminals.AttachAsync(new("zdotdir-" + Guid.NewGuid().ToString("N"), workspace, "client", 100, 30));
            var screen = new Output(session);
            try
            {
                await screen.WaitFor("USER_RC_LOADED", "the user's .zshrc");
                await screen.WaitFor(PromptStart + "\u0007custom> " + InputStart, "prompt marks around the user's own prompt");
                await screen.Run("false");
                await screen.WaitFor(CommandEnd + ";1\u0007", "the command's status, not the user precmd's");
                var before = screen.Text.Split(CommandEnd).Length;
                await screen.Run("");
                await screen.Run("echo EMPTY_DONE");
                await screen.WaitFor(CommandEnd + ";0\u0007", "the next command's end");
                Require(screen.Text.Split(CommandEnd).Length == before + 1, "an empty Enter must report no command: " + screen.Text);
                await screen.Run("echo ZDOT=$ZDOTDIR");
                await screen.WaitFor("ZDOT=" + dot, "the user's ZDOTDIR restored");
            }
            finally { await session.KillAsync(); await terminals.CloseAsync(session.Id); }
        }
        finally { Environment.SetEnvironmentVariable("ZDOTDIR", null); }
    }

    // Apple's bash 3.2 ignores ENV in POSIX mode and is left alone; it still starts, as a plain login shell.
    private static async Task AppleBash(ITerminalService terminals, string workspace)
    {
        await using var session = await terminals.AttachAsync(new("bash-" + Guid.NewGuid().ToString("N"), workspace, "client", 100, 30));
        var screen = new Output(session);
        try
        {
            await screen.Run("echo BASH_${ENV:-none}_$((6*7))");
            await screen.WaitFor("BASH_none_42", "Apple bash without ENV injection");
        }
        finally { await session.KillAsync(); await terminals.CloseAsync(session.Id); }
    }

    private sealed class Output
    {
        private readonly StringBuilder text = new();
        private readonly ITerminalSession session;
        public string Text { get { lock (text) return text.ToString(); } }

        public Output(ITerminalSession session)
        {
            this.session = session;
            text.Append(Encoding.UTF8.GetString(session.Replay.Span));
            _ = Task.Run(async () =>
            {
                try { await foreach (var chunk in session.ReadAsync()) lock (text) text.Append(Encoding.UTF8.GetString(chunk.Span)); }
                catch (Exception) when (session.Exit.IsCompleted || session.Detached.IsCompleted) { }
            });
        }

        public async Task Run(string command) => await session.WriteAsync(Encoding.UTF8.GetBytes(command + "\r"));

        public async Task WaitFor(string marker, string description)
        {
            var deadline = Awake.Now.AddSeconds(20);
            while (!Text.Contains(marker, StringComparison.Ordinal))
            {
                if (Awake.Now > deadline) throw new InvalidOperationException($"Shell integration: timed out waiting for {description}; output: {Text}");
                await Task.Delay(50);
            }
        }
    }
}