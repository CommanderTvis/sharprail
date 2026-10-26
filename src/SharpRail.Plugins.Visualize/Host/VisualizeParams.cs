using System.ComponentModel;

namespace SharpRail.Plugins.Visualize.Host;

public enum VisualizationType
{
    Diagram,
    Comparison,
    Svg,
    Markdown
}

public sealed record ComparisonOption([property: Description("Short label for this option.")] string Name)
{
    [Description("One or two sentences describing the option.")]
    public string? Description { get; init; }

    [Description("Advantages of this option.")]
    public IReadOnlyList<string>? Pros { get; init; }

    [Description("Drawbacks of this option.")]
    public IReadOnlyList<string>? Cons { get; init; }

    [Description("Set true on the single option you endorse; it is highlighted.")]
    public bool? Recommended { get; init; }

    [Description("Optional raw mermaid diagram illustrating this option.")]
    public string? Mermaid { get; init; }

    [Description("Optional hand-written SVG illustrating this option, under the same rules as type='svg'; ignored when `mermaid` is set.")]
    public string? Svg { get; init; }
}

/// <summary>The visualize tool's arguments, the schema ThinkRail's pi visualize extension shares.</summary>
public sealed record VisualizeParams(
    [property: Description("Which visualization to render. 'diagram' needs `mermaid`; 'comparison' needs `options`; 'svg' needs `svg`; 'markdown' needs `markdown`.")] VisualizationType Type)
{
    [Description("Optional heading shown above the visualization.")]
    public string? Title { get; init; }

    [Description("Required when type='diagram'. Raw mermaid source of any kind (flowchart, sequenceDiagram, classDiagram, stateDiagram, erDiagram, gantt, …).")]
    public string? Mermaid { get; init; }

    [Description("Required when type='svg'. A complete hand-written <svg> document (xmlns optional), at most 2 MB and 10000 elements. " +
        "No <script>, on* handlers, <foreignObject>, animation, DTDs or external references: href/xlink:href only to #id or data: PNG/JPEG/GIF/WebP/BMP images, url() only to #id. " +
        "Colours follow the pane's theme: currentColor and var(--fg) are the foreground, var(--bg) the background, var(--accent), var(--muted) and var(--border) the accent, muted text and border colours.")]
    public string? Svg { get; init; }

    [Description("Required when type='markdown'. Markdown text rendered like the app's Markdown preview: headings, lists, tables (including HTML tables), code, links and fenced mermaid blocks.")]
    public string? Markdown { get; init; }

    [Description("Required when type='comparison'. The alternatives being compared.")]
    public IReadOnlyList<ComparisonOption>? Options { get; init; }

    /// <summary>The shape the schema cannot capture: what each type requires. Null when valid.</summary>
    public string? ShapeError() => Type switch
    {
        VisualizationType.Diagram when string.IsNullOrWhiteSpace(Mermaid) =>
            "visualize: `mermaid` is required and must be a non-empty string when type is \"diagram\".",
        VisualizationType.Svg when string.IsNullOrWhiteSpace(Svg) =>
            "visualize: `svg` is required and must be a non-empty string when type is \"svg\".",
        VisualizationType.Markdown when string.IsNullOrWhiteSpace(Markdown) =>
            "visualize: `markdown` is required and must be a non-empty string when type is \"markdown\".",
        VisualizationType.Comparison when Options is not { Count: > 0 } =>
            "visualize: `options` is required and must be a non-empty array when type is \"comparison\".",
        VisualizationType.Comparison => Options!.Select((option, index) => (option, index))
            .Where(item => string.IsNullOrWhiteSpace(item.option.Name))
            .Select(item => $"visualize: options[{item.index}].name is required and must be non-empty.").FirstOrDefault(),
        _ => null
    };
}

/// <summary>visualize_read takes nothing: the runtime binds it to the calling terminal's view.</summary>
public sealed record VisualizeReadParams;