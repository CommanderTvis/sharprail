using System.Diagnostics;

using SharpRail.Checks.E2E;
using SharpRail.Host.Core;

namespace SharpRail.Checks;

/// <summary>Copy-on-write warm start of a created workspace: what is cloned, what is never touched, and the silent fallbacks.</summary>
internal static class WorkspaceCloneChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Run(string root)
    {
        IsolatedGit.ForProcess();
        var fixture = Path.Combine(root, "workspace-clone");
        var source = IsolatedGit.Repository(Path.Combine(fixture, "main"), (".gitignore", "obj/\nbin/\n.tools/\nnode_modules/\n"));
        foreach (var (path, text) in new[] { ("src/App/App.cs", "class App {}\n"), ("tracked/bin/keep.txt", "tracked\n") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, path))!);
            File.WriteAllText(Path.Combine(source, path), text);
        }
        IsolatedGit.Run(source, "add", "-f", "-A");
        IsolatedGit.Run(source, "commit", "-qm", "files");
        foreach (var (path, text) in new[] { (".tools/dotnet/sdk.bin", "sdk"), ("src/App/obj/cache.bin", "cache"), ("src/App/bin/App.dll", "dll"), ("node_modules/pkg/index.js", "module") })
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(source, path))!);
            File.WriteAllText(Path.Combine(source, path), text);
        }
        Directory.CreateSymbolicLink(Path.Combine(source, ".tools", "shared"), "/nonexistent-shared-store");
        var target = Path.Combine(fixture, "target");
        IsolatedGit.Run(source, "worktree", "add", "-q", "-b", "other", target);
        Directory.CreateDirectory(Path.Combine(target, "src/App/bin"));
        File.WriteAllText(Path.Combine(target, "src/App/bin/own.txt"), "own");

        // Fallbacks first: a refusing clone, and one that throws nothing, leave the target untouched.
        Require((await WorkspaceClone.CloneBuildStateAsync(source, target, CancellationToken.None, (_, _) => false)).Count == 0, "A refused clone reports nothing.");
        Require(!Directory.Exists(Path.Combine(target, ".tools")), "A refused clone leaves no partial directory.");
        Require((await WorkspaceClone.CloneBuildStateAsync(Path.Combine(fixture, "missing"), target, CancellationToken.None)).Count == 0, "A missing source is a no-op.");

        var timer = Stopwatch.StartNew();
        var cloned = await WorkspaceClone.CloneBuildStateAsync(source, target, CancellationToken.None);
        Console.WriteLine($"workspace-clone: {cloned.Count} paths in {timer.ElapsedMilliseconds} ms ({string.Join(", ", cloned)})");
        if (!WorkspaceClone.Supported) { Require(cloned.Count == 0, "Unsupported platforms clone nothing."); return; }
        Require(cloned.Order().SequenceEqual([".tools", "src/App/obj"]), "Cloned build directories: " + string.Join(", ", cloned));
        Require(File.ReadAllText(Path.Combine(target, "src/App/obj/cache.bin")) == "cache", "Cloned content matches.");
        Require(new FileInfo(Path.Combine(target, ".tools/shared")).LinkTarget == "/nonexistent-shared-store", "Symlinks are cloned as symlinks.");
        Require(!Directory.Exists(Path.Combine(target, "node_modules")), "Only named build directories are cloned.");
        Require(File.ReadAllText(Path.Combine(target, "src/App/bin/own.txt")) == "own" && !File.Exists(Path.Combine(target, "src/App/bin/App.dll")),
            "An existing target is not overwritten or merged.");
        Require(File.ReadAllText(Path.Combine(target, "tracked/bin/keep.txt")) == "tracked\n" && IsolatedGit.Run(target, "status", "--porcelain").Trim().Length == 0,
            "Tracked files are untouched and the clone is invisible to Git.");

        // Independent copies: writing one never reaches the other.
        File.WriteAllText(Path.Combine(target, "src/App/obj/cache.bin"), "changed");
        File.AppendAllText(Path.Combine(source, ".tools/dotnet/sdk.bin"), "!");
        Require(File.ReadAllText(Path.Combine(source, "src/App/obj/cache.bin")) == "cache" && File.ReadAllText(Path.Combine(target, ".tools/dotnet/sdk.bin")) == "sdk",
            "Clones share no write-through.");

        // A second run finds everything present.
        Require((await WorkspaceClone.CloneBuildStateAsync(source, target, CancellationToken.None)).Count == 0, "A repeated clone adds nothing.");
        Directory.Delete(fixture, true);
    }
}