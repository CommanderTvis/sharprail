#!/usr/bin/env dotnet
// `scripts/mise run native [--no-run] [--print] [app arguments]`: publishes the desktop app as NativeAOT for this machine's OS,
// architecture and CPU (IlcInstructionSet=native, optimisation preference Speed) into artifacts/native, then starts it.
// --no-run publishes and prints the executable's path; --print only prints the commands it would run. Anything else goes to the app.
// This is the user's own build: it does not go through the agent compile queue, and agents never run it.
#:include ../lib/Tasks.cs
using System.Diagnostics;
using System.Runtime.InteropServices;
using static Tasks;

return Guard(() =>
{
    var noRun = args.Contains("--no-run");
    var print = args.Contains("--print");
    var appArgs = args.Where(argument => argument is not ("--no-run" or "--print")).ToArray();
    var system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : OperatingSystem.IsLinux() ? "linux"
        : throw new TaskFailure("No NativeAOT target for this operating system.");
    var architecture = RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        var other => throw new TaskFailure($"No NativeAOT target for the architecture {other}."),
    };
    var rid = $"{system}-{architecture}";
    var output = At("artifacts", "native");
    // macOS names an unbundled app in the Dock after its executable, so there it carries the packages' name.
    var executable = Path.Combine(output, OperatingSystem.IsWindows() ? "Tereditor.UI.exe" : OperatingSystem.IsMacOS() ? AppExecutable : "Tereditor.UI");
    // Same locked restore as the packages (it also fetches the ILCompiler pack); the publish then reuses it.
    string[] restore = ["restore", "Tereditor.slnx", "-p:SelfContained=true", "-p:PublishReadyToRun=true"];
    // NativeAot.props already sets PublishAot and trimming; this adds what is specific to this build: code for this CPU's
    // instruction sets (it will not start on an older CPU) and speed over size.
    string[] publish = ["publish", "src/Tereditor.UI", "-c", "Release", "-r", rid, "--no-restore", "-p:IlcInstructionSet=native", "-p:OptimizationPreference=Speed", $"-p:TereditorBuildStamp={(print ? "<stamp>" : BuildStamp())}", "-o", output];
    Console.WriteLine("The NativeAOT compile uses several GB of memory and bypasses the agent compile queue (queue-heavy).");
    if (print)
    {
        Console.WriteLine($"{Dotnet} {string.Join(' ', restore)}");
        Console.WriteLine($"{Dotnet} {string.Join(' ', publish)}");
        Console.WriteLine(noRun ? "(no run)" : $"{executable} {string.Join(' ', appArgs)}".TrimEnd());
        return 0;
    }
    Check(Dotnet, restore);
    Check(Dotnet, publish);
    if (OperatingSystem.IsMacOS()) RenameExecutable(output, "Tereditor.UI", AppExecutable);
    if (noRun)
    {
        Console.WriteLine(executable);
        return 0;
    }
    // Same environment as `run dev`: the checkout is the initial workspace, and a Claude Code session's identity stays out of the app's terminals.
    Environment.SetEnvironmentVariable("TEREDITOR_ROOT", Env("TEREDITOR_ROOT") ?? Root);
    if (OperatingSystem.IsWindows() && Env("CLAUDECODE") is not null) Environment.SetEnvironmentVariable("NO_COLOR", null);
    foreach (var name in new[] { "CLAUDECODE", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_EXECPATH", "CLAUDE_CODE_SESSION_ID",
                 "CLAUDE_CODE_BRIDGE_SESSION_ID", "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_SESSION_ATTENDED",
                 "CLAUDE_CODE_SSE_PORT", "CLAUDE_PID", "CLAUDE_EFFORT" })
        Environment.SetEnvironmentVariable(name, null);
    if (!OperatingSystem.IsWindows()) return Run(executable, appArgs);
    Process.Start(new ProcessStartInfo(executable, appArgs) { UseShellExecute = false });
    return 0;
});
