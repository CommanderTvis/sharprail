using System.Text.Json;

using Tereditor.Host.Abstractions;
using Tereditor.Host.Core;
using Tereditor.Host.Core.Plugins;
using Tereditor.Plugins.Api;
using Tereditor.Plugins.Api.Host;

namespace Tereditor.Checks;

/// <summary>A dependent switched off by its dependency is remembered and comes back with it; nothing else does.</summary>
internal static partial class PluginHostChecks
{
    private static string? Suspended(Composed composed, string id) =>
        composed.State.Current.PluginSettings.TryGetValue(id, out var space) ? PluginSuspension.Mark(space) : null;

    private static Task Marked(Composed composed, string message, params (string Id, string? Cause)[] expected) =>
        Until(() => Task.FromResult(expected.All(item => Suspended(composed, item.Id) == item.Cause)), message);

    private static Task Status(Composed composed, string message, PluginStatus status, params string[] ids) =>
        Until(async () =>
        {
            var roster = await composed.Runtime.ListAsync();
            return ids.All(id => roster.Single(entry => entry.Id == id).Status == status);
        }, message);

    private static (PluginManifest, PluginHostModule?)[] SuspensionFixture(List<string> order)
    {
        Module Recording(string id) => new(Contract(id, Say), _ => { lock (order) order.Add(id); return ValueTask.FromResult<PluginDisposer?>(null); });
        return
        [
            (Manifest("trunk", true), Recording("trunk")),
            (Manifest("leaf-a", true, new PluginDependency("trunk", 1)), Recording("leaf-a")),
            (Manifest("leaf-b", true, new PluginDependency("trunk", 1)), Recording("leaf-b")),
            (Manifest("twig", true, new PluginDependency("leaf-a", 1)), Recording("twig")),
            (Manifest("leaf-off", false, new PluginDependency("trunk", 1)), Recording("leaf-off")),
            (Manifest("loop-a", false, new PluginDependency("loop-b", 1)), Recording("loop-a")),
            (Manifest("loop-b", false, new PluginDependency("loop-a", 1)), Recording("loop-b"))
        ];
    }

    internal static async Task Suspension(string directory)
    {
        var order = new List<string>();
        await using (var composed = await Compose(directory, SuspensionFixture(order)))
        {
            await composed.Runtime.Schedule();
            await Status(composed, "the fixture to start", PluginStatus.Active, "trunk", "leaf-a", "leaf-b", "twig");

            // A cascade remembers who was on, and what it waited for; the one the user had off is not remembered.
            // The marks land in the very batch that switches the dependency off, before the runtime reacts to it.
            await composed.State.ChangeAsync([HostStateChange.PluginEnabled("trunk", false)]);
            Require(Suspended(composed, "leaf-a") == "trunk" && Suspended(composed, "twig") == "leaf-a" && Suspended(composed, "leaf-off") is null,
                "The cascade and the marks are one batch.");
            await composed.Runtime.Schedule();
            await Marked(composed, "the cascade to be remembered", ("leaf-a", "trunk"), ("leaf-b", "trunk"), ("twig", "leaf-a"), ("leaf-off", null), ("trunk", null));
            await Status(composed, "the cascade to settle", PluginStatus.Disabled, "leaf-a", "leaf-b", "twig");
            var roster = (await composed.Runtime.ListAsync()).ToDictionary(entry => entry.Id);
            Require(roster["leaf-a"].Reason == "requires trunk" && roster["twig"].Reason == "requires leaf-a" && roster["leaf-off"].Reason is null && roster["trunk"].Reason is null,
                "A suspended dependent says what it requires; one the user turned off says nothing.");

            // The memory is persisted next to the flag, and survives a restart.
            var stored = new HostStateStore(directory).Current.PluginSettings;
            Require(stored["leaf-a"].GetRawText() == """{"enabled":false,"suspendedBy":"trunk"}""", "The mark is stored beside the flag: " + stored["leaf-a"].GetRawText());
        }

        order.Clear();
        await using (var composed = await Compose(directory, SuspensionFixture(order)))
        {
            await composed.Runtime.Schedule();
            Require(Suspended(composed, "twig") == "leaf-a" && (await composed.Row("leaf-b")).Status == PluginStatus.Disabled, "After a restart the dependents are still suspended.");

            // Back on: exactly the suspended ones return, dependency first.
            await composed.Enable("trunk");
            await Status(composed, "the dependents to return", PluginStatus.Active, "trunk", "leaf-a", "leaf-b", "twig");
            Require((await composed.Row("leaf-off")).Status == PluginStatus.Disabled, "A dependent the user had off stays off.");
            await Marked(composed, "the marks to be forgotten", ("leaf-a", null), ("leaf-b", null), ("twig", null));
            Require(order.IndexOf("trunk") < order.IndexOf("leaf-a") && order.IndexOf("leaf-a") < order.IndexOf("twig"), "Restored dependents activate dependency-first: " + string.Join(",", order));

            // A user's switch while suspended wins: on stays on, off stays off, and the mark is dropped.
            await composed.Enable("trunk", false);
            await Marked(composed, "the second cascade to be remembered", ("leaf-a", "trunk"), ("leaf-b", "trunk"), ("twig", "leaf-a"));
            await composed.Enable("leaf-a");
            await Marked(composed, "an explicit switch to drop the mark", ("leaf-a", null));
            await composed.Enable("leaf-b");
            await composed.Enable("leaf-b", false);
            await Marked(composed, "an explicit switch off to drop the mark", ("leaf-b", null));
            await composed.Enable("trunk");
            await Status(composed, "the remembered dependents to return", PluginStatus.Active, "trunk", "leaf-a", "twig");
            await Status(composed, "the dependent switched off meanwhile to stay off", PluginStatus.Disabled, "leaf-b");
            Require(Suspended(composed, "leaf-b") is null, "A dependent the user switched off while suspended stays off.");

            // A cycle is never touched, even when marks name each other.
            await composed.State.ChangeAsync([HostStateChange.PluginSettings("loop-a", """{"enabled":false,"suspendedBy":"loop-b"}"""),
                HostStateChange.PluginSettings("loop-b", """{"enabled":false,"suspendedBy":"loop-a"}""")]);
            await composed.Enable("trunk", false);
            await composed.Enable("trunk");
            await Status(composed, "the host to keep reconciling beside a cycle", PluginStatus.Active, "trunk", "leaf-a", "twig");
            var loop = await composed.Runtime.ListAsync();
            Require(loop.Single(entry => entry.Id == "loop-a").Status == PluginStatus.Refused && Suspended(composed, "loop-a") == "loop-b" && Suspended(composed, "loop-b") == "loop-a",
                "Marks inside a dependency cycle neither loop nor restore anything.");
        }

        // Removing a dependent forgets its memory.
        var removal = Path.Combine(directory, "removal");
        var plugins = Path.Combine(removal, "plugins");
        Directory.CreateDirectory(plugins);
        InstallExternal(plugins, "ext-leaf", manifest => manifest with { DependsOn = [new PluginDependency("trunk", 1)] });
        await using (var composed = await Compose(removal, SuspensionFixture([])))
        {
            await composed.State.ChangeAsync([HostStateChange.PluginEnabled("ext-leaf", true)]);
            await composed.Runtime.RescanAsync();
            await composed.Enable("trunk", false);
            await Marked(composed, "the external dependent to be remembered", ("ext-leaf", "trunk"));
            Directory.Delete(Path.Combine(plugins, "ext-leaf"), true);
            await composed.Runtime.RescanAsync();
            await Marked(composed, "a removed dependent to be forgotten", ("ext-leaf", null));
        }
        Console.WriteLine("PASS plugin suspension: a cascade is remembered and persisted, re-enable restores exactly those dependency-first, a user switch wins, user-off stays off, a cycle and a removal are safe");
    }
}