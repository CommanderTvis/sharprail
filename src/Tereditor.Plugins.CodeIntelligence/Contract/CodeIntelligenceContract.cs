using Tereditor.Plugins.Api;

namespace Tereditor.Plugins.CodeIntelligence;

/// <summary>The Code Intelligence plugin: the switch for every language feature the app shows. It has no methods or settings of its own.</summary>
public static class CodeIntelligenceContract
{
    public const string Id = "code-intelligence";

    public static readonly PluginContract Contract = PluginContract.Create(Id, 1, [], []);

    public static readonly PluginManifest Manifest = new(Id, "Code Intelligence", "code-s-slash-line", "0.1.0", PluginApi.Generation, 1)
    {
        Category = PluginCategories.Agents,
        Description = "The language features of the app: the Code Intelligence settings and tool window, and hover, go to definition, usages and diagnostics in the editor. The language server plugins and Air Context require it.",
        EnabledByDefault = true,
        Headless = HeadlessSupport.Yes,
        Host = "Tereditor.Plugins.CodeIntelligence.Host.dll"
    };
}