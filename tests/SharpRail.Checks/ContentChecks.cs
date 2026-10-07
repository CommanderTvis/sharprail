using System.Diagnostics;
using System.Net;
using System.Text;

using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.UI.Rendering;

namespace SharpRail.Checks;

/// <summary>Byte classification, byte-only diff sides, immutable original reads and inert active content, locally and over gRPC.</summary>
internal static class ContentChecks
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] PngEdited = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 9, 9, 9];
    private static readonly byte[] Pdf = "%PDF-1.4\nplain ascii body\n%%EOF\n"u8.ToArray();
    private const string Svg = "<?xml version=\"1.0\"?>\n<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"><script>alert(2)</script><image href=\"http://example.invalid/x.png\"/></svg>";

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

    private static ContentMetadata Classify(string text, string path = "f") => ContentClassifier.Classify(Encoding.UTF8.GetBytes(text), path);

    private static void CheckClassifier()
    {
        var png = ContentClassifier.Classify(Png, "x.txt");
        Require(png is { IsText: false, MediaType: "image/png", ByteLength: 11 } && png.Sha256 == ContentInfo.Sha256Hex(Png), "PNG magic wins over the filename.");
        Require(ContentClassifier.Classify([0xFF, 0xD8, 0xFF, 0xE0], "x").MediaType == "image/jpeg", "JPEG magic.");
        Require(ContentClassifier.Classify(Pdf, "x") is { IsText: false, MediaType: "application/pdf" }, "An ASCII-only PDF is byte-only.");
        Require(ContentClassifier.Classify("RIFF\0\0\0\0WEBPVP8 "u8, "x").MediaType == "image/webp", "WebP magic.");
        var svg = Classify(Svg, "x.svg");
        Require(svg is { IsText: true, MediaType: "image/svg+xml", IsActive: true }, "SVG is text with an active media type.");
        Require(Classify("<html><script>1</script></html>", "x.html") is { IsText: true, MediaType: "text/html", IsActive: true }, "HTML is active by filename.");
        Require(Classify("hello\n", "x.md").MediaType == "text/markdown" && Classify("hello\n", "x.bin").MediaType is null, "The filename is consulted when the bytes are silent.");
        Require(ContentClassifier.Classify("a\0b"u8, "x") is { IsText: false, MediaType: null }, "NUL in the sniffed prefix is byte-only.");
        Require(!ContentClassifier.Classify([0xC3, 0x28], "x").IsText, "Invalid UTF-8 is byte-only.");
        var oid = new string('a', 64);
        Require(Classify($"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize 12\n").MediaType == "application/vnd.git-lfs" &&
            Classify($"version https://git-lfs.github.com/spec/v1\noid sha256:{oid}\nsize 12\nextra\n").MediaType is null, "Only the exact three-line LFS pointer is one.");
        Require(ContentClassifier.Absent is { Sha256: null, ByteLength: null }, "An absent resource has no hash.");
    }

    public static async Task Run(string root)
    {
        CheckClassifier();
        var directory = Path.Combine(root, "content-checks");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var localRoot = Path.Combine(directory, "local");
        var remoteRoot = Path.Combine(directory, "remote");
        IProjectServices local = new LocalProjectAdapter(new ProjectServices(localRoot));
        Directory.CreateDirectory(localRoot);
        await Fixture(localRoot);
        await local.OpenProjectAsync(localRoot);
        var localLog = await Scenarios(local, localRoot);

        Directory.CreateDirectory(remoteRoot);
        await Fixture(remoteRoot);
        await using var server = RemoteServer.Create(remoteRoot, IPAddress.Loopback, 0, "content-test");
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteProjectAdapter(new Uri(address), "content-test");
            await remote.OpenProjectAsync(remoteRoot);
            var remoteLog = await Scenarios(remote, remoteRoot);
            Require(localLog.SequenceEqual(remoteLog), "Local and remote content results differ:\n" + string.Join('\n', localLog.Zip(remoteLog).Where(pair => pair.First != pair.Second).Select(pair => pair.First + "  <>  " + pair.Second)));
        }
        finally { await server.StopAsync(); }
        Console.WriteLine("PASS byte-only content checks");
    }

    /// <summary>Headless: a byte-only diff shows a card per side and decodes images as pixels, never as replacement characters.</summary>
    public static void RunUi(string root)
    {
        var (host, diff) = Task.Run(() => PrepareUi(root)).GetAwaiter().GetResult();
        SynchronizationContext.SetSynchronizationContext(new AvaloniaSynchronizationContext(Dispatcher.UIThread, DispatcherPriority.Normal));
        CheckUi(host, diff);
    }

    private static async Task<(IProjectServices Host, string Diff)> PrepareUi(string root)
    {
        var repo = Path.Combine(root, "content-checks", "ui");
        if (Directory.Exists(repo)) Directory.Delete(repo, recursive: true);
        Directory.CreateDirectory(repo);
        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "config", "user.name", "Checks");
        await Git(repo, "config", "user.email", "checks@localhost");
        // A one-pixel PNG, so Avalonia can really decode it.
        var pixel = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        await File.WriteAllBytesAsync(Path.Combine(repo, "pic.png"), pixel);
        await File.WriteAllBytesAsync(Path.Combine(repo, "doc.pdf"), Pdf);
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-q", "-m", "base");
        await File.WriteAllBytesAsync(Path.Combine(repo, "pic.png"), [.. pixel, 0]);
        await File.WriteAllBytesAsync(Path.Combine(repo, "doc.pdf"), [.. Pdf, .. "more\n"u8.ToArray()]);
        IProjectServices host = new LocalProjectAdapter(new ProjectServices(repo));
        await host.OpenProjectAsync(repo);
        return (host, await host.GetDiffAsync("pic.png", "uncommitted"));
    }

    private static void CheckUi(IProjectServices host, string diff)
    {
        Require(BinaryDiffView.IsBinaryDiff(diff) && !BinaryDiffView.IsBinaryDiff("diff --git a/x b/x\n@@ -1 +1 @@\n-Binary files x\n+y\n"), "Binary diffs are recognised by Git's notice and the absence of hunks.");
        foreach (var (path, images) in new[] { ("pic.png", 2), ("doc.pdf", 0) })
        {
            using var view = new BinaryDiffView(host, path, "uncommitted", "");
            var deadline = DateTime.UtcNow.AddSeconds(15);
            int Cards() => view.GetLogicalDescendants().OfType<StackPanel>().Count(panel => panel.Name?.StartsWith("BinarySide_", StringComparison.Ordinal) == true);
            while (Cards() < 2 && DateTime.UtcNow < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(20); }
            Require(Cards() == 2, path + ": both side cards must be shown.");
            Require(view.GetLogicalDescendants().OfType<Image>().Count() == images, path + ": raster images are drawn as pixels and other types only described.");
            var text = string.Concat(view.GetLogicalDescendants().OfType<TextBlock>().Select(block => block.Text));
            Require(!text.Contains('\uFFFD') && text.Contains("SHA-256", StringComparison.Ordinal), path + ": no replacement characters, identity shown.");
        }
        Console.WriteLine("PASS byte-only content UI checks");
    }

    private static async Task Fixture(string repo)
    {
        await Git(repo, "init", "-q", "-b", "main");
        await Git(repo, "config", "user.name", "Checks");
        await Git(repo, "config", "user.email", "checks@localhost");
        await File.WriteAllTextAsync(Path.Combine(repo, "t.txt"), "text\n");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-q", "-m", "root");
        await File.WriteAllBytesAsync(Path.Combine(repo, "pic.png"), Png);
        await File.WriteAllBytesAsync(Path.Combine(repo, "doc.pdf"), Pdf);
        await File.WriteAllTextAsync(Path.Combine(repo, "pic.svg"), Svg);
        await File.WriteAllTextAsync(Path.Combine(repo, "page.html"), "<html><script>alert(1)</script></html>");
        await Git(repo, "add", "-A");
        await Git(repo, "commit", "-q", "-m", "binary");
        await File.WriteAllBytesAsync(Path.Combine(repo, "pic.png"), PngEdited);
        await File.WriteAllBytesAsync(Path.Combine(repo, "new.png"), Png);
        await File.WriteAllBytesAsync(Path.Combine(repo, "nul.dat"), "a\0b\n"u8.ToArray());
    }

    private static string Show(ContentMetadata? info) => info is null ? "null" : $"{info.Sha256}|{info.ByteLength}|{info.IsText}|{info.MediaType}|{info.IsActive}";

    private static async Task<string> Fails(Func<ValueTask<ContentBytes>> read)
    {
        try { await read(); return "read"; }
        catch (Exception error) when (error is not OperationCanceledException) { return "refused"; }
    }

    private static async Task<List<string>> Scenarios(IProjectServices host, string repo)
    {
        var log = new List<string>();
        var head = (await Git(repo, "rev-parse", "HEAD")).Trim();
        var root = (await Git(repo, "rev-parse", "HEAD~1")).Trim();

        var sides = await host.GetDiffSidesAsync("pic.png", "uncommitted");
        log.Add($"png {sides.Original.Length}|{sides.Modified.Length}|{Show(sides.OriginalInfo)}|{Show(sides.ModifiedInfo)}|{sides.OriginalCommit == head}|{sides.OriginalRevision == head}|{sides.ModifiedRevision is null}");
        Require(sides.Original == "" && sides.Modified == "" && sides.OriginalInfo is { IsText: false, MediaType: "image/png" } && sides.ModifiedInfo!.Sha256 == ContentInfo.Sha256Hex(PngEdited),
            "A byte-only diff side travels empty with its metadata and a byte-only working side does not fail.");
        Require(sides.OriginalCommit == head && sides.OriginalHash == sides.OriginalInfo!.Sha256, "The original side is frozen to the HEAD commit.");

        var pdf = await host.GetDiffSidesAsync("doc.pdf", "branch", root);
        log.Add($"pdf {Show(pdf.OriginalInfo)}|{Show(pdf.ModifiedInfo)}|{pdf.OriginalCommit == root}");
        Require(pdf.OriginalInfo!.Sha256 is null && pdf.ModifiedInfo!.MediaType == "application/pdf" && !pdf.ModifiedInfo.IsText && pdf.OriginalCommit == root,
            "A branch diff pins the merge base and reports an added PDF byte-only.");

        var added = await host.GetDiffSidesAsync("new.png", "untracked");
        log.Add($"added {Show(added.OriginalInfo)}|{Show(added.ModifiedInfo)}|{added.OriginalCommit}|{added.OriginalRevision}");
        Require(added.OriginalInfo!.Sha256 is null && added.ModifiedInfo!.MediaType == "image/png" && added.OriginalCommit is null, "An untracked image has an absent original and no commit.");
        Require((await host.GetDiffAsync("new.png", "untracked")).Contains("Binary files /dev/null and b/new.png differ", StringComparison.Ordinal), "An untracked byte-only file's diff is a binary notice, not a failure.");
        log.Add("untracked-diff-binary");

        var nul = await host.GetDiffSidesAsync("nul.dat", "untracked");
        Require(nul.Modified == "" && nul.ModifiedInfo is { IsText: false }, "NUL-bearing bytes are byte-only.");

        var first = await host.GetDiffSidesAsync("t.txt", "commit", head);
        Require(first.OriginalCommit == root && first.Original == "text\n", "Text sides still decode.");
        var rootSides = await host.GetDiffSidesAsync("t.txt", "commit", root);
        log.Add($"root {rootSides.OriginalCommit is null}|{Show(rootSides.OriginalInfo)}|{Show(rootSides.ModifiedInfo)}");
        Require(rootSides.OriginalCommit is null && rootSides.ModifiedRevision == root, "A root commit has no original commit.");

        var svg = await host.GetDiffSidesAsync("pic.svg", "commit", head);
        log.Add($"svg {Show(svg.ModifiedInfo)}");
        Require(svg.Modified == Svg && svg.ModifiedInfo is { IsText: true, IsActive: true }, "Active SVG is text flagged active.");
        var html = await host.ReadContentBytesAsync("page.html", head);
        log.Add($"html {Show(html.Info)}");
        Require(html.Info is { IsActive: true, MediaType: "text/html" } && Encoding.UTF8.GetString(html.Data).Contains("<script>", StringComparison.Ordinal), "Active content is returned as inert bytes, flagged active.");

        var original = await host.ReadContentBytesAsync("pic.png", sides.OriginalRevision);
        var working = await host.ReadContentBytesAsync("pic.png", null);
        var index = await host.ReadContentBytesAsync("t.txt", "");
        log.Add($"bytes {Show(original.Info)}|{Show(working.Info)}|{Show(index.Info)}");
        Require(original.Data.SequenceEqual(Png) && working.Data.SequenceEqual(PngEdited) && index.Data.SequenceEqual("text\n"u8.ToArray()), "Bytes come back exact for a commit, the working tree and the index.");

        await File.WriteAllBytesAsync(Path.Combine(repo, "pic.png"), PngEdited);
        await Git(repo, "add", "pic.png");
        await Git(repo, "commit", "-q", "-m", "move HEAD");
        var again = await host.ReadContentBytesAsync("pic.png", head);
        Require(again.Data.SequenceEqual(Png) && again.Info == original.Info, "A frozen original reads the same bytes after the branch moves.");
        log.Add("immutable");

        var treeId = (await Git(repo, "rev-parse", "HEAD^{tree}")).Trim();
        var blobId = (await Git(repo, "rev-parse", "HEAD:t.txt")).Trim();
        log.Add(string.Join(',', new[]
        {
            await Fails(() => host.ReadContentBytesAsync("pic.png", "HEAD")),
            await Fails(() => host.ReadContentBytesAsync("pic.png", "main")),
            await Fails(() => host.ReadContentBytesAsync("pic.png", "--output=x")),
            await Fails(() => host.ReadContentBytesAsync("pic.png", new string('0', 40))),
            await Fails(() => host.ReadContentBytesAsync("pic.png", treeId)),
            await Fails(() => host.ReadContentBytesAsync("t.txt", blobId)),
            await Fails(() => host.ReadContentBytesAsync("../outside.png", head)),
            await Fails(() => host.ReadContentBytesAsync("../outside.png", null)),
            await Fails(() => host.ReadContentBytesAsync("missing.png", head))
        }));
        Require(log[^1] == string.Join(',', Enumerable.Repeat("refused", 9)), "Refs, abbreviations, unknown, tree and blob ids, escaping and missing paths are refused: " + log[^1]);

        var outside = Path.Combine(Path.GetDirectoryName(repo)!, "secret.png");
        await File.WriteAllBytesAsync(outside, Png);
        File.CreateSymbolicLink(Path.Combine(repo, "link.png"), outside);
        Require(await Fails(() => host.ReadContentBytesAsync("link.png", null)) == "refused", "A symbolic link out of the workspace is refused.");
        return log;
    }
}