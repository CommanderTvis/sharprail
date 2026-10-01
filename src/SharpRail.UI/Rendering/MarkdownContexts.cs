using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

namespace SharpRail.UI.Rendering;

/// <summary>Binds the kit's Markdown views to a workspace's host and the window's preferences.</summary>
public static class MarkdownContexts
{
    public static MarkdownContext For(IProjectServices host, Preferences preferences, Action<string, string?> navigate) => new(
        async (path, token) => (await host.ReadFileAsync(path, token)).ImageData,
        async token => (await host.ListSpecsAsync(token)).GroupBy(spec => spec.Id).ToDictionary(group => group.Key, group => group.First().Path),
        navigate, preferences.FontSize, preferences.MarkdownLineWidth, preferences.MarkdownLineWidthBounded);
}
