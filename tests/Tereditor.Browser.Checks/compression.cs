// Regression checks of scripts/browser-compress.cs: Brotli and gzip sidecars decode to the exact input, unchanged assets
// reuse their sidecars, a regenerated bundle keeps the content cache, changed inputs are recompressed, and small or
// non-compressible files get no sidecars. `scripts/mise run browser check` runs it:
// dotnet run --file tests/Tereditor.Browser.Checks/compression.cs -- [dotnet]
using System.Diagnostics;
using System.IO.Compression;
using System.Text;

var dotnet = args.Length > 0 ? args[0] : "dotnet";
var root = FindRoot();
var scratch = Path.Combine(Path.GetTempPath(), $"tereditor-wasm-compression-{Environment.ProcessId}");
var cache = Path.Combine(scratch, ".cache");
var bytes = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Browser assets λ\n", 4096)));
try
{
    Directory.CreateDirectory(Path.Combine(scratch, "_framework"));
    var file = Path.Combine(scratch, "_framework", "runtime.wasm");
    File.WriteAllBytes(file, bytes);
    File.WriteAllText(Path.Combine(scratch, "small.js"), "small");
    File.WriteAllBytes(Path.Combine(scratch, "image.png"), bytes);
    Compress();
    foreach (var suffix in new[] { "br", "gz" })
    {
        var packed = File.ReadAllBytes($"{file}.{suffix}");
        Assert(packed.Length < bytes.Length, $"The .{suffix} sidecar must be smaller than its input.");
        Assert(Decode(packed, suffix).SequenceEqual(bytes), $"The .{suffix} sidecar must decode to the exact input.");
    }
    var before = File.GetLastWriteTimeUtc($"{file}.br");
    Compress();
    Assert(File.GetLastWriteTimeUtc($"{file}.br") == before, "Unchanged assets must reuse compression.");
    var cachedFile = Directory.EnumerateFiles(cache).Single(name => name.EndsWith(".br11", StringComparison.Ordinal));
    var cachedTime = File.GetLastWriteTimeUtc(cachedFile);
    File.Delete($"{file}.br");
    Compress();
    Assert(Decode(File.ReadAllBytes($"{file}.br"), "br").SequenceEqual(bytes), "A regenerated bundle must get its exact sidecar back.");
    Assert(File.GetLastWriteTimeUtc(cachedFile) == cachedTime, "Regenerated bundles must preserve the content cache.");
    foreach (var name in new[] { "small.js.br", "small.js.gz", "image.png.br", "image.png.gz" })
        Assert(!File.Exists(Path.Combine(scratch, name)), $"{name} must not exist: small and non-compressible files are served as is.");
    var changed = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("Changed browser bundle\n", 4096)));
    File.WriteAllBytes(file, changed);
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(1));
    Compress();
    Assert(Decode(File.ReadAllBytes($"{file}.br"), "br").SequenceEqual(changed), "A changed input must be recompressed.");
    File.WriteAllText(file, "tiny");
    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddSeconds(2));
    Compress();
    Assert(!File.Exists($"{file}.br") && !File.Exists($"{file}.gz"), "An input that shrank below the threshold must lose its sidecars.");
    Console.WriteLine("PASS bundle compression, exact decode, incremental reuse, changed inputs and identity fallbacks");
    return 0;
}
catch (CheckFailure failure)
{
    Console.Error.WriteLine($"FAIL {failure.Message}");
    return 1;
}
finally
{
    if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true);
}

void Compress()
{
    using var process = Process.Start(new ProcessStartInfo(dotnet)
    {
        ArgumentList = { "run", "--file", Path.Combine(root, "scripts", "browser-compress.cs"), "--", scratch, cache },
        WorkingDirectory = root,
    }) ?? throw new CheckFailure($"Could not start {dotnet}.");
    process.WaitForExit();
    if (process.ExitCode != 0) throw new CheckFailure($"scripts/browser-compress.cs exited with {process.ExitCode}.");
}

static byte[] Decode(byte[] packed, string suffix)
{
    using var input = new MemoryStream(packed);
    using Stream decoder = suffix == "br" ? new BrotliStream(input, CompressionMode.Decompress) : new GZipStream(input, CompressionMode.Decompress);
    using var output = new MemoryStream();
    decoder.CopyTo(output);
    return output.ToArray();
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new CheckFailure(message);
}

// The checkout root: the nearest directory above the working directory that holds Tereditor.slnx.
static string FindRoot()
{
    for (var directory = new DirectoryInfo(Environment.CurrentDirectory); directory is not null; directory = directory.Parent)
        if (File.Exists(Path.Combine(directory.FullName, "Tereditor.slnx"))) return directory.FullName;
    throw new InvalidOperationException("Run from inside the tereditor checkout.");
}

sealed class CheckFailure(string message) : Exception(message);
