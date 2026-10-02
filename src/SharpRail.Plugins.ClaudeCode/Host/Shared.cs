using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>Content hashing and compare-and-swap writes for the configuration files the pane edits.</summary>
internal static class TextFile
{
    public static string ContentHash(string content) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];

    public static FileContent Read(string path)
    {
        var content = File.ReadAllText(path);
        return new(content, ContentHash(content));
    }

    public static FileWriteResult Write(string path, string content, string baseHash)
    {
        FileContent disk;
        try { disk = Read(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { disk = new("", ContentHash("")); }
        if (disk.Hash != baseHash) return new(false) { Disk = disk };
        File.WriteAllText(path, content);
        return new(true) { Hash = ContentHash(content) };
    }
}

/// <summary>The outcome of a child process the host started with a deadline.</summary>
internal sealed record BoundedRun(bool Ok, string Out, string Err, bool TimedOut = false, bool LaunchFailed = false);

/// <summary>Runs a command with a deadline, capturing both streams; the whole process tree is killed when it passes.</summary>
internal static class Bounded
{
    public static async Task<BoundedRun> RunAsync(IReadOnlyList<string> argv, TimeSpan timeout, string? cwd = null,
        IReadOnlyDictionary<string, string>? environment = null, CancellationToken cancellationToken = default)
    {
        var start = new ProcessStartInfo(argv[0])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            WorkingDirectory = cwd ?? Environment.CurrentDirectory
        };
        foreach (var argument in argv.Skip(1)) start.ArgumentList.Add(argument);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) start.Environment[key] = value;
        Process process;
        try { process = Process.Start(start) ?? throw new InvalidOperationException("The process did not start."); }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new(false, "", error.Message, LaunchFailed: true);
        }
        using (process)
        {
            process.StandardInput.Close();
            var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(timeout);
            try { await process.WaitForExitAsync(deadline.Token); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                cancellationToken.ThrowIfCancellationRequested();
                return new(false, await Drain(output), await Drain(errors), TimedOut: true);
            }
            return new(process.ExitCode == 0, await output, await errors);
        }
    }

    // A killed child's grandchildren can hold the pipes open; take what arrived rather than wait for them.
    private static async Task<string> Drain(Task<string> stream) =>
        await Task.WhenAny(stream, Task.Delay(250)) == stream ? await stream : "";
}

/// <summary>The paths and variables both the configuration reader and the IDE bridge resolve against.</summary>
internal static class ClaudeEnvironment
{
    public static string Home() =>
        Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home ? home : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Claude Code's configuration directory: <c>CLAUDE_CONFIG_DIR</c>, else <c>~/.claude</c>.</summary>
    public static string ClaudeHome() =>
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { } overridden && overridden.Trim().Length > 0 ? overridden : Path.Combine(Home(), ".claude");

    /// <summary>Follows <c>CLAUDE_CONFIG_DIR</c> exactly as Claude Code resolves it: the state file sits inside it, else in the home directory.</summary>
    public static string ClaudeStatePath() =>
        Path.Combine(Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { } overridden && overridden.Trim().Length > 0 ? overridden : Home(), ".claude.json");
}