using System.Net;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Protocol;
using SharpRail.Host.Remote;
using SharpRail.Plugins.AirGovernance;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Omp;
using SharpRail.Plugins.Omp.Host;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Checks;

// The omp plugin: the launch line (profile, session folder, model, approval mode, advisor, --no-lsp), the context-file
// resolution with nested, cycle-safe @-imports and the offers, the profile's MCP file, the version probe against a
// fixture omp and a missing one, process detection, revival, Air Governance's stance, and the UI end to end.
internal static class OmpChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("omp: " + message);
    }

    private const string SignInSecret = "{\"anthropic\":{\"key\":\"fixture-signin-7f3a\"}}";

    private static string FakeOmp(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "omp");
        // auth export prints fixture credentials (or fails when $HOME/fail-export exists); the profile's import reads stdin into the fixture profile.
        File.WriteAllText(path, "#!/bin/sh\nif [ \"$1\" = \"--version\" ]; then echo \"omp 9.8.7\"; exit 0; fi\n" +
            "if [ \"$1\" = auth ] && [ \"$2\" = export ] && [ -z \"$OMP_PROFILE\" ]; then if [ -f \"$HOME/fail-export\" ]; then echo \"error: rejected sk-ABCDEFGHIJKLMNOPQRSTUVWXYZ012345\" >&2; exit 3; fi; " +
            "printf '%s\\n' '" + SignInSecret + "'; exit 0; fi\n" +
            "if [ \"$1\" = --profile ] && [ \"$3\" = auth ] && [ \"$4\" = import ] && [ \"$5\" = - ]; then mkdir -p \"$HOME/.omp/profiles/$2\" && cat > \"$HOME/.omp/profiles/$2/imported.json\"; exit $?; fi\n" +
            "if [ \"$1\" = --profile ] && [ \"$3\" = config ]; then d=\"$HOME/.omp/profiles/$2\"; mkdir -p \"$d\"; echo \"$4 $5 $6\" >> \"$HOME/config-calls.txt\"; " +
            "if [ -f \"$HOME/hang-config\" ]; then sleep 60; fi; if [ -f \"$HOME/fail-config\" ]; then echo broken >&2; exit 4; fi; " +
            "if [ \"$4\" = get ] && [ \"$5\" = startup.setupWizard ]; then cat \"$d/wizard\" 2>/dev/null || echo true; exit 0; fi; " +
            "if [ \"$4\" = get ] && [ \"$5\" = setupVersion ]; then cat \"$d/version\" 2>/dev/null || echo 0; exit 0; fi; " +
            "if [ \"$4\" = set ] && [ \"$5\" = startup.setupWizard ]; then echo \"$6\" > \"$d/wizard\"; exit 0; fi; exit 2; fi\n" +
            "printf '%s\\n' \"$@\" > \"" + Path.Combine(directory, "argv.txt") + "\"\nsleep 30\n");
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    internal static async Task RunHost(string root)
    {
        Directory.CreateDirectory(root);
        Launch();
        Context(Path.Combine(root, "omp-context"));
        Profile(Path.Combine(root, "omp-profile"));
        await SetupWizard(Path.Combine(root, "omp-wizard"));
        await Version(Path.Combine(root, "omp-version"));
        Processes();
        Sessions(Path.Combine(root, "omp-sessions"));
        await Governed(Path.Combine(root, "omp-air"));
        Console.WriteLine("PASS omp host: launch line, context files and imports, profile MCP file, setup wizard switch, version and missing binary, detection, sessions, Air Governance through Central's Pi wire");
    }

    // Air Governance: a governed omp launch, run by a real shell against a fake omp and central, starts Central's proxy
    // and reaches omp with Central's Pi wire in its environment; a missing Pi integration fails closed; direct adds nothing.
    private static async Task Governed(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        var bin = Path.Combine(root, "bin");
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(home);
        var record = Path.Combine(root, "record.txt");
        File.WriteAllText(Path.Combine(bin, "omp"), "#!/bin/sh\n{ echo \"BASE=$ANTHROPIC_BASE_URL\"; echo \"KEY=$ANTHROPIC_API_KEY\"; printf 'ARG=%s\\n' \"$@\"; } >> \"" + record + "\"\n");
        File.WriteAllText(Path.Combine(bin, "central"), "#!/bin/sh\necho \"CENTRAL=$*\" >> \"" + record + "\"\n");
        foreach (var name in new[] { "omp", "central" }) File.SetUnixFileMode(Path.Combine(bin, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var settings = new AirSettings { Source = AirSources.Central };
        using var missing = new SharpRail.Plugins.AirGovernance.Host.AirGovernanceRuntime(new(home, bin), () => settings, _ => { });
        Require(missing.PiWire() is null && missing.Contribution(OmpManifest.Id) is { Wrapper: [AirLaunch.PiMissingCommand], ReplacesExecutable: true },
            "governed omp without Central's Pi integration is blocked");
        Require((await missing.StatusAsync(false, default)).Agents.Single(agent => agent.Id == OmpManifest.Id).Launch!.Contains("run `central add pi`", StringComparison.Ordinal),
            "the pane names `central add pi`");
        const string Wire = "http://127.0.0.1:19516/wire/0123abcd/pi";
        var extension = AirLaunch.PiExtensionPath(home);
        Directory.CreateDirectory(Path.GetDirectoryName(extension)!);
        File.WriteAllText(extension, "export default function (pi) {\n  pi.registerProvider(\"anthropic\", { baseUrl: \"" + Wire + "/anthropic\", apiKey: \"wire-proxy\" });\n}\n");
        var before = File.ReadAllText(extension);
        Require(missing.PiWire() == Wire, "the wire is read from Central's Pi integration");
        Require(AirLaunch.PiWire("registerProvider(\"anthropic\", { baseUrl: \"https://example.test/wire/x/pi/anthropic\" })") is null, "only a loopback wire is accepted");
        Require(AirLaunch.Contribution(settings, OmpManifest.Id, "/c", Wire, windows: true) is { Wrapper: [AirLaunch.WindowsUnsupportedCommand] }, "Windows, which gets no contributed environment, is blocked");
        Require(AirLaunch.Contribution(settings with { Omp = false }, OmpManifest.Id, "/c", Wire) is null && AirLaunch.Contribution(new(), OmpManifest.Id, "/c", Wire) is null,
            "direct or switched off contributes nothing");

        async Task<string[]> Run(AgentLaunchContribution? contribution)
        {
            File.Delete(record);
            var line = OmpLaunch.Line("omp", new() { Mcp = false, Model = "anthropic/x", Contributions = contribution is null ? [] : [contribution] });
            var start = new System.Diagnostics.ProcessStartInfo("/bin/sh", ["-c", line]) { RedirectStandardError = true };
            start.Environment["PATH"] = bin + ":/usr/bin:/bin";
            start.Environment.Remove("ANTHROPIC_BASE_URL");
            start.Environment.Remove("ANTHROPIC_API_KEY");
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            return File.Exists(record) ? File.ReadAllLines(record) : [];
        }

        var governed = await Run(missing.Contribution(OmpManifest.Id));
        Require(governed.SequenceEqual(["CENTRAL=proxy start --ensure-updated --caller agent", "BASE=" + Wire + "/anthropic", "KEY=wire-proxy", "ARG=--model", "ARG=anthropic/x", "ARG=--no-lsp"]),
            "governed omp starts the proxy and gets the wire, its arguments unchanged: " + string.Join(" | ", governed));
        var direct = await Run(AirLaunch.Contribution(new(), OmpManifest.Id, null, Wire));
        Require(direct.SequenceEqual(["BASE=", "KEY=", "ARG=--model", "ARG=anthropic/x", "ARG=--no-lsp"]), "a direct omp gets no wire: " + string.Join(" | ", direct));
        File.Delete(extension);
        Require(await Run(missing.Contribution(OmpManifest.Id)) is [], "without the integration the launch fails in the shell and omp never starts");
        Require(!File.Exists(extension) && before.Length > 0, "the integration is only read");
    }

    private static void Launch()
    {
        var plain = OmpLaunch.Line("", new() { Mcp = false });
        Require(plain == "omp --no-lsp", "a bare launch defaults to SharpRail's code intelligence: " + plain);
        var full = OmpLaunch.Line("omp", new()
        {
            UiLaunch = true,
            Model = "anthropic/claude-opus-4-6:high",
            ApprovalMode = "write",
            Advisor = true,
            SessionId = "0123456789abcdef0123456789abcdef",
            SystemPrompt = "be brief",
            InitialPrompt = "fix it",
            Images = ["/tmp/a.png"]
        });
        Require(full == "SHARPRAIL_UI_LAUNCH=omp omp --profile sharprail --session-dir \"$SHARPRAIL_OMP_SESSIONS/0123456789abcdef0123456789abcdef\" " +
            "--model anthropic/claude-opus-4-6:high --approval-mode write --advisor --no-lsp --append-system-prompt 'be brief' @/tmp/a.png 'fix it'",
            "the launch carries the profile, session folder, model, approval mode, advisor and --no-lsp: " + full);
        Require(OmpLaunch.SessionIdOf(full) == "0123456789abcdef0123456789abcdef", "the session folder is read back from the arguments");
        var own = OmpLaunch.Line("omp", new() { Mcp = false, OwnCodeIntelligence = true });
        Require(own == "omp", "omp's own code intelligence drops --no-lsp: " + own);
        var resume = OmpLaunch.Line("omp", new() { Mcp = false, SessionDirectory = "/s/abc", ResumePath = "/s/abc/x.jsonl", InitialPrompt = "ignored" });
        Require(resume == "omp --session-dir /s/abc -r /s/abc/x.jsonl --no-lsp", "a resume names the session file and drops the opening prompt: " + resume);
        Require(OmpLaunch.Line("omp", new() { Windows = true, SessionId = "0123456789abcdef0123456789abcdef", UiLaunch = true }) == "omp --profile sharprail --no-lsp",
            "Windows launches keep omp's own session store and no POSIX marker");
        Require(OmpLaunch.ApprovalModes.SequenceEqual(["always-ask", "always-ask-cached", "write", "yolo"]) && OmpLaunch.ModelSuggestions.Contains("@smol"), "the documented values are suggested");
    }

    private static void Context(string root)
    {
        var home = Path.Combine(root, "home");
        var project = Path.Combine(root, "work", "project");
        Directory.CreateDirectory(Path.Combine(project, "docs"));
        Directory.CreateDirectory(Path.Combine(project, ".claude"));
        Directory.CreateDirectory(Path.Combine(home, ".omp", "profiles", "sharprail", "agent"));
        File.WriteAllText(Path.Combine(project, "AGENTS.md"), "# Project\n\n@docs/a.md\nmail me at someone@example.test\n```\n@docs/fenced.md\n```\nsee `@docs/inline.md`\n");
        File.WriteAllText(Path.Combine(project, "docs", "a.md"), "A\n@b.md\n");
        File.WriteAllText(Path.Combine(project, "docs", "b.md"), "B\n@a.md\n@missing.md\n");
        File.WriteAllText(Path.Combine(project, "docs", "fenced.md"), "never\n");
        File.WriteAllText(Path.Combine(project, "docs", "inline.md"), "never\n");
        File.WriteAllText(Path.Combine(project, ".claude", "CLAUDE.md"), "shadowed by AGENTS.md\n");
        File.WriteAllText(Path.Combine(root, "work", "AGENTS.md"), "ancestor\n");
        File.WriteAllText(Path.Combine(home, ".omp", "profiles", "sharprail", "agent", "AGENTS.md"), "user\n");

        var snapshot = OmpContext.Resolve(project, home, profile: true);
        string Rows() => string.Join(" | ", snapshot.Files.Select(file => $"{file.Label}:{file.Scope}:{file.Provider}:{file.Depth}{(file.Shadowed ? ":shadowed" : "")}{(file.Cycle ? ":cycle" : "")}"));
        var labels = snapshot.Files.Select(file => file.Label).ToList();
        Require(labels.IndexOf("~/.omp/profiles/sharprail/agent/AGENTS.md") == 0, "the profile's user file comes first: " + Rows());
        Require(labels.IndexOf(Path.Combine(root, "work", "AGENTS.md")) < labels.IndexOf("AGENTS.md"), "an ancestor comes before the launch directory: " + Rows());
        var agents = snapshot.Files.Single(file => file.Label == "AGENTS.md");
        Require(agents is { Provider: "native", Depth: 0, Shadowed: false, Scope: OmpContextScope.Project } && agents.Tokens == (agents.Bytes + 3) / 4, "the project AGENTS.md wins natively: " + Rows());
        Require(snapshot.Files.Single(file => file.Label == Path.Combine(".claude", "CLAUDE.md")).Shadowed, "the Claude file at the same depth is shadowed: " + Rows());
        var a = labels.IndexOf(Path.Combine("docs", "a.md"));
        Require(a == labels.IndexOf("AGENTS.md") + 1 && snapshot.Files[a].Depth == 1, "an import follows its importer at depth one: " + Rows());
        Require(snapshot.Files[a + 1] is { Depth: 2, Cycle: false } && snapshot.Files[a + 1].Label == Path.Combine("docs", "b.md"), "a nested import is depth two: " + Rows());
        Require(snapshot.Files[a + 2] is { Depth: 3, Cycle: true } && snapshot.Files[a + 2].Label == Path.Combine("docs", "a.md"), "a cycle is marked and not followed: " + Rows());
        Require(!labels.Any(label => label.Contains("fenced", StringComparison.Ordinal) || label.Contains("inline", StringComparison.Ordinal) || label.Contains("missing", StringComparison.Ordinal) || label.Contains("example", StringComparison.Ordinal)),
            "code, e-mail addresses and unreadable imports are not imports: " + Rows());
        // A chain longer than five hops stops at the fifth.
        for (var hop = 0; hop < 8; hop++) File.WriteAllText(Path.Combine(project, "docs", $"chain{hop}.md"), $"@chain{hop + 1}.md\n");
        File.WriteAllText(Path.Combine(project, "docs", "chain8.md"), "end\n");
        Directory.CreateDirectory(Path.Combine(project, ".omp"));
        File.WriteAllText(Path.Combine(project, ".omp", "RULES.md"), "@../docs/chain0.md\n");
        snapshot = OmpContext.Resolve(project, home, profile: true);
        Require(snapshot.Files.Where(file => file.Label.Contains("chain", StringComparison.Ordinal)).Max(file => file.Depth) == OmpContext.MaxImportDepth, "imports stop after five hops: " + Rows());

        Require(snapshot.Offers.All(offer => offer.Target != OmpContextTarget.Agents) && snapshot.Offers.All(offer => offer.Target != OmpContextTarget.Rules) &&
            snapshot.Offers.Any(offer => offer is { Target: OmpContextTarget.AppendSystem, Label: "Add .omp/APPEND_SYSTEM.md" }), "only missing files are offered");
        var created = OmpContext.Create(project, OmpContextTarget.AppendSystem);
        Require(File.Exists(created) && OmpContext.Resolve(project, home, true).Offers.Count == 0, "adding a file fulfils its offer");
        File.WriteAllText(created, "mine\n");
        OmpContext.Create(project, OmpContextTarget.AppendSystem);
        Require(File.ReadAllText(created) == "mine\n", "adding never overwrites an existing file");
        Require(OmpContext.Resolve(project, home, profile: false).UserDirectory == Path.Combine(home, ".omp", "agent"), "without the profile, user files come from omp's own directory");
    }

    private static void Profile(string root)
    {
        OmpHost.WriteProfileMcp(root);
        var path = OmpHost.ProfileMcpPath(root);
        Require(path == Path.Combine(root, ".omp", "profiles", "sharprail", "agent", "mcp.json"), "the profile's MCP file path: " + path);
        var server = JsonDocument.Parse(File.ReadAllText(path)).RootElement.GetProperty("mcpServers").GetProperty("sharprail");
        Require(server.GetProperty("type").GetString() == "http" && server.GetProperty("url").GetString() == "${THINKRAIL_MCP_URL}",
            "the server's URL is each terminal's own, expanded by omp");
        File.WriteAllText(path, "{}");
        OmpHost.WriteProfileMcp(root);
        Require(File.ReadAllText(path).Contains("THINKRAIL_MCP_URL", StringComparison.Ordinal) && Directory.GetFiles(Path.GetDirectoryName(path)!).Length == 1,
            "a rewrite replaces the file whole and leaves no temporary file");
    }

    // The profile's first-run setup wizard is switched off once, only while it never completed, and never blocks on failures.
    private static async Task SetupWizard(string root)
    {
        var fake = FakeOmp(root);
        string Home(string name) { var h = Path.Combine(root, name); Directory.CreateDirectory(h); return h; }
        string Calls(string h) => File.Exists(Path.Combine(h, "config-calls.txt")) ? File.ReadAllText(Path.Combine(h, "config-calls.txt")) : "";
        string Wizard(string h) => File.ReadAllText(Path.Combine(h, ".omp", "profiles", "sharprail", "wizard")).Trim();

        var fresh = Home("fresh");
        Require(await OmpHost.SkipSetupWizardAsync(fake, fresh) == "set" && Wizard(fresh) == "false", "a never-completed wizard is turned off");
        var calls = Calls(fresh);
        Require(await OmpHost.SkipSetupWizardAsync(fake, fresh) != "set" && !Calls(fresh)[calls.Length..].Contains("set startup", StringComparison.Ordinal), "a second preparation does not set it again");

        var done = Home("done");
        Directory.CreateDirectory(Path.Combine(done, ".omp", "profiles", "sharprail"));
        File.WriteAllText(Path.Combine(done, ".omp", "profiles", "sharprail", "version"), "3\n");
        Require(await OmpHost.SkipSetupWizardAsync(fake, done) != "set" && !Calls(done).Contains("set startup", StringComparison.Ordinal), "a completed wizard (setupVersion > 0) is left alone");

        var failing = Home("failing");
        File.WriteAllText(Path.Combine(failing, "fail-config"), "");
        Require((await OmpHost.SkipSetupWizardAsync(fake, failing)).StartsWith("unchanged", StringComparison.Ordinal), "a failing config get changes nothing and does not throw");
        var hanging = Home("hanging");
        File.WriteAllText(Path.Combine(hanging, "hang-config"), "");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Require((await OmpHost.SkipSetupWizardAsync(fake, hanging)).StartsWith("failed", StringComparison.Ordinal) && watch.Elapsed < TimeSpan.FromSeconds(25), "a hanging omp is cut off by the timeout");
        Require((await OmpHost.SkipSetupWizardAsync(Path.Combine(root, "nowhere", "omp"), fresh)).StartsWith("failed", StringComparison.Ordinal), "a missing omp is tolerated");
    }

    private static async Task Version(string root)
    {
        var fake = FakeOmp(root);
        var status = await OmpHost.ReadStatusAsync(fake, root, true);
        Require(status is { Installed: true, Version: "9.8.7" } && status.ProfileMcpPath == OmpHost.ProfileMcpPath(root), "the version comes from omp --version: " + status);
        var missing = await OmpHost.ReadStatusAsync(Path.Combine(root, "nowhere", "omp"), root, false);
        Require(missing is { Installed: false, Version: null } && missing.Detail!.Contains("not found", StringComparison.Ordinal), "a missing binary is reported: " + missing);
    }

    private static void Processes()
    {
        var rows = ProcessSnapshot.ParseRows(ProcessSnapshot.Living("  10     1 Ss   zsh\n  11    10 Z    omp\n  12    10 S+   /usr/local/bin/omp\n"));
        var snapshot = new ProcessSnapshot(rows);
        Require(snapshot.FindDescendant(10, ["omp"]) is { Pid: 12 }, "a live omp under the shell is found and a zombie one skipped");
        var zombie = new ProcessSnapshot(ProcessSnapshot.ParseRows(ProcessSnapshot.Living("  10     1 Ss   zsh\n  11    10 Z    omp\n")));
        Require(zombie.FindDescendant(10, ["omp"]) is null, "an exited, unreaped omp keeps no agent");
    }

    private static void Sessions(string root)
    {
        var folder = Path.Combine(root, "abc");
        Require(OmpHost.NewestSession(folder) is null, "a session folder that was never written has no session");
        Directory.CreateDirectory(Path.Combine(folder, "nested"));
        File.WriteAllText(Path.Combine(folder, "old.jsonl"), "{}\n");
        File.SetLastWriteTimeUtc(Path.Combine(folder, "old.jsonl"), DateTime.UtcNow.AddMinutes(-5));
        File.WriteAllText(Path.Combine(folder, "nested", "new.jsonl"), "{}\n");
        Require(OmpHost.NewestSession(folder) == Path.Combine(folder, "nested", "new.jsonl"), "the newest session in the folder is resumed");
    }

    // The UI half against the real host over the remote adapter: the Early badges, version, missing state, context rows and offer, and a launch.
    internal static void RunUi(string root)
    {
        Directory.CreateDirectory(root);
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        var previousHome = Environment.GetEnvironmentVariable(OmpHost.HomeVariable);
        Environment.SetEnvironmentVariable(OmpHost.HomeVariable, home);
        var workspace = Path.Combine(root, "workspace");
        Directory.CreateDirectory(Path.Combine(workspace, ".git"));
        File.WriteAllText(Path.Combine(workspace, "AGENTS.md"), "# Rules\n@docs/more.md\n");
        Directory.CreateDirectory(Path.Combine(workspace, "docs"));
        File.WriteAllText(Path.Combine(workspace, "docs", "more.md"), "@../AGENTS.md\n");
        var fake = FakeOmp(Path.Combine(root, "fixture"));
        const string token = "omp-fixture";
        var server = RemoteServer.Create(workspace, IPAddress.Loopback, 0, token, workspace + "-host");
        Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
        var endpoint = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        var terminals = new RemoteTerminalAdapter(endpoint, token);
        var app = new E2eWorkspace(endpoint, token, workspace, workspace + "-profile", workspace, terminals: new E2eTerminals(terminals));
        try
        {
            E2eWorkspace.Until(() => app.Window.WorkspaceMounted);
            void Settings(object settings)
            {
                var json = JsonSerializer.Serialize(settings, SharpRail.Plugins.Api.PluginJson.Options);
                _ = app.Workbench.State.ChangeAsync(HostStateChange.PluginSettings(OmpManifest.Id, json));
                E2eWorkspace.Until(() => app.Workbench.State.Current.PluginSettings.GetValueOrDefault(OmpManifest.Id) is { ValueKind: JsonValueKind.Object } stored &&
                    JsonSerializer.SerializeToElement(settings, SharpRail.Plugins.Api.PluginJson.Options).EnumerateObject().All(member => stored.TryGetProperty(member.Name, out var value) && value.GetRawText() == member.Value.GetRawText()));
            }
            Settings(new { command = fake, model = "@smol", approvalMode = "write", advisor = true });
            _ = app.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled(OmpManifest.Id, true));
            E2eWorkspace.Until(() => app.Workbench.PluginLoader.Registry.Active.Contains(OmpManifest.Id));
            Require(app.Workbench.PluginLoader.Registry.Roster.Single(entry => entry.Id == OmpManifest.Id).Support == SharpRail.Plugins.Api.PluginSupportLevels.Early,
                "the roster carries the Early support level over the wire");

            app.Window.Layout.RestoreTool(OmpManifest.ContextTool);
            E2eWorkspace.Until(() => Find<Control>(app.Window, "OmpPanel").Any());
            var panel = Find<Control>(app.Window, "OmpPanel").Single();
            E2eWorkspace.Until(() => Find<TextBlock>(panel, "OmpVersion").Single().Text == "v9.8.7");
            Require(Find<Border>(panel, "OmpEarlyBadge").Single().IsEffectivelyVisible, "the pane wears the Early badge");
            Require(Find<TextBlock>(panel, "OmpCodeIntelligence").Single().Text!.Contains("SharpRail", StringComparison.Ordinal), "the pane says sessions use SharpRail's code intelligence");
            Require(Find<TextBlock>(panel, "OmpProfileNote").Single().IsEffectivelyVisible, "the pane notes what the profile keeps apart");
            E2eWorkspace.Until(() => Find<Control>(panel, "OmpContextRow").Count >= 3);
            var rows = Find<Control>(panel, "OmpContextRow");
            Require(rows.Any(row => row.Margin.Left == 16 && Equals(row.Tag, Path.Combine(workspace, "docs", "more.md"))) &&
                rows.Any(row => row.Margin.Left == 32 && Find<TextBlock>(row, "OmpContextTokens").Single().Text == "cycle"), "imports are nested rows and the cycle is marked");
            Require(rows.First(row => Equals(row.Tag, Path.Combine(workspace, "AGENTS.md"))) is var agents && Find<TextBlock>(agents, "OmpContextTokens").Single().Text!.EndsWith("tok", StringComparison.Ordinal),
                "a file shows its token estimate");
            E2eWorkspace.Until(() => Find<Button>(panel, "OmpOffer_rules").Any());
            Require(!Find<Button>(panel, "OmpOffer_agents").Any(), "an existing AGENTS.md is not offered");
            app.Click(Find<Button>(panel, "OmpOffer_rules").Single());
            E2eWorkspace.Until(() => File.Exists(Path.Combine(workspace, ".omp", "RULES.md")) && !Find<Button>(panel, "OmpOffer_rules").Any());
            Require(File.Exists(OmpHost.ProfileMcpPath(home)), "enabling writes the profile's MCP file under omp's home");

            // The sign-in import pipes the fixture's export into the profile's import on the host, only when pressed.
            var imported = Path.Combine(home, ".omp", "profiles", "sharprail", "imported.json");
            Require(!File.Exists(imported), "the sign-in import never runs by itself");
            E2eWorkspace.Until(() => Find<StackPanel>(panel, "OmpSignIn").Single().IsEffectivelyVisible);
            var signIn = Find<Button>(panel, "OmpImportSignIn").Single();
            var signInResult = Find<TextBlock>(panel, "OmpSignInResult").Single();
            app.Click(signIn);
            E2eWorkspace.Until(() => signInResult.Text?.Contains("now works", StringComparison.Ordinal) == true && signIn.IsEnabled);
            Require(File.ReadAllText(imported).Trim() == SignInSecret, "the default profile's export lands in the SharpRail profile");
            var leaks = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
                .Where(file => file != imported && !file.StartsWith(Path.Combine(root, "fixture"), StringComparison.Ordinal) && new FileInfo(file).Length < 4 << 20)
                .Where(file => { try { return File.ReadAllText(file).Contains("fixture-signin-7f3a", StringComparison.Ordinal); } catch (IOException) { return false; } }).ToList();
            Require(leaks.Count == 0, "SharpRail persists no credentials: " + string.Join(", ", leaks));
            File.WriteAllText(Path.Combine(home, "fail-export"), "");
            app.Click(signIn);
            E2eWorkspace.Until(() => signInResult.Text?.Contains("exit 3", StringComparison.Ordinal) == true && signIn.IsEnabled);
            Require(!signInResult.Text!.Contains("sk-ABCDEFGHIJ", StringComparison.Ordinal) && signInResult.Text.Contains("***", StringComparison.Ordinal),
                "a failure is reported with token-like text masked: " + signInResult.Text);
            Settings(new { command = fake, model = "@smol", approvalMode = "write", advisor = true, mcp = false });
            E2eWorkspace.Until(() => !Find<StackPanel>(panel, "OmpSignIn").Single().IsVisible);
            Settings(new { command = fake, model = "@smol", approvalMode = "write", advisor = true, mcp = true });
            E2eWorkspace.Until(() => Find<StackPanel>(panel, "OmpSignIn").Single().IsVisible);

            // The start action launches the fixture with the configured flags in its own session folder.
            E2eWorkspace.Until(() => Find<Button>(app.Window, "NewOmp").Any());
            // The start action wears omp's own mark (the asset drawn), not the terminal glyph that stands in until it is read.
            E2eWorkspace.Until(() => Find<Button>(app.Window, "NewOmp").First().Content is ContentControl { Tag: OmpManifest.Icon, Content: SvgAsset });
            // The launcher's own icon factory (new-agent menu, Start work, phone strip) draws the mark in whichever theme brush it is given.
            var launcherIcon = app.Workbench.PluginLoader!.Registry.LauncherList.Single(launcher => launcher.Id == OmpManifest.Id).CreateIcon!;
            foreach (var brush in new[] { Ui.Accent, Ui.TextBrush })
            {
                var icon = (ContentControl)launcherIcon(14, brush);
                E2eWorkspace.Until(() => icon.Content is SvgAsset { Width: 14 });
                Require(Equals(icon.Tag, OmpManifest.Icon), "the launcher icon carries omp's mark, not the terminal glyph");
            }
            var argv = Path.Combine(root, "fixture", "argv.txt");
            app.Click(Find<Button>(app.Window, "NewOmp").First());
            E2eWorkspace.Until(() => File.Exists(argv) && File.ReadAllText(argv).Contains("--no-lsp", StringComparison.Ordinal));
            var args = File.ReadAllLines(argv);
            Require(args.Take(2).SequenceEqual(["--profile", "sharprail"]) && args[2] == "--session-dir" &&
                OmpLaunch.SessionIdOf("--session-dir " + args[3]) is not null && args.Skip(4).SequenceEqual(["--model", "@smol", "--approval-mode", "write", "--advisor", "--no-lsp"]),
                "the launch argv: " + string.Join(" ", args));
            E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Button>().Any(button => button.Name?.StartsWith("Tab_", StringComparison.Ordinal) == true &&
                button.GetLogicalDescendants().OfType<Control>().Any(control => Equals(control.Tag, OmpManifest.Icon))));

            // A missing binary: the pane says so with the install link, and the launcher is unavailable.
            Settings(new { command = Path.Combine(root, "nowhere", "omp"), model = "@smol", approvalMode = "write", advisor = true });
            app.Click(Find<Button>(panel, "OmpRefresh").Single());
            E2eWorkspace.Until(() => Find<StackPanel>(panel, "OmpMissing").Single().IsVisible);
            Require(Find<Button>(panel, "OmpInstall").Single().IsEffectivelyVisible && Find<TextBlock>(panel, "OmpMissingText").Single().Text!.Contains("not installed", StringComparison.Ordinal),
                "the missing state offers the install link");
            Console.WriteLine("PASS omp UI: Early badge, version, code intelligence and profile notes, nested context rows, add offer, sign-in import, launch argv, omp mark on the start action and tab, missing binary");
        }
        finally
        {
            app.Dispose();
            terminals.Dispose();
            Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable(OmpHost.HomeVariable, previousHome);
        }
    }

    private static List<T> Find<T>(Control scope, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs();
        (TopLevel.GetTopLevel(scope) ?? scope).UpdateLayout();
        return [.. new[] { scope }.Concat(scope.GetLogicalDescendants()).OfType<T>().Where(item => item.Name == name)];
    }
}