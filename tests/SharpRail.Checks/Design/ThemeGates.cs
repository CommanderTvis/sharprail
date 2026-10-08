using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Media;
using Avalonia.Platform;

using SharpRail.UI.Rendering;

namespace SharpRail.Checks.Design;

/// <summary>What every bundled manifest must satisfy beyond being complete: strict parsing and legibility.</summary>
internal static class ThemeGates
{
    private static readonly string[] Resting = ["background", "content", "sidebar", "header", "elevated", "input"];
    private static readonly (string Key, double Floor)[] Floors = [("text", 4.5), ("muted", 4.5), ("hint", 3), ("accent", 4.5), ("success", 4.5)];
    // Neither is ever rendered as text on a control.
    private static readonly string[] NotOnInput = ["accent", "success"];

    internal static void Run(List<string> failures)
    {
        Strictness(failures);
        foreach (var theme in Themes.All) Legibility(theme, failures);
    }

    private static void Strictness(List<string> failures)
    {
        using var stream = AssetLoader.Open(new Uri("avares://SharpRail.UI/Assets/Themes/dark.theme.json"));
        var source = JsonNode.Parse(stream)!;
        bool Rejected(Action<JsonNode> change)
        {
            var copy = source.DeepClone();
            change(copy);
            try { Themes.Parse(JsonSerializer.SerializeToElement(copy), "probe"); return false; }
            catch (Exception error) when (error is InvalidDataException or KeyNotFoundException or InvalidOperationException) { return true; }
        }
        if (Rejected(_ => { })) failures.Add("The manifest parser rejected an unchanged bundled manifest.");
        if (!Rejected(theme => theme["syntax"]!.AsObject().Remove("keyword"))) failures.Add("The manifest parser accepted a partial syntax palette.");
        if (!Rejected(theme => theme["injectedCss"] = "body { display: none }")) failures.Add("The manifest parser accepted an unknown property.");
        if (!Rejected(theme => theme["colors"]!["extra"] = "#000000")) failures.Add("The manifest parser accepted an unknown colour key.");
        if (!Rejected(theme => theme["colors"]!["accent"] = "rgb(0, 0, 0)")) failures.Add("The manifest parser accepted a non-hex colour.");
        if (!Rejected(theme => theme["colors"]!["accent"] = "#8DFF4F")) failures.Add("The manifest parser accepted a non-canonical uppercase colour.");
        if (!Rejected(theme => theme["colors"]!["text"] = null)) failures.Add("The manifest parser accepted null for a required colour.");
        if (!Rejected(theme => theme["id"] = "Not A Slug")) failures.Add("The manifest parser accepted an id that is not a slug.");

        using var schemaStream = AssetLoader.Open(new Uri("avares://SharpRail.UI/Assets/theme.schema.json"));
        var schema = JsonNode.Parse(schemaStream)!;
        schema["$defs"]!["syntax"]!["required"]!.AsArray().RemoveAt(0);
        try
        {
            Themes.RequireSchemaAgreement(JsonSerializer.SerializeToElement(schema));
            failures.Add("A schema missing a required syntax key was taken to agree with the parser.");
        }
        catch (InvalidDataException) { }
    }

    private static void Legibility(ThemeManifest theme, List<string> failures)
    {
        void Floor(string foreground, Color top, string background, Color bottom, double floor)
        {
            var ratio = Contrast(top, bottom);
            if (ratio < floor) failures.Add($"{theme.Id}: {foreground} on {background} is {ratio:0.00}:1, below {floor:0.0#}.");
        }
        var high = theme.IsHighContrast;
        foreach (var (key, floor) in Floors)
        {
            // High contrast is held to AAA resting and full AA on hover; hint stays the quiet tier.
            var strict = high && key != "hint";
            foreach (var surface in Resting)
                if (surface != "input" || !NotOnInput.Contains(key))
                    Floor(key, theme[key], surface, theme[surface], strict ? Math.Max(floor, 7) : floor);
            Floor(key, theme[key], "hover", theme["hover"], strict ? 4.5 : 3);
        }
        foreach (var fill in new[] { "accentSolid", "accentHover" }) Floor("onAccent", theme["onAccent"], fill, theme[fill], 4.5);
        // A hover or selected fill that matches its surface is an invisible selection, however legible its text.
        foreach (var surface in Resting) Floor("hover", theme["hover"], surface, theme[surface], 1.15);
        if (!high) return;
        foreach (var (foreground, background) in new[] { ("selectionForeground", "selection"), ("editorSelectionForeground", "editorSelection") })
            if (theme.Colors[foreground] is { } color) Floor(foreground, color, background, theme[background], 4.5);
            else failures.Add($"{theme.Id}: a high-contrast manifest must supply {foreground}.");
    }

    /// <summary>The WCAG contrast ratio of two colours, ignoring alpha as the reference's gate does.</summary>
    internal static double Contrast(Color a, Color b)
    {
        static double Channel(byte value)
        {
            var channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color color) => 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
        var (first, second) = (Luminance(a), Luminance(b));
        return (Math.Max(first, second) + 0.05) / (Math.Min(first, second) + 0.05);
    }
}