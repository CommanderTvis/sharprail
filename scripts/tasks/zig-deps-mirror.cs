#!/usr/bin/env dotnet
// Rebuilds scripts/zig-deps.lock and the mirror archive for the pinned Ghostty revisions (run it after changing a Ghostty
// pin in mise.toml): scripts/mise run zig-deps:mirror. It builds the Ghostty libraries on this macOS host into a fresh Zig
// cache, so the package list is measured, then archives every package fetched from a host other than github.com and deps.files.ghostty.org (Codeberg today). Publishing is a
// separate, explicit step (printed at the end); this task uploads nothing.
#:include ../lib/Tasks.cs
using System.Runtime.InteropServices;
using static Tasks;

return Guard(() =>
{
    if (!OperatingSystem.IsMacOS()) throw new TaskFailure("Run on macOS: only it builds both Ghostty libraries.");
    var work = At(".bench", "zig-deps");
    DeleteAll(work);
    Directory.CreateDirectory(Path.Combine(work, "gc"));
    Directory.CreateDirectory(Path.Combine(work, "out"));
    // build-native.sh builds the view and libghostty-vt into one fresh cache; every other target (Linux, Windows, WASM)
    // fetches a subset of the VT packages.
    var architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
    Check("sh", [At("src", "Ghostty.Avalonia", "build-native.sh"), $"osx-{architecture}", Path.Combine(work, "native")], env: new Dictionary<string, string>
    {
        ["TEREDITOR_CACHE_DIR"] = Path.Combine(work, "cache"),
        ["ZIG_GLOBAL_CACHE_DIR"] = Path.Combine(work, "gc"),
        ["ZIG_DEPS_NO_RESTORE"] = "1",
    });
    Check(Dotnet, ["run", "--file", "scripts/zig-deps-pack.cs", "--", Path.Combine(work, "out"), Path.Combine(work, "gc"),
        Path.Combine(work, "cache", "ghostty-avalonia", "view-source"), Path.Combine(work, "cache", "ghostty-avalonia", "vt-source")]);
    File.Copy(Path.Combine(work, "out", "zig-deps.lock"), "scripts/zig-deps.lock", true);
    string Field(string key) => File.ReadLines("scripts/zig-deps.lock")
        .Select(line => line.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
        .FirstOrDefault(fields => fields.Length > 1 && fields[0] == key)?[1] ?? "";
    var (tag, asset) = (Field("tag"), Field("asset"));
    Console.WriteLine($"Publish with: gh release create {tag} --repo {Field("repo")} --title {tag} --notes 'Zig dependency mirror (scripts/zig-deps.sh)' {work}/out/{asset}");
    Console.WriteLine($"Then commit scripts/zig-deps.lock and remove {work}.");
    return 0;
});
