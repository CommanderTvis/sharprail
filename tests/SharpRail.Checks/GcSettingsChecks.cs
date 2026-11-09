using SharpRail.UI;

namespace SharpRail.Checks;

/// <summary>
/// The desktop app and the standalone host ship with the memory settings measured in performance/README.md, Android and Browser
/// do not take them, and the idle trim fires once per idle period, never during startup or while the process is busy.
/// </summary>
internal static class GcSettingsChecks
{
    internal static void Run(string root)
    {
        var repo = FindRepository();
        if (repo is not null)
        {
            foreach (var project in new[] { "src/SharpRail.UI/SharpRail.UI.csproj", "src/SharpRail.Host.Remote/SharpRail.Host.Remote.csproj" })
                Require(File.ReadAllText(Path.Combine(repo, project)).Contains("DesktopGc.props", StringComparison.Ordinal), project + " does not import DesktopGc.props.");
            foreach (var project in new[] { "src/SharpRail.Android/SharpRail.Android.csproj", "src/SharpRail.Browser/SharpRail.Browser.csproj", "Directory.Build.props" })
            {
                var text = File.ReadAllText(Path.Combine(repo, project));
                Require(!text.Contains("DesktopGc.props", StringComparison.Ordinal) && !text.Contains("System.GC.", StringComparison.Ordinal), project + " carries the desktop GC settings.");
            }
            var props = File.ReadAllText(Path.Combine(repo, "DesktopGc.props"));
            Require(props.Contains("System.GC.ConserveMemory", StringComparison.Ordinal) && !props.Contains("System.GC.Concurrent", StringComparison.Ordinal), "DesktopGc.props lost its settings.");
        }
        if (!OperatingSystem.IsWindows())
        {
            if (Environment.GetEnvironmentVariable(PackagedBinaries.ClientVariable) is { Length: > 0 } client) VerifyCompiledIn(Path.Combine(client, "SharpRail.UI"));
            if (Environment.GetEnvironmentVariable(PackagedBinaries.HostVariable) is { Length: > 0 } host) VerifyCompiledIn(Path.Combine(host, "SharpRail.Host.Remote"));
        }
        IdleTrim();
        ScrollTooltips();
        Console.WriteLine("PASS gc settings");
    }

    /// <summary>
    /// Fails unless a packaged NativeAOT binary (ELF or Mach-O) carries the memory setting, so a nightly artifact cannot lose it.
    /// NativeAOT compiles runtimeconfig options into the binary as NUL-terminated names; the GC's own table of setting names
    /// holds the name once in every binary, so the compiled option is a second occurrence.
    /// </summary>
    internal static void VerifyCompiledIn(string binary)
    {
        Require(File.Exists(binary), "No binary at " + binary);
        var bytes = File.ReadAllBytes(binary).AsSpan();
        var name = "\0System.GC.ConserveMemory\0"u8;
        var count = 0;
        for (int index; (index = bytes.IndexOf(name)) >= 0; bytes = bytes[(index + 1)..]) count++;
        Require(count >= 2, binary + " was not published with System.GC.ConserveMemory.");
    }

    private static void IdleTrim()
    {
        var trim = new IdleMemoryTrim();
        var cpu = TimeSpan.Zero;
        bool Tick(double seconds, double busy = 0)
        {
            cpu += TimeSpan.FromSeconds(busy);
            return trim.ShouldTrim(TimeSpan.FromSeconds(seconds), cpu);
        }
        Require(!Tick(5) && !Tick(40) && !Tick(59), "The trim fired during startup.");
        Require(Tick(65), "The trim did not fire after the startup grace and idle time.");
        Require(!Tick(70) && !Tick(300), "The trim fired twice in one idle period.");
        trim.Activity(TimeSpan.FromSeconds(310));
        Require(!Tick(320) && !Tick(335), "The trim fired before the idle time after input.");
        Require(Tick(345), "The trim did not fire after the next idle period.");
        Require(!Tick(350), "The trim fired twice after input.");
        // A process using more than the busy share of a core (a terminal streaming output) is not idle, whatever the input says.
        var seconds = 350.0;
        for (var i = 0; i < 12; i++) Require(!Tick(seconds += 5, 5 * 0.5), "The trim fired while the process was busy.");
        Require(!Tick(seconds + 5), "The trim fired before the idle time after being busy.");
        Require(Tick(seconds + 40), "The trim did not fire once the process was idle again.");
        // The compaction's own CPU does not count as activity.
        trim.Rebase(TimeSpan.FromSeconds(seconds + 41), cpu + TimeSpan.FromSeconds(1));
        Require(!Tick(seconds + 50, 0), "The trim fired again after its own compaction.");
    }

    private static void ScrollTooltips()
    {
        var gate = new ScrollTooltipGate();
        static TimeSpan At(int ms) => TimeSpan.FromMilliseconds(ms);
        Require(!gate.IsScrolling(At(0)), "Tooltips were suppressed before any scroll.");
        for (var ms = 0; ms < 1000; ms += 50)
        {
            gate.Scrolled(At(ms));
            Require(gate.IsScrolling(At(ms)), "Tooltips were not suppressed during a scroll burst.");
        }
        Require(gate.IsScrolling(At(950 + 100)), "Tooltips came back before the quiet period.");
        Require(!gate.IsScrolling(At(950) + ScrollTooltipGate.Quiet), "Tooltips stayed suppressed after the quiet period.");
    }

    private static string? FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "SharpRail.slnx"))) return directory.FullName;
        return null;
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}