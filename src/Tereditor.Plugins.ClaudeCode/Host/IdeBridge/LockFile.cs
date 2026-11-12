using System.Diagnostics;
using System.Text.Json.Nodes;

using SharpRail.Plugins.ClaudeCode.Host.ClaudeConfig;

namespace SharpRail.Plugins.ClaudeCode.Host.IdeBridge;

internal sealed record LockFileContents(int Pid, IReadOnlyList<string> WorkspaceFolders, string IdeName, string AuthToken);

/// <summary>The discovery file <c>&lt;claude home&gt;/ide/&lt;port&gt;.lock</c> a <c>claude</c> CLI scans to find an IDE.</summary>
internal static class LockFile
{
    public static string Directory() => Path.Combine(ClaudeEnvironment.ClaudeHome(), "ide");

    public static string PathOf(int port) => Path.Combine(Directory(), $"{port}.lock");

    public static void Write(int port, LockFileContents contents)
    {
        if (OperatingSystem.IsWindows()) System.IO.Directory.CreateDirectory(Directory());
        else System.IO.Directory.CreateDirectory(Directory(), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var json = new JsonObject
        {
            ["pid"] = contents.Pid,
            ["workspaceFolders"] = new JsonArray([.. contents.WorkspaceFolders.Select(folder => (JsonNode?)folder)]),
            ["ideName"] = contents.IdeName,
            ["transport"] = "ws",
            ["runningInWindows"] = OperatingSystem.IsWindows(),
            ["authToken"] = contents.AuthToken
        }.ToJsonString();
        var path = PathOf(port);
        if (OperatingSystem.IsWindows()) File.WriteAllText(path, json);
        else
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
            });
            using var writer = new StreamWriter(stream);
            writer.Write(json);
        }
    }

    public static void Remove(int port)
    {
        try { File.Delete(PathOf(port)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Clears lock files this host wrote in a previous run: a hard kill never runs the stop, and a stale file advertising
    /// a dead port makes the CLI offer a bridge nothing listens on. Ours are recognized by the IDE name, so another IDE's
    /// lock file is never touched, nor one whose process is still alive.
    /// </summary>
    public static void RemoveOwnStale(string ideName)
    {
        string[] entries;
        try { entries = System.IO.Directory.GetFiles(Directory(), "*.lock"); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return; }
        foreach (var path in entries)
        {
            if (Json.ReadObject(path) is not { } parsed || Json.String(parsed["ideName"]) != ideName) continue;
            if (parsed["pid"] is JsonValue pidValue && pidValue.TryGetValue<int>(out var pid) && pid != Environment.ProcessId && IsAlive(pid)) continue;
            try { File.Delete(path); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return false; }
    }
}