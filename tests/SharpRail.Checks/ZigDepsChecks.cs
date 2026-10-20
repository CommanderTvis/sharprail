using System.Text.RegularExpressions;

namespace SharpRail.Checks;

/// <summary>
/// Fails when scripts/zig-deps.lock (the pinned mirror of the Zig packages hosted on neither github.com nor deps.files.ghostty.org) no longer matches the
/// Ghostty revisions pinned in mise.toml, is malformed, or a native build path stopped restoring it.
/// </summary>
internal static class ZigDepsChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run()
    {
        if (BoundaryChecks.Checkout() is not { } root) { Console.WriteLine("SKIP zig-deps: no source checkout"); return; }
        var lines = File.ReadAllLines(Path.Combine(root, "scripts", "zig-deps.lock")).Where(l => l.Length > 0 && l[0] != '#').Select(l => l.Split(' ')).ToList();
        string One(string key) => lines.Single(l => l[0] == key)[1];
        var mise = File.ReadAllText(Path.Combine(root, "mise.toml"));
        foreach (var pin in new[] { "GHOSTTY_VIEW_COMMIT", "GHOSTTY_VT_COMMIT" })
        {
            var expected = Regex.Match(mise, pin + @" = ""([0-9a-f]{40})""").Groups[1].Value;
            var locked = lines.Single(l => l[0] == "pin" && l[1] == pin)[2];
            Require(expected == locked, $"zig-deps.lock was built for {pin} {locked} but mise.toml pins {expected}; run scripts/mise run zig-deps:mirror.");
        }
        Require(Regex.IsMatch(One("sha256"), "^[0-9a-f]{64}$"), "zig-deps.lock needs a SHA-256 for the mirror archive.");
        Require(One("asset") == One("tag") + ".tar.gz", "The mirror asset must be named after its release tag.");
        var packages = lines.Where(l => l[0] == "pkg").ToList();
        Require(packages.Count > 0, "zig-deps.lock lists no packages.");
        foreach (var package in packages)
        {
            Require(package.Length == 4 && package[2] is "dir" or "tgz" or "dir+tgz", $"Malformed package line for {package[1]}.");
            Require(!package[3].Contains("github.com") && !package[3].Contains("deps.files.ghostty.org"), $"{package[1]} is fetched upstream by design and needs no mirror.");
        }
        var ident = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', packages.Select(p => $"{p[1]} {p[2]}")))))[..12];
        Require(One("tag") == "zig-deps-" + ident, "The mirror tag does not match the package list; regenerate with scripts/mise run zig-deps:mirror.");
        foreach (var (file, marker) in new[]
        {
            ("src/Ghostty.Avalonia/build-native.sh", "zig-deps.sh\" dir"), ("src/Ghostty.Avalonia/build-native.sh", "zig-deps.sh\" tgz"),
            ("src/Ghostty.Avalonia/build-linux.sh", "zig-deps.sh"), ("src/Ghostty.Avalonia/build-android.sh", "zig-deps.sh"),
            ("scripts/wasm-native.sh", "zig-deps.sh"), ("src/Ghostty.Avalonia/build-windows.ps1", "Restore-ZigDeps")
        })
            Require(File.ReadAllText(Path.Combine(root, file)).Contains(marker), $"{file} does not restore the Zig dependency mirror.");
    }
}