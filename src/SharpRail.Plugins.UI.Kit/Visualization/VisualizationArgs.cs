using System.Text.Json;

namespace SharpRail.Plugins.UI.Kit.Visualization;

/// <summary>One comparison option as the card draws it, with every missing or mistyped field defaulted.</summary>
public sealed record ComparisonOptionView(string Name, string? Description, IReadOnlyList<string> Pros, IReadOnlyList<string> Cons, bool Recommended, string? Mermaid);

/// <summary>Tolerant reads of a visualize call's arguments, which arrive as the agent wrote them.</summary>
public static class VisualizationArgs
{
    public static string String(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    /// <summary>The options of a comparison, in order; a non-object entry keeps its position as an empty option.</summary>
    public static IReadOnlyList<ComparisonOptionView> ComparisonOptions(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return [];
        return [.. value.EnumerateArray().Select(entry =>
        {
            string? Text(string key) => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() : null;
            IReadOnlyList<string> Strings(string key) => entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Array
                ? [.. field.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!)]
                : [];
            var recommended = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("recommended", out var flag) && flag.ValueKind == JsonValueKind.True;
            return new ComparisonOptionView(Text("name") ?? "", Text("description"), Strings("pros"), Strings("cons"), recommended, Text("mermaid"));
        })];
    }
}