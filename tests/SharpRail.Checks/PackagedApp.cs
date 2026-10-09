using System.Reflection;
using System.Security.Cryptography;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.UI;

namespace SharpRail.Checks;

/// <summary>
/// Under <c>SHARPRAIL_PACKAGED_APP</c> the gate is the packaged one: the files the bundle ships must sit,
/// byte for byte, beside the running checks, and the product assemblies must have loaded from there.
/// </summary>
internal static class PackagedApp
{
    internal const string Variable = "SHARPRAIL_PACKAGED_APP";

    internal static void Run()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } bundle) return;
        var (count, superseded) = Verify(Path.Combine(bundle, "Contents", "MacOS"), AppContext.BaseDirectory,
            [typeof(WorkbenchWindow).Assembly.Location, typeof(ProjectServices).Assembly.Location, typeof(LocalHostAdapter).Assembly.Location,
                typeof(WorkspaceInfo).Assembly.Location, typeof(SharpRail.Scintilla.ScintillaEditor).Assembly.Location, typeof(Ghostty.Avalonia.GhosttySkiaView).Assembly.Location],
            file => { try { return AssemblyName.GetAssemblyName(file); } catch (BadImageFormatException) { return null; } });
        Console.WriteLine($"PASS packaged app: {count} shipped files are the ones under test" +
            (superseded.Count == 0 ? "" : $"; the remote host's newer {string.Join(", ", superseded)} stand in for the shipped versions"));
    }

    /// <summary>
    /// A shipped file may differ beside the checks only as a newer version of the same non-product assembly,
    /// which the remote host under test brings with it; those are returned by name rather than counted.
    /// </summary>
    internal static (int Identical, List<string> Superseded) Verify(string shipped, string running, IEnumerable<string> loaded, Func<string, AssemblyName?> identity)
    {
        static void Require(bool value, string message)
        {
            if (!value) throw new InvalidOperationException(message);
        }
        Require(Directory.Exists(shipped), "No packaged app at " + shipped);
        var count = 0;
        var superseded = new List<string>();
        foreach (var file in Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(shipped, file);
            var staged = Path.Combine(running, name);
            Require(File.Exists(staged), "The checks are not running beside the packaged " + name);
            if (SHA256.HashData(File.ReadAllBytes(staged)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(file)))) { count++; continue; }
            Require(!name.StartsWith("SharpRail.", StringComparison.Ordinal) && !name.StartsWith("Ghostty.", StringComparison.Ordinal) &&
                identity(file) is { Name: { } packaged, Version: { } version } && identity(staged) is { Name: { } replacement, Version: { } newer } &&
                packaged == replacement && newer > version, "The checks are not running the packaged " + name);
            superseded.Add(name);
        }
        foreach (var assembly in loaded)
            Require(File.Exists(Path.Combine(shipped, Path.GetFileName(assembly))) && Path.GetFullPath(Path.GetDirectoryName(assembly)!) == Path.GetFullPath(running).TrimEnd(Path.DirectorySeparatorChar),
                "A product assembly was not loaded from the packaged app: " + assembly);
        return (count, superseded);
    }
}