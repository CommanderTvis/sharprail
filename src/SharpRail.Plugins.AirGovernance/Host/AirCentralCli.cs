using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.AirGovernance.Host;

/// <summary>Where the host looks for Central and the agents' configuration; checks point it at a fixture home.</summary>
internal sealed record AirEnvironment(string Home, string SearchPath, string? ClaudeDirectory = null, string? CodexDirectory = null)
{
    /// <summary>Checks only: loopback endpoints standing in for the fixed production table. Nothing outside the checks sets it.</summary>
    internal AirAccessEndpoints? TestEndpoints { get; init; }

    public static AirEnvironment Current() => new(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("PATH") ?? "",
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), Environment.GetEnvironmentVariable("CODEX_HOME"));
}

/// <param name="Outcome">exited, launch-failed, timed-out or output-too-large.</param>
internal sealed record AirProcessResult(string Outcome, int ExitCode, string Stdout);

internal sealed record AirVersion(int Major, int Minor, int Patch)
{
    public string Text => $"{Major}.{Minor}.{Patch}";

    public static AirVersion? Parse(string output)
    {
        var match = Regex.Match(output.Trim(), @"^central\s+(\d+)\.(\d+)\.(\d+)(?:\s|$)", RegexOptions.CultureInvariant);
        if (!match.Success) return null;
        return int.TryParse(match.Groups[1].Value, out var major) && int.TryParse(match.Groups[2].Value, out var minor) && int.TryParse(match.Groups[3].Value, out var patch)
            ? new AirVersion(major, minor, patch) : null;
    }

    public int CompareTo(AirVersion other) => Major != other.Major ? Major.CompareTo(other.Major) : Minor != other.Minor ? Minor.CompareTo(other.Minor) : Patch.CompareTo(other.Patch);
}

/// <summary>
/// The adapter around the user's <c>central</c> binary, ported from the fork's <c>jbcentral</c> module: bounded
/// output, per-call timeouts, the version gate and the status-row parse. Its output is untrusted data: only the
/// version, the sign-in and proxy verdicts and sanitized access-source names ever leave this class.
/// </summary>
internal sealed partial class AirCentralCli(AirEnvironment environment)
{
    internal static readonly AirVersion Minimum = new(1, 4, 0);
    internal static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(15);
    internal static readonly TimeSpan ActionTimeout = TimeSpan.FromMinutes(2);
    internal static readonly TimeSpan UpdateTimeout = TimeSpan.FromMinutes(5);
    internal const int MaxOutput = 16 * 1024;
    internal const string InstallBase = "https://central-cli.labs.jb.gg";

    public AirEnvironment Environment { get; } = environment;

    public static string InstallCommand => RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
        ? $"irm {InstallBase}/install.ps1 | iex"
        : $"curl -fsSL {InstallBase}/install.sh | bash";

    private static string ExecutableName => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "central.exe" : "central";

    /// <summary>The absolute path of <c>central</c> on the search path, else in <c>~/.local/bin</c>.</summary>
    public string? Locate()
    {
        foreach (var directory in Environment.SearchPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!Path.IsPathRooted(directory)) continue;
            var candidate = Path.Combine(directory, ExecutableName);
            if (File.Exists(candidate)) return candidate;
        }
        var local = Path.Combine(Environment.Home, ".local", "bin", ExecutableName);
        return File.Exists(local) ? local : null;
    }

    private ProcessStartInfo Start(string executable, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["HOME"] = Environment.Home;
        start.Environment["USERPROFILE"] = Environment.Home;
        return start;
    }

    public async Task<AirProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, bool capture, CancellationToken cancellationToken)
    {
        Process process;
        try { process = Process.Start(Start(executable, arguments)) ?? throw new InvalidOperationException(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return new("launch-failed", -1, ""); }
        using (process)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(timeout);
            try
            {
                process.StandardInput.Close();
                var errors = Discard(process.StandardError, limit.Token);
                var output = await ReadBoundedAsync(process.StandardOutput, capture ? MaxOutput : 0, limit.Token);
                if (output is null) { Kill(process); return new("output-too-large", -1, ""); }
                await process.WaitForExitAsync(limit.Token);
                await errors;
                return new("exited", process.ExitCode, output);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                Kill(process);
                return new("timed-out", -1, "");
            }
            catch (OperationCanceledException) { Kill(process); throw; }
        }
    }

    /// <summary>Starts a long-lived command (sign-in opens a browser) and waits only <paramref name="grace"/> for it to fail fast.</summary>
    public async Task<(string Outcome, int ExitCode, Process? Running)> LaunchAsync(string executable, IReadOnlyList<string> arguments, TimeSpan grace, CancellationToken cancellationToken)
    {
        Process process;
        try { process = Process.Start(Start(executable, arguments)) ?? throw new InvalidOperationException(); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { return ("launch-failed", -1, null); }
        process.StandardInput.Close();
        _ = Discard(process.StandardOutput, CancellationToken.None);
        _ = Discard(process.StandardError, CancellationToken.None);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(grace, cancellationToken); }
        catch (TimeoutException) { return ("running", -1, process); }
        catch (OperationCanceledException) { Kill(process); process.Dispose(); throw; }
        var code = process.ExitCode;
        process.Dispose();
        return ("exited", code, null);
    }

    public static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task Discard(StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[1024];
        try { while (await reader.ReadAsync(buffer.AsMemory(), cancellationToken) > 0) { } }
        catch (Exception error) when (error is OperationCanceledException or IOException or ObjectDisposedException) { }
    }

    private static async Task<string?> ReadBoundedAsync(StreamReader reader, int maxChars, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        var buffer = new char[1024];
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            if (text.Length + read > maxChars) return maxChars == 0 ? "" : null;
            text.Append(buffer, 0, read);
        }
        return text.ToString();
    }

    /// <summary>Reads the binary's version only; no sign-in state, no network.</summary>
    public async Task<AirCentral> InspectAsync(CancellationToken cancellationToken)
    {
        var path = Locate();
        var install = InstallCommand;
        if (path is null) return new("absent", "unknown", "unknown", install);
        var result = await RunAsync(path, ["--version"], VersionTimeout, true, cancellationToken);
        if (result.Outcome != "exited" || result.ExitCode != 0) return new("probe-failed", "unknown", "unknown", install, path);
        if (AirVersion.Parse(result.Stdout) is not { } version) return new("malformed-version", "unknown", "unknown", install, path);
        return new(version.CompareTo(Minimum) < 0 ? "outdated" : "supported", "unknown", "unknown", install, path, version.Text);
    }

    [GeneratedRegex("\u001b\\[[0-9;]*m")]
    private static partial Regex Ansi();

    /// <summary>The Auth and Proxy rows of <c>central status</c>; only a positively observed negative sets a flag.</summary>
    public static (string Auth, string Proxy) ParseStatus(string output)
    {
        string auth = "unknown", proxy = "unknown";
        foreach (var raw in Ansi().Replace(output, "").Split('\n'))
        {
            var line = Regex.Replace(raw, @"\s+", " ").Trim();
            if (Regex.Match(line, @"(?:^|\s)Auth\s+(\S.*)$") is { Success: true } authRow)
                auth = authRow.Groups[1].Value.Contains("not connected", StringComparison.Ordinal) ? "signed-out" : "connected";
            if (Regex.Match(line, @"(?:^|\s)Proxy\s+(\S.*)$") is { Success: true } proxyRow)
                proxy = proxyRow.Groups[1].Value == "stopped" ? "stopped" : "running";
        }
        return (auth, proxy);
    }

    public async Task<(string Auth, string Proxy)> ProbeAsync(string path, CancellationToken cancellationToken)
    {
        var result = await RunAsync(path, ["status"], StatusTimeout, true, cancellationToken);
        return result is { Outcome: "exited", ExitCode: 0 } ? ParseStatus(result.Stdout) : ("unknown", "unknown");
    }

    [GeneratedRegex(@"\b((?:workspace|license):[A-Za-z0-9-]+(?::[A-Za-z0-9-]+)?)\b")]
    private static partial Regex SelectionId();

    public static bool IsSelectionId(string value) => SelectionId().Match(value) is { Success: true } match && match.Value == value;
}