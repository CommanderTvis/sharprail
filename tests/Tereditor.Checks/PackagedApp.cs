using System.Reflection;
using System.Security.Cryptography;

namespace Tereditor.Checks;

/// <summary>
/// Under <c>TEREDITOR_PACKAGED_APP</c> the gate is the packaged one. The app ships as one NativeAOT binary beside its native
/// libraries and assets, so no product assembly ships; the checks run beside the shipped files, byte for byte, and load
/// the shipped native libraries, while <see cref="PackagedBinaries"/> runs the binaries themselves.
/// </summary>
internal static class PackagedApp
{
    internal const string Variable = "TEREDITOR_PACKAGED_APP";

    /// <summary>Adds the checks' own files to a copy of the shipped files; a shipped file is never replaced.</summary>
    internal static void AddCheckFiles(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var staged = Path.Combine(destination, Path.GetRelativePath(source, file));
            if (File.Exists(staged)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(staged)!);
            File.Copy(file, staged);
        }
    }

    internal static void Run()
    {
        if (Environment.GetEnvironmentVariable(Variable) is not { Length: > 0 } bundle) return;
        // A macOS bundle keeps its files in Contents/MacOS; the variable names the Linux AppImage payload directly.
        var shipped = Path.Combine(bundle, "Contents", "MacOS");
        var count = Verify(Directory.Exists(shipped) ? shipped : bundle, AppContext.BaseDirectory, PackagedBinaries.AppExecutable, Identity);
        Console.WriteLine($"PASS packaged app: a NativeAOT app with no managed assemblies; its {count} shipped files are the ones under test");
    }

    internal static AssemblyName? Identity(string file)
    {
        try { return AssemblyName.GetAssemblyName(file); }
        catch (BadImageFormatException) { return null; }
    }

    /// <summary>Fails unless <paramref name="shipped"/> is a NativeAOT payload whose every file sits unchanged in <paramref name="running"/>.</summary>
    internal static int Verify(string shipped, string running, string executable, Func<string, AssemblyName?> identity)
    {
        RequireNativeAot(shipped, executable, identity);
        var count = 0;
        foreach (var file in Directory.EnumerateFiles(shipped, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(shipped, file);
            var staged = Path.Combine(running, name);
            Require(File.Exists(staged), "The checks are not running beside the packaged " + name);
            Require(SHA256.HashData(File.ReadAllBytes(staged)).AsSpan().SequenceEqual(SHA256.HashData(File.ReadAllBytes(file))), "The checks are not running the packaged " + name);
            count++;
        }
        return count;
    }

    /// <summary>
    /// Fails unless <paramref name="directory"/> holds the <paramref name="executable"/> binary and nothing of a JIT payload:
    /// no managed assembly, no runtimeconfig.json or deps.json, and no runtime or JIT library.
    /// </summary>
    internal static void RequireNativeAot(string directory, string executable, Func<string, AssemblyName?> identity)
    {
        Require(Directory.Exists(directory), "No packaged app at " + directory);
        Require(File.Exists(Path.Combine(directory, OperatingSystem.IsWindows() ? executable + ".exe" : executable)), $"The package at {directory} has no {executable} executable.");
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            Require(!name.EndsWith(".runtimeconfig.json", StringComparison.Ordinal) && !name.EndsWith(".deps.json", StringComparison.Ordinal) &&
                Path.GetFileNameWithoutExtension(name) is not ("libcoreclr" or "coreclr" or "libclrjit" or "clrjit" or "libhostfxr" or "hostfxr") &&
                identity(file) is null, "The package is not NativeAOT: it ships " + Path.GetRelativePath(directory, file));
        }
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}