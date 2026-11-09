// Prints the ManagedRuntimes pin entries for .NET SDK versions, after checking the publisher's SHA-512.
// Run from the checkout root: dotnet run scripts/pin-dotnet-sdk.cs -- 10.0.401 10.0.403 [--rids linux-x64 linux-arm64 osx-arm64 osx-x64 win-x64]
//
// For each SDK version and platform it reads the official release metadata, downloads the archive, requires the
// publisher's SHA-512 to match, and prints a C# entry with our own SHA-256 (the value the host verifies before
// extraction). Review the printed entries, then paste them into src/SharpRail.Host.Core/Language/ManagedRuntimes.cs.
// Nothing is written to disk.
using System.Security.Cryptography;
using System.Text.Json;

var platform = new Dictionary<string, string>
{
    ["linux-x64"] = "linux-x86_64", ["linux-arm64"] = "linux-aarch64", ["osx-arm64"] = "macos-aarch64", ["osx-x64"] = "macos-x86_64", ["win-x64"] = "windows-x86_64",
};
const string Host = "https://builds.dotnet.microsoft.com/";

var versions = new List<string>();
var rids = new List<string>();
var current = versions;
foreach (var argument in args)
{
    if (argument == "--rids") { current = rids; rids.Clear(); }
    else current.Add(argument);
}
if (versions.Count == 0) { Console.Error.WriteLine("usage: dotnet run scripts/pin-dotnet-sdk.cs -- <sdk version>... [--rids <rid>...]"); return 2; }
if (rids.Count == 0) rids.AddRange(platform.Keys);

using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(600) };
async Task<byte[]> Fetch(string url)
{
    if (!url.StartsWith(Host, StringComparison.Ordinal)) throw new Fail($"refusing {url}: not on {Host}");
    return await http.GetByteArrayAsync(url);
}

try
{
    var metadata = new Dictionary<string, JsonElement>();
    foreach (var version in versions)
    {
        var channel = string.Join('.', version.Split('.').Take(2));
        if (!metadata.ContainsKey(channel)) metadata[channel] = JsonDocument.Parse(await Fetch($"{Host}dotnet/release-metadata/{channel}/releases.json")).RootElement;
        (string Date, JsonElement Sdk)? found = null;
        foreach (var release in metadata[channel].GetProperty("releases").EnumerateArray())
        {
            var candidates = new List<JsonElement>();
            if (release.TryGetProperty("sdk", out var single) && single.ValueKind == JsonValueKind.Object) candidates.Add(single);
            if (release.TryGetProperty("sdks", out var several) && several.ValueKind == JsonValueKind.Array) candidates.AddRange(several.EnumerateArray());
            foreach (var sdk in candidates)
                if (sdk.TryGetProperty("version", out var v) && v.GetString() == version)
                    found = (release.GetProperty("release-date").GetString()!, sdk);
        }
        if (found is null) throw new Fail($"{version} is not in the {channel} release metadata");
        var (date, sdkEntry) = found.Value;
        foreach (var rid in rids)
        {
            JsonElement? entry = null;
            foreach (var file in sdkEntry.GetProperty("files").EnumerateArray())
            {
                var name = file.GetProperty("name").GetString()!;
                if (file.GetProperty("rid").GetString() == rid && name.StartsWith("dotnet-sdk-", StringComparison.Ordinal)
                    && (name.EndsWith(".tar.gz", StringComparison.Ordinal) || name.EndsWith(".zip", StringComparison.Ordinal))) { entry = file; break; }
            }
            if (entry is null) throw new Fail($"{version} has no SDK archive for {rid}");
            var url = entry.Value.GetProperty("url").GetString()!;
            var publisherHash = entry.Value.GetProperty("hash").GetString()!.ToLowerInvariant();
            var data = await Fetch(url);
            if (Convert.ToHexStringLower(SHA512.HashData(data)) != publisherHash) throw new Fail($"{version} {rid}: publisher SHA-512 does not match; nothing printed for it");
            Console.WriteLine($"    new(\"dotnet-sdk\", \"{version}\", \"{platform[rid]}\", \"{url}\",");
            Console.WriteLine($"        \"{Convert.ToHexStringLower(SHA256.HashData(data))}\", \"{publisherHash}\", \"{(entry.Value.GetProperty("name").GetString()!.EndsWith(".zip", StringComparison.Ordinal) ? "zip" : "tar.gz")}\",");
            Console.WriteLine($"        \"SHA-512 from {Host}dotnet/release-metadata/{version[..version.LastIndexOf('.')]}/releases.json (release {date}), verified against the download; SHA-256 computed from that download\"),");
        }
    }
    return 0;
}
catch (Fail failure)
{
    Console.Error.WriteLine(failure.Message);
    return 1;
}

sealed class Fail(string message) : Exception(message);
