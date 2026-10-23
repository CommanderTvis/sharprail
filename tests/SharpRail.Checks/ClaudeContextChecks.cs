using System.Text.Json;

using Avalonia.Controls;

using SharpRail.Checks.E2E;
using SharpRail.Plugins.ClaudeCode;
using SharpRail.Plugins.ClaudeCode.Host;
using SharpRail.Plugins.ClaudeCode.UI;

namespace SharpRail.Checks;

/// <summary>The Context surface's <c>/context</c> baseline: the report parser, the runner, the appended prompt difference, its triggers, live usage and the table rows.</summary>
internal static class ClaudeContextChecks
{
    // A trimmed capture of `claude -p /context --output-format json`'s result, with the paths made up.
    public const string Report = """
        ## Context Usage

        **Model:** claude-opus-5-5
        **Tokens:** 16.6k / 1m (2%)

        ### Estimated usage by category

        | Category | Tokens | Percentage |
        |----------|--------|------------|
        | System prompt | 2.5k | 0.3% |
        | System tools | 4.3k | 0.4% |
        | MCP tools | 671 | 0.1% |
        | MCP server instructions | 717 | 0.1% |
        | MCP tools (deferred) | 1.4k | 0.1% |
        | System tools (deferred) | 13.5k | 1.3% |
        | Memory files | 2.2k | 0.2% |
        | Skills | 6.2k | 0.6% |
        | Messages | 10 | 0.0% |
        | Free space | 950.4k | 95.0% |
        | Autocompact buffer | 33k | 3.3% |

        ### MCP Tools

        | Tool | Server | Tokens |
        |------|--------|--------|
        | mcp__docs__create | docs | 256 |
        | mcp__docs__read | docs | 324 |
        | mcp__notes__find | notes | 91 |

        ### Memory Files

        | Type | Path | Tokens |
        |------|------|--------|
        | User | /home/user/.claude/CLAUDE.md | 2.2k |

        ### Skills

        | Skill | Source | Tokens |
        |-------|--------|--------|
        | example:review | Plugin (example) | ~150 |
        | tidy | Built-in | < 20 |
        | dataviz | Built-in | ~480 |
        """;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static ClaudeContextBreakdown Fixture() => ContextReport.Parse(Report)!;

    public static async Task Host(string directory)
    {
        Directory.CreateDirectory(directory);
        var json = JsonSerializer.Serialize(new { type = "result", result = Report, total_cost_usd = 0 });
        var parsed = ContextReport.ParseJson(json);
        Require(parsed is { Model: "claude-opus-5-5", Used: 16_600, Window: 1_000_000 } && parsed.Categories.Count == 11 &&
            parsed.Categories[3] == new ClaudeContextCategory("MCP server instructions", 717, 0.1) && parsed.Categories[^1].Tokens == 33_000,
            "The /context report's header and category table parse in Claude Code's order.");
        Require(parsed!.McpTools.Count == 3 && parsed.McpTools[0] == new ClaudeContextItem("mcp__docs__create", "docs", 256) &&
            parsed.MemoryFiles.Single() == new ClaudeContextItem("/home/user/.claude/CLAUDE.md", "User", 2_200) &&
            parsed.Skills.Select(skill => skill.Tokens).SequenceEqual([150L, 20, 480]),
            "MCP tools, memory files and skills parse with approximate counts (~, <) read as numbers.");
        Require(ContextReport.ParseJson("{\"result\":\"Unknown command\"}") is null && ContextReport.ParseJson("not json") is null,
            "Output without a context report is refused, not misread.");

        // Memory paths are matched to the pane's rows: ~ expanded, and a link matched through its target.
        var home = Directory.CreateDirectory(Path.Combine(directory, "home")).FullName;
        var real = Path.Combine(home, "real.md");
        File.WriteAllText(real, "x");
        var link = Path.Combine(home, "CLAUDE.md");
        File.CreateSymbolicLink(link, real);
        var memory = parsed with { MemoryFiles = [new("~/x.md", "User", 1), new(real, "Project", 2), new("rules/a.md", "Project", 3)] };
        var previousHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("HOME", home);
        try
        {
            var normal = ContextReport.Normalise(memory, home, [link]).MemoryFiles.Select(file => file.Name).ToArray();
            Require(normal.SequenceEqual([Path.Combine(home, "x.md"), link, Path.Combine(home, "rules", "a.md")]),
                "Memory file paths are expanded, made absolute and matched through links to the pane's rows. Got: " + string.Join(", ", normal));
        }
        finally { Environment.SetEnvironmentVariable("HOME", previousHome); }

        var flags = ContextLaunch.Flags("claude --append-system-prompt-file " + Path.Combine(directory, "missing.md") +
            " --plugin-dir /p --add-dir /s --model opus --effort=high --resume abc --strict-mcp-config 'do it'", directory);
        Require(flags.SequenceEqual(["--plugin-dir", "/p", "--add-dir", "/s", "--model", "opus", "--effort", "high", "--strict-mcp-config"]),
            "Only context-shaping launch flags carry over; resume and the prompt are dropped. Got: " + string.Join(' ', flags));

        // The fake CLI is a POSIX shell script.
        if (!OperatingSystem.IsWindows()) await Runner(directory, json);
        LiveUsage(directory);
        Console.WriteLine("PASS Claude Code context: /context parsing, background runner, appended prompt difference, debounced triggers and live usage");
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static async Task Runner(string directory, string json)
    {
        // A fake claude that logs its directory and arguments, sleeps when asked and answers with the fixture.
        var bin = Path.Combine(directory, "fake-claude");
        var log = Path.Combine(directory, "fake-claude.log");
        var answer = Path.Combine(directory, "answer.json");
        File.WriteAllText(answer, json);
        File.WriteAllText(bin, $"#!/bin/sh\necho \"$(pwd -P)|$*|$FAKE_MARK\" >> '{log}'\n[ -n \"$FAKE_SLEEP\" ] && sleep \"$FAKE_SLEEP\"\ncat '{answer}'\n");
        File.SetUnixFileMode(bin, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var cwd = Directory.CreateDirectory(Path.Combine(directory, "cwd")).FullName;
        var input = Path.Combine(cwd, "CLAUDE.md");
        File.WriteAllText(input, "one\n");
        var finished = 0;
        using var runner = new ContextUsageRunner((spec, timeout, token) => Bounded.RunAsync(spec.Argv, timeout, spec.Cwd, spec.Environment, token),
            TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(300), _ => Interlocked.Increment(ref finished));
        ContextRunSpec Spec(string? sleep = null) => new("workspace", bin, cwd, ["--model", "opus"],
            new Dictionary<string, string> { ["FAKE_MARK"] = "env", ["FAKE_SLEEP"] = sleep ?? "" }, [input]);

        var first = runner.Get(Spec(), refresh: false);
        var second = runner.Get(Spec(), refresh: true);
        Require(first.Running && second.Running && runner.Runs == 1, "A baseline measures in the background, one run at a time per directory and flags.");
        await runner.IdleAsync();
        var lines = File.ReadAllLines(log);
        Require(lines.Length == 1 && lines[0].EndsWith("/cwd|-p /context --output-format json --no-session-persistence --model opus|env", StringComparison.Ordinal),
            "The run happens in the tab's directory with its flags and environment, without session persistence. Got: " + string.Join('\n', lines));
        var cached = runner.Get(Spec(), refresh: false);
        Require(!cached.Running && cached.Breakdown?.Window == 1_000_000 && cached.MeasuredAt is not null && runner.Runs == 1 && finished == 1,
            "An unchanged baseline is served from the cache and announced once.");

        // A burst of changes to an input runs once, after the burst is quiet.
        for (var index = 0; index < 5; index++)
        {
            File.WriteAllText(input, "changed " + index + new string('x', index) + "\n");
            runner.Changed([input]);
            await Task.Delay(50);
        }
        runner.Changed([Path.Combine(cwd, "unrelated.txt")]);
        await Task.Delay(800);
        await runner.IdleAsync();
        Require(runner.Runs == 2 && File.ReadAllLines(log).Length == 2, "A debounced burst of input changes re-runs the baseline exactly once.");
        runner.Changed([input]);
        await Task.Delay(800);
        await runner.IdleAsync();
        Require(runner.Runs == 2, "A change notice whose file did not actually change starts no run.");

        // An appended prompt is measured as the System prompt difference against the same run without it; editing the
        // appended file re-runs only the run that reads it.
        var appendix = Path.Combine(directory, "appendix.md");
        File.WriteAllText(appendix, "appended\n");
        File.WriteAllText(Path.Combine(directory, "appended.json"), json.Replace("| System prompt | 2.5k |", "| System prompt | 3.8k |", StringComparison.Ordinal));
        File.WriteAllText(bin, $"#!/bin/sh\necho \"$(pwd -P)|$*|$FAKE_MARK\" >> '{log}'\n[ -n \"$FAKE_SLEEP\" ] && sleep \"$FAKE_SLEEP\"\n" +
            $"case \"$*\" in *--append-system-prompt-file*) cat '{Path.Combine(directory, "appended.json")}' ;; *) cat '{answer}' ;; esac\n");
        var withAppendix = Spec() with { Flags = ["--append-system-prompt-file", appendix, "--model", "appended"], Inputs = [input, appendix] };
        var before = File.ReadAllLines(log).Length;
        runner.Get(withAppendix, refresh: false);
        await runner.IdleAsync();
        var measuredAppendix = runner.Get(withAppendix, refresh: false).Breakdown;
        var pair = File.ReadAllLines(log)[before..];
        Require(measuredAppendix?.AppendedPromptTokens == 1_300 && pair.Length == 2 &&
            pair.Count(line => line.Contains("--append-system-prompt-file", StringComparison.Ordinal)) == 1,
            "The appended prompt costs the System prompt difference of two runs, one without the file. Got: " + measuredAppendix?.AppendedPromptTokens + "\n" + string.Join('\n', pair));
        File.WriteAllText(appendix, "appended, and longer now\n");
        runner.Changed([appendix]);
        await Task.Delay(800);
        await runner.IdleAsync();
        Require(File.ReadAllLines(log).Length == before + 3, "Editing the appended file re-runs only the run with it; the bare run stays cached.");

        var slow = Spec("10") with { Flags = ["--model", "slow"] };
        runner.Get(slow, refresh: false);
        await runner.IdleAsync();
        var timedOut = runner.Get(slow, refresh: false);
        Require(timedOut is { Running: false, Breakdown: null } && timedOut.Error?.Contains("did not finish", StringComparison.Ordinal) == true,
            "A run past its deadline is killed and reported as an error.");
        var cancelled = Spec("10") with { Flags = ["--model", "cancel"] };
        runner.Get(cancelled, refresh: false);
        runner.Dispose();
        var started = DateTime.UtcNow;
        await runner.IdleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Require(DateTime.UtcNow - started < TimeSpan.FromSeconds(3), "Disposing the runner cancels a run under way.");

    }

    private static void LiveUsage(string directory)
    {
        // Live usage: the main transcript's latest assistant message, input and cache together.
        var transcript = Path.Combine(directory, "live.jsonl");
        static string Turn(string id, int input, int read, int write) => JsonSerializer.Serialize(new
        { type = "assistant", message = new { id, usage = new { input_tokens = input, output_tokens = 1, cache_read_input_tokens = read, cache_creation_input_tokens = write } } }) + "\n";
        File.WriteAllText(transcript, Turn("a", 5, 1000, 200) + Turn("b", 7, 1200, 30));
        var usage = new TranscriptUsage();
        Require(usage.Read(transcript).Context == 1237, "Live context is the latest message's input plus cache reads and writes.");
        File.AppendAllText(transcript, Turn("c", 3, 1300, 0));
        Require(usage.Read(transcript).Context == 1303, "Live context follows an appended turn with a tail read.");
    }

    public static void Ui()
    {
        // The baseline's rows: Claude's own prompt less the appendix, built-in tools, MCP tools by server, server
        // instructions and skills; memory files, deferred tools, the message, free space and the reserve are not rows.
        var rows = ClaudeConfigPanel.BaselineRows(Fixture(), claudePrompt: 1_300);
        Require(rows.Select(row => row.Key).SequenceEqual(["system-prompt", "system-tools", "mcp-tools", "mcp-instructions", "skills"]) &&
            rows[0].Tokens == 1_300 && rows.Sum(row => row.Tokens) == 1_300 + 4_300 + 671 + 717 + 6_200,
            "The baseline rows are the categories a fresh session pays for, the system prompt without the appended file. Got: " +
            string.Join(", ", rows.Select(row => row.Key + "=" + row.Tokens)));
        var mcp = rows.Single(row => row.Key == "mcp-tools");
        Require(mcp.Children.Select(server => server.Label).SequenceEqual(["docs", "notes"]) && mcp.Children[0].Tokens == 580 &&
            mcp.Children[0].Children.Select(tool => tool.Label).SequenceEqual(["read", "create"]),
            "MCP tools open into servers, heaviest first with subtotals, and each server into its tools without the prefix.");
        Require(rows.Single(row => row.Key == "skills").Children.Select(skill => skill.Label).SequenceEqual(["dataviz", "example:review", "tidy"]),
            "Skills open into each skill, heaviest first.");

        var view = new ClaudeContextUsageView();
        var window = new Window { Width = 520, Height = 200, Content = view };
        window.Show();
        try
        {
            var at = new DateTimeOffset(2026, 1, 1, 8, 50, 0, TimeSpan.Zero);
            view.Show(new(false) { Breakdown = Fixture(), MeasuredAt = at.ToString("O") });
            E2eWorkspace.Settle();
            Require(view.FindControl<TextBlock>("ClaudeContextMessage")!.Text == $"measured {at.ToLocalTime():HH:mm}" &&
                !view.FindControl<Button>("ClaudeContextRetry")!.IsVisible, "A measured baseline says only when it was measured.");
            view.Show(new(false) { Error = "boom" });
            Require(view.FindControl<Button>("ClaudeContextRetry")!.IsVisible &&
                view.FindControl<TextBlock>("ClaudeContextMessage")!.Text!.Contains("boom", StringComparison.Ordinal),
                "A failed measurement shows a small inline error with a retry.");
        }
        finally { window.Close(); }
        Console.WriteLine("PASS Claude Code context UI: baseline rows, MCP and skill parts, freshness note, inline error");
    }
}