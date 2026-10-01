using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.Api.UI;

namespace SharpRail.UI.Plugins;

/// <summary>
/// Loads an external plugin's UI half from bytes the host serves, into a load context keyed by plugin id and the
/// entry assembly's content hash. Changed bytes load into a new context; earlier contexts stay resident, since
/// contexts are not collectible.
/// </summary>
public static class ExternalPlugins
{
    private static readonly ConcurrentDictionary<(string Id, string Hash), Lazy<Type>> Modules = new();

    /// <summary>Reads and loads the entry assembly and the siblings it references, off the UI thread, and returns its module type.</summary>
    public static async Task<Type> LoadAsync(IPluginService service, PluginRosterEntry entry, CancellationToken cancellationToken = default)
    {
        var entryPath = entry.Ui ?? throw new InvalidOperationException($"Plugin {entry.Id} has no UI half.");
        var bytes = await service.ReadFileAsync(entry.Id, entryPath, cancellationToken)
            ?? throw new FileNotFoundException($"Plugin {entry.Id}'s UI assembly {entryPath} is missing.");
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (Modules.TryGetValue((entry.Id, hash), out var loaded)) return loaded.Value;
        var directory = Path.GetDirectoryName(entryPath.Replace('\\', '/'))?.Replace('\\', '/') ?? "";
        var siblings = await FetchSiblingsAsync(service, entry.Id, directory, bytes, cancellationToken);
        return Modules.GetOrAdd((entry.Id, hash), _ => new(() =>
        {
            var context = new PluginLoadContext($"plugin:{entry.Id}:{hash[..12]}", siblings,
                name => service.ReadFileAsync(entry.Id, Combine(directory, name + ".dll")).AsTask().GetAwaiter().GetResult());
            return ModuleType(entry.Id, context.LoadFromStream(new MemoryStream(bytes)));
        })).Value;
    }

    /// <summary>Whether a dependency resolves from the app rather than the plugin directory, so its types unify with the app's.</summary>
    public static bool IsShared(AssemblyName name) =>
        name.Name is { } simple && (SharedNames.Value.Contains(simple) ||
            AssemblyLoadContext.Default.Assemblies.Any(assembly => assembly.GetName().Name == simple));

    private static readonly Lazy<HashSet<string>> SharedNames = new(() =>
        ((AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string) ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase));

    private static Type ModuleType(string id, Assembly assembly)
    {
        var modules = assembly.GetExportedTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false } && typeof(PluginUIModule).IsAssignableFrom(type) && type.GetConstructor(Type.EmptyTypes) is not null)
            .ToArray();
        return modules.Length == 1 ? modules[0]
            : throw new InvalidOperationException($"Plugin {id}'s UI assembly must expose exactly one public PluginUIModule with a parameterless constructor; it exposes {modules.Length}.");
    }

    // Reads every non-shared assembly the entry references, transitively, so activation never waits on the host.
    private static async Task<Dictionary<string, byte[]>> FetchSiblingsAsync(IPluginService service, string id, string directory, byte[] entry, CancellationToken cancellationToken)
    {
        var fetched = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var pending = new Queue<byte[]>([entry]);
        while (pending.TryDequeue(out var image))
            foreach (var reference in References(image))
            {
                if (IsShared(reference) || reference.Name is not { } name || fetched.ContainsKey(name)) continue;
                if (await service.ReadFileAsync(id, Combine(directory, name + ".dll"), cancellationToken) is not { } sibling) continue;
                fetched[name] = sibling;
                pending.Enqueue(sibling);
            }
        return fetched;
    }

    private static IEnumerable<AssemblyName> References(byte[] image)
    {
        using var reader = new System.Reflection.PortableExecutable.PEReader(new MemoryStream(image));
        var metadata = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(reader);
        return [.. metadata.AssemblyReferences.Select(handle => metadata.GetAssemblyReference(handle).GetAssemblyName())];
    }

    private static string Combine(string directory, string file) => directory.Length == 0 ? file : directory + "/" + file;

    private sealed class PluginLoadContext(string name, Dictionary<string, byte[]> siblings, Func<string, byte[]?> fetch)
        : AssemblyLoadContext(name, isCollectible: false)
    {
        // Null defers to the default context: the framework, Avalonia, SkiaSharp, the API assemblies and the kit.
        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (IsShared(assemblyName) || assemblyName.Name is not { } simple) return null;
            var image = siblings.GetValueOrDefault(simple) ?? fetch(simple);
            return image is null ? null : LoadFromStream(new MemoryStream(image));
        }
    }
}