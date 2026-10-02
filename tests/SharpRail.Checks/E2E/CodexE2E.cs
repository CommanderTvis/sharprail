using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Remote;
using SharpRail.Plugins.Api.UI;
using SharpRail.Plugins.Codex;
using SharpRail.Plugins.Codex.Host;
using SharpRail.Plugins.UI.Kit;
using SharpRail.UI.Panels;

namespace SharpRail.Checks.E2E;

// The fork's e2e/plugins/codex specs against the in-process host with real PTY terminals, the real Codex plugin halves,
// headless input, a fixture codex executable and Codex's real IDE-context IPC frames. The subscription notice spec is
// not translated: it invites the user to Pi, which SharpRail excludes.
internal static class CodexE2E
{
    private static void Require(bool condition, string message) => E2eWorkspace.Require(condition, "Codex E2E: " + message);

    /// <summary>The fixture programs this executable doubles as; null when <paramref name="mode"/> is not one.</summary>
    internal static int? Fake(string mode, string[] args) => mode switch
    {
        "--fake-codex" => CodexChecks.FakeCodex(args),
        "--fake-codex-picker" => FakePicker(),
        "--fake-codex-input" => FakeInput(),
        _ => null
    };

    private static void Out(string text)
    {
        using var stdout = Console.OpenStandardOutput();
        stdout.Write(Encoding.UTF8.GetBytes(text));
        stdout.Flush();
    }

    [DllImport("libc", EntryPoint = "read", SetLastError = true)]
    private static extern nint ReadInput(int descriptor, [Out] byte[] buffer, nuint count);

    private static void RawInput()
    {
        using var stty = Process.Start(new ProcessStartInfo("stty", ["raw", "-echo"]) { UseShellExecute = false })!;
        stty.WaitForExit();
        if (stty.ExitCode != 0) throw new InvalidOperationException("The Codex fixture requires a raw PTY.");
    }

    // e2e/fixtures/fake-codex-model-picker.ts: Codex's /model picker, "s" for this session, Enter saves the default.
    // The headless terminal keeps its whole output rather than a screen, so leaving the picker scrolls its rows out of
    // the accessory's 48-line tail, as a real screen would clear them.
    private static int FakePicker()
    {
        (string Name, string Slug, bool Efforts)[] models =
            [("GPT-6-Astra (default)", "gpt-6-astra", true), ("GPT-5.6 Sol", "gpt-5.6-sol", true), ("GPT-5.6 Terra", "gpt-5.6-terra", false), ("GPT-5.6 Luna (current)", "gpt-5.6-luna", true)];
        string[] efforts = ["Low", "Medium (default)", "High"];
        var view = "composer";
        var highlighted = 3;
        var model = 3;
        var typed = "";
        string Rows(IEnumerable<string> names) => string.Join("\r\n", names.Select((name, index) => $"{(index == highlighted ? "›" : " ")} {index + 1}. {name}  A description"));
        void Render()
        {
            var body = view == "models" ? "  Select Model and Effort\r\n\r\n" + Rows(models.Select(item => item.Name))
                : $"  Select Reasoning Level for {models[model].Name}\r\n\r\n" + Rows(efforts);
            Out($"\x1b[?1049h\x1b[2J\x1b[H{body}\r\n\r\n  enter default · s session · esc back\r\n");
        }
        int Leave(string message)
        {
            Out("\x1b[?1049l" + string.Concat(Enumerable.Repeat("\r\n", 60)) + $"• {message}\r\n");
            return 0;
        }
        using var stdout = Console.OpenStandardOutput();
        RawInput();
        Out("fake-codex ready\r\n› Ask Codex to do anything\r\n");
        var buffer = new byte[64];
        int read;
        while ((read = (int)ReadInput(0, buffer, (nuint)buffer.Length)) > 0)
        {
            var data = Encoding.UTF8.GetString(buffer, 0, read);
            if (view == "composer")
            {
                typed += data;
                if (typed.Contains("/model", StringComparison.Ordinal) && typed.Contains('\r')) { view = "models"; Render(); }
                continue;
            }
            var size = view == "models" ? models.Length : efforts.Length;
            if (data.Contains("\x1b[B", StringComparison.Ordinal)) highlighted = (highlighted + 1) % size;
            else if (data.Contains("\x1b[A", StringComparison.Ordinal)) highlighted = (highlighted + size - 1) % size;
            else if (data == "\x1b") return Leave("Kept model");
            else if (view == "models" && data == "\r")
            {
                if (!models[highlighted].Efforts) return Leave("Saved the default model");
                model = highlighted;
                view = "effort";
                highlighted = 1;
            }
            else if (view == "models" && data == "s" && !models[highlighted].Efforts) return Leave($"Model changed to {models[highlighted].Slug} for this session only");
            else if (view == "effort" && data == "s")
                return Leave($"Model changed to {models[model].Slug} {efforts[highlighted].Split(' ')[0].ToLowerInvariant()} for this session only");
            else if (view == "effort" && data == "\r") return Leave("Saved the default model");
            Render();
        }
        return 0;
    }

    // e2e/fixtures/codex-command-input.ts: reports a Codex session, then echoes each command submitted with an Enter
    // that arrives on its own, as Codex's paste-burst handling requires.
    private static int FakeInput()
    {
        var typed = new StringBuilder();
        var last = Stopwatch.StartNew();
        var submitted = 0;
        using var stdout = Console.OpenStandardOutput();
        RawInput();
        Out("codex command input ready\r\n");
        using (var http = new HttpClient())
            http.PostAsync(Environment.GetEnvironmentVariable("THINKRAIL_CODEX_STATUS_URL"),
                new StringContent("{\"hook_event_name\":\"SessionStart\",\"session_id\":\"codex-ide-command-test\"}", Encoding.UTF8, "application/json")).GetAwaiter().GetResult();
        var buffer = new byte[64];
        int read;
        while ((read = (int)ReadInput(0, buffer, (nuint)buffer.Length)) > 0)
            foreach (var character in Encoding.UTF8.GetString(buffer, 0, read))
            {
                if (character == '\x03') return 0;
                if (character == '\r' && last.ElapsedMilliseconds >= 100)
                {
                    submitted++;
                    Out($"\r\nSUBMITTED {submitted}: {JsonSerializer.Serialize(typed.ToString())}\r\n");
                    typed.Clear();
                }
                else
                {
                    typed.Append(character == '\r' ? '\n' : character);
                    last.Restart();
                }
            }
        return 0;
    }

    private static T One<T>(Control scope, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs(); (TopLevel.GetTopLevel(scope) ?? scope).UpdateLayout();
        return scope.GetLogicalDescendants().OfType<T>().Single(item => item.Name == name);
    }

    private static IReadOnlyList<T> All<T>(Control scope, string name) where T : Control
    {
        Dispatcher.UIThread.RunJobs(); (TopLevel.GetTopLevel(scope) ?? scope).UpdateLayout();
        return [.. scope.GetLogicalDescendants().OfType<T>().Where(item => item.Name == name && item.IsEffectivelyVisible)];
    }

    private static bool Has(Control scope, string name) => All<Control>(scope, name).Count > 0;

    private static string Texts(Control control) =>
        string.Join(" ", new[] { control }.Concat(control.GetLogicalDescendants()).OfType<TextBlock>().Where(text => text.IsEffectivelyVisible).Select(text => text.Text));

    private sealed class Codex : IDisposable
    {
        public readonly E2eWorkspace App;
        public readonly string Home;
        private readonly string? previousHome;
        private readonly Microsoft.AspNetCore.Builder.WebApplication server;
        private readonly RemoteTerminalAdapter terminalAdapter;

        public Codex(string root, bool enable = true)
        {
            previousHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            Home = CodexChecks.ShortTemp("tr-codex-");
            Environment.SetEnvironmentVariable("CODEX_HOME", Home);
            Directory.CreateDirectory(root);
            File.WriteAllText(Path.Combine(root, "notes.txt"), "plain-text-fixture\n");
            // Its own repository marker, so Codex's AGENTS.md discovery stops here, not at the checkout holding the fixtures.
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            const string token = "codex-fixture";
            server = RemoteServer.Create(root, IPAddress.Loopback, 0, token, root + "-host");
            Task.Run(() => server.StartAsync()).GetAwaiter().GetResult();
            var endpoint = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            terminalAdapter = new RemoteTerminalAdapter(endpoint, token);
            App = new E2eWorkspace(endpoint, token, root, root + "-profile", root, terminals: new E2eTerminals(terminalAdapter));
            E2eWorkspace.Until(() => App.Window.WorkspaceMounted);
            if (enable) Enable(true);
        }

        public SharpRail.UI.Plugins.PluginLoader Loader => App.Workbench.PluginLoader;

        public void Enable(bool on)
        {
            _ = App.Workbench.State.ChangeAsync(HostStateChange.PluginEnabled(CodexManifest.Id, on));
            E2eWorkspace.Until(() => Loader.Registry.Active.Contains(CodexManifest.Id) == on);
            E2eWorkspace.Settle(100);
        }

        public void Settings(object settings)
        {
            var json = JsonSerializer.Serialize(settings, SharpRail.Plugins.Api.PluginJson.Options);
            _ = App.Workbench.State.ChangeAsync(HostStateChange.PluginSettings(CodexManifest.Id, json));
            E2eWorkspace.Until(() => JsonSerializer.SerializeToElement(settings, SharpRail.Plugins.Api.PluginJson.Options).EnumerateObject()
                .All(member => App.Workbench.State.Current.PluginSettings.GetValueOrDefault(CodexManifest.Id) is { ValueKind: JsonValueKind.Object } stored &&
                    stored.TryGetProperty(member.Name, out var value) && value.GetRawText() == member.Value.GetRawText()));
        }

        public Control Panel()
        {
            App.Window.Layout.RestoreTool(CodexManifest.ConfigTool);
            E2eWorkspace.Until(() => Has(App.Window, "CodexConfig"));
            return One<Control>(App.Window, "CodexConfig");
        }

        public void Surface(string name) => App.Click(One<ToggleButton>(App.Window, "CodexSurface_" + name));

        public HostTerminal Terminal()
        {
            var count = App.Terminals.Views.Count;
            App.Window.Layout.NewTerminal(App.Center);
            E2eWorkspace.Until(() => App.Terminals.Views.Count > count);
            var terminal = App.Terminals.Views[^1];
            E2eWorkspace.Until(() => terminal.Started.IsCompleted);
            return terminal;
        }

        public void Hook(HostTerminal terminal, object body) =>
            terminal.Run($"curl -s -o /dev/null -H 'Content-Type: application/json' -d '{JsonSerializer.Serialize(body)}' \"$THINKRAIL_CODEX_STATUS_URL\"");

        public void Dispose()
        {
            App.Dispose();
            terminalAdapter.Dispose();
            Task.Run(async () => { await server.StopAsync(); await server.DisposeAsync(); }).GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable("CODEX_HOME", previousHome);
            try { Directory.Delete(Home, true); } catch (IOException) { }
        }
    }

    internal static void RunTerminals(string root)
    {
        Directory.CreateDirectory(root);
        ModelPicker(Path.Combine(root, "codex-model-picker"));
        Status(Path.Combine(root, "codex-status"));
        Console.WriteLine("PASS Codex terminal checks: hooks, IDE commands, session model picker, token totals and plan");
    }

    internal static void Run(string root)
    {
        Directory.CreateDirectory(root);
        Account(Path.Combine(root, "codex-account"));
        ConfigFile(Path.Combine(root, "codex-config-file"));
        Context(Path.Combine(root, "codex-context"));
        Capabilities(Path.Combine(root, "codex-capabilities"));
        IdeContext(Path.Combine(root, "codex-ide"));
        Launcher(Path.Combine(root, "codex-launcher"));
        if (!OperatingSystem.IsWindows()) LaunchInstructions(Path.Combine(root, "codex-launch-instructions"));
        ModelPicker(Path.Combine(root, "codex-model-picker"));
        Status(Path.Combine(root, "codex-status"));
        Console.WriteLine("PASS Codex plugin E2E: account, config file, context, IDE context, launcher, launch instructions, model picker, status");
    }

    private static string FakeExecutable(string directory, CodexChecks.FakeControl control, out string controlPath, out string log)
    {
        Directory.CreateDirectory(directory);
        controlPath = Path.Combine(directory, "control.json");
        log = Path.Combine(directory, "requests.jsonl");
        CodexChecks.WriteControl(controlPath, control);
        File.WriteAllText(log, "");
        return CodexChecks.FakeCodexExecutable(Path.Combine(directory, "codex-fixture"), controlPath, log);
    }

    private static int LastPid(string log) =>
        File.ReadAllLines(log).Select(line => JsonDocument.Parse(line).RootElement).Last(entry => entry.TryGetProperty("pid", out _)).GetProperty("pid").GetInt32();

    private static bool Running(int pid)
    {
        try { return !Process.GetProcessById(pid).HasExited; }
        catch (ArgumentException) { return false; }
    }

    // codex-account.spec.ts
    private static void Account(string root)
    {
        using var codex = new Codex(root, enable: false);
        var identity = JsonSerializer.SerializeToElement(new { account = new { type = "chatgpt", email = "codex@example.test", planType = "pro" }, requiresOpenaiAuth = true });
        JsonElement Limits(double used) => JsonSerializer.SerializeToElement(new
        {
            rateLimits = new
            {
                limitId = "codex",
                limitName = "Codex",
                primary = new { usedPercent = used, windowDurationMins = 300, resetsAt = 1_800_000_000 },
                secondary = new { usedPercent = 80, windowDurationMins = 10080, resetsAt = (long?)null }
            }
        });
        var executable = FakeExecutable(Path.Combine(root, "fixture"), new() { Account = identity, Limits = Limits(25), ErrorMessage = "Usage temporarily unavailable" },
            out var control, out var log);
        void Set(CodexChecks.FakeControl next) => CodexChecks.WriteControl(control, next with { ErrorMessage = "Usage temporarily unavailable" });
        codex.Settings(new { command = executable });
        codex.Enable(true);
        var panel = codex.Panel();
        codex.Surface("account");
        E2eWorkspace.Until(() => Has(panel, "CodexAccount"));
        var account = One<Control>(panel, "CodexAccount");
        Require(Texts(account).Contains("codex@example.test", StringComparison.Ordinal) && Texts(account).Contains("pro", StringComparison.Ordinal), "identity: " + Texts(account));
        var windows = All<Control>(panel, "CodexUsageWindow");
        Require(windows.Count == 2, "two usage windows");
        Require(Texts(One<Control>(panel, "CodexAccountSection")).Contains("Email", StringComparison.Ordinal) &&
            Texts(One<Control>(panel, "CodexAccountSection")).Contains("Plan", StringComparison.Ordinal) &&
            !Texts(One<Control>(panel, "CodexAccountSection")).Contains("Version", StringComparison.Ordinal), "the account section has labelled rows, no version");
        Require(Texts(One<Control>(panel, "CodexCliSection")).Contains("Version", StringComparison.Ordinal) && Texts(One<Control>(panel, "CodexCliSection")).Contains("1.0.0", StringComparison.Ordinal),
            "the CLI version has its own section");
        Require(Texts(windows[0]).Contains("25% used", StringComparison.Ordinal) && windows[0].GetLogicalDescendants().OfType<ProgressBar>().Single().Value == 25 &&
            Texts(windows[0]).Contains("5 hr", StringComparison.Ordinal) && Texts(windows[0]).Contains("Resets", StringComparison.Ordinal), "the first window: " + Texts(windows[0]));
        Require(Texts(windows[1]).Contains("80% used", StringComparison.Ordinal), "the second window");
        var pid = LastPid(log);

        Set(new() { Account = identity, Limits = Limits(40) });
        codex.App.Click(One<Button>(panel, "CodexConfigRefresh"));
        E2eWorkspace.Until(() => All<Control>(panel, "CodexUsageWindow") is { Count: > 0 } now && Texts(now[0]).Contains("40% used", StringComparison.Ordinal));
        Require(All<Control>(panel, "CodexUsageWindow")[0].GetLogicalDescendants().OfType<ProgressBar>().Single().Value == 40, "the bar follows");
        Require(LastPid(log) == pid, "refresh reuses the server");

        Set(new() { Account = identity, Limits = Limits(40), Error = "account/rateLimits/read" });
        codex.App.Click(One<Button>(panel, "CodexConfigRefresh"));
        E2eWorkspace.Until(() => Has(panel, "CodexUsageError"));
        Require(One<TextBlock>(panel, "CodexUsageError").Text!.Contains("Usage temporarily unavailable", StringComparison.Ordinal) &&
            Texts(One<Control>(panel, "CodexAccount")).Contains("codex@example.test", StringComparison.Ordinal) && All<Control>(panel, "CodexUsageWindow").Count == 0,
            "an error keeps the identity, never the old usage");

        Set(new() { Account = JsonSerializer.SerializeToElement(new { account = new { type = "apiKey" }, requiresOpenaiAuth = true }) });
        codex.App.Click(One<Button>(panel, "CodexConfigRefresh"));
        E2eWorkspace.Until(() => Has(panel, "CodexUsageEmpty") && One<TextBlock>(panel, "CodexUsageEmpty").Text!.Contains("ChatGPT accounts", StringComparison.Ordinal));
        Set(new() { Account = JsonSerializer.SerializeToElement(new { account = (object?)null, requiresOpenaiAuth = true }) });
        codex.App.Click(One<Button>(panel, "CodexConfigRefresh"));
        E2eWorkspace.Until(() => Has(panel, "CodexAccountSignedOut") && One<TextBlock>(panel, "CodexAccountSignedOut").Text!.Contains("codex login", StringComparison.Ordinal));

        // A changed command closes the old server and starts a new one; disabling closes it too.
        codex.Settings(new { command = executable + " --model changed" });
        Set(new() { Account = identity, Limits = Limits(25) });
        codex.App.Click(One<Button>(panel, "CodexConfigRefresh"));
        E2eWorkspace.Until(() => Has(panel, "CodexAccount") && Texts(One<Control>(panel, "CodexAccount")).Contains("codex@example.test", StringComparison.Ordinal));
        var replacement = LastPid(log);
        Require(replacement != pid, "a new command starts a new server");
        E2eWorkspace.Until(() => !Running(pid));
        codex.Enable(false);
        E2eWorkspace.Until(() => !Running(replacement));
    }

    private static DialogWindow Dialog(E2eWorkspace app)
    {
        E2eWorkspace.Until(() => app.Window.OwnedWindows.OfType<DialogWindow>().Any(window => Equals(window.Tag, "CodexValueDialog")));
        return app.Window.OwnedWindows.OfType<DialogWindow>().Single(window => Equals(window.Tag, "CodexValueDialog"));
    }

    private static Control SettingRow(Control panel, string key)
    {
        Dispatcher.UIThread.RunJobs();
        return panel.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == "CodexSetting" && Equals(item.Tag, key));
    }

    // codex-config-file.spec.ts
    private static void ConfigFile(string root)
    {
        using var codex = new Codex(root, enable: false);
        var path = Path.Combine(codex.Home, "config.toml");
        File.WriteAllText(path, "model = \"config-link-fixture\"\napproval_policy = \"on-request\"\n[features]\nhooks = true\n");
        codex.Enable(true);
        var panel = codex.Panel();
        codex.Surface("settings");
        E2eWorkspace.Until(() => panel.GetLogicalDescendants().OfType<Control>().Any(item => item.Name == "CodexSetting" && Equals(item.Tag, "features.hooks")));
        Require(One<Button>(SettingRow(panel, "features.hooks"), "CodexSettingChange").GetLogicalParent()?.GetType().Name == "CodexSettingValue" &&
            Has(panel, "CodexProjectTrust") && One<Control>(panel, "CodexProjectTrust").GetLogicalParent()?.GetType().Name == "CodexSettingsList",
            "setting values and the trust line come from compiled frames");

        void Toggle(bool from)
        {
            codex.App.Click(One<Button>(SettingRow(panel, "features.hooks"), "CodexSettingChange"));
            var dialog = Dialog(codex.App);
            var toggle = One<Button>(dialog, "CodexValueSwitch");
            Require(Equals(toggle.Tag, from), "the switch shows the current value");
            codex.App.Click(toggle, freshGesture: false);
            Require(Equals(One<Button>(dialog, "CodexValueSwitch").Tag, !from) && Texts(One<Button>(dialog, "CodexValueSwitch")).Contains((!from).ToString().ToLowerInvariant(), StringComparison.Ordinal), "the switch flips");
            codex.App.Click(One<Button>(dialog, "CodexValueContinue"), freshGesture: false);
            E2eWorkspace.Until(() => File.ReadAllText(path).Contains($"hooks = {(!from).ToString().ToLowerInvariant()}", StringComparison.Ordinal));
            E2eWorkspace.Until(() => !codex.App.Window.OwnedWindows.Any());
        }
        Toggle(true);
        E2eWorkspace.Until(() => SettingRow(panel, "features.hooks").GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "false"));
        Toggle(false);

        codex.App.Click(One<Button>(SettingRow(panel, "model"), "CodexSettingChange"));
        var text = Dialog(codex.App);
        Require(One<TextBox>(text, "CodexValueText").Text == "config-link-fixture", "a text value");
        text.Close();
        E2eWorkspace.Until(() => !codex.App.Window.OwnedWindows.Any());
        codex.App.Click(One<Button>(SettingRow(panel, "approval_policy"), "CodexSettingChange"));
        var choice = Dialog(codex.App);
        Require(Has(choice, "CodexValueChoice_on-request") && Has(choice, "CodexValueChoice_never"), "an enum key picks from its choices");
        choice.Close();
        E2eWorkspace.Until(() => !codex.App.Window.OwnedWindows.Any());

        // The source link opens the external file in an editor tab; only the plugin's exact files are readable.
        codex.App.Click(One<Button>(SettingRow(panel, "model"), "CodexOpenSource"));
        E2eWorkspace.Until(() => codex.App.Window.Layout.State.Groups.Any(group => codex.App.Window.Layout.Tabs(group.Id).Any(tab => tab.Path == path)));
        Require(Task.Run(async () => (await codex.App.Host.ReadFileAsync(path)).Text).GetAwaiter().GetResult().Contains("config-link-fixture", StringComparison.Ordinal),
            "the config file reads through the host");
        bool Refused(string file)
        {
            try { Task.Run(async () => await codex.App.Host.ReadFileAsync(file)).GetAwaiter().GetResult(); return false; }
            catch (UnauthorizedAccessException) { return true; }
            catch (Grpc.Core.RpcException error) when (error.StatusCode == Grpc.Core.StatusCode.FailedPrecondition && error.Status.Detail == "The path is outside this workspace.") { return true; }
        }
        File.WriteAllText(Path.Combine(codex.Home, "auth.json"), "{}");
        Require(Refused(Path.Combine(codex.Home, "auth.json")), "an unrelated file in CODEX_HOME stays outside");
        codex.Enable(false);
        Require(Refused(path), "a disabled plugin exposes nothing");
    }

    // codex-context.spec.ts
    private static void Context(string root)
    {
        using var codex = new Codex(root);
        var panel = codex.Panel();
        E2eWorkspace.Until(() => Has(panel, "CodexOffer_project"));
        codex.App.Click(One<Button>(panel, "CodexOffer_project"));
        E2eWorkspace.Until(() => codex.App.Tabs.Any(tab => tab.Path == "AGENTS.md"));
        var agents = Path.Combine(codex.App.Window.WorkspaceRoot, "AGENTS.md");
        E2eWorkspace.Until(() => Has(panel, "CodexInstructions"));
        Require(!Has(panel, "CodexOffer_project") && Has(panel, "CodexOffer_project_override") && Has(panel, "CodexOffer_global"),
            "creating project instructions removes only the fulfilled offer");
        var row = All<Control>(panel, "CodexInstructions").Single();
        Require(One<Border>(row, "CodexInstructionsScope").Tag as string == "project" && Texts(row).Contains("AGENTS.md", StringComparison.Ordinal), "the project's file and scope");
        var source = One<Button>(row, "CodexInstructionsSource");
        Require(Equals(ToolTip.GetTip(source), agents) && Texts(source) == ScopedSetting.AbbreviateHomePath(agents), "the source path, full in its tooltip");
        var size = One<TextBlock>(row, "CodexInstructionsSize").Text;
        Require(Texts(One<Control>(panel, "CodexContextTotal")).Contains(size!, StringComparison.Ordinal), "the summed persistent context");
        var group = codex.App.Window.Layout.State.Groups.First(item => codex.App.Window.Layout.Tabs(item.Id).Any(tab => tab.Path == "AGENTS.md"));
        codex.App.Window.Layout.Close(group.Id, codex.App.Window.Layout.Tabs(group.Id).First(tab => tab.Path == "AGENTS.md").Id);
        E2eWorkspace.Until(() => !codex.App.Tabs.Any(tab => tab.Path == "AGENTS.md"));
        codex.App.Click(One<Button>(panel, "CodexInstructionsSource"));
        E2eWorkspace.Until(() => codex.App.Window.Layout.Selected(codex.App.Center)?.Path == "AGENTS.md");

        codex.App.Click(One<Button>(panel, "CodexOffer_project_override"));
        E2eWorkspace.Until(() => codex.App.Tabs.Any(tab => tab.Path == "AGENTS.override.md") && !Has(panel, "CodexOffer_project_override"));
        Require(All<Control>(panel, "CodexInstructions").Count == 1 && Texts(One<Control>(panel, "CodexInstructions")).Contains("AGENTS.override.md", StringComparison.Ordinal),
            "the override replaces the project's ordinary instruction row");
        codex.App.Click(One<Button>(panel, "CodexOffer_global"));
        E2eWorkspace.Until(() => All<Control>(panel, "CodexInstructions").Count == 2 && !Has(panel, "CodexOffer_global"));
        var global = All<Control>(panel, "CodexInstructions").Single(item => Equals(item.Tag, Path.Combine(codex.Home, "AGENTS.md")));
        Require(One<Border>(global, "CodexInstructionsScope").Tag as string == "user", "global instructions use the user's scope chip");
        codex.App.Click(One<Button>(global, "CodexInstructionsSource"));
        E2eWorkspace.Until(() => codex.App.Window.Layout.Selected(codex.App.Center)?.Path == Path.Combine(codex.Home, "AGENTS.md"));
    }

    private static void Capabilities(string root)
    {
        using var codex = new Codex(root, enable: false);
        var config = Path.Combine(codex.Home, "config.toml");
        File.WriteAllText(config, "[mcp_servers.reader]\ncommand = \"fixture-reader\"\n");
        codex.Enable(true);
        var panel = codex.Panel();
        codex.Surface("capabilities");
        E2eWorkspace.Until(() => Has(panel, "CodexInstallHooks"));
        Control Hooks() => All<Control>(panel, "CodexCapability").Single(row => Equals(row.Tag, "hooks"));
        Control Server() => All<Control>(panel, "CodexCapability").Single(row => Equals(row.Tag, "mcp"));
        Require(Texts(Hooks()).Contains("Inert outside a SharpRail terminal", StringComparison.Ordinal), "uninstalled hooks explain their terminal ownership");
        Require(Texts(Server()).Contains("reader", StringComparison.Ordinal) && Texts(Server()).Contains("fixture-reader", StringComparison.Ordinal) &&
            One<Border>(Server(), "ScopeChip").Tag as string == "user", "configured MCP capability has its target and scope");
        Require(Equals(ToolTip.GetTip(One<Button>(Server(), "SourcePath")), config), "the capability's source path has the full tooltip");
        Require(Texts(panel).Contains("added to every session", StringComparison.Ordinal), "the launcher's own MCP explanation remains visible");
        codex.App.Click(One<Button>(Hooks(), "CodexInstallHooks"));
        E2eWorkspace.Until(() => !Has(panel, "CodexInstallHooks") && Texts(Hooks()).Contains("Installed; waiting", StringComparison.Ordinal));
        Require(One<Border>(Hooks(), "ScopeChip").Tag as string == "user" && Has(panel, "CodexConfigProblem"), "installed hooks retain the trust warning and user scope");
        Require(One<Control>(panel, "CodexConfigProblem").GetType().Name == "CodexProblemNotice", "configuration notices are compiled frames");
        var hookPath = Path.Combine(codex.Home, "hooks.json");
        Require(File.Exists(hookPath) && !File.ReadAllText(config).Contains("hooks.state", StringComparison.Ordinal), "installing hooks does not grant trust");
        File.AppendAllText(config, "\n[hooks.state]\n" + JsonSerializer.Serialize(hookPath + ":fixture") + " = \"trusted\"\n");
        codex.App.Click(One<Button>(panel, "CodexConfigRefresh"));
        E2eWorkspace.Until(() => Texts(Hooks()).Contains("Installed and trusted.", StringComparison.Ordinal) && !Has(panel, "CodexConfigProblem"));
        codex.App.Click(One<Button>(Server(), "SourcePath"));
        E2eWorkspace.Until(() => codex.App.Window.Layout.Selected(codex.App.Center)?.Path == config);
    }

    private static JsonElement Ide(string home, string workspaceRoot)
    {
        var request = Task.Run(() => CodexChecks.IdeRequest(Path.Combine(home, "ipc", "ipc.sock"), workspaceRoot));
        E2eWorkspace.Until(() => request.IsCompleted);
        return JsonSerializer.SerializeToElement(request.GetAwaiter().GetResult());
    }

    private static string IdeResult(string home, string workspaceRoot)
    {
        try { return Ide(home, workspaceRoot).GetProperty("resultType").GetString()!; }
        catch (Exception error) when (error is IOException or System.Net.Sockets.SocketException or OperationCanceledException or AggregateException) { return "unavailable"; }
    }

    // codex-ide.spec.ts. The Scintilla editor reports no selections yet (UI/Plugins/SPEC.md), so the selection enters
    // the editor-event stream the way a plugin's own document reports one.
    private static void IdeContext(string root)
    {
        using var codex = new Codex(root);
        var workspace = codex.App.Window.WorkspaceRoot;
        codex.App.Open("notes.txt", keep: true);
        var tab = codex.App.Tabs.Single(item => item.Path == "notes.txt");
        var editor = new EditorRef(workspace + ":" + tab.Id, workspace, "notes.txt", EditorKind.File, false);
        codex.Loader.Editors.Emit(new EditorSelectionEvent(editor, new EditorSelection(1, 1, 2, 1, "plain-text-fixture\n")));
        JsonElement context = default;
        E2eWorkspace.Until(() =>
        {
            try { context = Ide(codex.Home, workspace); }
            catch (Exception) { return false; }
            return context.GetProperty("resultType").GetString() == "success" &&
                context.GetProperty("result").GetProperty("ideContext").GetProperty("activeFile") is { ValueKind: JsonValueKind.Object } active &&
                active.GetProperty("activeSelectionContent").GetString()!.Contains("plain-text-fixture", StringComparison.Ordinal);
        });
        var ide = context.GetProperty("result").GetProperty("ideContext");
        Require(ide.GetProperty("openTabs")[0].GetProperty("path").GetString() == "notes.txt" && ide.GetProperty("activeFile").GetProperty("path").GetString() == "notes.txt",
            "the open file, relative to the workspace");
        var selection = ide.GetProperty("activeFile").GetProperty("selection");
        Require(selection.GetProperty("start").GetProperty("line").GetInt32() == 0 && selection.GetProperty("start").GetProperty("character").GetInt32() == 0 &&
            selection.GetProperty("end").GetProperty("line").GetInt32() == 1 && selection.GetProperty("end").GetProperty("character").GetInt32() == 0, "zero-based positions");
        Require(IdeResult(codex.Home, workspace + "-other") == "error", "a sibling directory is another workspace");
        Directory.CreateDirectory(Path.Combine(workspace, "nested"));
        Require(Ide(codex.Home, Path.Combine(workspace, "nested")).GetProperty("result").GetProperty("ideContext").GetProperty("activeFile").GetProperty("path").GetString() ==
            Path.Combine("..", "notes.txt"), "paths are rebased onto a nested directory");

        // A second window that is active but shows no part of this workspace leaves the background window to answer.
        using (var peer = codex.App.NewWindow())
        {
            peer.Window.Activate();
            E2eWorkspace.Until(() => ReferenceEquals(codex.App.Workbench.ActiveWindow, peer.Window));
            _ = peer.Window.OpenProjectHomeAsync(codex.App.Window.ProjectRoot);
            E2eWorkspace.Until(() => peer.Window.ShowsWelcome);
            Require(Ide(codex.Home, workspace).GetProperty("result").GetProperty("ideContext").GetProperty("activeFile").GetProperty("path").GetString() == "notes.txt",
                "a background window showing the workspace answers through the unfocused fallback");
            peer.Window.Close();
        }
        codex.App.Window.Activate();

        var group = codex.App.Window.Layout.State.Groups.First(item => codex.App.Window.Layout.Tabs(item.Id).Any(tab => tab.Path == "notes.txt"));
        codex.App.Window.Layout.Close(group.Id, codex.App.Window.Layout.Tabs(group.Id).First(tab => tab.Path == "notes.txt").Id);
        E2eWorkspace.Until(() =>
        {
            try
            {
                var empty = Ide(codex.Home, workspace).GetProperty("result").GetProperty("ideContext");
                return empty.GetProperty("openTabs").GetArrayLength() == 0 && empty.GetProperty("activeFile").ValueKind == JsonValueKind.Null;
            }
            catch (Exception) { return false; }
        });
        codex.Enable(false);
        E2eWorkspace.Until(() => IdeResult(codex.Home, workspace) != "success");
        codex.Enable(true);
        E2eWorkspace.Until(() => IdeResult(codex.Home, workspace) == "success");
    }

    // codex-launcher.spec.ts: SharpRail's added instructions have a setting and an inspectable launch source.
    private static void LaunchInstructions(string root)
    {
        using var codex = new Codex(root, enable: false);
        var argsLog = Path.Combine(root, "args.json");
        var executable = FakeExecutable(Path.Combine(root, "fixture"), new() { ArgsLog = argsLog, ReportSession = true }, out _, out _);
        codex.Settings(new { command = executable, appendSystemPrompt = true });
        codex.Enable(true);
        var panel = codex.Panel();
        Require(!Has(panel, "CodexLaunchFlagChip"), "no launch source before a session");
        E2eWorkspace.Until(() => Has(codex.App.Window, "NewCodex"));
        codex.App.Click(One<Button>(codex.App.Window, "NewCodex"));
        E2eWorkspace.Until(() => Args(argsLog).Length > 0);
        var args = Args(argsLog);
        var content = File.ReadAllText(CodexSystemPrompt.FilePath());
        Require(args.Length == 4 && args[0] == "-c" && args[1].StartsWith("mcp_servers.thinkrail.url=", StringComparison.Ordinal) && args[2] == "-c" &&
            args[3].StartsWith("developer_instructions=", StringComparison.Ordinal) &&
            JsonSerializer.Deserialize<string>(args[3]["developer_instructions=".Length..]) == content, "launch arguments: " + string.Join(" | ", args));
        Require(content.Contains(CodexLaunch.WorktreesVariable, StringComparison.Ordinal), "the instructions name the worktrees folder");
        E2eWorkspace.Until(() => Has(panel, "CodexLaunchFlagChip"));
        var row = panel.GetLogicalDescendants().OfType<Control>().Single(control => control.Name == "CodexInstructions" &&
            control.GetLogicalDescendants().OfType<Control>().Any(child => child.Name == "CodexLaunchFlagChip"));
        Require(row.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text == "developer-instructions.md"), "the chip marks the instructions file");
    }

    private static string[] Args(string log) => File.Exists(log) && File.ReadAllText(log) is { Length: > 0 } text ? JsonSerializer.Deserialize<string[]>(text)! : [];

    // codex-launcher.spec.ts. SharpRail's Start work dialog takes neither a prompt nor a model, so its case is the
    // registered launcher's command, read from the registry.
    private static void Launcher(string root)
    {
        using var codex = new Codex(root, enable: false);
        var argsLog = Path.Combine(root, "fixture", "args.json");
        var models = JsonSerializer.SerializeToElement(new object[]
        {
            new { model = "gpt-6-astra", displayName = "GPT-6 Astra" }, new { model = "gpt-5.6-sol", displayName = "GPT-5.6 Sol" },
            new { model = "gpt-5.6-terra", displayName = "GPT-5.6 Terra" }, new { model = "gpt-5.6-luna", displayName = "GPT-5.6 Luna" }
        });
        var executable = FakeExecutable(Path.Combine(root, "fixture"), new() { Models = models, ArgsLog = argsLog }, out _, out _);
        Require(!Has(codex.App.Window, "NewCodex"), "no launcher while Codex is off");
        codex.Settings(new { command = executable, appendSystemPrompt = false });
        codex.Enable(true);
        var app = codex.App;

        void Launch(Action start, params string[] expected)
        {
            File.Delete(argsLog);
            start();
            try { E2eWorkspace.Until(() => Args(argsLog).Length > 0); }
            catch (InvalidOperationException error)
            {
                var command = app.Workbench.State.Current.PluginSettings.GetValueOrDefault(CodexManifest.Id);
                throw new InvalidOperationException("Codex fixture launch did not report arguments: " + command.GetRawText(), error);
            }
            var args = Args(argsLog);
            Require(args.Length == expected.Length + 2 && args[0] == "-c" && args[1].StartsWith("mcp_servers.thinkrail.url=", StringComparison.Ordinal) &&
                args[2..].SequenceEqual(expected), "launch arguments: " + string.Join(" ", args));
            E2eWorkspace.Settle(100);
            // Closing a live terminal resolves asynchronously and ignores closes asked meanwhile, so ask until none remain.
            E2eWorkspace.Until(() =>
            {
                var open = app.Window.Layout.State.Groups.SelectMany(group => app.Window.Layout.Tabs(group.Id)
                    .Where(tab => tab.Kind == "terminal").Select(tab => (Group: group.Id, Tab: tab.Id))).ToArray();
                foreach (var (group, tab) in open) app.Window.Layout.Close(group, tab);
                return open.Length == 0 && Has(app.Window, "NewCodex");
            });
        }
        void Menu(string preset)
        {
            var button = One<Button>(app.Window, "NewCodex");
            app.Click(button, mouseButton: Avalonia.Input.MouseButton.Right);
            var menu = button.ContextMenu!;
            E2eWorkspace.Until(() => menu.IsOpen);
            app.Click(menu.Items.OfType<MenuItem>().Single(item => item.Name == "CodexLaunch_" + preset), freshGesture: false);
        }

        E2eWorkspace.Until(() => Has(app.Window, "NewCodex"));
        Launch(() => app.Click(One<Button>(app.Window, "NewCodex")));
        E2eWorkspace.Until(() => codex.Loader.Registry.Active.Contains(CodexManifest.Id));
        var launcherButton = One<Button>(app.Window, "NewCodex");
        app.Click(launcherButton, mouseButton: Avalonia.Input.MouseButton.Right);
        E2eWorkspace.Until(() => launcherButton.ContextMenu!.IsOpen);
        var labels = launcherButton.ContextMenu!.Items.OfType<MenuItem>().Where(item => item.Name!.StartsWith("CodexLaunch_model-", StringComparison.Ordinal))
            .Select(item => (string)item.Header!).ToArray();
        Require(labels.SequenceEqual(["GPT-6 Astra", "GPT-5.6 Sol", "GPT-5.6 Terra", "GPT-5.6 Luna"]), "the catalog's models: " + string.Join(",", labels));
        launcherButton.ContextMenu.Close();
        Launch(() => Menu("model-gpt-6-astra"), "--model", "gpt-6-astra");

        // Start work: the launcher's command with a picked model, the saved permissions and the prompt.
        codex.Settings(new { permissionMode = "full-auto" });
        var launcher = codex.Loader.Registry.LauncherList.Single(item => item.Id == CodexManifest.Id);
        var firstIcon = (ContentControl)launcher.CreateIcon!(14, Ui.Accent);
        var secondIcon = (ContentControl)launcher.CreateIcon!(20, Ui.TextBrush);
        Require(!ReferenceEquals(firstIcon, secondIcon) && firstIcon.Width == 14 && secondIcon.Width == 20,
            "the launcher icon factory returns fresh controls at the requested sizes");
        E2eWorkspace.Until(() => firstIcon.Content is SvgAsset && secondIcon.Content is SvgAsset);
        Require(launcher.Models!().Select(model => model.Id).SequenceEqual(["gpt-6-astra", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna"]), "the launcher's models");
        Require(launcher.TerminalCommand(new() { Model = "gpt-5.6-sol", InitialPrompt = "probe the build" }) ==
            $"{executable} {CodexLaunch.McpOverride} --model gpt-5.6-sol --sandbox workspace-write --ask-for-approval on-request 'probe the build'", "Start work with a model");
        Require(launcher.TerminalCommand(new() { InitialPrompt = "probe the build" }) ==
            $"{executable} {CodexLaunch.McpOverride} --sandbox workspace-write --ask-for-approval on-request 'probe the build'", "Start work with the default model");

        // Settings persist the default permissions; a menu permission preset overrides them for one launch.
        codex.Settings(new { permissionMode = "default" });
        app.Window.ShowSettings("Plugins");
        E2eWorkspace.Until(() => app.Window.OwnedWindows.OfType<SettingsWindow>().Any());
        var settings = app.Window.OwnedWindows.OfType<SettingsWindow>().Single();
        E2eWorkspace.Until(() => Has(settings, "Settings_plugin_codex_codex"));
        settings.ShowSection("plugin:codex:codex");
        var ide = One<ToggleButton>(settings, "CodexIdeContextToggle");
        Require(ide.IsChecked == false, "IDE context starts off");
        app.Click(ide, freshGesture: false);
        E2eWorkspace.Until(() => codex.App.Workbench.State.Current.PluginSettings[CodexManifest.Id].TryGetProperty("ideContext", out var value) && value.GetBoolean());
        var section = One<StackPanel>(settings, "SettingsCodex");
        var parent = (ContentControl)section.Parent!;
        parent.Content = null;
        codex.Settings(new { ideContext = false });
        parent.Content = section;
        E2eWorkspace.Until(() => ide.IsChecked == false);
        codex.Settings(new { ideContext = true });
        E2eWorkspace.Until(() => ide.IsChecked == true);
        // An enabled row keeps the quiet control fill; Fluent's accent fill would wash out its text.
        var presenter = ide.GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ContentPresenter>().First(item => item.Name == "PART_ContentPresenter");
        Require(presenter.Background is ISolidColorBrush { Color: var fill } && fill == Ui.Elevated.Color, $"an enabled setting row keeps its fill, not {presenter.Background}");
        var picker = One<ComboBox>(settings, "CodexPermissionMode");
        Require((picker.SelectedItem as ComboBoxItem)?.Content as string == "Use Codex configuration", "the default mode");
        app.Click(picker, freshGesture: false);
        E2eWorkspace.Until(() => picker.IsDropDownOpen);
        app.Click(picker.Items.OfType<ComboBoxItem>().Single(item => item.Name == "CodexPermissionMode_sandbox-danger-full-access"), freshGesture: false);
        E2eWorkspace.Until(() => codex.App.Workbench.State.Current.PluginSettings[CodexManifest.Id].TryGetProperty("permissionMode", out var value) &&
            value.GetString() == "sandbox-danger-full-access");
        Require((picker.SelectedItem as ComboBoxItem)?.Content as string == "Sandbox: danger-full-access", "the picker shows the saved mode");
        settings.Close();
        E2eWorkspace.Until(() => !app.Window.OwnedWindows.Any());
        Launch(() => app.Click(One<Button>(app.Window, "NewCodex")), "-s", "danger-full-access");
        Launch(() => Menu("sandbox-read-only"), "-s", "read-only");
        codex.Settings(new { ideContext = false, permissionMode = "default" });
    }

    private static string ScreenOf(HostTerminal terminal)
    {
        Dispatcher.UIThread.RunJobs();
        return terminal.Text;
    }

    private static string Self()
    {
        var self = Environment.ProcessPath!;
        return Path.GetFileNameWithoutExtension(self) == "dotnet" ? $"'{self}' '{typeof(CodexE2E).Assembly.Location}'" : $"'{self}'";
    }

    // codex-model-picker.spec.ts
    private static void ModelPicker(string root)
    {
        using var codex = new Codex(root, enable: false);
        var models = JsonSerializer.SerializeToElement(new object[]
        {
            new { model = "gpt-6-astra", displayName = "GPT-6 Astra" }, new { model = "gpt-5.6-sol", displayName = "GPT-5.6 Sol" },
            new { model = "gpt-5.6-terra", displayName = "GPT-5.6 Terra" }, new { model = "gpt-5.6-luna", displayName = "GPT-5.6 Luna" }
        });
        var executable = FakeExecutable(Path.Combine(root, "fixture"), new() { Models = models }, out _, out _);
        codex.Settings(new { command = executable });
        codex.Enable(true);
        var terminal = codex.Terminal();
        codex.Hook(terminal, new { hook_event_name = "SessionStart", session_id = "codex-model-test", model = "gpt-5.6-luna" });
        Button Chip() => codex.App.Window.GetLogicalDescendants().OfType<Button>().Single(item => item.Name == "TerminalAgentFact" && Equals(item.Tag, "model"));
        E2eWorkspace.Until(() => codex.App.Window.GetLogicalDescendants().OfType<Button>().Any(item => item.Name == "TerminalAgentFact" && Equals(item.Tag, "model")) &&
            Texts(Chip()).Contains("gpt-5.6-luna", StringComparison.Ordinal));
        Require(Chip().GetLogicalParent()?.GetType().Name == "CodexTerminalRow", "the accessory row is a compiled frame");

        terminal.Run($"stty raw -echo; {Self()} --fake-codex-picker; stty sane");
        E2eWorkspace.Until(() => ScreenOf(terminal).Contains("fake-codex ready", StringComparison.Ordinal));
        var chip = Chip();
        codex.App.Click(chip);
        var menu = chip.ContextMenu!;
        E2eWorkspace.Until(() => menu.IsOpen && menu.Items.OfType<MenuItem>().Count() == 4);
        var items = menu.Items.OfType<MenuItem>().ToArray();
        Require(items.Length == 4 && items.All(item => item.Icon is not null), "four models, each wearing OpenAI's mark: " + string.Join(",", items.Select(item => item.Header)));
        var option = items.Single(item => Equals(item.Header, "GPT-5.6 Sol"));
        E2eWorkspace.Until(() => TopLevel.GetTopLevel(option) is not null);
        codex.App.Click(option, freshGesture: false);

        // Down to Sol, Enter into its efforts, then s on the highlighted one: never Enter there, which saves the default.
        try { E2eWorkspace.Until(() => ScreenOf(terminal).Contains("Model changed to gpt-5.6-sol medium for this session only", StringComparison.Ordinal)); }
        catch (InvalidOperationException)
        {
            File.WriteAllText(Path.Combine(root, "picker-failure.txt"), ScreenOf(terminal) + "\n" + Texts(codex.App.Window));
            throw;
        }
        E2eWorkspace.Until(() => !Has(codex.App.Window, "TerminalDrivingOverlay") && Texts(Chip()).Contains("gpt-5.6-sol", StringComparison.Ordinal));
    }

    // codex-status.spec.ts. SharpRail does not adopt terminal titles, so the title half of the action-required case,
    // and the remount half of the IDE chip case (a terminal's body survives tab switches), have no counterpart.
    private static void Status(string root)
    {
        using var codex = new Codex(root);
        var app = codex.App;

        // IDE chips submit commands separately from text.
        var terminal = codex.Terminal();
        terminal.Run($"stty raw -echo; {Self()} --fake-codex-input; stty sane");
        E2eWorkspace.Until(() => ScreenOf(terminal).Contains("codex command input ready", StringComparison.Ordinal));
        Button Ide() => app.Window.GetLogicalDescendants().OfType<Button>().Single(item => item.Name == "TerminalIdeContextToggle" && item.IsEffectivelyVisible);
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Button>().Any(item => item.Name == "TerminalIdeContextToggle" && item.IsEffectivelyVisible));
        Require(Equals(Ide().Tag, false), "IDE context starts off");
        app.Click(Ide(), twice: true);
        E2eWorkspace.Until(() => ScreenOf(terminal).Contains("SUBMITTED 1: \"/ide on\"", StringComparison.Ordinal));
        E2eWorkspace.Until(() => Equals(Ide().Tag, true));
        E2eWorkspace.Settle(400);
        Require(!ScreenOf(terminal).Contains("SUBMITTED 2", StringComparison.Ordinal), "a click while the command is sent is ignored");
        app.Click(Ide());
        E2eWorkspace.Until(() => ScreenOf(terminal).Contains("SUBMITTED 2: \"/ide off\"", StringComparison.Ordinal));
        E2eWorkspace.Until(() => Equals(Ide().Tag, false));
        terminal.Send("\x03");

        // Action required is an accessible icon on the tab.
        var blocked = codex.Terminal();
        codex.Hook(blocked, new { hook_event_name = "PermissionRequest", session_id = "codex-status-test" });
        Control Badge() => app.Window.GetLogicalDescendants().OfType<Control>().Single(item => item.Name == "TerminalCodexStatus" && item.IsEffectivelyVisible);
        E2eWorkspace.Until(() => app.Window.GetLogicalDescendants().OfType<Control>().Any(item => item.Name == "TerminalCodexStatus" && item.IsEffectivelyVisible && Equals(item.Tag, "blocked")));
        Require(Equals(ToolTip.GetTip(Badge()), "Codex: Action required") && Avalonia.Automation.AutomationProperties.GetName(Badge()) == "Codex: Action required",
            "the badge names the status");
        codex.Hook(blocked, new { hook_event_name = "PostToolUse", session_id = "codex-status-test" });
        E2eWorkspace.Until(() => Equals(Badge().Tag, "running"));

        // A Codex terminal shows its session's token totals and plan from its rollout.
        var rollout = Path.Combine(root, "rollout.jsonl");
        File.WriteAllText(rollout, string.Concat(new object[]
        {
            new { type = "response_item", payload = new { type = "function_call", name = "update_plan",
                arguments = JsonSerializer.Serialize(new { plan = new object[] { new { step = "Read the code", status = "completed" }, new { step = "Fix it", status = "in_progress" } } }) } },
            new { type = "event_msg", payload = new { type = "token_count", info = new { total_token_usage = new { input_tokens = 23_611, cached_input_tokens = 11_008, cache_write_input_tokens = 0, output_tokens = 33 } } } }
        }.Select(line => JsonSerializer.Serialize(line) + "\n")));
        codex.Hook(blocked, new { hook_event_name = "PostToolUse", session_id = "codex-usage-test", transcript_path = rollout });
        Control? Usage() => app.Window.GetLogicalDescendants().OfType<Control>().SingleOrDefault(item => item.Name == "TerminalAgentFact_usage" && item.IsEffectivelyVisible);
        E2eWorkspace.Until(() => Usage() is { } usage && Texts(usage) == "↑13k · ↓33 · R11k");
        Require((ToolTip.GetTip(Usage()!) as string)?.Contains("Tokens this Codex session has spent", StringComparison.Ordinal) == true, "the totals explain themselves");
        var toggle = app.Window.GetLogicalDescendants().OfType<Button>().Single(item => item.Name == "TerminalPlanToggle" && item.IsEffectivelyVisible);
        Require(Texts(toggle) == "1/2", "the plan's progress");
        app.Click(toggle);
        var plan = toggle.GetLogicalAncestors().OfType<Panel>().First().GetLogicalDescendants().OfType<Popup>().Single();
        E2eWorkspace.Until(() => plan.IsOpen);
        var steps = plan.Child!.GetLogicalDescendants().OfType<Control>().Where(item => item.Name == "TerminalPlanItem").Select(Texts).ToArray();
        Require(steps.SequenceEqual(["Read the code", "Fix it"]), "the plan's steps: " + string.Join(",", steps));
    }
}