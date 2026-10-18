using SharpRail.Plugins.Api;
using SharpRail.Plugins.Codex;

namespace SharpRail.Checks;

// The agent launch seam in isolation: ordering, replacement, quoting and the custom-command guard of
// AgentLaunchComposition, and its use by the Codex launch line.
internal static class AgentLaunchContributionChecks
{
    private static void Equal(string actual, string expected, string message)
    {
        if (actual != expected) throw new InvalidOperationException($"{message}: expected [{expected}] but got [{actual}]");
    }

    private static AgentLaunchContribution Wrap(params string[] words) => new() { Wrapper = words };
    private static AgentLaunchContribution Replace(params string[] words) => new() { Wrapper = words, ReplacesExecutable = true };

    internal static void Run()
    {
        Equal(AgentLaunchComposition.Apply("claude --model opus", "claude", [], false), "claude --model opus", "no contributions leaves the line alone");
        Equal(AgentLaunchComposition.Apply("claude --model opus", "claude", [Replace("central", "run", "claude-code", "--")]), "central run claude-code -- --model opus", "a replacer stands in for the executable");
        Equal(AgentLaunchComposition.Apply("/usr/bin/claude -x", "claude", [Wrap("time")]), "time /usr/bin/claude -x", "a wrapper prefixes the executable and keeps its path");
        Equal(AgentLaunchComposition.Apply("'/My Apps/claude' -x", "claude", [Replace("r", "--")]), "r -- -x", "a quoted executable path is recognised");
        Equal(AgentLaunchComposition.Apply("npx claude", "claude", [Replace("r", "--")]), "npx claude", "a custom command line is never rewritten");
        Equal(AgentLaunchComposition.Apply("claude.exe a", "claude", [Wrap("w")], true), "w claude.exe a", "a .exe suffix still names the agent");

        // Ordering: non-replacing wrappers outermost first, then the first replacer, then arguments in order, first variable wins.
        var first = new AgentLaunchContribution { Wrapper = ["outer"], Arguments = ["--a"], Environment = new Dictionary<string, string> { ["X"] = "1", ["SHARPRAIL_X"] = "no", ["bad name"] = "no" } };
        var second = new AgentLaunchContribution { Wrapper = ["inner"], Arguments = ["--b"], Environment = new Dictionary<string, string> { ["X"] = "2", ["Y"] = "two words" } };
        Equal(AgentLaunchComposition.Apply("codex go", "codex", [first, Replace("run", "--"), second, Replace("ignored", "--")]),
            "X=1 Y='two words' outer inner run -- --a --b go", "wrappers, then the first replacer, then arguments; first variable wins; reserved names dropped");

        // Quoting: words are literal on POSIX, and unsafe words on Windows skip the contribution.
        Equal(AgentLaunchComposition.Apply("claude", "claude", [new() { Wrapper = ["a b", "it's", "$(x);`y`"], Arguments = ["--p", "a\nb"] }]), "claude", "a control character skips the contribution");
        Equal(AgentLaunchComposition.Apply("claude", "claude", [new() { Wrapper = ["a b", "it's", "$(x);`y`"] }]), "'a b' 'it'\\''s' '$(x);`y`' claude", "POSIX words are single-quoted");
        Equal(AgentLaunchComposition.Apply("claude", "claude", [new() { Wrapper = ["C:\\Program Files\\c.exe"], Environment = new Dictionary<string, string> { ["X"] = "1" } }], true), "\"C:\\Program Files\\c.exe\" claude", "Windows words are double-quoted and variables dropped");
        Equal(AgentLaunchComposition.Apply("claude", "claude", [new() { Wrapper = ["a%PATH%"] }], true), "claude", "a Windows word with expansion characters skips the contribution");
        Equal(AgentLaunchComposition.Apply("claude", "claude", [new() { Wrapper = [""] }]), "claude", "an empty word skips the contribution");

        // The Codex line applies contributions to its executable only, under its own prefixes.
        Equal(CodexLaunch.Line("codex", new(false, false) { Resume = true, ResumeSessionId = "abc", Contributions = [Replace("central", "run", "codex", "--")] }), "central run codex -- resume abc", "the Codex line routes through a replacer");
        Equal(CodexLaunch.Line("codex", new(false, false) { UiLaunch = true, Contributions = [Wrap("w")] }), "SHARPRAIL_UI_LAUNCH=codex w codex", "SharpRail's own prefix stays outermost");
        Equal(CodexLaunch.Line("my-codex --x", new(false, false) { Contributions = [Replace("central", "run", "codex", "--")] }), "my-codex --x", "a custom Codex command is not wrapped");
        Console.WriteLine("PASS agent launch contributions: ordering, replacement, quoting and the custom-command guard");
    }
}