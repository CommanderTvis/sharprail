using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Plugins.Api;

namespace SharpRail.Host.Core.Plugins;

/// <summary>One edit to a plugin's settings namespace: switch it on again, and set or drop its suspension mark.</summary>
internal readonly record struct SuspensionChange(string Id, bool Enable, string? SuspendedBy);

/// <summary>
/// A plugin switched off because a dependency was switched off is remembered in its settings namespace as
/// <c>"suspendedBy": "&lt;dependency&gt;"</c> beside <c>"enabled": false</c>, and switched on again when that
/// dependency is. A dependent the user had turned off stays off; an explicit switch of a suspended dependent drops the mark.
/// </summary>
internal static class PluginSuspension
{
    /// <summary>The namespace member that holds the mark; plugin settings types may not declare it.</summary>
    public const string Key = "suspendedBy";

    public static string? Mark(JsonElement space) =>
        space.ValueKind == JsonValueKind.Object && space.TryGetProperty(Key, out var mark) && mark.ValueKind == JsonValueKind.String ? mark.GetString() : null;

    /// <summary>
    /// What a change of the stored flags asks for. <paramref name="order"/> lists the plugins dependency-first (a cycle is
    /// left out, so it is never touched); <paramref name="before"/> and <paramref name="now"/> say which are switched on
    /// per the settings, and <paramref name="marks"/> holds the marks now stored.
    /// </summary>
    public static IReadOnlyList<SuspensionChange> Plan(
        IReadOnlyList<string> order,
        IReadOnlyDictionary<string, string[]> dependencies,
        IReadOnlyDictionary<string, bool> before,
        IReadOnlyDictionary<string, bool> now,
        IReadOnlyDictionary<string, string?> marks)
    {
        var on = order.ToDictionary(id => id, id => now.GetValueOrDefault(id));
        var held = order.ToDictionary(id => id, id => marks.GetValueOrDefault(id));
        var changes = new List<SuspensionChange>();
        foreach (var id in order)
        {
            if (held[id] is { } mark)
            {
                // An explicit switch on wins over the memory; so does a dependency that came back.
                if (on[id]) changes.Add(new(id, false, null));
                else if (on.TryGetValue(mark, out var markOn) ? markOn : now.GetValueOrDefault(mark))
                {
                    changes.Add(new(id, true, null));
                    on[id] = true;
                }
                else continue;
                held[id] = null;
            }
            else if (before.GetValueOrDefault(id) && !on[id] &&
                dependencies.GetValueOrDefault(id)?.FirstOrDefault(dependency => before.GetValueOrDefault(dependency) && !now.GetValueOrDefault(dependency)) is { } cause)
            {
                changes.Add(new(id, false, cause));
                held[id] = cause;
            }
        }
        return changes;
    }

    /// <summary>Applies <paramref name="change"/> to a stored namespace; null when nothing differs.</summary>
    public static JsonElement? Apply(JsonElement space, SuspensionChange change)
    {
        var node = space.ValueKind == JsonValueKind.Object ? JsonObject.Create(space.Clone())! : [];
        if (change.Enable) node["enabled"] = true;
        if (change.SuspendedBy is { } cause) node[Key] = cause;
        else node.Remove(Key);
        var result = PluginJson.SerializeToElement(node);
        return space.ValueKind == JsonValueKind.Object && JsonElement.DeepEquals(space, result) ? null : result;
    }
}