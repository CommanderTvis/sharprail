using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SharpRail.Host.Core;

public sealed partial class HostStateStore
{
    private sealed record LanguageMetadata(string Workspace, string Server, string Configuration);

    private string? LanguageMetadataPath(string workspace, string server) => DirectoryPath is null ? null :
        Path.Combine(DirectoryPath, "language", "workspaces", Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspace))), server + ".json");

    private void OpenLanguageMetadata()
    {
        if (DirectoryPath is null) return;
        try
        {
            var values = state.WorkspaceLanguages.ToDictionary();
            var root = Path.Combine(DirectoryPath, "language", "workspaces");
            if (Directory.Exists(root))
                foreach (var directory in Directory.EnumerateDirectories(root).Where(path => Path.GetFileName(path) is { Length: 64 } name && name.All(Uri.IsHexDigit)))
                    foreach (var file in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
                    {
                        var entry = JsonSerializer.Deserialize<LanguageMetadata>(File.ReadAllText(file))
                            ?? throw new JsonException("Invalid workspace language metadata.");
                        if (string.IsNullOrEmpty(entry.Workspace) || string.IsNullOrEmpty(entry.Server) || string.IsNullOrEmpty(entry.Configuration) || !Path.IsPathFullyQualified(entry.Workspace) || Language.LanguageServers.Known(entry.Server) is null ||
                            file != LanguageMetadataPath(entry.Workspace, entry.Server)) throw new JsonException("Invalid workspace language metadata identity.");
                        var servers = JsonNode.Parse(values.GetValueOrDefault(entry.Workspace) ?? "{}")!.AsObject();
                        servers[entry.Server] = JsonNode.Parse(entry.Configuration);
                        values[entry.Workspace] = servers.ToJsonString();
                    }
            state = state with { WorkspaceLanguages = values };
            PersistLanguageMetadata(new Dictionary<string, string>(), values);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { LastError ??= error.Message; }
    }

    private void PersistLanguageMetadata(IReadOnlyDictionary<string, string> previous, IReadOnlyDictionary<string, string> next)
    {
        if (DirectoryPath is null || ReferenceEquals(previous, next)) return;
        foreach (var workspace in previous.Keys.Union(next.Keys))
        {
            var before = JsonNode.Parse(previous.GetValueOrDefault(workspace) ?? "{}")!.AsObject();
            var after = JsonNode.Parse(next.GetValueOrDefault(workspace) ?? "{}")!.AsObject();
            foreach (var id in before.Select(item => item.Key).Union(after.Select(item => item.Key)))
            {
                if (JsonNode.DeepEquals(before[id], after[id])) continue;
                var file = LanguageMetadataPath(workspace, id)!;
                if (after[id] is null) { File.Delete(file); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(temporary, JsonSerializer.Serialize(new LanguageMetadata(workspace, id, after[id]!.ToJsonString())));
                    File.Move(temporary, file, overwrite: true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
    }
}