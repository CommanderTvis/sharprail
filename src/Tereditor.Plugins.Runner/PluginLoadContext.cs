using System.Reflection;
using System.Runtime.Loader;

using SharpRail.Plugins.Api.UI;

namespace SharpRail.Plugins.Runner;

/// <summary>
/// The one external plugin this runner serves. Shared assemblies (the framework and everything the runner itself ships:
/// the API, the kit, the chat contract, Avalonia) come from the default context, so the plugin's module is the runner's
/// type; anything else loads from the plugin's directory and nowhere else.
/// </summary>
internal sealed class PluginLoadContext(string directory) : AssemblyLoadContext("plugin:" + Path.GetFileName(directory))
{
    private static readonly HashSet<string> Shared = new(
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension).OfType<string>(), StringComparer.OrdinalIgnoreCase);

    protected override Assembly? Load(AssemblyName name)
    {
        if (name.Name is not { } simple || Shared.Contains(simple)) return null;
        var candidate = Path.Combine(directory, simple + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }

    /// <summary>
    /// Loads the UI half's entry assembly (relative to the plugin directory and inside it) and makes its single public
    /// concrete <see cref="PluginUIModule"/>; anything else throws with a reason the roster shows.
    /// </summary>
    public static PluginUIModule LoadUI(string directory, string entry)
    {
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, entry));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"The UI assembly {entry} is outside the plugin directory.");
        if (!File.Exists(path)) throw new FileNotFoundException($"The UI assembly {entry} does not exist.");
        var assembly = new PluginLoadContext(root).LoadFromAssemblyPath(path);
        Type[] types;
        try { types = assembly.GetExportedTypes(); }
        catch (Exception error) when (error is ReflectionTypeLoadException or FileNotFoundException or FileLoadException or TypeLoadException)
        {
            throw new InvalidOperationException($"{entry} could not be inspected: {error.Message}");
        }
        var modules = types.Where(type => type is { IsClass: true, IsAbstract: false } && typeof(PluginUIModule).IsAssignableFrom(type)).ToArray();
        if (modules.Length != 1) throw new InvalidOperationException($"{entry} must expose exactly one public concrete PluginUIModule, found {modules.Length}.");
        if (modules[0].GetConstructor(Type.EmptyTypes) is null) throw new InvalidOperationException($"{modules[0].FullName} has no public parameterless constructor.");
        return (PluginUIModule)Activator.CreateInstance(modules[0])!;
    }
}