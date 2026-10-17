using System.Net;
using System.Security.Cryptography;
using System.Text;

using Avalonia.Threading;

using Ghostty.Avalonia;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Checks.E2E;
using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.UI;
using SharpRail.UI.State;
using SharpRail.UI.Terminal;

namespace SharpRail.Checks;

internal static class TerminalCommitLinkChecks
{
    private static void Require(bool value, string message)
    { if (!value) throw new InvalidOperationException("Terminal commit links: " + message); }

    internal static async Task Run(string root)
    {
        var repo = IsolatedGit.Repository(Path.Combine(root, "terminal-commits"));
        var sha = IsolatedGit.Run(repo, "rev-parse", "HEAD").Trim();
        var blob = IsolatedGit.Run(repo, "rev-parse", "HEAD:README.md").Trim();
        IsolatedGit.Run(repo, "tag", "-a", "tagged", "-m", "annotation");
        var tag = IsolatedGit.Run(repo, "rev-parse", "tagged").Trim();
        var ambiguous = AmbiguousCommits(repo);
        async Task Exercise(IProjectServices project)
        {
            await project.OpenProjectAsync(repo);
            var text = $"commit ({sha}) and {sha[..8].ToUpperInvariant()}";
            var links = await TerminalCommitLinks.Resolve(project, text, default);
            Require(links.Count == 2 && links.All(link => TerminalCommitLinks.Commit(link) == sha) &&
                links.Select(link => text.Substring(link.Start, link.Length)).SequenceEqual(new[] { sha, sha[..8].ToUpperInvariant() }),
                "Full and short ids must link the exact visible range to the canonical commit.");
            foreach (var absent in new[] { "deadbeefdeadbeef", blob, tag, ambiguous, "x" + sha, sha + "x", "https://example.test/" + sha, "file:///tmp/" + sha, sha[..3] })
                Require((await TerminalCommitLinks.Resolve(project, absent, default)).Count == 0, "Invalid, ambiguous, noncommit or URL token linked: " + absent);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            try { await TerminalCommitLinks.Resolve(project, sha, cancelled.Token); throw new InvalidOperationException("Cancellation ignored."); }
            catch (OperationCanceledException) { }
            var gitless = Path.Combine(root, "terminal-commits-gitless"); Directory.CreateDirectory(gitless);
            await project.OpenProjectAsync(gitless);
            Require((await TerminalCommitLinks.Resolve(project, sha, default)).Count == 0, "Gitless workspaces cannot resolve commits from another workspace.");
        }
        await Exercise(new LocalProjectAdapter(new ProjectServices(repo)));
        await using var server = RemoteServer.Create(repo, IPAddress.Loopback, 0, "terminal-commits");
        await server.StartAsync();
        try
        {
            var address = new Uri(server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var remote = new RemoteProjectAdapter(address, "terminal-commits");
            await Exercise(remote);
        }
        finally { await server.StopAsync(); }
        await FilePrecedence(repo, sha);
        await Hover();
        Console.WriteLine("PASS terminal commit ids, offsets, URLs, ambiguous/noncommit/missing/gitless rejection, local/remote parity and stale hover cancellation");
    }

    private static async Task FilePrecedence(string repo, string sha)
    {
        var path = Path.Combine(repo, sha);
        await File.WriteAllTextAsync(path, "hash-shaped file");
        await using var terminals = new PtyTerminalService(shell: "/bin/sh");
        await using var session = await terminals.AttachAsync(new("commit-file", repo, "commit-file"));
        var project = new LocalProjectAdapter(new ProjectServices(repo));
        var launch = new TerminalLaunch(repo, "commit-file", repo, "commit-file")
        { ResolveCommitLinks = (text, token) => TerminalCommitLinks.Resolve(project, text, token) };
        var links = await SharpRail.UI.Terminal.TerminalFileLinks.Resolve(terminals, launch, sha, default);
        Require(links.Count == 1 && links[0].Path == path && TerminalCommitLinks.Commit(links[0]) is null,
            "An existing file whose name is a commit hash must retain file-link precedence.");
        var failing = launch with { ResolveCommitLinks = (_, _) => throw new IOException("Commit lookup disconnected.") };
        links = await SharpRail.UI.Terminal.TerminalFileLinks.Resolve(terminals, failing, sha, default);
        Require(links.Count == 1 && links[0].Path == path, "A commit lookup failure must preserve an independently resolved file link.");
        await terminals.CloseAsync("commit-file");
        File.Delete(path);
    }

    private static void CheckStaleHighlights()
    {
        var first = new TaskCompletionSource<IReadOnlyList<TerminalLink>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var highlights = new TerminalLinkHighlights(() => { });
        var latest = new TerminalLink(0, 3, "latest", 1, 1, false);
        highlights.Resolve = (text, token) =>
        {
            Interlocked.Increment(ref calls);
            return text == "old" ? first.Task : Task.FromResult<IReadOnlyList<TerminalLink>>([latest]);
        };
        highlights.Update("old");
        E2eWorkspace.Until(() => calls == 1);
        highlights.Update("new");
        E2eWorkspace.Until(() => highlights.Update("new").Count == 1);
        first.SetResult([new(0, 3, "old", 1, 1, false)]);
        E2eWorkspace.Settle(150);
        Require(highlights.Update("new").Single() == latest, "Late visible-link resolution replaced the newer viewport.");
        Require(calls == 2, "A resolved viewport must reuse its bounded highlight cache.");
        highlights.Clear();
        Require(highlights.Update("new").Count == 0, "Detaching must discard cached visible links.");
        highlights.Dispose();
        E2eWorkspace.Settle(150);
        Require(highlights.Update("new").Count == 0, "Disposal must prevent late highlight publication.");
        Console.WriteLine("PASS terminal commit highlights reject superseded output and disposal, and clear cached links on detach");
    }

    internal static void RunUi(string root)
    {
        GhosttySkiaChecks.CheckPersistentHighlights();
        CheckStaleHighlights();
        var repo = IsolatedGit.Repository(Path.Combine(root, "terminal-commit-navigation"));
        var sha = IsolatedGit.Run(repo, "rev-parse", "HEAD").Trim();
        TerminalLaunch? captured = null;
        var terminals = E2eTerminals.Plain;
        var window = new WorkbenchWindow(new LocalProjectAdapter(new ProjectServices(repo)), repo, new ProfileStore(repo + "-profile"), launch =>
        {
            captured = launch;
            return terminals(launch);
        });
        window.Show();
        try
        {
            E2eWorkspace.Until(() => window.WorkspaceMounted);
            window.Layout.NewTerminal(window.Layout.View.FocusedCenter);
            E2eWorkspace.Until(() => captured is not null);
            var launch = captured!;
            var resolution = launch.ResolveCommitLinks!(sha[..8], default);
            E2eWorkspace.Until(() => resolution.IsCompleted);
            var link = resolution.GetAwaiter().GetResult().Single();
            var open = launch.OpenFileLink!(link);
            E2eWorkspace.Until(() => open.IsCompleted);
            open.GetAwaiter().GetResult();
            Require(window.Layout.Tabs(window.Layout.View.FocusedCenter).Any(tab => tab.Kind == "changes" && tab.Scope == "commit" && tab.Comparison == sha),
                "Activating a terminal hash must open the existing commit Changes tab, not an editor file or external URI.");
            var before = window.Layout.Tabs(window.Layout.View.FocusedCenter).Count;
            open = launch.OpenFileLink(link);
            E2eWorkspace.Until(() => open.IsCompleted);
            Require(window.Layout.Tabs(window.Layout.View.FocusedCenter).Count == before, "Opening a commit again must reuse its kept Changes tab.");
            var home = window.OpenProjectHomeAsync(repo);
            E2eWorkspace.Until(() => home.IsCompleted);
            home.GetAwaiter().GetResult();
            resolution = launch.ResolveCommitLinks(sha, default);
            E2eWorkspace.Until(() => resolution.IsCompleted);
            Require(resolution.GetAwaiter().GetResult().Count == 0, "An inactive workspace's terminal must not resolve navigation links.");
            Console.WriteLine("PASS terminal commit activation opens and reuses committed Changes, and rejects inactive workspace results");
        }
        finally { window.Close(); Dispatcher.UIThread.RunJobs(); }
    }

    private static string AmbiguousCommits(string repo)
    {
        var tree = IsolatedGit.Run(repo, "rev-parse", "HEAD^{tree}").Trim();
        var found = new Dictionary<string, string>();
        for (var index = 0; index < 5000; index++)
        {
            var body = $"tree {tree}\nauthor Checks <checks@sharprail.invalid> 1700000000 +0000\ncommitter Checks <checks@sharprail.invalid> 1700000000 +0000\n\ncommit {index}\n";
            var bytes = Encoding.UTF8.GetBytes(body);
            var sha = Convert.ToHexStringLower(SHA1.HashData(Encoding.UTF8.GetBytes("commit " + bytes.Length + "\0").Concat(bytes).ToArray()));
            if (found.TryGetValue(sha[..4], out var first))
            {
                var fixture = Path.Combine(repo, "commit-object");
                File.WriteAllText(fixture, first); IsolatedGit.Run(repo, "hash-object", "-t", "commit", "-w", fixture);
                File.WriteAllText(fixture, body); IsolatedGit.Run(repo, "hash-object", "-t", "commit", "-w", fixture);
                File.Delete(fixture);
                return sha[..4];
            }
            found[sha[..4]] = body;
        }
        throw new InvalidOperationException("Could not construct ambiguous fixture.");
    }

    private static async Task Hover()
    {
        var first = new TaskCompletionSource<IReadOnlyList<TerminalLink>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var latest = new TerminalLink(0, 8, "latest", 1, 1, false);
        var calls = 0;
        using var hover = new TerminalLinkHover(() => { });
        hover.Resolve = async (text, token) =>
        {
            Interlocked.Increment(ref calls);
            if (text == "first") { started.TrySetResult(); return await first.Task; }
            return [latest];
        };
        hover.Update("first", 0);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        hover.Update("second", 0);
        for (var index = 0; hover.Link != latest && index < 100; index++) await Task.Delay(10);
        Require(hover.Link == latest, "Latest hover did not resolve.");
        first.SetResult([new(0, 8, "stale", 1, 1, false)]);
        await Task.Delay(20);
        Require(hover.Link == latest, "Late resolution replaced a newer hover.");
        hover.Update("second", 1);
        Require(calls == 2, "Moving within the same line must reuse the bounded hover cache.");
        var opened = false;
        hover.Open = _ => { opened = true; return Task.CompletedTask; };
        hover.Resolve = (_, _) => Task.FromResult<IReadOnlyList<TerminalLink>>([]);
        await hover.Activate(latest);
        Require(!opened, "Activation must recheck a cached target before opening it.");
    }
}