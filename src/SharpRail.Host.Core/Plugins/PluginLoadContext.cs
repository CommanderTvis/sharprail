using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>
/// One external plugin directory's assemblies at one content hash of its entry assembly. Shared assemblies
/// (the framework, the API, anything the default context already has or can resolve) come from the default
/// context so the plugin's module is the host's type; everything else loads from the plugin's directory.
/// Contexts are not collectible: a changed entry assembly loads into a new one and the old one stays resident.
/// </summary>
public sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> Platform = new(
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension).OfType<string>(), StringComparer.OrdinalIgnoreCase);

    private readonly string directory;

    private PluginLoadContext(string directory, string hash) : base($"plugin:{Path.GetFileName(directory)}:{hash}")
    {
        this.directory = directory;
    }

    /// <summary>Whether an assembly name resolves from the default context rather than a plugin's directory.</summary>
    public static bool IsShared(AssemblyName name) =>
        name.Name is { } simple && (Platform.Contains(simple) || Default.Assemblies.Any(loaded => loaded.GetName().Name == simple));

    protected override Assembly? Load(AssemblyName name)
    {
        if (IsShared(name)) return null;
        var candidate = Path.Combine(directory, name.Name + ".dll");
        return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
    }

    private static readonly Lock Gate = new();
    private static readonly Dictionary<(string Directory, string Hash), (PluginLoadContext Context, Assembly Entry)> Loaded = [];

    /// <summary>The content hash of an entry assembly, which keys its load context.</summary>
    public static string Hash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))[..16];

    /// <summary>
    /// Loads a host half's entry assembly (relative to the plugin directory and contained in it) and
    /// instantiates its single public concrete <see cref="PluginHostModule"/>.
    /// </summary>
    public static (PluginHostModule Module, string Hash) LoadHost(string directory, string entry)
    {
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, entry));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new InvalidOperationException($"the host assembly {entry} is outside the plugin directory");
        if (!File.Exists(path)) throw new FileNotFoundException($"the host assembly {entry} does not exist");
        var bytes = File.ReadAllBytes(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes))[..16];
        Assembly assembly;
        lock (Gate)
        {
            if (Loaded.TryGetValue((root, hash), out var known)) assembly = known.Entry;
            else
            {
                var context = new PluginLoadContext(root, hash);
                using var stream = new MemoryStream(bytes);
                assembly = context.LoadFromStream(stream);
                Loaded[(root, hash)] = (context, assembly);
            }
        }
        Type[] types;
        try { types = assembly.GetExportedTypes(); }
        catch (Exception error) when (error is ReflectionTypeLoadException or FileNotFoundException or FileLoadException or TypeLoadException)
        {
            throw new InvalidOperationException($"{entry} could not be inspected: {error.Message}");
        }
        var modules = types.Where(type => type is { IsClass: true, IsAbstract: false } && typeof(PluginHostModule).IsAssignableFrom(type)).ToArray();
        if (modules.Length != 1)
            throw new InvalidOperationException($"{entry} must expose exactly one public concrete PluginHostModule, found {modules.Length}");
        if (modules[0].GetConstructor(Type.EmptyTypes) is not { } constructor)
            throw new InvalidOperationException($"{modules[0].FullName} has no public parameterless constructor");
        try { return ((PluginHostModule)constructor.Invoke(null), hash); }
        catch (TargetInvocationException error) when (error.InnerException is { } inner)
        {
            throw new InvalidOperationException($"{modules[0].FullName} threw while constructing: {inner.Message}");
        }
    }
}