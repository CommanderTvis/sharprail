using System.Diagnostics;
using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>Host-owned revert and undo: pure line arithmetic, then the same scenarios through the embedded host and a real gRPC host.</summary>
internal static class ChangeChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task<string> Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0) throw new IOException(await error);
        return await output;
    }

    // The checks run on POSIX hosts; Windows has no mode bits to assert.
    private static void Chmod(string path, UnixFileMode mode) { if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, mode); }
    private static UnixFileMode Mode(string path) => OperatingSystem.IsWindows() ? default : File.GetUnixFileMode(path);

    private static LineSpan S(int start, int count) => new(start, count);

    private static string Splice(string original, string modified, LineSpan from, LineSpan to) =>
        TextSplice.RevertedText(TextSplice.SplitLines(original), TextSplice.SplitLines(modified), from, to);

    public static async Task Run(string root)
    {
        CheckSplice();
        CheckRetention();
        var directory = Path.Combine(root, "change-checks");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var trash = Path.Combine(directory, "trash");
        var previous = Environment.GetEnvironmentVariable("SHARPRAIL_TRASH_DIR");
        Environment.SetEnvironmentVariable("SHARPRAIL_TRASH_DIR", trash);
        try
        {
            var localRoot = await Fixture(Path.Combine(directory, "local"));
            var remoteRoot = await Fixture(Path.Combine(directory, "remote"));
            var core = new ProjectServices(localRoot);
            IProjectServices local = new LocalProjectAdapter(core);
            await local.OpenProjectAsync(localRoot);
            var localLog = await Scenarios(local, localRoot);
            await CheckLifecycle(core, local, localRoot);

            await using var server = RemoteServer.Create(remoteRoot, IPAddress.Loopback, 0, "change-test");
            await server.StartAsync();
            try
            {
                var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
                using var remote = new RemoteProjectAdapter(new Uri(address), "change-test");
                await remote.OpenProjectAsync(remoteRoot);
                var remoteLog = await Scenarios(remote, remoteRoot);
                Require(localLog.SequenceEqual(remoteLog),
                    "Local and remote change results differ:\n" + string.Join('\n', localLog.Zip(remoteLog).Where(pair => pair.First != pair.Second).Select(pair => pair.First + "  <>  " + pair.Second)));
            }
            finally { await server.StopAsync(); }
        }
        finally { Environment.SetEnvironmentVariable("SHARPRAIL_TRASH_DIR", previous); }
        Console.WriteLine("PASS change revert and undo checks");
    }

    private static void CheckSplice()
    {
        Require(Splice("a\nb\nc\n", "a\nB\nc\n", S(2, 1), S(2, 1)) == "a\nb\nc\n", "LF hunk revert.");
        Require(Splice("a\r\nb\r\nc\r\n", "a\r\nB\r\nc\r\n", S(2, 1), S(2, 1)) == "a\r\nb\r\nc\r\n", "CRLF hunk revert.");
        Require(Splice("a\nb\n", "a\nB", S(2, 1), S(2, 1)) == "a\nb\n", "Both spans reaching the tail take the original's final newline.");
        Require(Splice("a\nb", "a\nB\n", S(2, 1), S(2, 1)) == "a\nb", "Both spans reaching the tail take the original's missing final newline.");
        Require(Splice("a\nb\nc\n", "a\nc\n", S(2, 1), S(2, 0)) == "a\nb\nc\n", "Insertion point on the modified side.");
        Require(Splice("a\nc\n", "a\nb\nc\n", S(2, 0), S(2, 1)) == "a\nc\n", "Insertion point on the original side.");
        Require(Splice("a\n", "a\nx\ny\n", S(2, 0), S(2, 2)) == "a\n", "Whole-tail removal.");
        Require(Splice("", "x\ny\n", S(1, 0), S(1, 2)) == "", "An empty original reverts to nothing.");
        Require(Splice("a\nb\r\nc\n", "a\nB\r\nc\n", S(2, 1), S(2, 1)) == "a\nb\r\nc\n", "A restored line keeps its own ending.");
        Require(Splice("p\nq", "p\nQ\nr\n", S(2, 1), S(2, 1)) == "p\nq\nr\n", "A restored unterminated line gets the file's dominant ending.");
        Require(Splice("a\nb\n", "a\nb", S(3, 0), S(3, 0)) == "a\nb\n", "Tail insertion points on both sides count as reaching the tail.");
        Require(Splice("a\nb\n", "a\nb", S(1, 1), S(1, 1)) == "a\nb", "A final line without a newline stays so when the tail is untouched.");
        Require(TextSplice.SplitLines("a\r\nb").SequenceEqual(["a\r\n", "b"]) && TextSplice.SplitLines("").Count == 0 && TextSplice.SplitLines("a\rb\n").Count == 1,
            "Only a line feed terminates a line.");
        Require(TextSplice.SpanFits(S(3, 0), 2) && !TextSplice.SpanFits(S(4, 0), 2) && !TextSplice.SpanFits(S(3, 1), 2) && TextSplice.SpanFits(S(2, 1), 2) &&
            !TextSplice.SpanFits(S(0, 1), 2) && !TextSplice.SpanFits(S(1, -1), 2) && !TextSplice.SpanFits(S(int.MaxValue, 1), 2),
            "Span bounds.");
    }

    private static void CheckRetention()
    {
        var ring = Enumerable.Range(1, 30).Select(index => (Id: index, Size: 1L)).ToList();
        var kept = ProjectServices.RetainReceipts(ring, held => held.Size);
        Require(kept.Count == 20 && kept[0].Id == 11 && kept[^1].Id == 30, "The ring keeps the newest 20 receipts.");
        var big = new[] { (Id: 1, Size: 40L), (Id: 2, Size: 40L), (Id: 3, Size: 40L) };
        Require(ProjectServices.RetainReceipts(big, held => held.Size, 20, 100).Select(held => held.Id).SequenceEqual([2, 3]), "Held bytes are capped, oldest evicted.");
        Require(ProjectServices.RetainReceipts(new[] { (Id: 1, Size: 500L) }, held => held.Size, 20, 100).Count == 1, "The newest receipt is kept even above the byte cap.");
    }

    private static async Task<string> Fixture(string repo)
    {
        Directory.CreateDirectory(repo);
        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "config", "core.autocrlf", "false");
        await Git(repo, "config", "user.name", "Checks");
        await Git(repo, "config", "user.email", "checks@localhost");
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "one\ntwo\nthree\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "c.txt"), "crlf\r\nline\r\n");
        await File.WriteAllTextAsync(Path.Combine(repo, "run.sh"), "#!/bin/sh\necho hi\n");
        Chmod(Path.Combine(repo, "run.sh"), (UnixFileMode)0x1ED);
        await File.WriteAllBytesAsync(Path.Combine(repo, "bin.dat"), [0xFF, 0xFE, 0x00, 0x01]);
        File.CreateSymbolicLink(Path.Combine(repo, "lnk"), "a.txt");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-q", "-m", "base");
        return repo;
    }

    private static string Project(ChangeReceipt receipt) =>
        $"{receipt.Path}|{receipt.Kind}|{receipt.Before.Hash}|{receipt.Before.ByteLength}|{receipt.Before.Mode}|{receipt.After.Hash}|{receipt.After.ByteLength}|{receipt.After.Mode}|{receipt.Trashed is not null}";

    private static async Task<string> Outcome(Func<ValueTask<ChangeReceipt>> action)
    {
        try { return Project(await action()); }
        catch (ChangeException error) { return "code:" + error.Code; }
        catch (UnauthorizedAccessException) { return "denied"; }
        catch (Grpc.Core.RpcException) { return "denied"; }
    }

    private static string Hash(string path) => ContentInfo.Sha256Hex(File.ReadAllBytes(path));

    private static async Task<List<string>> Scenarios(IProjectServices host, string repo)
    {
        var log = new List<string>();
        string P(string name) => Path.Combine(repo, name);
        async Task<ChangeReceipt> Revert(string path, string scope, RevertTarget target, string comparison = "")
        {
            var sides = await host.GetDiffSidesAsync(path, scope, comparison);
            return await host.RevertChangeAsync(path, scope, comparison, target, new(sides.OriginalHash, sides.ModifiedHash));
        }
        async Task Reset(string name) { await Git(repo, "checkout", "-q", "--", name); }
        var trash = Environment.GetEnvironmentVariable("SHARPRAIL_TRASH_DIR")!;

        // Hunks: modified, inserted and deleted blocks restore exact bytes, and the file's own mode survives.
        await File.WriteAllTextAsync(P("a.txt"), "one\nTWO\nthree\n");
        Chmod(P("a.txt"), (UnixFileMode)0x180);
        var receipt = await Revert("a.txt", "uncommitted", new(S(2, 1), S(2, 1)));
        Require(File.ReadAllText(P("a.txt")) == "one\ntwo\nthree\n" && (Mode(P("a.txt")) & (UnixFileMode)0x1FF) == (UnixFileMode)0x180, "Modified hunk revert.");
        Require(receipt.After.Hash == Hash(P("a.txt")) && receipt.Before.Mode == 0x180 && receipt.After.Mode == 0x180, "Receipt identities.");
        log.Add(Project(receipt));
        var undone = await host.UndoChangeAsync(receipt.Id, receipt.After.Hash);
        Require(File.ReadAllText(P("a.txt")) == "one\nTWO\nthree\n" && undone.Kind == "undo", "Undo restores the previous bytes.");
        log.Add(Project(undone));
        var redone = await host.UndoChangeAsync(undone.Id, undone.After.Hash);
        Require(File.ReadAllText(P("a.txt")) == "one\ntwo\nthree\n", "An undo can itself be undone once.");
        log.Add(Project(redone));
        log.Add(await Outcome(() => host.UndoChangeAsync(undone.Id, undone.After.Hash)));
        Require(log[^1] == "code:ReceiptUnknown", "A used receipt is gone.");
        await Reset("a.txt");
        await File.WriteAllTextAsync(P("a.txt"), "one\nnew\ntwo\nthree\n");
        log.Add(Project(await Revert("a.txt", "uncommitted", new(S(2, 0), S(2, 1)))));
        Require(File.ReadAllText(P("a.txt")) == "one\ntwo\nthree\n", "Inserted block revert.");
        await File.WriteAllTextAsync(P("a.txt"), "one\nthree\n");
        log.Add(Project(await Revert("a.txt", "uncommitted", new(S(2, 1), S(2, 0)))));
        Require(File.ReadAllText(P("a.txt")) == "one\ntwo\nthree\n", "Deleted block revert.");
        await File.WriteAllTextAsync(P("c.txt"), "crlf\r\nCHANGED\r\n");
        log.Add(Project(await Revert("c.txt", "uncommitted", new(S(2, 1), S(2, 1)))));
        Require(File.ReadAllText(P("c.txt")) == "crlf\r\nline\r\n", "CRLF files keep their endings.");

        // Whole files: modified restored, deleted restored with the executable bit, added moved to the trash.
        await File.WriteAllTextAsync(P("a.txt"), "rewritten\n");
        log.Add(Project(await Revert("a.txt", "uncommitted", new())));
        Require(File.ReadAllText(P("a.txt")) == "one\ntwo\nthree\n", "Whole-file revert.");
        File.Delete(P("run.sh"));
        log.Add(Project(await Revert("run.sh", "uncommitted", new())));
        Require(File.ReadAllText(P("run.sh")).Contains("echo hi", StringComparison.Ordinal) && (Mode(P("run.sh")) & UnixFileMode.UserExecute) != 0, "A deleted file returns with its Git mode.");
        await File.WriteAllTextAsync(P("new.txt"), "added\n");
        var trashed = await Revert("new.txt", "uncommitted", new());
        Require(!File.Exists(P("new.txt")) && trashed.Trashed is not null && trashed.After.Hash is null && File.ReadAllText(Path.Combine(trash, "new.txt")) == "added\n", "An untracked file goes to the trash.");
        Require(Directory.GetFileSystemEntries(repo).All(entry => !Path.GetFileName(entry).StartsWith(".sharprail-", StringComparison.Ordinal)), "No claim or temp file may remain.");
        log.Add(Project(trashed));
        log.Add(Project(await host.UndoChangeAsync(trashed.Id, null)));
        Require(File.Exists(P("new.txt")), "Undo of a trash revert restores the file.");
        log.Add(await Outcome(() => host.RevertChangeAsync("new.txt", "uncommitted", "", new(), new(null, null))));
        File.Delete(P("new.txt"));
        log.Add(await Outcome(() => host.RevertChangeAsync("new.txt", "uncommitted", "", new(), new(null, null))));
        Require(log[^1] == "code:RangeInvalid", "No change to revert.");

        // Compare-and-swap: nothing is written on a mismatch.
        await File.WriteAllTextAsync(P("a.txt"), "one\nTWO\nthree\n");
        var seen = await host.GetDiffSidesAsync("a.txt", "uncommitted");
        await File.WriteAllTextAsync(P("a.txt"), "one\nTWO!\nthree\n");
        var stamp = File.GetLastWriteTimeUtc(P("a.txt"));
        log.Add(await Outcome(() => host.RevertChangeAsync("a.txt", "uncommitted", "", new(S(2, 1), S(2, 1)), new(seen.OriginalHash, seen.ModifiedHash))));
        Require(log[^1] == "code:StaleView" && File.ReadAllText(P("a.txt")) == "one\nTWO!\nthree\n" && File.GetLastWriteTimeUtc(P("a.txt")) == stamp, "A stale modified side writes nothing.");
        var working = await host.GetDiffSidesAsync("a.txt", "working");
        await Git(repo, "add", "a.txt");
        log.Add(await Outcome(() => host.RevertChangeAsync("a.txt", "working", "", new(S(2, 1), S(2, 1)), new(working.OriginalHash, working.ModifiedHash))));
        Require(log[^1] == "code:StaleView", "A stale original side is refused.");
        await Git(repo, "reset", "-q", "--", "a.txt");
        await Reset("a.txt");
        var racing = await host.GetDiffSidesAsync("a.txt", "uncommitted");
        await File.WriteAllTextAsync(P("a.txt"), "one\nTWO\nthree\n");
        racing = await host.GetDiffSidesAsync("a.txt", "uncommitted");
        var request = () => Outcome(() => host.RevertChangeAsync("a.txt", "uncommitted", "", new(S(2, 1), S(2, 1)), new(racing.OriginalHash, racing.ModifiedHash)));
        var results = await Task.WhenAll(request(), request());
        Require(results.Count(result => result.StartsWith("a.txt|revert", StringComparison.Ordinal)) == 1 && results.Count(result => result == "code:StaleView") == 1, "Concurrent reverts are serialized.");
        log.Add(results.Single(result => result.StartsWith("a.txt|revert", StringComparison.Ordinal)));

        // Refusals.
        log.Add(await Outcome(() => host.RevertChangeAsync("a.txt", "commit", "HEAD", new(), new(null, null))));
        Require(log[^1] == "code:ScopeImmutable", "A commit scope is immutable.");
        log.Add(await Outcome(() => host.RevertChangeAsync("a.txt", "staged", "", new(), new(null, null))));
        Require(log[^1] == "code:ScopeImmutable", "The staged scope is immutable.");
        await File.WriteAllBytesAsync(P("bin.dat"), [0xFF, 0x00, 0x80]);
        var blob = ContentInfo.Sha256Hex(await File.ReadAllBytesAsync(P("bin.dat")));
        var committed = ContentInfo.Sha256Hex([0xFF, 0xFE, 0x00, 0x01]);
        log.Add(await Outcome(() => host.RevertChangeAsync("bin.dat", "uncommitted", "", new(S(1, 1), S(1, 1)), new(committed, blob))));
        Require(log[^1] == "code:RangeInvalid", "A range revert of invalid UTF-8 is invalid.");
        log.Add(Project(await host.RevertChangeAsync("bin.dat", "uncommitted", "", new(), new(committed, blob))));
        Require(Hash(P("bin.dat")) == committed, "A byte-only file can be reverted as a whole.");
        var original = Hash(P("a.txt"));
        File.Delete(P("a.txt"));
        log.Add(await Outcome(() => host.RevertChangeAsync("a.txt", "uncommitted", "", new(S(1, 1), S(1, 0)), new(original, null))));
        Require(log[^1] == "code:RangeInvalid", "A range revert of an absent file is invalid.");
        await Git(repo, "checkout", "-q", "--", "a.txt");
        File.Delete(P("c.txt"));
        File.CreateSymbolicLink(P("c.txt"), "a.txt");
        log.Add(await Outcome(() => host.RevertChangeAsync("c.txt", "uncommitted", "", new(), new(null, null))));
        Require(log[^1] == "code:UnsupportedChange", "A symbolic link in the worktree is unsupported.");
        File.Delete(P("c.txt")); await Git(repo, "checkout", "-q", "--", "c.txt");
        File.Delete(P("lnk"));
        log.Add(await Outcome(() => host.RevertChangeAsync("lnk", "uncommitted", "", new(), new(Hash(P("a.txt")), null))));
        Require(log[^1] == "code:UnsupportedChange", "A symbolic link in Git is unsupported.");
        await Git(repo, "checkout", "-q", "--", "lnk");
        Chmod(P("a.txt"), (UnixFileMode)0x1ED);
        var same = Hash(P("a.txt"));
        log.Add(await Outcome(() => host.RevertChangeAsync("a.txt", "uncommitted", "", new(), new(same, same))));
        Require(log[^1] == "code:UnsupportedChange", "A mode-only change is unsupported.");
        Chmod(P("a.txt"), (UnixFileMode)0x1A4);
        log.Add(await Outcome(() => host.RevertChangeAsync("a.txt", "branch", "no-such-branch", new(), new(same, same))));
        Require(log[^1] == "code:StaleView", "An original ref that no longer resolves is stale.");
        log.Add(await Outcome(() => host.UndoChangeAsync("00000000000000000000000000", null)));
        Require(log[^1] == "code:ReceiptUnknown", "An unknown receipt is reported.");
        log.Add(await Outcome(() => host.RevertChangeAsync("../outside.txt", "uncommitted", "", new(), new(null, null))));
        log.Add(await Outcome(() => host.RevertChangeAsync(".git/config", "uncommitted", "", new(), new(null, null))));
        Require(log[^2] == "denied" && log[^1] == "denied", "Escapes and .git are refused.");
        if (Environment.UserName != "root")
        {
            await File.WriteAllTextAsync(P("a.txt"), "locked\n");
            Chmod(P("a.txt"), UnixFileMode.None);
            log.Add(await Outcome(() => host.RevertChangeAsync("a.txt", "uncommitted", "", new(), new(original, null))));
            Chmod(P("a.txt"), (UnixFileMode)0x1A4);
            Require(log[^1] == "denied" && File.ReadAllText(P("a.txt")) == "locked\n", "An unreadable file is not absence.");
            await Git(repo, "checkout", "-q", "--", "a.txt");
        }

        // Undo is refused after an external edit and leaves the file alone.
        await File.WriteAllTextAsync(P("a.txt"), "one\nTWO\nthree\n");
        var guarded = await Revert("a.txt", "uncommitted", new(S(2, 1), S(2, 1)));
        await File.WriteAllTextAsync(P("a.txt"), "external\n");
        log.Add(await Outcome(() => host.UndoChangeAsync(guarded.Id, guarded.After.Hash)));
        Require(log[^1] == "code:StaleView" && File.ReadAllText(P("a.txt")) == "external\n", "Undo after an external edit is stale.");
        await Git(repo, "checkout", "-q", "--", "a.txt");

        // Eviction: the 21st receipt pushes the first out.
        await File.WriteAllTextAsync(P("a.txt"), "x0\n");
        var first = await Revert("a.txt", "uncommitted", new());
        for (var index = 1; index <= 20; index++)
        {
            await File.WriteAllTextAsync(P("a.txt"), $"x{index}\n");
            await Revert("a.txt", "uncommitted", new());
        }
        log.Add(await Outcome(() => host.UndoChangeAsync(first.Id, first.After.Hash)));
        Require(log[^1] == "code:ReceiptUnknown", "Only the newest 20 receipts are held.");
        return log;
    }

    private static async Task CheckLifecycle(ProjectServices core, IProjectServices host, string repo)
    {
        await File.WriteAllTextAsync(Path.Combine(repo, "a.txt"), "closing\n");
        var sides = await host.GetDiffSidesAsync("a.txt", "uncommitted");
        var receipt = await host.RevertChangeAsync("a.txt", "uncommitted", "", new(), new(sides.OriginalHash, sides.ModifiedHash));
        var other = Path.Combine(Path.GetDirectoryName(repo)!, "other");
        Directory.CreateDirectory(other);
        await core.OpenProjectAsync(other);
        await core.OpenProjectAsync(repo);
        try { await host.UndoChangeAsync(receipt.Id, receipt.After.Hash); throw new InvalidOperationException("A receipt survived leaving its workspace."); }
        catch (ChangeException error) { Require(error.Code == ChangeFailure.ReceiptUnknown, "Receipts are dropped with the workspace."); }
    }
}