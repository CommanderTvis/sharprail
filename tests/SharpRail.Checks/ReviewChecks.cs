using Avalonia.Controls;
using Avalonia.LogicalTree;

using SharpRail.Checks.E2E;
using SharpRail.UI.State;

using static SharpRail.Checks.E2E.ChangesFixture;
using static SharpRail.Checks.E2E.E2eWorkspace;

namespace SharpRail.Checks;

/// <summary>
/// SharpRail's own checks of the Review panel. Pull requests run against a bare origin and a PATH shim for gh, as
/// the host checks do; nothing reaches a network.
/// </summary>
internal static class ReviewChecks
{
    internal static void Run(string root)
    {
        if (!OperatingSystem.IsWindows()) PullRequests(Path.Combine(root, "pull-requests"));
    }

    private static T? Maybe<T>(E2eWorkspace app, string name) where T : Control =>
        app.Window.GetLogicalDescendants().OfType<T>().FirstOrDefault(control => control.Name == name);

    private static T Part<T>(Control owner, string name) where T : Control =>
        owner.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    private static bool Toasted(E2eWorkspace app, string title) => app.Window.Toasts.Items.Any(toast => toast.Title == title);

    private static void ClearToasts(E2eWorkspace app)
    {
        foreach (var toast in app.Window.Toasts.Items.ToArray()) app.Window.Toasts.Dismiss(toast.Id);
    }

    private static string PrLabel(E2eWorkspace app) => Maybe<Button>(app, "ReviewOpenPr") is { } button ? Text(button) : "";

    private static Window Compose(E2eWorkspace app)
    {
        app.Click(app.Find<Button>("ReviewOpenPr"));
        return WorkspaceFixture.Dialog(app, "PrComposeDialog");
    }

    private static bool Open(E2eWorkspace app, string tag) => app.Window.OwnedWindows.Any(window => Equals(window.Tag, tag));

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void PullRequests(string parent)
    {
        var shim = Path.Combine(parent, "shim");
        var gitOnly = Path.Combine(parent, "git-only");
        Directory.CreateDirectory(shim);
        Directory.CreateDirectory(gitOnly);
        var gh = Path.Combine(shim, "gh");
        File.WriteAllText(gh, PullRequestChecks.GhShim.Replace("\n        ", "\n").TrimStart());
        File.SetUnixFileMode(gh, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var gitPath = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':').Select(dir => Path.Combine(dir, "git")).First(File.Exists);
        File.CreateSymbolicLink(Path.Combine(gitOnly, "git"), gitPath);
        var config = Path.Combine(parent, "gitconfig");
        File.WriteAllText(config, "[user]\n\tname = T\n\temail = t@example.com\n[commit]\n\tgpgsign = false\n[http]\n\tproxy = http://127.0.0.1:1\n");
        var saved = new[] { "PATH", "GIT_CONFIG_GLOBAL", "GIT_CONFIG_NOSYSTEM", "SHIM_DIR", "SHARPRAIL_GH_OFFLINE", "GIT_SSH_COMMAND" }
            .ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            Environment.SetEnvironmentVariable("GIT_CONFIG_GLOBAL", config);
            Environment.SetEnvironmentVariable("GIT_CONFIG_NOSYSTEM", "1");
            Environment.SetEnvironmentVariable("SHIM_DIR", shim);
            Environment.SetEnvironmentVariable("SHARPRAIL_GH_OFFLINE", null);
            Environment.SetEnvironmentVariable("GIT_SSH_COMMAND", null);
            Environment.SetEnvironmentVariable("PATH", shim + ":" + gitOnly + ":/usr/bin:/bin");
            // The fixture's own awaits must not resume on the dispatcher this thread is blocking.
            var work = Task.Run(() => PullRequestChecks.MakeRepo(parent, "hub", github: true)).GetAwaiter().GetResult();
            Flow(work, Path.Combine(parent, "hub.git"), shim);
        }
        finally { foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value); }
    }

    private static void Flow(string work, string bare, string shim)
    {
        const string hub = "https://github.com/o/r.git";
        string[] Log() => File.Exists(Path.Combine(shim, "log")) ? File.ReadAllLines(Path.Combine(shim, "log")) : [];
        using var app = new E2eWorkspace(work, openFiles: false);
        var links = new List<string>();
        app.Window.OpenLink = links.Add;
        Until(() => app.Find<TextBlock>("BranchLabel").Text == "feature");
        app.Click(app.Find<Button>("Tab_review"));
        Until(() => PrLabel(app) == "Open PR" && Log().Any(line => line.StartsWith("pr list", StringComparison.Ordinal)));
        Require(Maybe<Button>(app, "ReviewPrChip") is null && Maybe<Button>(app, "ReviewOpenDraftPr") is not null,
            "Without an open pull request the panel offers Open PR and Open draft PR and no chip.");

        // A failed push keeps the dialog and what was typed; the next submit sends those edits.
        Git(work, "config", "--unset", "url." + bare + ".pushInsteadOf");
        var compose = Compose(app);
        var title = Part<TextBox>(compose, "PrComposeTitle");
        var body = Part<TextBox>(compose, "PrComposeBody");
        Require(Part<TextBlock>(compose, "DialogHeading").Text == "Open PR" && title.Text == "feature" && body.Text == "- Add b",
            $"The compose dialog must start from the host's draft: {title.Text} / {body.Text}.");
        title.Text = "Feature title"; body.Text = "Hand-written description";
        app.Click(Part<Button>(compose, "PrComposeSubmit"));
        Until(() => Toasted(app, "Open PR failed"));
        Require(Open(app, "PrComposeDialog") && title.Text == "Feature title" && body.Text == "Hand-written description" && Maybe<Button>(app, "ReviewPrChip") is null,
            "A failed submit must leave the dialog open with its edits.");
        Git(work, "config", "url." + bare + ".pushInsteadOf", hub);
        ClearToasts(app);
        Until(() => Part<Button>(compose, "PrComposeSubmit").IsEnabled);
        app.Click(Part<Button>(compose, "PrComposeSubmit"));
        Until(() => Toasted(app, "PR opened") && !Open(app, "PrComposeDialog"));
        Until(() => Maybe<Button>(app, "ReviewPrChip") is not null && PrLabel(app) == "Push updates");
        Require(Log().Any(line => line.StartsWith("pr create --base main --head feature --title Feature title --body Hand-written description", StringComparison.Ordinal)),
            "The pull request must be created from the edited title and description.");
        Require(Toasted(app, "Uncommitted changes") && app.Window.Toasts.Items.Any(toast => toast.Message == "PR #7 is open."),
            "Opening reports the pull request and the files that stayed local.");
        Require(Maybe<Button>(app, "ReviewOpenDraftPr") is null && Git(bare, "rev-parse", "refs/heads/feature") == Git(work, "rev-parse", "HEAD"),
            "The branch must be on origin and the draft action gone once a pull request exists.");
        app.Click(app.Find<Button>("ReviewPrChip"));
        Until(() => links.Count > 0);
        Require(Text(app.Find<Button>("ReviewPrChip")) == "PR #7" && links.SequenceEqual(["https://github.com/o/r/pull/7"]), "The chip must link to the pull request.");
        Console.WriteLine("PASS Review opens a pull request through the compose dialog, keeps edits across a failure and shows its chip");

        // New local commits are counted against the pull request and pushed through the same dialog.
        ClearToasts(app);
        Git(work, "commit", "--allow-empty", "-m", "More");
        app.Window.RefreshOpenReview();
        Until(() => PrLabel(app) == "Push updates (1)");
        Require(app.Find<TextBlock>("ReviewPrNotice").Text!.Contains("1 new commit isn't in PR #7 yet.", StringComparison.Ordinal), "Unpushed commits must be named.");
        File.Delete(Path.Combine(shim, "log"));
        compose = Compose(app);
        Require(Part<TextBlock>(compose, "DialogHeading").Text == "Push updates" && Part<TextBox>(compose, "PrComposeBody").Text == "- More\n- Add b",
            "Pushing updates must show the regenerated description before it replaces the pull request's.");
        app.Click(Part<Button>(compose, "PrComposeSubmit"));
        Until(() => Toasted(app, "PR updated") && !Open(app, "PrComposeDialog") && PrLabel(app) == "Push updates");
        Require(Log().Any(line => line.StartsWith("pr edit 7 --body", StringComparison.Ordinal) && !line.Contains("--title", StringComparison.Ordinal)) &&
            Git(bare, "rev-parse", "refs/heads/feature") == Git(work, "rev-parse", "HEAD"), "An update pushes and edits the description, never an untouched title.");
        Console.WriteLine("PASS Review counts unpushed commits and pushes updates to the open pull request");

        // A gh that cannot be used hands over to setup guidance; Try again resubmits what was typed.
        ClearToasts(app);
        File.WriteAllText(Path.Combine(shim, "fail-list"), "");
        File.WriteAllText(Path.Combine(shim, "unauth"), "");
        app.Window.RefreshOpenReview();
        Until(() => PrLabel(app) == "Open PR");
        compose = Compose(app);
        Part<TextBox>(compose, "PrComposeBody").Text = "Kept for the retry";
        app.Click(Part<Button>(compose, "PrComposeSubmit"));
        Until(() => Open(app, "PrSetupDialog") && !Open(app, "PrComposeDialog"));
        var setup = WorkspaceFixture.Dialog(app, "PrSetupDialog");
        Require(Part<TextBlock>(setup, "DialogHeading").Text == "GitHub CLI isn't signed in" &&
            setup.GetLogicalDescendants().OfType<Button>().Where(button => button.Name == "PrSetupCopy").Select(button => button.Tag).SequenceEqual(["gh auth login"]) &&
            !Toasted(app, "Branch pushed"), "An unauthenticated gh must explain the sign-in command instead of toasting an outcome.");
        File.Delete(Path.Combine(shim, "fail-list"));
        File.Delete(Path.Combine(shim, "unauth"));
        File.Delete(Path.Combine(shim, "log"));
        app.Click(Part<Button>(setup, "PrSetupRetry"));
        Until(() => Toasted(app, "PR updated") && !Open(app, "PrSetupDialog"));
        Require(Log().Any(line => line.StartsWith("pr edit 7 --body Kept for the retry", StringComparison.Ordinal)), "Try again must resubmit the last edited description.");

        ClearToasts(app);
        File.WriteAllText(Path.Combine(shim, "fail-list"), "");
        File.WriteAllText(Path.Combine(shim, "unauth"), "");
        app.Window.RefreshOpenReview();
        Until(() => PrLabel(app) == "Open PR");
        app.Click(Part<Button>(Compose(app), "PrComposeSubmit"));
        Until(() => Open(app, "PrSetupDialog"));
        links.Clear();
        app.Click(Part<Button>(WorkspaceFixture.Dialog(app, "PrSetupDialog"), "PrSetupCompare"));
        Until(() => !Open(app, "PrSetupDialog") && links.Count == 1);
        Require(links[0].StartsWith("https://github.com/o/r/compare/main...feature?quick_pull=1", StringComparison.Ordinal), "The compare page must be the host's prefilled link.");
        File.Delete(Path.Combine(shim, "fail-list"));
        File.Delete(Path.Combine(shim, "unauth"));
        Console.WriteLine("PASS Review hands a gh setup gap to guidance with Try again and the compare page");

        // Origin moving ahead is a sync conflict: the panel offers the rebase command, never a push. The fixture's
        // origin is only reachable for pushes, so the fetched behind count is supplied at the host boundary.
        ClearToasts(app);
        Git(work, "commit", "--allow-empty", "-m", "Local");
        var origin = Git(bare, "rev-parse", "refs/heads/feature");
        app.Host.Review = review => review is null ? null : review with { BehindCommits = 1 };
        app.Window.RefreshOpenReview();
        Until(() => PrLabel(app) == "Branch diverged");
        Require(app.Find<TextBlock>("ReviewIntegrateCommand").Text == "git pull --rebase origin feature" &&
            app.Find<TextBlock>("ReviewPrNotice").Text!.Contains("force-pushing would drop them", StringComparison.Ordinal),
            "A diverged branch must name the safe integrate command.");
        app.Click(app.Find<Button>("ReviewOpenPr"));
        Until(() => Toasted(app, "Command copied") || Toasted(app, "Copy failed"));
        Require(!Open(app, "PrComposeDialog") && Git(bare, "rev-parse", "refs/heads/feature") == origin, "A diverged branch must not be pushed.");
        Console.WriteLine("PASS Review treats a diverged branch as a sync conflict with a copyable integrate command");
    }
}