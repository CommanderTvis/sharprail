using System.Diagnostics;
using System.Globalization;

namespace SharpRail.Checks;

/// <summary>
/// Keeps a macOS machine from idle-sleeping under a run. The assertion is scoped to system sleep and to
/// this process, so the display may still sleep and an abrupt exit releases it.
/// </summary>
internal static class IdleSleep
{
    internal static Process? Hold(string executable = "/usr/bin/caffeinate")
    {
        if (!OperatingSystem.IsMacOS()) return null;
        var start = new ProcessStartInfo(executable);
        foreach (var argument in new[] { "-i", "-w", Environment.ProcessId.ToString(CultureInfo.InvariantCulture) }) start.ArgumentList.Add(argument);
        var assertion = Process.Start(start)!;
        if (assertion.WaitForExit(100))
            throw new InvalidOperationException($"macOS idle-sleep assertion exited during startup with code {assertion.ExitCode}.");
        return assertion;
    }
}