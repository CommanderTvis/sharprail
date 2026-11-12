#!/usr/bin/env dotnet
// `scripts/mise run dev`: builds tereditor from source and runs it, like the reference's `bun run desktop:dev`.
// The workspace defaults to this checkout (TEREDITOR_ROOT) and the profile to ~/.tereditor;
// set TEREDITOR_PROFILE to try something without touching your own. Arguments go to the app.
// It is a Release build (a Debug one holds more memory); TEREDITOR_CONFIGURATION=Debug selects Debug for debugging.
// On Windows the app is a GUI executable: it is built, then started detached so no console window stays open for it.
#:include ../lib/Tasks.cs
using System.Diagnostics;
using static Tasks;

return Guard(() =>
{
    // The app host finds the checkout's runtime, and terminal relays inherit it.
    Environment.SetEnvironmentVariable("DOTNET_ROOT", Tool("dotnet"));
    Environment.SetEnvironmentVariable("TEREDITOR_ROOT", Env("TEREDITOR_ROOT") ?? Root);
    var configuration = Env("TEREDITOR_CONFIGURATION") ?? "Release";
    // Run from inside a Claude Code session, the app must not carry that session's identity into its terminals.
    // On Windows that includes the session's NO_COLOR, which would otherwise switch colour off in every terminal tab.
    if (OperatingSystem.IsWindows() && Env("CLAUDECODE") is not null) Environment.SetEnvironmentVariable("NO_COLOR", null);
    foreach (var name in new[] { "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_SESSION_ID",
                 "CLAUDE_CODE_BRIDGE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SESSION_ATTENDED",
                 "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID", "CLAUDE_EFFORT" })
        Environment.SetEnvironmentVariable(name, null);
    if (!OperatingSystem.IsWindows())
        return Run(Dotnet, ["run", "--project", "src/Tereditor.UI", "-c", configuration, "--", .. args]);
    Check(Dotnet, ["build", "src/Tereditor.UI", "-c", configuration]);
    var info = new ProcessStartInfo(At("src", "Tereditor.UI", "bin", configuration, "net10.0", "Tereditor.UI.exe")) { UseShellExecute = false };
    foreach (var argument in args) info.ArgumentList.Add(argument);
    Process.Start(info);
    return 0;
});
