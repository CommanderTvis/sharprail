using System.Diagnostics;

namespace SharpRail.Checks;

/// <summary>
/// The clock for wait bounds: it stands still while the machine sleeps, so a deadline measures time the
/// checks could actually use and a suspended run does not wake up to find every wait expired.
/// </summary>
internal static class Awake
{
    private static readonly DateTime started = DateTime.UtcNow;
    private static readonly Stopwatch running = Stopwatch.StartNew();

    internal static DateTime Now => started + running.Elapsed;
}