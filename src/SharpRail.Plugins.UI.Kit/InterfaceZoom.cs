using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace SharpRail.Plugins.UI.Kit;

/// <summary>
/// Browser-style page zoom for every window and popup: each root wraps its content in a
/// <c>LayoutTransformControl</c> bound to the <see cref="ResourceKey"/> transform, so layout, icons and text scale together.
/// </summary>
public static class InterfaceZoom
{
    public const string ResourceKey = "InterfaceZoom";
    public static readonly double[] Factors = [0.5, 0.67, 0.8, 0.9, 1, 1.1, 1.25, 1.5, 1.75, 2];
    private const double Tolerance = 0.001;

    public static double Current { get; private set; } = 1;
    public static event Action? Changed;

    /// <summary>The adjacent factor in the step's direction, staying put at either bound; step 0 resets to 1.</summary>
    public static double Next(double current, int step) => step switch
    {
        0 => 1,
        > 0 => Factors.FirstOrDefault(factor => factor > current + Tolerance, current),
        _ => Factors.LastOrDefault(factor => factor < current - Tolerance, current),
    };

    /// <summary>Keeps a factor inside the bounds, so a hand-edited profile cannot leave them. A pinch may rest between the steps.</summary>
    public static double Normalize(double factor) =>
        double.IsFinite(factor) ? Math.Clamp(factor, Factors[0], Factors[^1]) : 1;

    /// <summary>The factor a pinch reports: its scale against the factor the gesture started at, never the previous step.</summary>
    public static double ForGesture(double start, double scale) =>
        double.IsFinite(scale) && scale > 0 ? Normalize(start * scale) : start;

    public static void Apply(Application app, double factor)
    {
        var next = Normalize(factor);
        if (next == Current && app.Resources.ContainsKey(ResourceKey)) return;
        Current = next;
        app.Resources[ResourceKey] = new ImmutableTransform(Matrix.CreateScale(Current, Current));
        Changed?.Invoke();
    }
}