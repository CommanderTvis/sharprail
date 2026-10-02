using System.Text;
using System.Text.Json;

namespace SharpRail.Plugins.Codex.Host;

/// <summary>The latest token totals and plan Codex itself recorded in a rollout.</summary>
public sealed record CodexRolloutFacts(CodexTokenUsage? Usage, IReadOnlyList<CodexPlanItem>? Plan);

/// <summary>
/// Reads each rollout forward from where it last stopped, complete lines only, keeping the last <c>token_count</c>'s
/// totals and the last <c>update_plan</c> call. Nothing is estimated; a malformed plan or unknown status is dropped.
/// </summary>
public sealed class CodexRolloutReader
{
    private sealed class FileState
    {
        public long Offset;
        public CodexTokenUsage? Usage;
        public IReadOnlyList<CodexPlanItem>? Plan;
    }

    private readonly Lock gate = new();
    private readonly Dictionary<string, FileState> files = [];

    private static long Count(JsonElement total, string name) =>
        total.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? (long)number : 0;

    private static CodexTokenUsage UsageOf(JsonElement total)
    {
        var input = Count(total, "input_tokens");
        var cached = Count(total, "cached_input_tokens");
        return new(Math.Max(0, input - cached), Count(total, "output_tokens"), cached, Count(total, "cache_write_input_tokens"));
    }

    private static IReadOnlyList<CodexPlanItem>? PlanOf(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.String) return null;
        JsonElement parsed;
        try { parsed = JsonSerializer.Deserialize<JsonElement>(arguments.GetString()!); }
        catch (JsonException) { return null; }
        if (parsed.ValueKind != JsonValueKind.Object || !parsed.TryGetProperty("plan", out var steps) || steps.ValueKind != JsonValueKind.Array) return null;
        var plan = new List<CodexPlanItem>();
        foreach (var step in steps.EnumerateArray())
        {
            if (step.ValueKind != JsonValueKind.Object || !step.TryGetProperty("step", out var content) || content.ValueKind != JsonValueKind.String) continue;
            var status = step.TryGetProperty("status", out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() switch
            {
                "pending" => CodexPlanStatus.Pending,
                "in_progress" => CodexPlanStatus.InProgress,
                "completed" => CodexPlanStatus.Completed,
                _ => (CodexPlanStatus?)null
            } : null;
            if (status is { } known) plan.Add(new(content.GetString()!, known));
        }
        return plan;
    }

    private static void Apply(FileState state, string line)
    {
        JsonElement entry;
        try { entry = JsonSerializer.Deserialize<JsonElement>(line); }
        catch (JsonException) { return; }
        if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return;
        var type = entry.TryGetProperty("type", out var entryType) && entryType.ValueKind == JsonValueKind.String ? entryType.GetString() : null;
        var kind = payload.TryGetProperty("type", out var payloadType) && payloadType.ValueKind == JsonValueKind.String ? payloadType.GetString() : null;
        if (type == "event_msg" && kind == "token_count")
        {
            if (payload.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object &&
                info.TryGetProperty("total_token_usage", out var total) && total.ValueKind == JsonValueKind.Object)
                state.Usage = UsageOf(total);
        }
        else if (type == "response_item" && kind == "function_call" && payload.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String &&
            name.GetString() == "update_plan" && payload.TryGetProperty("arguments", out var arguments) && PlanOf(arguments) is { } plan)
            state.Plan = plan;
    }

    private static byte[]? ReadFrom(string path, long offset)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length <= offset) return [];
            var buffer = new byte[stream.Length - offset];
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            return buffer;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    public CodexRolloutFacts Read(string path)
    {
        lock (gate)
        {
            if (!files.TryGetValue(path, out var state)) files[path] = state = new();
            if (ReadFrom(path, state.Offset) is { } chunk)
            {
                var complete = Array.LastIndexOf(chunk, (byte)'\n') + 1;
                foreach (var line in Encoding.UTF8.GetString(chunk, 0, complete).Split('\n'))
                    if (line.Length > 0) Apply(state, line);
                state.Offset += complete;
            }
            return new(state.Usage, state.Plan);
        }
    }

    public void Forget(string path)
    {
        lock (gate) files.Remove(path);
    }
}