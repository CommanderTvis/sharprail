using System.Text.Json;

using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Runner;

/// <summary>
/// Avalonia in the plugin process: SharpRail's remote platform backend (<see cref="RemotePlatform"/>) with Skia's CPU
/// raster, on the thread that starts it, which becomes the plugin's UI thread. Every plugin call runs there. Popups are
/// top-levels of their own, which the client places, not drawn inside their surface.
/// </summary>
internal static class RunnerPlatform
{
    /// <summary>Sets the platform up on this thread and runs its loop until <paramref name="body"/> (started on the pool) completes.</summary>
    public static int Run(Func<Task<int>> body)
    {
        AppBuilder.Configure<RunnerApp>()
            .UseSkia()
            .UseRemote()
            .SetupWithoutStarting();
        var running = Task.Run(body);
        using var stop = new CancellationTokenSource();
        _ = running.ContinueWith(_ => stop.Cancel(), TaskScheduler.Default);
        Dispatcher.UIThread.MainLoop(stop.Token);
        return running.GetAwaiter().GetResult();
    }

    /// <summary>Applies a client's kit theme (the JSON <c>RemoteSurface</c> writes) to the whole process, unless it is the one applied.</summary>
    public static void ApplyTheme(string json)
    {
        if (json.Length == 0 || json == appliedTheme) return;
        var theme = ReadTheme(json);
        appliedTheme = json;
        Ui.Apply(theme);
        if (Application.Current is { } app) app.RequestedThemeVariant = theme.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
    }

    private static string? appliedTheme;

    private static ThemeManifest ReadTheme(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            static Color Parse(JsonElement value) => Color.Parse(value.GetString() ?? throw new FormatException("A colour is missing."));
            var colors = root.GetProperty("colors").EnumerateObject().ToDictionary(color => color.Name, color => color.Value.ValueKind == JsonValueKind.Null ? (Color?)null : Parse(color.Value));
            var ansi = root.GetProperty("ansi").EnumerateArray().Select(Parse).ToArray();
            var syntax = root.GetProperty("syntax").EnumerateObject().ToDictionary(color => color.Name, color => Parse(color.Value));
            return new(root.GetProperty("id").GetString()!, root.GetProperty("label").GetString()!, root.GetProperty("order").GetInt32(),
                root.GetProperty("appearance").GetString()!, root.GetProperty("contrast").GetString()!, colors, ansi, syntax);
        }
        catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new Isolation.IsoCallException(Isolation.IsoError.InvalidParams, "The theme is not valid: " + error.Message);
        }
    }
}

/// <summary>The plugin process's application: Fluent with the kit's palettes, and the kit's text roles.</summary>
internal sealed class RunnerApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme { Palettes = { [ThemeVariant.Dark] = new ColorPaletteResources(), [ThemeVariant.Light] = new ColorPaletteResources() } });
        Styles.Add(Typography.Styles());
    }
}