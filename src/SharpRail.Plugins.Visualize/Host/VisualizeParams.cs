using System.ComponentModel;

namespace SharpRail.Plugins.Visualize.Host;

public enum VisualizationType
{
    Diagram,
    Comparison
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
}

/// <summary>The visualize tool's arguments, the schema ThinkRail's pi visualize extension shares.</summary>
public sealed record VisualizeParams(
    [property: Description("Which visualization to render. 'diagram' needs `mermaid`; 'comparison' needs `options`.")] VisualizationType Type)
{
    [Description("Optional heading shown above the visualization.")]
    public string? Title { get; init; }

    [Description("Required when type='diagram'. Raw mermaid source of any kind (flowchart, sequenceDiagram, classDiagram, stateDiagram, erDiagram, gantt, …).")]
    public string? Mermaid { get; init; }

    [Description("Required when type='comparison'. The alternatives being compared.")]
    public IReadOnlyList<ComparisonOption>? Options { get; init; }

    /// <summary>The shape the schema cannot capture: what each type requires. Null when valid.</summary>
    public string? ShapeError() => Type switch
    {
        VisualizationType.Diagram when string.IsNullOrWhiteSpace(Mermaid) =>
            "visualize: `mermaid` is required and must be a non-empty string when type is \"diagram\".",
        VisualizationType.Comparison when Options is not { Count: > 0 } =>
            "visualize: `options` is required and must be a non-empty array when type is \"comparison\".",
        VisualizationType.Comparison => Options!.Select((option, index) => (option, index))
            .Where(item => string.IsNullOrWhiteSpace(item.option.Name))
            .Select(item => $"visualize: options[{item.index}].name is required and must be non-empty.").FirstOrDefault(),
        _ => null
    };
}