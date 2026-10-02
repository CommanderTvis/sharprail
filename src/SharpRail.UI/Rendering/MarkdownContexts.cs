using SharpRail.Host.Abstractions;
using SharpRail.UI.State;

namespace SharpRail.UI.Rendering;

/// <summary>Binds the kit's Markdown views to a workspace's host and the window's preferences.</summary>
public static class MarkdownContexts
{
    /// <param name="resolveSpecLink">Maps a <c>[[id]]</c> to its path; without one every spec link renders disabled.</param>
    public static MarkdownContext For(IProjectServices host, Preferences preferences, Action<string, string?> navigate, Func<string, string?>? resolveSpecLink = null) => new(
        async (path, token) => (await host.ReadFileAsync(path, token)).ImageData,
        id => resolveSpecLink?.Invoke(id),
        navigate, preferences.FontSize, preferences.MarkdownLineWidth, preferences.MarkdownLineWidthBounded);
}