using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.UI;
using SharpRail.UI.Docking;

namespace SharpRail.Checks;

/// <summary>Code Intelligence is a plugin: off, the UI has no language feature and every plugin that requires it is unavailable; on again, all return.</summary>
internal static partial class PluginHostChecks
{
    private const string CodeIntelligence = "code-intelligence";

    internal static async Task CodeIntelligencePlugin(string root)
    {
        var all = BuiltinPlugins.Create(null);
        var byId = all.ToDictionary(plugin => plugin.Manifest.Id, plugin => plugin.Manifest);
        Require(byId[CodeIntelligence] is { EnabledByDefault: true, Headless: HeadlessSupport.Yes }, "Code Intelligence ships enabled and runs headless.");
        var dependents = all.Where(plugin => plugin.Manifest.DependsOn.Any(dependency => dependency.Id == CodeIntelligence)).Select(plugin => plugin.Manifest.Id).Order().ToArray();
        string[] expected = [.. BuiltinPlugins.LanguageServers.Select(server => server.Contract.Id), "air-context"];
        Require(dependents.SequenceEqual(expected.Order()), "Every language server plugin and Air Context require Code Intelligence: " + string.Join(", ", dependents));
        Require(byId[CodeIntelligence].DependsOn.Count == 0, "Code Intelligence requires nothing.");
        // No cycle: a plugin reaches the base through DependsOn, and the base reaches nothing.
        foreach (var plugin in all)
        {
            var seen = new HashSet<string>();
            var stack = new Stack<string>([plugin.Manifest.Id]);
            while (stack.TryPop(out var current))
                foreach (var dependency in byId[current].DependsOn)
                {
                    Require(dependency.Id != plugin.Manifest.Id, $"{plugin.Manifest.Id} is part of a dependency cycle.");
                    if (seen.Add(dependency.Id)) stack.Push(dependency.Id);
                }
        }

        var directory = Path.Combine(root, "code-intelligence-plugin");
        await using var composed = await Compose(directory, BuiltinPlugins.LanguageServerEntries);
        await composed.Runtime.Schedule();
        var on = await composed.Runtime.ListAsync();
        Require(on.Single(entry => entry.Id == CodeIntelligence).Status == PluginStatus.Active && on.Single(entry => entry.Id == "lsp-css").Status == PluginStatus.Active, "On: Code Intelligence and a language server run.");
        LanguageSupport.Update(on);
        var session = new LayoutSession();
        session.SetLanguageAvailable(LanguageSupport.Available);
        Require(LanguageSupport.Available && LanguageSupport.Serves("site.css") && session.IsToolAvailable("lsp") && session.Tools.Any(tool => tool.Id == "lsp"),
            "On: the language features, the Language tool and its catalog entry are present.");
        var saved = DockState.Preset("balanced");
        saved.Groups.First(group => group.Region == "right").Tools.Add(DockState.Tool("lsp"));

        await composed.Enable(CodeIntelligence, false);
        await composed.Runtime.Schedule();
        IReadOnlyList<PluginRosterEntry> off = await composed.Runtime.ListAsync();
        Require(off.Single(entry => entry.Id == CodeIntelligence).Status == PluginStatus.Disabled, "Off: Code Intelligence is disabled.");
        var blocked = off.Where(entry => entry.DependsOn.Contains(CodeIntelligence)).ToArray();
        Require(blocked.Length == expected.Length - 1 && blocked.All(entry => entry.Status == PluginStatus.Disabled), "Off: switching Code Intelligence off switches off every plugin that requires it.");
        Require(SharpRail.UI.Plugins.PluginRegistry.ActiveDependents(CodeIntelligence, on).Count == expected.Length - 1, "A disable of Code Intelligence cascades to every dependent in the roster (Air Context is not part of this fixture).");
        await composed.Enable("lsp-css");
        var refused = await composed.Row("lsp-css");
        Require(refused is { Status: PluginStatus.Refused, Reason: "depends on code-intelligence, which is off" }, "Off: enabling a dependent is refused, saying what it requires: " + refused.Reason);
        off = await composed.Runtime.ListAsync();
        LanguageSupport.Update(off);
        session.SetLanguageAvailable(LanguageSupport.Available);
        Require(!LanguageSupport.Available && !LanguageSupport.Serves("site.css") && !LanguageSupport.Serves("Program.cs") && !session.IsToolAvailable("lsp") && session.Tools.All(tool => tool.Id != "lsp"),
            "Off: no language feature, no Language tool and no catalog entry; the layout still names the tool and stays valid.");
        Require(LayoutSession.IsValid(saved), "A saved layout naming the Language tool stays valid with the plugin off.");

        await composed.Enable(CodeIntelligence);
        var again = await composed.Runtime.ListAsync();
        Require(again.Single(entry => entry.Id == CodeIntelligence).Status == PluginStatus.Active && again.Single(entry => entry.Id == "lsp-css").Status == PluginStatus.Active,
            "Back on: Code Intelligence runs and the dependent that was enabled meanwhile activates; the ones the switch-off turned off stay off.");
        Require(again.Count(entry => entry.DependsOn.Contains(CodeIntelligence) && entry.Status == PluginStatus.Active) == 1, "Back on: only the dependent enabled meanwhile is active.");
        Require(SharpRail.UI.Plugins.PluginRegistry.ToEnable("lsp-css", off).SequenceEqual([CodeIntelligence]), "Enabling a plugin that requires a disabled one turns the base on with it.");
        LanguageSupport.Update(again);
        session.SetLanguageAvailable(LanguageSupport.Available);
        Require(LanguageSupport.Available && LanguageSupport.Serves("site.css") && session.IsToolAvailable("lsp"), "Back on: the language features and the Language tool return.");
        Console.WriteLine("PASS code intelligence plugin: off removes the language UI and leaves dependents unavailable naming it, on restores them; no dependency cycle");
    }
}