#!/usr/bin/env dotnet
// `scripts/mise run native [--no-run] [--print] [app arguments]`: publishes the desktop app as NativeAOT for this machine's OS,
// architecture and CPU (IlcInstructionSet=native, optimisation preference Speed) into artifacts/native, then starts it.
// On macOS it then wraps the output in artifacts/native/tereditor.app and starts the executable inside it, so the Dock and
// Finder take the bundle's compiled icon (and with it the system's icon styles) and the terminal keeps the app's output.
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
    // macOS runs it from an app bundle laid out like the package's (same identifier, so settings and state stay where they are).
    var bundle = Path.Combine(output, "tereditor.app");
    var executable = OperatingSystem.IsMacOS() ? Path.Combine(bundle, "Contents", "MacOS", AppExecutable)
        : Path.Combine(output, OperatingSystem.IsWindows() ? "Tereditor.UI.exe" : "Tereditor.UI");
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
        if (OperatingSystem.IsMacOS()) Console.WriteLine($"(lay out {bundle} around the output)\ncodesign --force --deep --sign - {bundle}");
        Console.WriteLine(noRun ? "(no run)" : $"{executable} {string.Join(' ', appArgs)}".TrimEnd());
        return 0;
    }
    Check(Dotnet, restore);
    Check(Dotnet, publish);
    if (OperatingSystem.IsMacOS())
    {
        // The restamp in the layout breaks the linker's ad-hoc signature, and Apple silicon runs no unsigned code.
        DeleteAll(Path.Combine(output, AppExecutable)); // left in the output by runs from before the bundle
        LayOutMacBundle(output, bundle);
        Check("codesign", ["--force", "--deep", "--sign", "-", bundle]);
    }
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
