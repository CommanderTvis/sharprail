namespace Tereditor.PluginFixture.Outsider.Words;

/// <summary>What the outsider fixture says, from an assembly only its plugin directory holds.</summary>
public static class Words
{
    public static string Greeting(string asset) => asset.Trim() + " (outsider)";
}