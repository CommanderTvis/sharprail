using System.Net;

using Avalonia.Controls;
using Avalonia.LogicalTree;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.ChangesFixture;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>
/// Excluding a project's <c>.sharprail</c> directory through <c>.git/info/exclude</c>: the status query, the append,
/// and the per-project decision to keep it in the repository, locally and over WebSocket RPC with equal results.
/// </summary>
internal static class SharprailExcludeChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static string Git(string directory, params string[] arguments) => E2E.IsolatedGit.Run(directory, arguments);

    private static string ExcludeFile(string repository) => Path.Combine(repository, ".git", "info", "exclude");

    private static string ExcludeText(string repository) => File.Exists(ExcludeFile(repository)) ? File.ReadAllText(ExcludeFile(repository)) : "<none>";

    private static string Seed(string directory)
    {
        Directory.CreateDirectory(Path.Combine(directory, ".sharprail"));
        File.WriteAllText(Path.Combine(directory, ".sharprail", "workspace.json"), "{}\n");
        return directory;
    }

    public static async Task Host(string root)
    {
        var directory = Path.Combine(root, "sharprail-exclude");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        Directory.CreateDirectory(directory);
        var local = await Scenarios(Path.Combine(directory, "local"), async path =>
        {
            var host = new LocalProjectAdapter(new ProjectServices(path, new HostStateStore(Path.Combine(directory, "local-state"))));
            await host.OpenProjectAsync(path);
            return host;
        });

        var remoteRoot = E2E.IsolatedGit.Repository(Path.Combine(directory, "remote-seed"));
        await using var server = RemoteServer.Create(remoteRoot, IPAddress.Loopback, 0, "sharprail-exclude", Path.Combine(directory, "remote-state"));
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            var adapters = new List<RemoteProjectAdapter>();
            var remote = await Scenarios(Path.Combine(directory, "remote"), async path =>
            {
                var adapter = new RemoteProjectAdapter(new Uri(address), "sharprail-exclude");
                adapters.Add(adapter);
                await adapter.OpenProjectAsync(path);
                return adapter;
            });
            foreach (var adapter in adapters) adapter.Dispose();
            Require(local.SequenceEqual(remote),
                "Local and remote results differ:\n" + string.Join('\n', local.Zip(remote).Where(pair => pair.First != pair.Second).Select(pair => pair.First + "  <>  " + pair.Second)));

            using var remoteState = new RemoteStateAdapter(new Uri(address), "sharprail-exclude");
            await remoteState.ChangeAsync([HostStateChange.DeclineSharprailExclude(remoteRoot)]);
            Require((await remoteState.GetStateAsync()).SharprailExcludeDeclined.SequenceEqual([remoteRoot]), "The decision to keep .sharprail did not reach a remote client.");
        }
        finally { await server.StopAsync(); }
        await Declined(Path.Combine(directory, "declined"));
        Console.WriteLine("PASS .sharprail exclusion host checks: status, idempotent append, common git dir of linked worktrees, tracked and ignored directories, existing content, declined projects, locally and over WebSocket RPC");
    }

    // Each scenario runs against the same repository layout through either transport and logs what it saw.
    private static async Task<List<string>> Scenarios(string parent, Func<string, Task<IProjectServices>> open)
    {
        var log = new List<string>();
        static string Text(string repository) => ExcludeText(repository).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

        // Absent: nothing to exclude and nothing written.
        var plain = E2E.IsolatedGit.Repository(Path.Combine(parent, "plain"));
        var host = await open(plain);
        log.Add("absent " + await host.GetSharprailDirectoryAsync());
        var untouched = ExcludeText(plain);
        var none = await host.ExcludeSharprailDirectoryAsync();
        log.Add("absent exclude " + none);
        Require(!none.Written && !none.CanExclude && ExcludeText(plain) == untouched, "Excluding an absent directory must change nothing.");

        // Untracked and not ignored: offered, then excluded without touching the shared ignore file.
        Seed(plain);
        var before = ExcludeText(plain);
        var status = await host.GetSharprailDirectoryAsync();
        log.Add("untracked " + status);
        Require(status.CanExclude && Git(plain, "status", "--porcelain").Contains(".sharprail/", StringComparison.Ordinal), "An untracked .sharprail must be offered and show as noise.");
        var written = await host.ExcludeSharprailDirectoryAsync();
        log.Add("excluded " + written);
        Require(written is { Written: true, Ignored: true } && ExcludeText(plain) == before + "/.sharprail/\n" && !Git(plain, "status", "--porcelain").Contains(".sharprail", StringComparison.Ordinal),
            "Excluding must append the entry, hide the directory and keep the existing text: " + Text(plain));
        Require(!File.Exists(Path.Combine(plain, ".gitignore")), "Excluding must never create or touch .gitignore.");
        var again = ExcludeText(plain);
        var repeated = await host.ExcludeSharprailDirectoryAsync();
        log.Add("repeated " + repeated);
        Require(!repeated.Written && ExcludeText(plain) == again, "A second exclusion must change nothing: " + Text(plain));
        log.Add("after " + await host.GetSharprailDirectoryAsync());

        // Concurrent requests (several windows) never duplicate the entry.
        var racing = Seed(E2E.IsolatedGit.Repository(Path.Combine(parent, "racing")));
        var racer = await open(racing);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ => await racer.ExcludeSharprailDirectoryAsync()));
        var entries = ExcludeText(racing).Split('\n').Count(line => line == "/.sharprail/");
        log.Add("racing entries " + entries);
        Require(entries == 1, "Concurrent exclusions must leave one entry: " + Text(racing));

        // Existing exclude content survives, a missing final newline is supplied, and CRLF files stay CRLF.
        var existing = Seed(E2E.IsolatedGit.Repository(Path.Combine(parent, "existing")));
        File.WriteAllText(ExcludeFile(existing), "# mine\nbuild/");
        await (await open(existing)).ExcludeSharprailDirectoryAsync();
        log.Add("existing " + Text(existing));
        Require(ExcludeText(existing) == "# mine\nbuild/\n/.sharprail/\n", "Existing content must be kept: " + Text(existing));
        var crlf = Seed(E2E.IsolatedGit.Repository(Path.Combine(parent, "crlf")));
        File.WriteAllText(ExcludeFile(crlf), "# mine\r\n");
        await (await open(crlf)).ExcludeSharprailDirectoryAsync();
        log.Add("crlf " + Text(crlf));
        Require(ExcludeText(crlf) == "# mine\r\n/.sharprail/\r\n", "Line endings must be kept: " + Text(crlf));

        // A repository without info/ gets one.
        var bare = Seed(E2E.IsolatedGit.Repository(Path.Combine(parent, "no-info")));
        Directory.Delete(Path.Combine(bare, ".git", "info"), recursive: true);
        await (await open(bare)).ExcludeSharprailDirectoryAsync();
        log.Add("no info " + Text(bare));
        Require(ExcludeText(bare) == "/.sharprail/\n", "A missing info directory must be created: " + Text(bare));
        Require(!Directory.EnumerateFiles(Path.Combine(bare, ".git", "info")).Any(file => file.EndsWith(".tmp", StringComparison.Ordinal)), "The atomic write must not leave its temporary file.");

        // Tracked: excluding would have no effect, so nothing is written.
        var tracked = Seed(E2E.IsolatedGit.Repository(Path.Combine(parent, "tracked")));
        Git(tracked, "add", "-A"); Git(tracked, "commit", "-m", "track");
        var trackedHost = await open(tracked);
        var trackedBefore = ExcludeText(tracked);
        log.Add("tracked " + await trackedHost.GetSharprailDirectoryAsync());
        var refused = await trackedHost.ExcludeSharprailDirectoryAsync();
        log.Add("tracked exclude " + refused);
        Require(refused is { Written: false, Tracked: true } && refused.Detail.Contains("no effect", StringComparison.Ordinal) && ExcludeText(tracked) == trackedBefore,
            "A tracked directory must never be excluded.");

        // Already ignored by .gitignore (decided by Git, not by text): reported, not rewritten.
        var ignored = Seed(E2E.IsolatedGit.Repository(Path.Combine(parent, "ignored")));
        File.WriteAllText(Path.Combine(ignored, ".gitignore"), "# comment mentioning .sharprail\n.sharp*\n");
        var ignoredHost = await open(ignored);
        var ignoredBefore = ExcludeText(ignored);
        log.Add("ignored " + await ignoredHost.GetSharprailDirectoryAsync());
        log.Add("ignored exclude " + await ignoredHost.ExcludeSharprailDirectoryAsync());
        Require(ExcludeText(ignored) == ignoredBefore, "An ignored directory must not be written again.");
        var mentioned = Seed(E2E.IsolatedGit.Repository(Path.Combine(parent, "mentioned")));
        File.WriteAllText(Path.Combine(mentioned, ".gitignore"), "# .sharprail\n");
        Require((await (await open(mentioned)).GetSharprailDirectoryAsync()).CanExclude, "A comment is not an ignore rule.");

        // A linked worktree shares the common git directory: the entry lands there and covers every workspace.
        var main = E2E.IsolatedGit.Repository(Path.Combine(parent, "main"));
        var linked = Path.Combine(parent, "linked");
        Git(main, "worktree", "add", "-b", "feature", linked);
        Seed(linked);
        var linkedHost = await open(linked);
        log.Add("linked " + await linkedHost.GetSharprailDirectoryAsync());
        log.Add("linked exclude " + await linkedHost.ExcludeSharprailDirectoryAsync());
        Require(ExcludeText(main).EndsWith("/.sharprail/\n", StringComparison.Ordinal) && !File.Exists(Path.Combine(main, ".git", "worktrees", "linked", "info", "exclude")),
            "A linked worktree must write the common info/exclude: " + Text(main));
        Seed(main);
        var mainStatus = await (await open(main)).GetSharprailDirectoryAsync();
        log.Add("main after linked " + mainStatus);
        Require(mainStatus.Ignored, "The project's other workspace must see the shared exclusion.");

        // Outside a repository there is nothing to exclude.
        var folder = Seed(Path.Combine(parent, "not-a-repository"));
        log.Add("no repository " + await (await open(folder)).GetSharprailDirectoryAsync());
        return log;
    }

    // The decision to keep the directory in the repository is host state: per project, persistent, idempotent.
    private static async Task Declined(string directory)
    {
        var store = new HostStateStore(directory);
        var project = Path.Combine(directory, "project");
        Require(store.Current.SharprailExcludeDeclined.Count == 0, "No project starts declined.");
        await store.ChangeAsync([HostStateChange.DeclineSharprailExclude(project)]);
        await store.ChangeAsync([HostStateChange.DeclineSharprailExclude(project)]);
        Require(store.Current.SharprailExcludeDeclined.SequenceEqual([project]), "Declining twice must keep one entry.");
        Require(new HostStateStore(directory).Current.SharprailExcludeDeclined.SequenceEqual([project]), "The decision must survive a restart.");
        try { await store.ChangeAsync([HostStateChange.DeclineSharprailExclude("relative/path")]); throw new InvalidOperationException("A relative project path was accepted."); }
        catch (ArgumentException) { }
    }

    private static Border[] Cards(E2E.E2eWorkspace app) =>
        app.Window.GetLogicalDescendants().OfType<Border>().Where(border => border.Name == "Toast").ToArray();

    private static Button? Offer(E2E.E2eWorkspace app, string button) => Cards(app)
        .Where(card => card.GetLogicalDescendants().OfType<TextBlock>().Any(text => text.Text?.Contains("add to .git/info/exclude?", StringComparison.Ordinal) == true))
        .SelectMany(card => card.GetLogicalDescendants().OfType<Button>()).FirstOrDefault(candidate => candidate.Name == button);

    private static void ClearToasts(E2E.E2eWorkspace app)
    {
        foreach (var toast in app.Window.Toasts.Items.ToArray()) app.Window.Toasts.Dismiss(toast.Id);
    }

    /// <summary>The prompt appears when .sharprail shows up untracked, acts through real clicks, and a decline is remembered for the project.</summary>
    public static void Ui(string root)
    {
        var directory = Path.Combine(root, "sharprail-exclude-ui");
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);

        // Accepting: the directory appears while the window is open, the offer is raised once, and its action excludes it.
        var accepted = E2E.IsolatedGit.Repository(Path.Combine(directory, "accepted"));
        using (var app = new E2E.E2eWorkspace(accepted, openFiles: false))
        {
            ShowChanges(app);
            Settle();
            Require(Offer(app, "ToastAction") is null, "Nothing is offered without a .sharprail directory.");
            Seed(accepted);
            Until(() => Offer(app, "ToastAction") is not null && Offer(app, "ToastSecondaryAction") is not null);
            Require(app.Window.Toasts.Items.Count(toast => toast.Secondary is not null) == 1,
                "One dismissible offer carrying both choices.");
            app.Window.Toasts.Dismiss(app.Window.Toasts.Items.Single(toast => toast.Secondary is not null).Id);
            Require(!app.State!.Current.SharprailExcludeDeclined.Contains(accepted), "Dismissing the offer must not record a decision.");
            File.WriteAllText(Path.Combine(accepted, ".sharprail", "language.json"), "{}\n");
            Settle(800);
            Require(Offer(app, "ToastAction") is null, "A dismissed offer must not return for the same workspace.");
            // The change row keeps a way to do it later.
            Until(() => Paths(app).Any(path => path.StartsWith(".sharprail/", StringComparison.Ordinal)));
            var row = Frames(app).First(frame => RowPath(frame).StartsWith(".sharprail/", StringComparison.Ordinal));
            var item = MenuItems(RowButton(row).ContextMenu!.Items).Single(menu => Equals(menu.Header, "Exclude locally (.git/info/exclude)"));
            Require(!Frames(app).Where(frame => !RowPath(frame).StartsWith(".sharprail/", StringComparison.Ordinal)).Any(frame =>
                MenuItems(RowButton(frame).ContextMenu!.Items).Any(menu => Equals(menu.Header, item.Header))), "Only .sharprail rows offer the exclusion.");
            item.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(MenuItem.ClickEvent));
            Until(() => ExcludeText(accepted).Contains("/.sharprail/\n", StringComparison.Ordinal));
            Until(() => !Paths(app).Any(path => path.StartsWith(".sharprail/", StringComparison.Ordinal)));
            Until(() => app.Window.Toasts.Items.Any(toast => toast.Message.Contains("is excluded", StringComparison.Ordinal)));
            Require(!File.Exists(Path.Combine(accepted, ".gitignore")), "The exclusion must not create .gitignore.");
        }

        // Accepting from the offer itself.
        var prompted = Seed(E2E.IsolatedGit.Repository(Path.Combine(directory, "prompted")));
        using (var app = new E2E.E2eWorkspace(prompted, openFiles: false))
        {
            Until(() => Offer(app, "ToastAction") is not null);
            app.Click(Offer(app, "ToastAction")!);
            Until(() => ExcludeText(prompted) == "/.sharprail/\n" || ExcludeText(prompted).EndsWith("/.sharprail/\n", StringComparison.Ordinal));
            Until(() => Offer(app, "ToastAction") is null);
        }

        // Declining: remembered by the host for the project, so neither this window nor a new one asks again.
        var kept = Seed(E2E.IsolatedGit.Repository(Path.Combine(directory, "kept")));
        using (var app = new E2E.E2eWorkspace(kept, openFiles: false))
        {
            Until(() => Offer(app, "ToastSecondaryAction") is not null);
            var before = ExcludeText(kept);
            app.Click(Offer(app, "ToastSecondaryAction")!);
            Until(() => app.State!.Current.SharprailExcludeDeclined.Contains(kept));
            Until(() => Offer(app, "ToastAction") is null);
            Require(ExcludeText(kept) == before, "Keeping .sharprail in the repo must not write the exclude file.");
            File.WriteAllText(Path.Combine(kept, ".sharprail", "other.json"), "{}\n");
            using var second = app.NewWindow();
            Settle(800);
            Require(Offer(app, "ToastAction") is null && Offer(second, "ToastAction") is null, "A declined project must not be asked again, in any window.");
        }
        Console.WriteLine("PASS .sharprail exclusion prompt: raised once when the directory appears untracked, accept and keep-in-repo through real clicks, Changes row action, remembered per project");
    }
}