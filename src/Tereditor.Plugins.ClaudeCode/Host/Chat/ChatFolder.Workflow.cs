using System.Text.Json;

namespace Tereditor.Plugins.ClaudeCode.Host.Chat;

// A workflow's components. The CLI streams them as the workflow_progress array of a task_progress message: workflow_phase entries
// (index, title), workflow_agent entries (index, label, phaseIndex, phaseTitle, state start/progress/done/error, startedAt, queuedAt,
// lastToolName, lastToolSummary, resultPreview, error, lastProgressAt) and workflow_log lines. The array is cumulative, and a message
// may omit it while updates are throttled, so an absent array keeps what the card has.
internal sealed partial class ChatFolder
{
    private readonly Dictionary<string, (IReadOnlyList<string> Phases, IReadOnlyList<ChatWorkflowAgent> Agents)> workflows = [];

    private void FoldWorkflowProgress(ChatTask task, JsonElement message)
    {
        if (!message.TryGetProperty("workflow_progress", out var entries) || entries.ValueKind != JsonValueKind.Array) return;
        var phases = new SortedDictionary<int, string>();
        var agents = new SortedDictionary<int, ChatWorkflowAgent>();
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || Long(entry, "index") is not { } index) continue;
            switch (Str(entry, "type"))
            {
                case "workflow_phase" when Str(entry, "title") is { Length: > 0 } title:
                    phases[(int)index] = title;
                    break;
                case "workflow_agent":
                    agents[(int)index] = Agent((int)index, entry);
                    break;
            }
        }
        workflows[task.TaskId] = ([.. phases.Values], [.. agents.Values]);
        SyncWorkflow(task.TaskId);
    }

    private static ChatWorkflowAgent Agent(int index, JsonElement entry)
    {
        var state = Str(entry, "state") switch
        {
            "done" => ChatAgentState.Done,
            "error" => ChatAgentState.Failed,
            "start" when Long(entry, "startedAt") is null && Long(entry, "queuedAt") is not null => ChatAgentState.Queued,
            _ => ChatAgentState.Running
        };
        var finished = state is ChatAgentState.Done or ChatAgentState.Failed;
        var duration = finished && Long(entry, "startedAt") is { } started && Long(entry, "lastProgressAt") is { } ended && ended >= started ? ended - started : (long?)null;
        var tool = Str(entry, "lastToolSummary") is { Length: > 0 } summary ? summary : Str(entry, "lastToolName");
        return new(index, Str(entry, "label") is { Length: > 0 } label ? label : "Agent " + (index + 1), state)
        {
            Phase = Str(entry, "phaseTitle"),
            Activity = state == ChatAgentState.Running ? tool : null,
            Result = Str(entry, "resultPreview"),
            Error = Str(entry, "error"),
            DurationMs = duration
        };
    }

    private void SyncWorkflow(string taskId)
    {
        if (!workflows.TryGetValue(taskId, out var parts)) return;
        var task = tasks.GetValueOrDefault(taskId);
        if (task?.ToolUseId is null || items.GetValueOrDefault(task.ToolUseId) is not ChatToolItem tool) return;
        if (tool.WorkflowAgents is { } agents && agents.SequenceEqual(parts.Agents) && tool.WorkflowPhases is { } phases && phases.SequenceEqual(parts.Phases)) return;
        Put(tool with { WorkflowPhases = parts.Phases, WorkflowAgents = parts.Agents });
    }
}