using System.Security.Cryptography;

namespace SharpRail.Host.Core.Context;

internal sealed record ContextBundle(string Version, string Platform, Uri Url, string Sha256);

internal static class ContextDistribution
{
    internal const string Version = "0.9.16.1161";
    internal const string TermsUrl = "https://www.jetbrains.com/legal/docs/terms/jetbrains-context-cli-eap/";

    internal static ContextBundle Bundle(string os, string arch)
    {
        var hash = (os, arch) switch
        {
            ("macos", "arm64") => "fea673d93d2d4435956a6bfcc153a0692592135a048cbc4d0199969e754aadfc",
            ("macos", "x64") => "45e26b32d267917c9e8ea4e30c67c2f2586de7a7c9ba75f6f99ad34866e1cf3f",
            ("linux", "arm64") => "63f733dc00c341dff67e88eed67bf3f059101ff265f55643b1af91ed39c596fb",
            ("linux", "x64") => "890033b5ae9379976003ea3ea98846dfbe247ba7b6996e23d3a478ae87f1e2e8",
            _ => throw new PlatformNotSupportedException($"No verified JetBrains Context artifact for {os}/{arch}.")
        };
        return new(Version, os + "-" + arch,
            new($"https://download.jetbrains.com/jetbrains-context/builds/v{Version}/context-native-{os}-{arch}-{Version}"), hash);
    }

    internal static string Installation(string storage, ContextBundle bundle) => Path.Combine(Path.GetFullPath(storage), "servers", "jbcontext", bundle.Version + "-" + bundle.Platform);

    internal static async Task<string> InstallAsync(string storage, ContextBundle bundle, HttpClient http,
        CancellationToken token)
    {
        await new ContextConsent(storage, http, bundle).RequireAsync(token);
        if (bundle.Url.Scheme != "https" || bundle.Url.Host != "download.jetbrains.com" ||
            new[] { bundle.Version, bundle.Platform }.Any(value => value.Length == 0 || value.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('.' or '-'))) ||
            bundle.Sha256.Length != 64 || bundle.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
            throw new IOException("Invalid JetBrains Context artifact identity.");

        var parent = Path.Combine(Path.GetFullPath(storage), "servers", "jbcontext");
        Directory.CreateDirectory(parent);
        var destination = Path.Combine(parent, bundle.Version + "-" + bundle.Platform);
        FileStream lease;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try { lease = new(destination + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); break; }
            catch (IOException) { await Task.Delay(50, token); }
        }
        await using var held = lease;
        await new ContextConsent(storage, http, bundle).RequireAsync(token);
        if (File.Exists(destination) && await MatchesAsync(destination, bundle.Sha256, token)) return destination;

        var partial = destination + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            using var response = await http.GetAsync(bundle.Url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            const long maximumBytes = 512L * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximumBytes) throw new IOException("JetBrains Context artifact exceeds the download limit.");
            await using var input = await response.Content.ReadAsStreamAsync(token);
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += read;
                    if (total > maximumBytes) throw new IOException("JetBrains Context artifact exceeds the download limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                }
            }
            if (!await MatchesAsync(partial, bundle.Sha256, token)) throw new IOException("JetBrains Context download failed SHA-256 verification.");
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(partial, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            File.Move(partial, destination, true);
            return destination;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    internal static async Task<bool> MatchesAsync(string path, string expected, CancellationToken token)
    {
        if (new FileInfo(path).LinkTarget is not null) throw new IOException("JetBrains Context managed artifacts cannot be symbolic links.");
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }
}