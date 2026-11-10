using System.Text.Json.Nodes;

using SharpRail.Host.Core;

namespace SharpRail.Host.Headless;

/// <summary>
/// How the language tools meet the paths an agent types. A path inside the workspace root is accepted absolute or relative to the root;
/// a relative path that does not exist but exists under exactly one child directory is refused with that path as a suggestion.
/// <c>lsp_status</c> also states the root, because the agent often cannot tell where the server was started.
/// </summary>
internal static class WorkspacePaths
{
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "node_modules", "bin", "obj" };

    internal static McpServer.McpTool Apply(McpServer.McpTool tool, string root)
    {
        if (!tool.Name.StartsWith("lsp_", StringComparison.Ordinal)) return tool;
        var call = tool.Call;
        return tool with
        {
            Call = async (arguments, token) =>
            {
                if (Normalize(arguments, root) is { } refusal) return (refusal, true);
                var reply = await call(arguments, token);
                return tool.Name == "lsp_status" && !reply.Error ? (WithRoot(reply.Text, root), false) : reply;
            }
        };
    }

    /// <summary>Rewrites <c>path</c> and <c>paths</c> to root-relative form, or returns the refusal.</summary>
    internal static string? Normalize(JsonObject arguments, string root)
    {
        if (arguments["path"] is JsonValue single && single.TryGetValue<string>(out var text))
        {
            var (fixedPath, refusal) = Resolve(text, root);
            if (refusal is not null) return refusal;
            arguments["path"] = fixedPath;
        }
        if (arguments["paths"] is JsonArray list)
            for (var index = 0; index < list.Count; index++)
            {
                if (list[index] is not JsonValue item || !item.TryGetValue<string>(out var entry)) continue;
                var (fixedPath, refusal) = Resolve(entry, root);
                if (refusal is not null) return refusal;
                list[index] = fixedPath;
            }
        return null;
    }

    private static (string Path, string? Refusal) Resolve(string value, string root)
    {
        // URIs of dependencies (jar:, jrt:, file:) and globs belong to the tool.
        if (value.Length == 0 || value.Contains("://", StringComparison.Ordinal) || value.IndexOfAny(['*', '?', '[', '{']) >= 0) return (value, null);
        if (Path.IsPathRooted(value))
        {
            var full = Path.GetFullPath(value);
            var prefix = root + Path.DirectorySeparatorChar;
            if (full == root) return (".", null);
            if (full.StartsWith(prefix, StringComparison.Ordinal)) return (Path.GetRelativePath(root, full).Replace('\\', '/'), null);
            return (value, $"{value} is outside the workspace root {root}. Use a path inside it.");
        }
        var inside = Path.GetFullPath(value, root);
        if (File.Exists(inside) || Directory.Exists(inside)) return (value, null);
        string[] candidates;
        try
        {
            candidates = [.. Directory.EnumerateDirectories(root)
                .Where(child => !Path.GetFileName(child).StartsWith('.') && !Skipped.Contains(Path.GetFileName(child)))
                .Where(child => File.Exists(Path.Combine(child, value)) || Directory.Exists(Path.Combine(child, value)))
                .Select(child => Path.GetFileName(child) + "/" + value.Replace('\\', '/'))];
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return (value, null); }
        return candidates.Length == 1
            ? (value, $"{value} does not exist under the workspace root {root}. Did you mean {candidates[0]}?")
            : (value, null);
    }

    private static string WithRoot(string text, string root)
    {
        try
        {
            if (JsonNode.Parse(text) is JsonObject status)
            {
                status["workspaceRoot"] = root;
                return status.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
            }
        }
        catch (System.Text.Json.JsonException) { }
        return $"workspace root: {root}\n{text}";
    }
}