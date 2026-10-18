using System.Text;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.AirGovernance;
using SharpRail.Plugins.AirGovernance.Host;
using SharpRail.Plugins.AirGovernance.UI;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.Host;
using SharpRail.UI;
using SharpRail.UI.Panels;
using SharpRail.UI.State;

namespace SharpRail.Checks;

// The Air Governance builtin plugin against a fake `central`, ported from the fork's e2e/fixtures/bin/central: the
// fork's version/status/login/access/proxy surface, with `add`/`remove` for the claude-code and codex agents in
// place of its pi extension. The real CLI is never run, and every path lives under a throwaway HOME.
internal static class AirGovernanceChecks
{
    private const string Sentinel = "E2E_CENTRAL_CHILD_SENTINEL";
    private const string Original = "{\"theme\":\"dark\"}\n";

    // Control tokens live in $HOME/.central-stub/state, so axes compose as in the fork's fixture: the version a probe
    // reports, whether credentials exist and how an action fails are separate facts about a host.
    private const string FakeCentral = """
        #!/bin/sh
        set -u
        stub="$HOME/.central-stub"
        mkdir -p "$stub"
        state=""
        [ -f "$stub/state" ] && state=$(cat "$stub/state")
        has() { case " $state " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }
        drop() { next=""; for t in $state; do [ "$t" != "$1" ] && next="$next $t"; done; state=${next# }; printf '%s\n' "$state" > "$stub/state"; }
        add() { state="$state $1"; printf '%s\n' "$state" > "$stub/state"; }
        printf '%s\n' "$*" >> "$stub/log"
                case "${1:-}" in
        --version)
          if has probe-error; then echo E2E_CENTRAL_CHILD_SENTINEL_probe >&2; exit 7
          elif has malformed; then echo 'synthetic malformed version'
          elif has outdated; then echo 'central 1.3.9 (synthetic)'
          else echo 'central 1.13.0 (commit: synthetic)'; fi ;;
        status)
          if has needs-login; then printf '\033[1mAuth      \033[m \033[31mnot connected\033[m\n'; else printf '\033[1mAuth      \033[m E2E Synthetic Access\n'; fi
          if has proxy-stopped; then printf '\033[1mProxy     \033[m stopped\n'; else printf '\033[1mProxy     \033[m running on port 19516\n'; fi
          echo E2E_CENTRAL_CHILD_SENTINEL_status ;;
        access)
          has access-error && exit 17
          if [ -n "${2:-}" ]; then
            has slow-access && sleep 0.4
            echo "$2" > "$stub/access"; exit 0
          fi
          current=$(cat "$stub/access" 2>/dev/null || echo workspace:org-1:ws-1)
          for id in workspace:org-1:ws-1 license:lic-9; do
            name="Team workspace"; [ "$id" = license:lic-9 ] && name="Personal licence"
            if [ "$id" = "$current" ]; then printf '* %s  %s\n' "$id" "$name"; else printf '  %s  %s\n' "$id" "$name"; fi
          done
          printf '  not-a-source  E2E_CENTRAL_CHILD_SENTINEL_access\n' ;;
        proxy)
          case "${2:-}" in
          start) has proxy-start-error && exit 14; drop proxy-stopped ;;
          restart) has proxy-restart-error && exit 15; drop proxy-stopped ;;
          stop) add proxy-stopped ;;
          esac ;;
        login)
          if [ "${2:-}" = export ]; then
            if has needs-login || [ ! -f "$stub/export" ]; then exit 9; fi
            cat "$stub/export"; exit 0
          fi
          has login-error && exit 12; if has slow-login; then sleep 3; fi; drop needs-login ;;
        logout) add needs-login ;;
        update) has update-error && exit 13; drop outdated ;;
        run)
          shift
          { echo "run $#"; for a in "$@"; do printf 'arg:%s\n' "$a"; done; echo "env:CENTRAL_STUB_PROBE=${CENTRAL_STUB_PROBE-}"; } >> "$stub/run.log" ;;
        *) exit 8 ;;
        esac
        """;

    private sealed class Fixture : IDisposable
    {
        internal Fixture(string directory)
        {
            Home = Path.Combine(directory, "home");
            var bin = Path.Combine(directory, "bin");
            Directory.CreateDirectory(Home); Directory.CreateDirectory(bin);
            Central = Path.Combine(bin, "central");
            File.WriteAllText(Central, FakeCentral.Replace("\n        ", "\n").TrimStart());
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Central, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory(Path.GetDirectoryName(ClaudeFile)!);
            File.WriteAllText(ClaudeFile, Original);
            Directory.CreateDirectory(Path.GetDirectoryName(CodexFile)!);
            File.WriteAllText(CodexFile, "model = \"gpt\"\n");
            Directory.CreateDirectory(Path.Combine(Home, "claude-custom")); File.WriteAllText(Path.Combine(Home, "claude-custom", "settings.json"), "{}\n");
            Directory.CreateDirectory(Path.Combine(Home, "codex-custom")); File.WriteAllText(Path.Combine(Home, "codex-custom", "config.toml"), "x = 1\n");
            Environment = new(Home, bin, Path.Combine(Home, "claude-custom"), Path.Combine(Home, "codex-custom"));
        }

        internal string Home { get; }
        internal string Central { get; }
        internal AirEnvironment Environment { get; }
        internal string ClaudeFile => Path.Combine(Home, ".claude", "settings.json");
        internal string CodexFile => Path.Combine(Home, ".codex", "config.toml");
        internal string StubFile(string name) => Path.Combine(Home, ".central-stub", name);
        internal string[] Log => File.Exists(StubFile("log")) ? File.ReadAllLines(StubFile("log")) : [];
        internal void Tokens(string tokens) { Directory.CreateDirectory(Path.GetDirectoryName(StubFile("state"))!); File.WriteAllText(StubFile("state"), tokens + "\n"); }
        public void Dispose() { }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static AirGovernanceRuntime Runtime(Fixture fixture, AirSettings settings, List<AirStatus>? published = null) =>
        new(fixture.Environment, () => settings, status => published?.Add(status));

    internal static async Task Run(string root)
    {
        Parsing();
        var directory = Path.Combine(root, "air-governance");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        await Detection(Path.Combine(directory, "detect"));
        Contributions();
        await AutomaticCheck(Path.Combine(directory, "automatic"));
        await Untouched(Path.Combine(directory, "untouched"));
        await Launches(Path.Combine(directory, "launches"));
        await Failures(Path.Combine(directory, "failures"));
        await Access(Path.Combine(directory, "access"));
        await Transport(Path.Combine(directory, "transport"));
        Directory.Delete(directory, true);
        Console.WriteLine("PASS Air Governance: detection, source contributions, launch command shapes, untouched agent configuration, failures, access sources and the host seam over plugin RPC");
    }

    private static void Parsing()
    {
        Require(AirVersion.Parse("central 1.13.0 (commit: abc)")?.Text == "1.13.0" && AirVersion.Parse("synthetic malformed version") is null, "Versions parse like the fork's adapter.");
        Require(AirCentralCli.ParseStatus("\u001b[1mAuth      \u001b[m \u001b[31mnot connected\u001b[m\n\u001b[1mProxy     \u001b[m stopped\n") == ("signed-out", "stopped") &&
            AirCentralCli.ParseStatus("Auth      Someone\nProxy     running on port 1\n") == ("connected", "running") && AirCentralCli.ParseStatus("noise") == ("unknown", "unknown"),
            "Only the Auth and Proxy rows count, and unknown never sets a flag.");
        Require(AirCentralCli.IsSelectionId("license:abc-1") && !AirCentralCli.IsSelectionId("license:a b") && !AirCentralCli.IsSelectionId("--help") && !AirCentralCli.IsSelectionId("workspace:a:b:c"),
            "Only well-formed selection ids reach `central access`.");
    }

    private static async Task Detection(string directory)
    {
        using var fixture = new Fixture(directory);
        using var runtime = Runtime(fixture, new());
        var status = await runtime.StatusAsync(false, default);
        Require(status.Central is { State: "supported", Version: "1.13.0", Auth: "unknown", Proxy: "unknown" } && status.Central.Path == fixture.Central, "Detection reports where central is and its version without asking for sign-in.");
        Require(fixture.Log.SequenceEqual(["--version"]), "Opening the pane runs only `central --version`: " + string.Join("|", fixture.Log));
        fixture.Tokens("needs-login proxy-stopped");
        status = await runtime.StatusAsync(true, default);
        Require(status.Central is { Auth: "signed-out", Proxy: "stopped" }, "An explicit check reads the sign-in and proxy rows.");
        foreach (var (tokens, state) in new[] { ("outdated", "outdated"), ("malformed", "malformed-version"), ("probe-error", "probe-failed") })
        {
            fixture.Tokens(tokens);
            Require((await runtime.StatusAsync(false, default)).Central.State == state, $"{tokens} central is {state}.");
        }
        var empty = new AirEnvironment(fixture.Home, Path.Combine(directory, "nowhere"));
        using var absent = new AirGovernanceRuntime(empty, () => new(), _ => { });
        var missing = await absent.StatusAsync(false, default);
        Require(missing.Central is { State: "absent", Path: null } && missing.Central.InstallCommand.StartsWith(OperatingSystem.IsWindows() ? "irm " : "curl ", StringComparison.Ordinal), "A missing CLI offers the install command, never runs it.");
        var local = Path.Combine(fixture.Home, ".local", "bin");
        Directory.CreateDirectory(local);
        File.Copy(fixture.Central, Path.Combine(local, "central"));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(local, "central"), UnixFileMode.UserRead | UnixFileMode.UserExecute);
        fixture.Tokens("");
        Require((await absent.StatusAsync(false, default)).Central.Path == Path.Combine(local, "central"), "~/.local/bin is searched after PATH.");
    }

    private static void Contributions()
    {
        var central = new AirSettings { Source = AirSources.Central };
        Require(AirLaunch.Contribution(new(), "claude", "/c") is null && AirLaunch.Contribution(new(), "codex", "/c") is null, "Direct contributes nothing.");
        Require(AirLaunch.Contribution(central, "claude", "/opt/c/central") is { ReplacesExecutable: true, Wrapper: ["/opt/c/central", "run", "claude-code", "--"] } &&
            AirLaunch.Contribution(central, "codex", "/opt/c/central") is { ReplacesExecutable: true, Wrapper: ["/opt/c/central", "run", "codex", "--"] }, "Central wraps each agent in `central run <agent> --`.");
        Require(AirLaunch.Contribution(central with { Codex = false }, "codex", "/c") is null && AirLaunch.Contribution(central with { Codex = false }, "claude", "/c") is not null &&
            AirLaunch.Contribution(central with { ClaudeCode = false }, "claude", "/c") is null, "Each agent has its own switch.");
        Require(AirLaunch.Contribution(central, "gemini", "/c") is null && AirLaunch.Contribution(central, "codex", null)!.Wrapper[0] == "central", "Unknown launchers are untouched and a missing path fails closed with the bare name.");
        Require(AirLaunch.Shape(central, "claude", "/c") == "/c run claude-code -- <claude arguments>" && AirLaunch.Shape(new(), "claude", "/c") is null, "The pane shows the exact command shape.");
    }

    private static string[] Snapshot(Fixture fixture) => [.. Directory.EnumerateFiles(fixture.Home, "*", SearchOption.AllDirectories)
        .Where(file => !file.Contains(".central-stub", StringComparison.Ordinal)).Order(StringComparer.Ordinal)
        .Select(file => file + "|" + Convert.ToHexString(File.ReadAllBytes(file)) + "|" + File.GetLastWriteTimeUtc(file).Ticks)];

    // The automatic check is StatusAsync(probe): only --version and status run, no secret reaches the status.
    private static async Task AutomaticCheck(string directory)
    {
        using var fixture = new Fixture(directory);
        fixture.Tokens("needs-login");
        using var runtime = Runtime(fixture, new() { Source = AirSources.Central });
        var first = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => runtime.StatusAsync(true, default)));
        Require(first.All(status => status.Central.Auth == "signed-out"), "The automatic check reports signed out.");
        fixture.Tokens("");
        var flipped = await runtime.StatusAsync(true, default);
        Require(flipped.Central.Auth == "connected", "After the fixture signs in, the next check reports signed in.");
        Require(fixture.Log.All(line => line is "--version" or "status") && !JsonSerializer.Serialize(first.Concat([flipped])).Contains(Sentinel, StringComparison.Ordinal),
            "The automatic check runs only --version and status (never login, export or access) and leaks nothing: " + string.Join("|", fixture.Log));
    }

    private static async Task Untouched(string directory)
    {
        using var fixture = new Fixture(directory);
        var before = Snapshot(fixture);
        var dirs = Directory.EnumerateDirectories(fixture.Home, "*", SearchOption.AllDirectories).Where(path => !path.Contains(".central-stub", StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToArray();
        foreach (var source in new[] { AirSources.Central, AirSources.Direct, AirSources.Central })
        {
            using var runtime = Runtime(fixture, new() { Source = source });
            await runtime.StatusAsync(true, default);
            await runtime.SignInAsync(); await runtime.SignOutAsync(); await runtime.StartProxyAsync(); await runtime.SwitchAccessAsync("license:lic-9");
            Require(AirLaunch.Contribution(new() { Source = source }, "claude", fixture.Central) is null == (source == AirSources.Direct), "Switching sources only changes contributions.");
        }
        Require(Snapshot(fixture).SequenceEqual(before) && Directory.EnumerateDirectories(fixture.Home, "*", SearchOption.AllDirectories).Where(path => !path.Contains(".central-stub", StringComparison.Ordinal)).Order(StringComparer.Ordinal).SequenceEqual(dirs),
            "With a fake HOME, ~/.claude, ~/.codex, CLAUDE_CONFIG_DIR and CODEX_HOME (and everything else under HOME) are byte- and time-identical after every action and source switch.");
        Require(fixture.Log.All(line => !line.StartsWith("add", StringComparison.Ordinal) && !line.StartsWith("remove", StringComparison.Ordinal)), "Central's add and remove are never called: " + string.Join("|", fixture.Log));
    }

    // The launch lines the agents build, run by a real shell against the fake central, whose `run` records its argv.
    private static async Task Launches(string directory)
    {
        using var fixture = new Fixture(directory);
        var central = new AirSettings { Source = AirSources.Central };
        var claudeShape = SharpRail.Plugins.ClaudeCode.UI.ClaudeLaunch.SessionCommand("claude", "'it'\\''s \"q\" $(touch injected)'", disableAgentView: false, appendSystemPrompt: false,
            contributions: [AirLaunch.Contribution(central, "claude", fixture.Central)!]);
        var codexShape = SharpRail.Plugins.Codex.CodexLaunch.Line("codex", new(false, false) { InitialPrompt = "it's \"q\" $(touch injected)", Contributions = [AirLaunch.Contribution(central, "codex", fixture.Central)!] });
        Require(claudeShape.StartsWith("SHARPRAIL_UI_LAUNCH=claude " + fixture.Central + " run claude-code -- ", StringComparison.Ordinal), "Claude's line keeps SharpRail's marker outermost: " + claudeShape);
        Require(codexShape == fixture.Central + " run codex -- 'it'\\''s \"q\" $(touch injected)'", "Codex's line: " + codexShape);
        var direct = SharpRail.Plugins.ClaudeCode.UI.ClaudeLaunch.SessionCommand("claude", "x", false, false, contributions: []);
        Require(direct == "SHARPRAIL_UI_LAUNCH=claude claude --plugin-dir \"$" + SharpRail.Plugins.ClaudeCode.ClaudeCodeContract.PluginDirectoryVariable + "\" --add-dir \"$" + SharpRail.Plugins.ClaudeCode.ClaudeCodeContract.ScratchVariable + "\" x", "Direct leaves Claude's line as before: " + direct);
        foreach (var line in new[] { claudeShape, codexShape })
        {
            var start = new System.Diagnostics.ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            start.ArgumentList.Add("-c"); start.ArgumentList.Add(line);
            start.Environment["HOME"] = fixture.Home;
            start.Environment[SharpRail.Plugins.ClaudeCode.ClaudeCodeContract.PluginDirectoryVariable] = "/plug in";
            using var process = System.Diagnostics.Process.Start(start)!;
            await process.WaitForExitAsync();
            Require(process.ExitCode == 0, "The shell runs the governed line: " + await process.StandardError.ReadToEndAsync());
        }
        var run = File.ReadAllText(fixture.StubFile("run.log"));
        Require(run.Contains("arg:claude-code\n", StringComparison.Ordinal) && run.Contains("arg:--\n", StringComparison.Ordinal) && run.Contains("arg:codex\n", StringComparison.Ordinal) &&
            run.Split('\n').Count(line => line == "arg:it's \"q\" $(touch injected)") == 2 && run.Contains("arg:/plug in\n", StringComparison.Ordinal),
            "The prompt and a path with a space reach central run as single, literal arguments: " + run);
        Require(!File.Exists(Path.Combine(directory, "injected")), "Quoting stopped a command substitution in the prompt.");

        // Resume and flags keep their shape under the wrapper.
        Require(SharpRail.Plugins.Codex.Host.CodexResume.Command("codex --model x", "abc", false, false, null, contributions: [AirLaunch.Contribution(central, "codex", "/c")!]) is { } revived && revived.StartsWith("/c run codex -- --model x resume ", StringComparison.Ordinal),
            "A revived Codex terminal is governed too.");
    }

    private static async Task Failures(string directory)
    {
        using var fixture = new Fixture(directory);
        using var runtime = Runtime(fixture, new() { Source = AirSources.Central });
        fixture.Tokens("outdated update-error");
        Require(!(await runtime.UpdateAsync()).Ok, "A failed update reports failure.");
        fixture.Tokens("outdated");
        Require((await runtime.UpdateAsync()).Ok && (await runtime.StatusAsync(false, default)).Central.State == "supported", "Update brings Central to a supported version on explicit request.");
        fixture.Tokens("needs-login");
        Require((await runtime.SignInAsync()).Ok && (await runtime.StatusAsync(true, default)).Central.Auth == "connected", "Sign-in runs only on request and clears the signed-out state.");
        Require((await runtime.SignOutAsync()).Ok && (await runtime.StatusAsync(true, default)).Central.Auth == "signed-out", "Sign-out is explicit and reflected.");
        fixture.Tokens("login-error");
        var failed = await runtime.SignInAsync();
        Require(!failed.Ok && !failed.Message.Contains(Sentinel, StringComparison.Ordinal), "A sign-in that fails fast is reported without Central's output.");
        fixture.Tokens("slow-login");
        var slow = await runtime.SignInAsync();
        Require(slow.Ok && slow.Message.Contains("browser", StringComparison.Ordinal), "A sign-in still waiting on the browser returns without blocking.");
        fixture.Tokens("proxy-stopped");
        Require((await runtime.StartProxyAsync()).Ok && (await runtime.StatusAsync(true, default)).Central.Proxy == "running", "The proxy starts on request.");
        fixture.Tokens("needs-login proxy-stopped");
        var status = await runtime.StatusAsync(true, default);
        Require(status.Source == AirSources.Central && status.Agents.All(agent => agent.Launch is not null) && !JsonSerializer.Serialize(status).Contains(Sentinel, StringComparison.Ordinal), "Status shows the launch shapes and no Central output.");
    }

    private const string RefreshSecret = "SECRET-REFRESH-TOKEN-1f9";
    private const string AccessSecret = "SECRET-ACCESS-TOKEN-7c2";

    private static string Envelope(string environment = "prod", string? refresh = RefreshSecret, string? current = "ws") =>
        "central-" + System.Buffers.Text.Base64Url.EncodeToString(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["environment"] = environment,
            ["refresh_token"] = refresh,
            ["ai_access"] = current == "ws" ? new { type = "workspace", org_id = "org-1", workspace_id = "ws-1" } : new { type = "license", license_id = "lic-9" }
        })));

    private const string Options = """
        {"aiAccessOptions":[
          {"type":"workspace","orgId":"org-1","workspaceId":"ws-1","workspaceName":"Team\u0007 workspace","aiAccessEnabled":true},
          {"type":"license","licenseId":"lic-9","licenseName":"Personal licence","aiAccessEnabled":true},
          {"type":"license","licenseId":"lic-off","licenseName":"Disabled","aiAccessEnabled":false}]}
        """;

    /// <summary>A loopback HTTP endpoint standing in for the OAuth server and Cloud; the production table is never contacted.</summary>
    private sealed class FakeEndpoint : IDisposable
    {
        private readonly System.Net.HttpListener listener = new();
        internal List<string> Requests { get; } = [];
        internal string? TokenBody, Authorization;
        internal Func<(int Status, string Body, string? Location)> Token = () => (200, $"{{\"access_token\":\"{AccessSecret}\",\"refresh_token\":\"{RefreshSecret}\"}}", null);
        internal Func<(int Status, string Body, string? Location)> Access = () => (200, Options, null);

        internal FakeEndpoint()
        {
            var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0); probe.Start();
            var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
            Prefix = $"http://127.0.0.1:{port}/";
            listener.Prefixes.Add(Prefix); listener.Start();
            _ = Task.Run(async () =>
            {
                while (listener.IsListening)
                {
                    System.Net.HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch (Exception) { return; }
                    var token = context.Request.Url!.AbsolutePath == "/token";
                    Requests.Add(context.Request.HttpMethod + " " + context.Request.Url.AbsolutePath);
                    if (token) { using var reader = new StreamReader(context.Request.InputStream); TokenBody = await reader.ReadToEndAsync(); }
                    else Authorization = context.Request.Headers["Authorization"];
                    var (status, body, location) = token ? Token() : Access();
                    context.Response.StatusCode = status;
                    if (location is not null) context.Response.Headers["Location"] = location;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    await context.Response.OutputStream.WriteAsync(bytes);
                    context.Response.Close();
                }
            });
        }

        internal string Prefix { get; }
        internal AirAccessEndpoints Endpoints => new(new(Prefix + "token"), new(Prefix + "options"));
        public void Dispose() => listener.Close();
    }

    private static async Task Access(string directory)
    {
        var table = AirAccessEndpoints.For("prod")!;
        var staging = AirAccessEndpoints.For("staging")!;
        Require(AirAccessEndpoints.For("dev") is null && AirAccessEndpoints.For("") is null && new[] { table, staging }.All(known => known.Token.Scheme == "https" && known.Options.Scheme == "https") &&
            table.Token.Host == "oauth.account.jetbrains.com" && table.Options.Host == "api.jetbrains.cloud" && staging.Options.Host == "api.stgn.jetbrains.cloud",
            "Production endpoints are a fixed HTTPS table keyed by the environment name, nothing the envelope can name.");

        using var fixture = new Fixture(directory);
        using var endpoint = new FakeEndpoint();
        var environment = fixture.Environment with { TestEndpoints = endpoint.Endpoints };
        var surfaced = new List<string>();
        var published = new List<AirStatus>();
        using var runtime = new AirGovernanceRuntime(environment, () => new(), status => published.Add(status));
        void Export(string text) { Directory.CreateDirectory(Path.GetDirectoryName(fixture.StubFile("export"))!); File.WriteAllText(fixture.StubFile("export"), text + "\n"); }
        async Task<AirAccessList> List() { var list = await runtime.AccessSourcesAsync(default); surfaced.Add(JsonSerializer.Serialize(list)); return list; }

        // Success: enabled sources only, the current one from the exported selection, names sanitized.
        Export(Envelope());
        var before = fixture.Log.Length;
        var sources = await List();
        Require(sources.Detail is null && sources.Sources.Count == 2 && sources.Sources[0] is { Id: "workspace:org-1:ws-1", Name: "Team workspace", Current: true } && sources.Sources[1] is { Id: "license:lic-9", Current: false },
            "Listing reads the AI access options endpoint: " + JsonSerializer.Serialize(sources));
        Require(fixture.Log.Skip(before).All(line => line is "--version" or "login export") && fixture.Log.Contains("login export"), "Listing runs `login export`, not `central access`.");
        Require(endpoint.TokenBody == $"grant_type=refresh_token&client_id=central-cli&refresh_token={RefreshSecret}" && endpoint.Authorization == "Bearer " + AccessSecret && endpoint.Requests.SequenceEqual(["POST /token", "GET /options"]),
            "The refresh token is exchanged once and the access token is sent only to the options endpoint.");
        Export(Envelope(current: "license"));
        Require((await List()).Sources.Single(source => source.Current).Id == "license:lic-9", "The current marker follows the exported selection.");

        // Genuine empty list keeps the old message.
        endpoint.Access = () => (200, "{\"aiAccessOptions\":[]}", null);
        var empty = await List();
        Require(empty.Sources.Count == 0 && empty.Detail == "Central listed no selectable AI access sources.", "Only a genuinely empty list says there are no selectable sources.");
        endpoint.Access = () => (200, Options, null);

        async Task Refused(string name, string expected, int requests)
        {
            endpoint.Requests.Clear();
            var list = await List();
            Require(list.Sources.Count == 0 && list.Detail is not null && list.Detail.Contains(expected, StringComparison.Ordinal) && !list.Detail.Contains("no selectable", StringComparison.Ordinal) && endpoint.Requests.Count == requests,
                $"{name}: expected '{expected}' after {requests} requests, got '{list.Detail}' after {endpoint.Requests.Count}.");
        }

        fixture.Tokens("needs-login");
        await Refused("Signed out", "no signed-in session", 0);
        fixture.Tokens("");
        File.Delete(fixture.StubFile("export"));
        await Refused("Export fails", "no signed-in session", 0);
        Export("not an envelope"); await Refused("Garbage export", "not in the expected format", 0);
        Export("central-" + System.Buffers.Text.Base64Url.EncodeToString("[]"u8)); await Refused("Wrong shape", "not in the expected format", 0);
        Export(Envelope(refresh: null)); await Refused("No refresh token", "not in the expected format", 0);
        Export(Envelope(environment: "dev")); await Refused("Unknown environment", "unknown environment", 0);
        Export(Envelope());
        endpoint.Token = () => (302, "", "http://127.0.0.1:1/elsewhere"); await Refused("Token redirect", "redirected", 1);
        endpoint.Token = () => (200, $"{{\"access_token\":\"{AccessSecret}\"}}", null);
        endpoint.Access = () => (302, "", "http://127.0.0.1:1/elsewhere"); await Refused("Options redirect", "redirected", 2);
        endpoint.Access = () => (200, "{\"aiAccessOptions\":{}}", null); await Refused("Malformed options", "unexpected list", 2);
        endpoint.Access = () => (200, "{\"aiAccessOptions\":[{\"type\":\"license\",\"licenseId\":\"a b\",\"licenseName\":\"x\",\"aiAccessEnabled\":true}]}", null); await Refused("Bad id", "unexpected list", 2);
        endpoint.Access = () => (200, "not json", null); await Refused("Not JSON", "unexpected list", 2);
        endpoint.Access = () => (500, "boom " + RefreshSecret, null); await Refused("Endpoint error", "HTTP 500", 2);
        endpoint.Access = () => (200, new string(' ', AirAccessLister.MaxResponse + 10) + Options, null); await Refused("Oversized", "too large", 2);
        endpoint.Access = () => (200, Options, null);
        endpoint.Token = () => (401, "{}", null); await Refused("Rejected session", "rejected", 1);
        endpoint.Token = () => (200, "{\"refresh_token\":\"x\"}", null); await Refused("No access token", "unexpected token", 1);
        endpoint.Token = () => (200, $"{{\"access_token\":\"{AccessSecret}\",\"refresh_token\":\"ROTATED-{RefreshSecret}\"}}", null); await Refused("Rotation", "rotated", 1);
        Require(endpoint.Requests.All(request => request != "GET /options"), "A rotated refresh token stops before the options request.");

        // Nothing surfaced carries a secret.
        var everything = string.Concat(surfaced) + JsonSerializer.Serialize(published) + string.Join("\n", fixture.Log);
        Require(!everything.Contains("SECRET-", StringComparison.Ordinal) && !everything.Contains("ROTATED-", StringComparison.Ordinal) && !Snapshot(fixture).Any(line => line.Contains(Convert.ToHexString(Encoding.UTF8.GetBytes(RefreshSecret)), StringComparison.Ordinal)),
            "No token appears in any listing, status or logged argument.");

        // Switching: central access <id>, then the proxy restart, reported separately.
        var switched = await runtime.SwitchAccessAsync("license:lic-9");
        Require(switched.Ok && File.ReadAllText(fixture.StubFile("access")).Trim() == "license:lic-9" && fixture.Log.Contains("proxy restart"), "Switching selects the source and restarts the proxy.");
        fixture.Tokens("proxy-restart-error");
        var warned = await runtime.SwitchAccessAsync("workspace:org-1:ws-1");
        Require(warned.Ok && warned.Message.Contains("could not be restarted", StringComparison.Ordinal), "A proxy that will not restart is a warning, as in the fork.");
        Require(!(await runtime.SwitchAccessAsync("--accept-tos")).Ok && fixture.Log.All(line => !line.Contains("--accept-tos", StringComparison.Ordinal)), "Anything that is not a selection id never reaches Central.");
        fixture.Tokens("access-error");
        Require(!(await runtime.SwitchAccessAsync("license:lic-9")).Ok, "A failing switch returns failure.");

        // Serialisation: distinct sources queue in order, a repeat of the latest queued source shares its switch, A, B, A ends on A.
        fixture.Tokens("slow-access");
        File.Delete(fixture.StubFile("log"));
        var first = runtime.SwitchAccessAsync("license:lic-9");
        var second = runtime.SwitchAccessAsync("workspace:org-1:ws-1");
        var third = runtime.SwitchAccessAsync("license:lic-9");
        var shared = runtime.SwitchAccessAsync("license:lic-9");
        await Task.WhenAll(first, second, third, shared);
        Require(ReferenceEquals(third, shared) && !ReferenceEquals(first, third), "Only a request for the latest queued source is shared.");
        Require(fixture.Log.Where(line => line.StartsWith("access ", StringComparison.Ordinal)).SequenceEqual(["access license:lic-9", "access workspace:org-1:ws-1", "access license:lic-9"]) &&
            File.ReadAllText(fixture.StubFile("access")).Trim() == "license:lic-9", "Switches run one at a time and A, B, A is preserved: " + string.Join("|", fixture.Log));
    }

    private sealed class Recorder(Action<IPluginHostContext> activated) : PluginHostModule
    {
        public override PluginContract Contract { get; } = PluginContract.Create<AirSettings>("agents", 1, [], []);
        public override ValueTask<PluginDisposer?> ActivateAsync(IPluginHostContext context) { activated(context); return ValueTask.FromResult<PluginDisposer?>(null); }
    }

    private static async Task Transport(string directory)
    {
        using var fixture = new Fixture(directory);
        var state = new HostStateStore(Path.Combine(directory, "state"));
        IPluginHostContext? agents = null;
        await using var runtime = new PluginRuntime(new()
        {
            StateDirectory = Path.Combine(directory, "state"),
            State = state,
            Builtins = [(AirGovernanceContract.Manifest with { EnabledByDefault = true }, new AirGovernanceHost { Environment = fixture.Environment }),
                (new PluginManifest("agents", "Agents", "puzzle-2-line", "1", PluginApi.Generation, 1) { EnabledByDefault = true }, new Recorder(context => agents = context))]
        });
        await runtime.Start();
        Require((await runtime.ListAsync()).Single(entry => entry.Id == "air-governance") is { Status: PluginStatus.Active, Origin: PluginOrigin.Builtin }, "Air Governance is a builtin.");
        Require(!AirGovernanceContract.Manifest.EnabledByDefault, "It ships disabled: governing agents is the user's choice.");
        async Task<T> Call<T>(string method, object parameters) => PluginJson.Convert<T>(await runtime.CallAsync(new("air-governance", method, parameters, "fixture")));
        var status = await Call<AirStatus>("status", new AirStatusParams());
        Require(status.Source == AirSources.Direct && status.Central.State == "supported" && status.Agents.All(agent => agent.Launch is null), "Status answers over plugin RPC, direct by default.");
        Require(agents!.AgentLaunchContributions("codex").Count == 0, "Direct contributes nothing on the host.");
        await state.ChangeAsync([HostStateChange.PluginSettings("air-governance", JsonSerializer.Serialize(new { enabled = true, source = AirSources.Central, codex = false }))]);
        status = await Call<AirStatus>("status", new AirStatusParams());
        Require(status.Agents.Single(agent => agent.Id == "claude").Launch == fixture.Central + " run claude-code -- <claude arguments>" && status.Agents.Single(agent => agent.Id == "codex").Launch is null, "The persisted source and agent switches drive the launch shapes.");
        Require(agents.AgentLaunchContributions("claude").Single() is { ReplacesExecutable: true } contribution && contribution.Wrapper[0] == fixture.Central && agents.AgentLaunchContributions("codex").Count == 0,
            "The host half contributes to revived launches for the enabled agent only.");
        Require(AgentLaunchComposition.Apply("claude --resume abc", "claude", agents.AgentLaunchContributions("claude")) == fixture.Central + " run claude-code -- --resume abc", "A revive offer is rewritten.");
        await state.ChangeAsync([HostStateChange.PluginSettings("air-governance", JsonSerializer.Serialize(new { enabled = false }))]);
        for (var attempt = 0; attempt < 200 && agents.AgentLaunchContributions("claude").Count > 0; attempt++) await Task.Delay(25);
        Require(agents.AgentLaunchContributions("claude").Count == 0, "A disabled Air Governance contributes nothing.");
        await state.ChangeAsync([HostStateChange.PluginSettings("air-governance", JsonSerializer.Serialize(new { enabled = true, source = AirSources.Direct }))]);
        await Task.Delay(300);
        Require(agents.AgentLaunchContributions("claude").Count == 0 && fixture.Log.All(line => !line.StartsWith("run", StringComparison.Ordinal)), "Switching back removes the contribution; Central was never asked to run an agent.");
    }

    internal static void RunUi(string root)
    {
        var directory = Path.Combine(root, "air-governance-ui");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        Directory.CreateDirectory(directory);
        var profile = new ProfileStore(Path.Combine(directory, "profile"));
        var store = profile.OpenState();
        var roster = new PluginRosterEntry("air-governance", "Air Governance", "asset:air-governance.svg", "0.1.0", 1, PluginOrigin.Builtin, PluginStatus.Disabled)
        {
            Channels = new Dictionary<string, PluginRosterChannel> { ["status"] = new(PluginChannelKind.State, "status", []) }
        };
        var fake = new FakePluginHost(new LocalStateAdapter(store), store.Current, roster);
        AirStatus Status(string state, string auth, string source)
        {
            var settings = new AirSettings { Source = source };
            var path = state == "absent" ? null : "/usr/local/bin/central";
            return new(new(state, auth, "running", "curl -fsSL https://example.test/install.sh | bash", path, state == "absent" ? null : "1.13.0"), source,
                [new("claude", "Claude Code", true, AirLaunch.Shape(settings, "claude", path)), new("codex", "Codex", true, AirLaunch.Shape(settings, "codex", path))]);
        }
        fake.Replies["status"] = Status("absent", "unknown", AirSources.Direct);
        using var workbench = new Workbench(profile, new SharedState(fake, profile.Data.Preferences), E2eTerminals.Plain, false, () => new LocalProjectAdapter(new ProjectServices(directory, store)), fake);
        var window = workbench.Open(profile.Data.Windows[0], "");
        window.Width = 1200; window.Height = 800; window.Show();
        try
        {
            _ = workbench.State.ChangeAsync(HostStateChange.PluginEnabled("air-governance", true));
            E2eWorkspace.Until(() => workbench.PluginRegistry.Active.Contains("air-governance"));
            Require(fake.Calls.All(call => call.Method == "status"), "Enabling the plugin only reads its status.");
            window.ShowSettings("plugin:air-governance:air-governance");
            E2eWorkspace.Until(() => window.OwnedWindows.OfType<SettingsWindow>().Any());
            var settings = window.OwnedWindows.OfType<SettingsWindow>().Single();
            T Find<T>(string name) where T : Control => settings.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);
            E2eWorkspace.Until(() => fake.Subscriptions > 1 && Find<TextBlock>("AirCentralLine").Text?.Contains("not installed", StringComparison.Ordinal) == true);
            Require(Find<StackPanel>("AirInstall").IsVisible && Find<TextBox>("AirInstallCommand").Text!.Contains("install.sh", StringComparison.Ordinal) && !Find<Button>("AirCheck").IsEnabled,
                "A missing CLI shows the install command and offers no action.");

            fake.Push("air-governance", "status", Status("supported", "unknown", AirSources.Direct));
            E2eWorkspace.Until(() => Find<TextBlock>("AirCentralLine").Text?.Contains("1.13.0", StringComparison.Ordinal) == true);
            Require(Find<RadioButton>("AirSourceDirect").IsChecked == true && workbench.PluginRegistry.LaunchContributions("claude").Count == 0 &&
                Find<TextBlock>("AirLaunches").Text!.Contains("claude: unchanged", StringComparison.OrdinalIgnoreCase) == false && Find<TextBlock>("AirLaunches").Text!.Contains("unchanged", StringComparison.Ordinal),
                "Direct is the default and contributes nothing to launches.");

            fake.Replies["status"] = Status("supported", "unknown", AirSources.Central);
            Find<RadioButton>("AirSourceCentral").IsChecked = true;
            E2eWorkspace.Until(() => workbench.State.Current.PluginSettings.TryGetValue("air-governance", out var saved) && saved.TryGetProperty("source", out var source) && source.GetString() == AirSources.Central);
            E2eWorkspace.Until(() => Find<TextBlock>("AirLaunches").Text!.Contains("run claude-code --", StringComparison.Ordinal));
            Require(Find<TextBlock>("AirLaunches").Text!.Contains("/usr/local/bin/central run codex -- <codex arguments>", StringComparison.Ordinal) && fake.Calls.All(call => call.Method == "status"),
                "Choosing a source persists it and shows the exact command shape, running nothing.");
            Require(workbench.PluginRegistry.LaunchContributions("claude").Single().Wrapper.SequenceEqual(["/usr/local/bin/central", "run", "claude-code", "--"]), "The UI half contributes the wrapper with the detected path.");
            Find<CheckBox>("AirAgentCodex").IsChecked = false;
            E2eWorkspace.Until(() => workbench.State.Current.PluginSettings["air-governance"].TryGetProperty("codex", out var codex) && codex.ValueKind == System.Text.Json.JsonValueKind.False);
            Require(workbench.PluginRegistry.LaunchContributions("codex").Count == 0 && workbench.PluginRegistry.LaunchContributions("claude").Count == 1, "Each agent's toggle is honoured by new launches.");

            fake.Replies["access-sources"] = new AirAccessList([new("workspace:o:w", "Team workspace", true), new("license:l", "Personal licence", false)], null);
            Find<Button>("AirListAccess").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2eWorkspace.Until(() => settings.GetLogicalDescendants().OfType<Button>().Count(button => button.Name == "AirSwitchAccess") == 1);
            fake.Replies["switch-access"] = new AirOutcome(true, "Switched. The proxy was restarted.", Status("supported", "connected", AirSources.Central));
            settings.GetLogicalDescendants().OfType<Button>().Single(button => button.Name == "AirSwitchAccess").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2eWorkspace.Until(() => fake.Calls.Any(call => call.Method == "switch-access"));
            Require(fake.Calls.Last(call => call.Method == "switch-access").Params is AirSwitchParams { SourceId: "license:l" }, "Switching sends the opaque id.");

            // Automatic checks: once on open, throttled on activation, polled while a sign-in is awaited, never listing.
            int Probes() => fake.Calls.Count(call => call.Method == "status" && call.Params is AirStatusParams { Probe: true });
            var view = settings.GetLogicalDescendants().OfType<AirSettingsView>().Single();
            Require(Probes() >= 1 && fake.Calls.All(call => call.Method is "status" or "access-sources" or "switch-access"), "Opening the pane checked sign-in on its own.");
            var before = Probes();
            view.OnActivated(); view.OnActivated();
            Dispatcher.UIThread.RunJobs(); System.Threading.Thread.Sleep(200); Dispatcher.UIThread.RunJobs();
            Require(Probes() == before, "Activation inside the throttle window runs no check.");
            var throttle = AirSettingsView.Throttle; var interval = AirSettingsView.PollInterval;
            AirSettingsView.Throttle = TimeSpan.Zero; AirSettingsView.PollInterval = TimeSpan.FromMilliseconds(100);
            try
            {
                view.OnActivated();
                E2eWorkspace.Until(() => Probes() == before + 1);
                fake.Replies["status"] = Status("supported", "signed-out", AirSources.Central);
                fake.Replies["sign-in"] = new AirOutcome(true, "Complete sign-in in the browser.", Status("supported", "unknown", AirSources.Central));
                Find<Button>("AirSignIn").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                E2eWorkspace.Until(() => Probes() >= before + 4);
                Require(Find<TextBlock>("AirCentralLine").Text!.Contains("signed out", StringComparison.Ordinal) && Find<Button>("AirSignIn").IsEnabled && !Find<Button>("AirSignOut").IsEnabled,
                    "While a sign-in is awaited the pane polls and shows signed out.");
                fake.Replies["status"] = Status("supported", "connected", AirSources.Central);
                E2eWorkspace.Until(() => Find<TextBlock>("AirCentralLine").Text!.Contains("signed in", StringComparison.Ordinal));
                var settled = Probes();
                System.Threading.Thread.Sleep(500); Dispatcher.UIThread.RunJobs();
                Require(Probes() == settled, "Polling stops once signed in.");
            }
            finally { AirSettingsView.Throttle = throttle; AirSettingsView.PollInterval = interval; }

            fake.Replies["status"] = Status("supported", "signed-out", AirSources.Central);
            Find<Button>("AirCheck").RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
            E2eWorkspace.Until(() => Find<TextBlock>("AirCentralLine").Text!.Contains("signed out", StringComparison.Ordinal));
            Require(Find<Button>("AirSignIn").IsEnabled && !Find<Button>("AirSignOut").IsEnabled && fake.Calls.Last(call => call.Method == "status").Params is AirStatusParams { Probe: true },
                "Only the Check button asks for the sign-in state, and signed out offers Sign in.");
            settings.Close();
            E2eWorkspace.Until(() => fake.Subscriptions == 1);
            _ = workbench.State.ChangeAsync(HostStateChange.PluginEnabled("air-governance", false));
            E2eWorkspace.Until(() => !workbench.PluginRegistry.Active.Contains("air-governance"));
            Require(workbench.PluginRegistry.LaunchContributions("claude").Count == 0, "Disabling the plugin removes its contributions at once.");
        }
        finally { window.Close(); }
        RunLaunchers(root);
        Console.WriteLine("PASS Air Governance UI: plugin settings persistence, launch command shape, access switching, contributions and subscription teardown");
    }

    // The real launchers of the Claude Code and Codex plugins in the app, with a fake central first on PATH.
    private static void RunLaunchers(string root)
    {
        var directory = Path.Combine(root, "air-governance-launchers");
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
        using var fixture = new Fixture(directory);
        var saved = new Dictionary<string, string?>();
        foreach (var (name, value) in new Dictionary<string, string> { ["PATH"] = Path.GetDirectoryName(fixture.Central) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"), ["SHARPRAIL_STATE_DIR"] = Path.Combine(directory, "state") })
        {
            saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }
        try
        {
            using var app = new E2eWorkspace(Path.Combine(directory, "project"), openFiles: false);
            var registry = app.Workbench.PluginLoader!.Registry;
            E2eWorkspace.Until(() => registry.Entry("air-governance") is not null);
            string Command(string id) => registry.LauncherList.Single(launcher => launcher.Id == id).TerminalCommand(new() { InitialPrompt = "go" });
            void Settings(string plugin, object value) => app.State!.ChangeAsync([HostStateChange.PluginSettings(plugin, JsonSerializer.Serialize(value))]).AsTask().GetAwaiter().GetResult();
            Settings("claude-code", new { enabled = true }); Settings("codex", new { enabled = true });
            E2eWorkspace.Until(() => registry.LauncherList.Any(launcher => launcher.Id == "claude") && registry.LauncherList.Any(launcher => launcher.Id == "codex"));
            var plainClaude = Command("claude"); var plainCodex = Command("codex");
            Require(!plainClaude.Contains("central", StringComparison.Ordinal) && !plainCodex.Contains("central", StringComparison.Ordinal), "Without the plugin the launchers are unchanged.");
            Settings("air-governance", new { enabled = true, source = AirSources.Central });
            E2eWorkspace.Until(() => registry.Active.Contains("air-governance") && Command("claude").Contains(fixture.Central, StringComparison.Ordinal));
            var claude = Command("claude"); var codex = Command("codex");
            Require(claude.Contains(fixture.Central + " run claude-code -- ", StringComparison.Ordinal) && claude.StartsWith("SHARPRAIL_UI_LAUNCH=claude ", StringComparison.Ordinal) &&
                codex.Contains("SHARPRAIL_UI_LAUNCH=codex " + fixture.Central + " run codex -- ", StringComparison.Ordinal), "Both agents' launchers run through `central run`: " + claude + " / " + codex);
            Settings("air-governance", new { enabled = true, source = AirSources.Central, codex = false });
            E2eWorkspace.Until(() => !Command("codex").Contains("central", StringComparison.Ordinal));
            Require(Command("claude").Contains("central", StringComparison.Ordinal), "The Codex toggle leaves Claude governed.");
            Settings("claude-code", new { enabled = true, command = "my-claude --x" });
            E2eWorkspace.Until(() => Command("claude").Contains("my-claude", StringComparison.Ordinal));
            Require(!Command("claude").Contains("central", StringComparison.Ordinal), "A custom Claude command is never rewritten.");
            Settings("claude-code", new { enabled = true, command = "" });
            Settings("air-governance", new { enabled = true, source = AirSources.Direct });
            E2eWorkspace.Until(() => !Command("claude").Contains("central", StringComparison.Ordinal));
            Settings("air-governance", new { enabled = true, source = AirSources.Central });
            E2eWorkspace.Until(() => Command("claude").Contains("central", StringComparison.Ordinal));
            Settings("air-governance", new { enabled = false, source = AirSources.Central });
            E2eWorkspace.Until(() => !registry.Active.Contains("air-governance"));
            Require(!Command("claude").Contains("central", StringComparison.Ordinal) && !Command("codex").Contains("central", StringComparison.Ordinal), "Disabling the plugin returns every launch to normal.");
            Require(fixture.Log.All(line => !line.StartsWith("run", StringComparison.Ordinal)), "Building commands never runs an agent through central.");
        }
        finally { foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value); }
    }
}