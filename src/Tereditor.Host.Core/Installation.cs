using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Host.Core;

/// <summary>
/// The identity of one state directory, minted once in <c>installation.json</c>. Processes that race to
/// create it agree on whoever created the file, so the id never changes after anyone has read it.
/// </summary>
public static class Installation
{
    public const string FileName = "installation.json";

    public static string EnsureIn(string directory)
    {
        Directory.CreateDirectory(directory);
        if (Read(directory) is { } existing) return existing;
        var target = Path.Combine(directory, FileName);
        var id = Guid.NewGuid().ToString();
        try
        {
            using var file = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(Encoding.UTF8.GetBytes(new JsonObject { ["id"] = id }.ToJsonString(new() { WriteIndented = true }) + "\n"));
            return id;
        }
        catch (IOException) when (File.Exists(target))
        {
            // The winner may still be writing; a file that stays unreadable is malformed and is never replaced.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (Read(directory) is { } winner) return winner;
                Thread.Sleep(25);
            }
            throw new IOException($"{FileName} exists without a valid installation id.");
        }
    }

    private static string? Read(string directory)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(Path.Combine(directory, FileName))) is JsonObject record &&
                record["id"] is JsonValue value && value.TryGetValue<string>(out var id) && id.Length > 0 ? id : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return null; }
    }
}