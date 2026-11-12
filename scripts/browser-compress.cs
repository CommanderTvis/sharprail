// Writes Brotli (.br) and gzip (.gz) sidecars beside the browser bundle's compressible files, reusing compressed bytes
// from a content-addressed cache. Run: dotnet run --file scripts/browser-compress.cs -- <bundle-dir> [cache-dir]
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;

if (args.Length < 1) { Console.Error.WriteLine("usage: dotnet run --file scripts/browser-compress.cs -- <bundle-dir> [cache-dir]"); return 2; }
var bundle = Path.GetFullPath(args[0]);
var cache = Path.GetFullPath(args.Length > 1 ? args[1] : CacheDir("browser-compression"));
Directory.CreateDirectory(cache);
HashSet<string> extensions = [".wasm", ".dll", ".js", ".mjs", ".json", ".html", ".css", ".svg", ".dat", ".blat", ".txt"];
long rawTotal = 0, brTotal = 0, gzipTotal = 0;

foreach (var file in Directory.EnumerateFiles(bundle, "*", SearchOption.AllDirectories).Where(file => extensions.Contains(Path.GetExtension(file))))
{
    var bytes = await File.ReadAllBytesAsync(file);
    var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
    rawTotal += bytes.Length;
    foreach (var suffix in new[] { "br", "gz" })
    {
        var sidecar = $"{file}.{suffix}";
        var cachedFile = Path.Combine(cache, $"{digest}.{(suffix == "br" ? "br11" : "gz9")}");
        byte[]? compressed = null;
        var cached = false;
        if (File.Exists(sidecar) && File.GetLastWriteTimeUtc(sidecar) >= File.GetLastWriteTimeUtc(file))
        {
            compressed = await File.ReadAllBytesAsync(sidecar);
            cached = true;
        }
        if (compressed is null && bytes.Length >= 1024 && File.Exists(cachedFile)) compressed = await File.ReadAllBytesAsync(cachedFile);
        compressed ??= bytes.Length >= 1024 ? Compress(bytes, suffix) : bytes;
        if (compressed.Length < bytes.Length)
        {
            if (!cached) await WriteAtomic(sidecar, compressed);
            if (!File.Exists(cachedFile)) await WriteAtomic(cachedFile, compressed);
        }
        else File.Delete(sidecar);
        if (suffix == "br") brTotal += Math.Min(bytes.Length, compressed.Length);
        else gzipTotal += Math.Min(bytes.Length, compressed.Length);
    }
}
Console.WriteLine($"Browser compressible assets: {rawTotal} bytes raw, {brTotal} Brotli, {gzipTotal} gzip (including identity fallbacks).");
return 0;

static byte[] Compress(byte[] bytes, string suffix)
{
    using var output = new MemoryStream();
    if (suffix == "br")
    {
        using var brotli = new BrotliStream(output, new BrotliCompressionOptions { Quality = 11 }, leaveOpen: true);
        brotli.Write(bytes);
    }
    else
    {
        using var gzip = new GZipStream(output, new ZLibCompressionOptions { CompressionLevel = 9 }, leaveOpen: true);
        gzip.Write(bytes);
    }
    return output.ToArray();
}

static async Task WriteAtomic(string file, byte[] bytes)
{
    var temporary = $"{file}.{Environment.ProcessId}.tmp";
    try
    {
        await File.WriteAllBytesAsync(temporary, bytes);
        File.Move(temporary, file, overwrite: true);
    }
    finally { File.Delete(temporary); }
}

// The machine-wide build cache every checkout shares: TEREDITOR_CACHE_DIR from mise.toml, else the same default, for
// scripts that run outside mise. A root created here is excluded from Time Machine on macOS, as scripts/native-cache.sh does.
static string CacheDir(params string[] parts)
{
    var root = Environment.GetEnvironmentVariable("TEREDITOR_CACHE_DIR") is { Length: > 0 } configured ? configured
        : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") is { Length: > 0 } local ? local : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Local"), "tereditor", "cache")
        : Path.Combine(Environment.GetEnvironmentVariable("XDG_CACHE_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"), "tereditor");
    if (!Directory.Exists(root))
    {
        Directory.CreateDirectory(root);
        if (OperatingSystem.IsMacOS())
        {
            try { using var tmutil = Process.Start(new ProcessStartInfo("tmutil") { ArgumentList = { "addexclusion", root }, RedirectStandardOutput = true, RedirectStandardError = true }); tmutil?.WaitForExit(); }
            catch (System.ComponentModel.Win32Exception) { }
        }
    }
    return Path.Combine([root, .. parts]);
}
