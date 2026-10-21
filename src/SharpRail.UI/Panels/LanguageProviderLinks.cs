using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace SharpRail.UI.Panels;

internal static class LanguageProviderLinks
{
    internal static void Apply(JsonNode schema, Button website, Button repository, WrapPanel links)
    {
        foreach (var (button, key) in new[] { (website, "website"), (repository, "repository") })
        {
            button.Tag = Uri.TryCreate(schema[key]?.ToString(), UriKind.Absolute, out var uri) && uri.Scheme == "https" ? uri : null;
            button.IsVisible = button.Tag is Uri;
            ToolTip.SetTip(button, button.Tag?.ToString());
        }
        if (website.Tag is Uri site && repository.Tag is Uri source &&
            site.AbsoluteUri.TrimEnd('/').Equals(source.AbsoluteUri.TrimEnd('/'), StringComparison.Ordinal))
            website.IsVisible = false;
        repository.Content = (schema["repositoryLabel"]?.ToString() ?? "Source repository") + " ↗";
        links.IsVisible = website.IsVisible || repository.IsVisible;
    }

    /// <summary>Opens a link through the window's launcher: every link button and the Help menu's Report an Issue… go through it.</summary>
    internal static Func<TopLevel, Uri, Task> Launch { get; set; } = (top, uri) => top.Launcher.LaunchUriAsync(uri);

    internal static async void Open(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: Uri uri } button && TopLevel.GetTopLevel(button) is { } top)
            await Launch(top, uri);
    }
}