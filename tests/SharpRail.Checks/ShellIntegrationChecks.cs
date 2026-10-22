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

// Ghostty's shell integration in shells the host starts: real zsh in a fixture HOME with no user startup files, locally
// and through a real WebSocket RPC host, with the host setting on and off.
internal static partial class ShellIntegrationChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Shell integration: " + message); }

    internal static async Task RunHost(string root)
    {
        if (!OperatingSystem.IsMacOS() && !OperatingSystem.IsLinux()) { Console.WriteLine("SKIP shell integration: PTYs require macOS or Linux."); return; }
        var resources = ShellIntegration.DefaultResources;
        Require(File.Exists(Path.Combine(resources, "shell-integration", "zsh", ".zshenv")) &&
            File.Exists(Path.Combine(resources, "shell-integration", "bash", "ghostty.bash")) &&
            Directory.Exists(Path.Combine(resources, "shell-integration", "fish", "vendor_conf.d")),
            "the app must ship Ghostty's zsh, bash and fish scripts in " + resources);
        Plans(resources);
        var fixture = Fixture(root);
        await WithFixture(fixture, async () =>
        {
            var workspace = Path.Combine(fixture, "workspace");
            await using (var on = new PtyTerminalService("/bin/zsh", shellIntegration: () => true))
            {
                await Marked(new LocalTerminalAdapter(on), workspace, "local");
                await UserStartup(new LocalTerminalAdapter(on), fixture, workspace);
            }
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
        Console.WriteLine("PASS shell integration: Ghostty's scripts ship with the app; zsh marks prompts (OSC 133 A/B/C/D) and reports its directory (OSC 7) locally and remotely, keeps the user's ZDOTDIR; bash, fish and elvish get Ghostty's injection; Apple's /bin/bash and the setting off inject nothing");
    }

    // The injection Ghostty's shell_integration.zig performs, for shells this machine may not have.
    private static void Plans(string resources)
    {
        var integration = Path.Combine(resources, "shell-integration");
        var empty = new Dictionary<string, string>();
        var zsh = ShellIntegration.For("/bin/zsh", resources, new Dictionary<string, string> { ["ZDOTDIR"] = "/home/me/.config/zsh" })
            ?? throw new InvalidOperationException("Shell integration: zsh must be integrated.");
        Require(zsh.Variables["ZDOTDIR"] == Path.Combine(integration, "zsh") && zsh.Variables["GHOSTTY_ZSH_ZDOTDIR"] == "/home/me/.config/zsh" &&
            zsh.Arguments.Length == 0 && zsh.Variables["GHOSTTY_RESOURCES_DIR"] == resources && zsh.Variables["GHOSTTY_SHELL_FEATURES"] == "cursor:blink,title",
            "zsh must start from Ghostty's ZDOTDIR and keep the user's in GHOSTTY_ZSH_ZDOTDIR.");
        Require(!(ShellIntegration.For("/bin/zsh", resources, empty)?.Variables.ContainsKey("GHOSTTY_ZSH_ZDOTDIR") ?? true), "an unset ZDOTDIR must stay unset.");
        var bash = ShellIntegration.For("/usr/local/bin/bash", resources, new Dictionary<string, string> { ["ENV"] = "/home/me/.envrc" })
            ?? throw new InvalidOperationException("Shell integration: a non-Apple bash must be integrated.");
        Require(bash.Arguments.SequenceEqual(["--posix"]) && bash.Variables["ENV"] == Path.Combine(integration, "bash", "ghostty.bash") &&
            bash.Variables["GHOSTTY_BASH_INJECT"] == "1" && bash.Variables["GHOSTTY_BASH_ENV"] == "/home/me/.envrc" &&
            bash.Variables["HISTFILE"].EndsWith("/.bash_history", StringComparison.Ordinal) && bash.Variables["GHOSTTY_BASH_UNEXPORT_HISTFILE"] == "1",
            "bash must start in POSIX mode reading Ghostty's script through ENV, keeping ENV and the history file.");
        Require(!ShellIntegration.For("/usr/local/bin/bash", resources, new Dictionary<string, string> { ["HISTFILE"] = "/h" })!.Value.Variables.ContainsKey("HISTFILE"),
            "a user's HISTFILE must be left alone.");
        var fish = ShellIntegration.For("/usr/bin/fish", resources, new Dictionary<string, string> { ["XDG_DATA_DIRS"] = "/opt/share" })
            ?? throw new InvalidOperationException("Shell integration: fish must be integrated.");
        Require(fish.Variables["XDG_DATA_DIRS"] == integration + ":/opt/share" && fish.Variables["GHOSTTY_SHELL_INTEGRATION_XDG_DIR"] == integration &&
            ShellIntegration.For("/usr/bin/fish", resources, empty)!.Value.Variables["XDG_DATA_DIRS"] == integration + ":/usr/local/share:/usr/share",
            "fish must find Ghostty's vendor_conf.d first in XDG_DATA_DIRS.");
        Require(ShellIntegration.For("/bin/sh", resources, empty) is null && ShellIntegration.For("/usr/local/bin/bash", Path.Combine(resources, "missing"), empty) is null,
            "an unknown shell or missing scripts must inject nothing.");
        Require(ShellIntegration.For("/bin/bash", resources, empty) is null == OperatingSystem.IsMacOS(), "only Apple's /bin/bash, which ignores ENV, is left alone.");
    }

    private static string Fixture(string root)
    {
        var fixture = Path.Combine(root, "shell-integration");
        if (Directory.Exists(fixture)) Directory.Delete(fixture, true);
        Directory.CreateDirectory(Path.Combine(fixture, "home"));
        Directory.CreateDirectory(Path.Combine(fixture, "workspace", "nested dir"));
        return fixture;
    }

    // A HOME with no startup files, so only the system's and Ghostty's run, and no inherited ZDOTDIR.
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
            await screen.WaitFor("\u001b]7;kitty-shell-cwd://", mode + " working directory");
            await screen.WaitFor("/workspace\u0007", mode + " OSC 7 naming the workspace");
            await screen.Run("echo INTEGRATED_$((6*7))");
            await screen.WaitFor(OutputStart, mode + " output start");
            await screen.WaitFor("INTEGRATED_42", mode + " command output");
            await screen.WaitFor(CommandEnd + ";0", mode + " command end with status");
            await screen.Run("cd 'nested dir'");
            await screen.WaitFor("nested dir\u0007", mode + " OSC 7 after cd");
            await screen.Run("env | grep -c '^GHOSTTY_ZSH_ZDOTDIR=\\|^ZDOTDIR=' ; echo ZDOT_$?");
            await screen.WaitFor("ZDOT_1", mode + " ZDOTDIR restored after startup");
        }
        finally { await session.KillAsync(); await terminals.CloseAsync(session.Id); }
    }

    private static async Task Unmarked(ITerminalService terminals, string workspace, string mode)
    {
        await using var session = await terminals.AttachAsync(new("plain-" + Guid.NewGuid().ToString("N"), workspace, "client", 100, 30));
        var screen = new Output(session);
        try
        {
            await screen.Run("echo PLAIN_$((6*7)); env | grep -c '^GHOSTTY_'");
            await screen.WaitFor("PLAIN_42", mode + " plain output");
            await Task.Delay(300);
            Require(!screen.Text.Contains("\u001b]133;", StringComparison.Ordinal) && !screen.Text.Contains("\u001b]7;", StringComparison.Ordinal),
                mode + " with integration off a shell must emit no prompt marks or directory reports: " + screen.Text);
            await screen.WaitFor("\n0", mode + " no GHOSTTY_ variables");
        }
        finally { await session.KillAsync(); await terminals.CloseAsync(session.Id); }
    }

    // The user's ZDOTDIR still runs: Ghostty's .zshenv restores it before zsh reads .zshrc from it.
    private static async Task UserStartup(ITerminalService terminals, string fixture, string workspace)
    {
        var dot = Path.Combine(fixture, "zdotdir");
        Directory.CreateDirectory(dot);
        await File.WriteAllTextAsync(Path.Combine(dot, ".zshrc"), "echo USER_RC_LOADED\n");
        Environment.SetEnvironmentVariable("ZDOTDIR", dot);
        try
        {
            await using var session = await terminals.AttachAsync(new("zdotdir-" + Guid.NewGuid().ToString("N"), workspace, "client", 100, 30));
            var screen = new Output(session);
            try
            {
                await screen.WaitFor("USER_RC_LOADED", "the user's .zshrc");
                await screen.WaitFor(PromptStart, "prompt marks with a user ZDOTDIR");
                await screen.Run("echo ZDOT=$ZDOTDIR");
                await screen.WaitFor("ZDOT=" + dot, "the user's ZDOTDIR restored");
            }
            finally { await session.KillAsync(); await terminals.CloseAsync(session.Id); }
        }
        finally { Environment.SetEnvironmentVariable("ZDOTDIR", null); }
    }

    // Ghostty leaves Apple's bash 3.2 alone; it still starts, as a plain login shell.
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