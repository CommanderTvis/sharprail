using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Controls;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Core.Language;
using SharpRail.UI.Panels;

namespace SharpRail.Checks;

/// <summary>
/// Language-server limits with fake servers: idle stop and transparent restart, the per-kind cap evicting the least recently used idle
/// server, queueing when every server is busy, the memory budget, memory pressure, project close, orphan cleanup, death with the host,
/// the settings round trip and the Settings list.
/// </summary>
internal static class LanguageLimitChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static async Task Until(Func<Task<bool>> condition, string what, int seconds = 30)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(30);
        }
    }

    private static string Workspace(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "code.ts"), "const target = 1;\n");
        return path;
    }

    private static async Task<HostStateStore> FakeHost(string root)
    {
        var state = new HostStateStore(Path.Combine(root, "state"));
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        await state.ChangeAsync([HostStateChange.Setting("language-server:typescript", JsonSerializer.Serialize(new LanguageServerOptions(Command: executable, Arguments: arguments), JsonSerializerOptions.Web))]);
        return state;
    }

    internal static async Task Run(string fixture)
    {
        var root = Path.Combine(fixture, "language-limits");
        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        var state = await FakeHost(root);
        try
        {
            var languages = state.Languages;
            long clock = 1_000_000;
            languages.Ticks = () => Interlocked.Read(ref clock);
            languages.Pressure = () => false;
            languages.OnDemandWait = TimeSpan.FromSeconds(5);
            void Advance(TimeSpan by) => Interlocked.Add(ref clock, (long)by.TotalMilliseconds);
            await state.ChangeAsync([HostStateChange.Setting("language-limits", """{"idleMinutes":1,"memoryBudgetMb":1048576,"caps":{"typescript":2}}""")]);
            Require(state.Current.Settings.LanguageLimits == """{"idleMinutes":1,"memoryBudgetMb":1048576,"caps":{"typescript":2}}""", "The limits setting was not stored normalized: " + state.Current.Settings.LanguageLimits);
            try { await state.ChangeAsync([HostStateChange.Setting("language-limits", """{"idleMinutes":-1}""")]); Require(false, "A negative idle timeout was accepted."); }
            catch (ArgumentException) { }
            try { await state.ChangeAsync([HostStateChange.Setting("language-limits", "not json")]); Require(false, "Invalid limits JSON was accepted."); }
            catch (ArgumentException) { }

            async Task<LanguageResult> Status(string workspace) => await languages.ExecuteAsync(new(workspace, "code.ts", "status"));
            async Task<string> Row(string workspace) => (await languages.ExecuteAsync(new(workspace, Operation: "servers"))).Servers.Single(row => row.Id == "typescript").State;
            async Task Ready(string workspace) { await Status(workspace); await Until(async () => await Row(workspace) == "ready", "typescript ready in " + Path.GetFileName(workspace)); }
            LanguageServerState[] Running() => [.. languages.ExecuteAsync(new("", Operation: "running")).AsTask().GetAwaiter().GetResult().Servers];
            bool Runs(string workspace) => Running().Any(row => row.Workspace == workspace);

            // Idle: a server unused past the timeout stops, and the next agent call starts it again and answers.
            var a = Workspace(root, "a");
            await Ready(a);
            var registry = Path.Combine(root, "state", "language", "processes");
            Require(OperatingSystem.IsWindows() || Directory.EnumerateFiles(registry, "*.pids").Any(file => File.ReadAllText(file).Length > 0), "The started server was not recorded for orphan cleanup.");
            Advance(TimeSpan.FromSeconds(30));
            await languages.EnforceAsync();
            Require(Runs(a), "A server used 30 s ago was stopped by a 1 min idle timeout.");
            Advance(TimeSpan.FromSeconds(31));
            await languages.EnforceAsync();
            Require(!Runs(a), "A server idle past the timeout kept running.");
            var exit = (await languages.ExecuteAsync(new(a, Operation: "servers"))).Servers.Single(row => row.Id == "typescript").Exit;
            Require(exit is { Unexpected: false } && exit.Reason.Contains("unused for 1 min", StringComparison.Ordinal), "The idle stop has no reason: " + exit?.Reason);
            var again = await languages.ExecuteForAgentAsync(new(a, "code.ts", "symbols"), default);
            Require(again.State is not ("stopped" or "initializing" or "error"), "An agent call after an idle stop did not restart the server transparently: " + again.State + " " + again.Detail);
            Console.WriteLine("PASS language limits: idle stop with its reason, transparent restart on the next agent call");

            // Cap: a third server stops the least recently used idle one of its kind.
            var b = Workspace(root, "b");
            Advance(TimeSpan.FromSeconds(1)); await Ready(b);
            var c = Workspace(root, "c");
            Advance(TimeSpan.FromSeconds(1)); await Ready(c);
            Require(!Runs(a) && Runs(b) && Runs(c), "The cap did not evict the least recently used server: " + string.Join(", ", Running().Select(row => Path.GetFileName(row.Workspace))));
            Require(Running().Count(row => row.Id == "typescript") == 2, "More typescript servers run than the cap.");

            // An open editor protects a server even when it is the least recently used.
            await languages.ExecuteAsync(new(b, "code.ts", "inspect", Text: "const target = 2;\n", DocumentId: "editor-b", Version: 1));
            Advance(TimeSpan.FromSeconds(1));
            await languages.ExecuteAsync(new(c, "code.ts", "inspect"));
            Advance(TimeSpan.FromSeconds(1));
            var d = Workspace(root, "d");
            await Ready(d);
            Require(Runs(b) && !Runs(c) && Runs(d), "The cap evicted a server with an open editor: " + string.Join(", ", Running().Select(row => Path.GetFileName(row.Workspace))));

            // Every server busy (editors open): the next start waits for a slot instead of exceeding the cap.
            await languages.ExecuteAsync(new(d, "code.ts", "inspect", Text: "const target = 3;\n", DocumentId: "editor-d", Version: 1));
            var e = Workspace(root, "e");
            await Status(e);
            await Until(async () => (await Status(e)).Detail == LanguageService.WaitingForSlot, "the queued start to report waiting for a slot");
            Require(Running().Count(row => row.Id == "typescript" && row.State == "ready") == 2, "A queued start exceeded the cap.");
            var waiting = await languages.ExecuteForAgentAsync(new(e, "code.ts", "symbols"), default);
            Require(waiting.State == "initializing" && waiting.Detail.Contains("free language server slot", StringComparison.Ordinal), "An agent call on a queued server did not report the slot wait: " + waiting.State + " " + waiting.Detail);
            await languages.ExecuteAsync(new(d, "code.ts", "close", DocumentId: "editor-d"));
            await Until(async () => await Row(e) == "ready", "the queued server to start once a slot freed");
            Require(Runs(b) && !Runs(d) && Runs(e), "Closing the editor did not free its server's slot for the queued start.");
            Console.WriteLine("PASS language limits: cap evicts the least recently used idle server, never one with an open editor, and queues when all are busy");

            // Memory budget: over budget, idle servers stop least recently used first; servers with an editor stay.
            // A pass already in flight (the periodic tick) read the old limits; an explicit pass after a settings change must wait for it and
            // run on its own, not return early and leave the over-budget servers running.
            using var inFlight = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            languages.MemoryOf = (_, _, _) => { inFlight.Set(); release.Wait(); return 1L << 30; };
            var stale = Task.Run(languages.EnforceAsync);
            Require(inFlight.Wait(TimeSpan.FromSeconds(30)), "The enforcement pass did not start.");
            await state.ChangeAsync([HostStateChange.Setting("language-limits", """{"idleMinutes":0,"memoryBudgetMb":1024,"caps":{"typescript":8}}""")]);
            var explicitPass = Task.Run(languages.EnforceAsync);
            release.Set();
            await Task.WhenAll(stale, explicitPass);
            Require(Runs(b) && !Runs(e), "Over budget, the idle server was not stopped (or the editor's was).");
            var f = Workspace(root, "f");
            await Status(f);
            await Until(async () => (await Status(f)).Detail == LanguageService.WaitingForSlot, "a start over budget with nothing to stop to wait");
            await languages.ExecuteAsync(new(b, "code.ts", "close", DocumentId: "editor-b"));
            await Until(async () => await Row(f) == "ready", "the start over budget to proceed once the editor closed");
            Require(!Runs(b) && Runs(f), "The budget did not stop the least recently used idle server for the waiting start.");
            var summary = JsonNode.Parse((await languages.ExecuteAsync(new("", Operation: "running"))).Configuration)!;
            Require(summary["budgetBytes"]!.GetValue<long>() == 1024L << 20 && summary["totalBytes"]!.GetValue<long>() == 1L << 30, "The running listing misreports the budget: " + summary.ToJsonString());
            Console.WriteLine("PASS language limits: memory budget stops least recently used idle servers and queues a start when nothing can stop");

            // Pressure: shrink to the servers used within the grace period.
            languages.MemoryOf = (_, _, sampled) => sampled;
            await state.ChangeAsync([HostStateChange.Setting("language-limits", """{"idleMinutes":0,"memoryBudgetMb":1048576,"caps":{"typescript":8}}""")]);
            var g = Workspace(root, "g");
            await Ready(g);
            Advance(LanguageService.PressureGrace + TimeSpan.FromSeconds(1));
            await languages.ExecuteAsync(new(g, "code.ts", "inspect"));
            languages.Pressure = () => true;
            await languages.EnforceAsync();
            languages.Pressure = () => false;
            Require(!Runs(f) && Runs(g), "Memory pressure did not shrink to the recently used servers.");

            // Settings › Stop releases a server; its next request starts it again without a remembered stop.
            var released = await languages.ExecuteAsync(new(g, Operation: "release", Server: "typescript"));
            Require(released.State == "stopped" && !Runs(g), "Release did not stop the server.");
            await Ready(g);
            Console.WriteLine("PASS language limits: memory pressure keeps only active servers; Settings stop restarts on demand");

            // Closing a project stops its servers now.
            var project = Workspace(root, "project");
            await state.ChangeAsync([HostStateChange.OpenProject(project)]);
            await Ready(project);
            await state.ChangeAsync([HostStateChange.CloseProject(project)]);
            Require(!Runs(project), "Closing a project left its language server running.");
            Console.WriteLine("PASS language limits: closing a project stops its servers");
        }
        finally
        {
            await state.StopLanguagesAsync();
            Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled);
        }
        await Orphans(root);
    }

    private static Process Sleeper()
    {
        var start = new ProcessStartInfo("/bin/sleep") { UseShellExecute = false };
        start.ArgumentList.Add("600");
        return Process.Start(start)!;
    }

    private static string Line(Process process) => string.Join('\t', process.Id.ToString(CultureInfo.InvariantCulture),
        new DateTimeOffset(process.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), process.ProcessName);

    /// <summary>A dead host's servers are killed at the next start, a live host's are not, and a host's watchdog kills its servers when it dies.</summary>
    private static async Task Orphans(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        var directory = Path.Combine(root, "orphans", "language", "processes");
        Directory.CreateDirectory(directory);
        using var deadHost = Sleeper();
        var deadStart = new DateTimeOffset(deadHost.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds();
        deadHost.Kill(); await deadHost.WaitForExitAsync();
        using var orphan = Sleeper();
        using var reused = Sleeper();
        await File.WriteAllLinesAsync(Path.Combine(directory, $"{deadHost.Id}-{deadStart}.pids"),
            [Line(orphan), string.Join('\t', reused.Id.ToString(CultureInfo.InvariantCulture), "1000", reused.ProcessName)]);
        using var liveHost = Sleeper();
        using var liveServer = Sleeper();
        await File.WriteAllLinesAsync(Path.Combine(directory, $"{liveHost.Id}-{new DateTimeOffset(liveHost.StartTime.ToUniversalTime()).ToUnixTimeMilliseconds()}.pids"), [Line(liveServer)]);
        try
        {
            // A new host sweeps on start, off the caller's thread.
            var state = new HostStateStore(Path.Combine(root, "orphans"));
            _ = state.Languages;
            await Until(() => Task.FromResult(orphan.HasExited), "the orphaned server of a dead host to be killed at start");
            Require(!reused.HasExited, "A process whose start time differs from the record (a reused id) was killed.");
            Require(!liveServer.HasExited, "A server of a host that is still running was killed.");
            await state.StopLanguagesAsync();
            Console.WriteLine("PASS language limits: start kills a dead host's matching servers only");

            // Death with the host: the watchdog kills the recorded servers when the host process ends, however it ends.
            var file = Path.Combine(directory, $"{liveHost.Id}-watch.pids");
            await File.WriteAllLinesAsync(file, [Line(liveServer)]);
            using var watchdog = LanguageProcesses.Watch(file, liveHost.Id);
            Require(watchdog is not null, "No watchdog started.");
            liveHost.Kill();
            await Until(() => Task.FromResult(liveServer.HasExited), "the server to die with its host", 15);
            Console.WriteLine("PASS language limits: servers die with their host process");
        }
        finally
        {
            foreach (var process in new[] { orphan, reused, liveHost, liveServer })
                try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
        }
    }

    /// <summary>Settings › Code Intelligence lists running servers with memory and saves the limits through host state.</summary>
    internal static void Ui(string fixture)
    {
        var root = Path.Combine(fixture, "language-limits-ui");
        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        var state = Task.Run(() => FakeHost(root)).GetAwaiter().GetResult();
        try
        {
            var workspace = Workspace(root, "ui");
            Task.Run(async () =>
            {
                await state.Languages.ExecuteAsync(new(workspace, "code.ts", "status"));
                await Until(async () => (await state.Languages.ExecuteAsync(new(workspace, Operation: "servers"))).Servers.Single(row => row.Id == "typescript").State == "ready", "the UI check's server");
            }).GetAwaiter().GetResult();
            state.Languages.MemoryOf = (_, _, _) => 300L << 20;
            IProjectServices host = new LocalProjectAdapter(new ProjectServices(workspace, state));
            var view = new LanguageLimitsView(host, workspace, () => state.Current.Settings.LanguageLimits,
                json => state.ChangeAsync([HostStateChange.Setting("language-limits", json)]).AsTask());
            var window = new Window { Width = 720, Height = 600, Content = new ScrollViewer { Content = view } };
            window.Show();
            try
            {
                var rows = view.FindControl<StackPanel>("RunningServers")!;
                E2E.E2eWorkspace.Until(() => rows.Children.Count == 1);
                var text = string.Join(" ", ((Grid)rows.Children[0]).Children.OfType<TextBlock>().Select(block => block.Text));
                Require(text.Contains("ui", StringComparison.Ordinal) && text.Contains("300 MiB", StringComparison.Ordinal), "The running row lacks its workspace or memory: " + text);
                Require(view.FindControl<TextBlock>("LimitsSummary")!.Text!.Contains("budget", StringComparison.Ordinal), "The summary lacks the budget.");
                Require(view.FindControl<TextBox>("LimitsCSharp")!.Text == "4" && view.FindControl<TextBox>("LimitsIdle")!.Text == "10", "The defaults are not shown.");
                view.FindControl<TextBox>("LimitsIdle")!.Text = "25";
                view.FindControl<TextBox>("LimitsCSharp")!.Text = "3";
                view.FindControl<Button>("LimitsSave")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                E2E.E2eWorkspace.Until(() => state.Current.Settings.LanguageLimits.Contains("\"idleMinutes\":25", StringComparison.Ordinal));
                Require(state.Current.Settings.LanguageLimits.Contains("\"csharp\":3", StringComparison.Ordinal), "The C# cap was not saved: " + state.Current.Settings.LanguageLimits);
                var stop = ((Grid)rows.Children[0]).Children.OfType<Button>().Single();
                stop.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                E2E.E2eWorkspace.Until(() => rows.Children.Count == 0);
                Console.WriteLine("PASS language limits UI: running servers with workspace and memory, limits saved through host state, Stop");
            }
            finally { window.Close(); }
        }
        finally
        {
            Task.Run(async () => await state.StopLanguagesAsync()).GetAwaiter().GetResult();
            Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled);
        }
    }
}