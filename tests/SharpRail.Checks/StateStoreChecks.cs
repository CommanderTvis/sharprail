using System.Text.Json.Nodes;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Core;

namespace SharpRail.Checks;

/// <summary>What the state store keeps on disk: forward-compatible settings, theme normalization and the installation identity.</summary>
internal static class StateStoreChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static async Task Run(string root)
    {
        var directory = Path.Combine(root, "state-store");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, HostStateStore.FileName);
        File.WriteAllText(file, """
            { "Settings": { "Theme": "dark", "ThemeMode": "system", "SystemLight": "light", "SystemDark": "",
                "FutureFlag": true, "FutureShape": { "depth": [1, 2] } } }
            """);
        var store = new HostStateStore(directory);
        Require(store.Current.Settings is { Theme: "dark", ThemeMode: "system", SystemLight: "", SystemDark: "" },
            "Half a system theme pair must be dropped on load without discarding valid siblings.");
        await store.ChangeAsync([HostStateChange.Setting("file-width", "100")]);
        var settings = JsonNode.Parse(File.ReadAllText(file))!["Settings"]!;
        Require(settings["FutureFlag"]!.GetValue<bool>() && settings["FutureShape"]!["depth"]!.AsArray().Count == 2 &&
            settings["FileLineWidth"]!.GetValue<int>() == 100, "A valid update must keep settings this host does not know.");
        Require(new HostStateStore(directory).Current.Settings.FileLineWidth == 100, "State with unknown settings must load again.");
        try
        {
            await store.ChangeAsync([HostStateChange.Setting("FutureFlag", "false")]);
            throw new InvalidOperationException("An unknown setting key was accepted as a change.");
        }
        catch (ArgumentException) { }

        // Upstream #764: a project rename is display-only, normalised, persisted and kept across close and reopen.
        var renamed = Path.Combine(directory, "renamed-project");
        await store.ChangeAsync([HostStateChange.OpenProject(renamed)]);
        var named = await store.ChangeAsync([HostStateChange.RenameProject(renamed, "  My \t  project ")]);
        Require(named.ProjectName(renamed) == "My project" && named.Projects.Contains(renamed), "A project rename collapses whitespace and keeps the path.");
        foreach (var bad in new[] { "   ", new string('x', 101), "bad\u0001name" })
            try { await store.ChangeAsync([HostStateChange.RenameProject(renamed, bad)]); throw new InvalidOperationException("An invalid project name was accepted: " + bad); }
            catch (ArgumentException) { }
        await store.ChangeAsync([HostStateChange.CloseProject(renamed)]);
        await store.ChangeAsync([HostStateChange.OpenProject(renamed)]);
        Require(new HostStateStore(directory).Current.ProjectName(renamed) == "My project", "A project name survives close, reopen and a reload.");
        var cleared = await store.ChangeAsync([HostStateChange.RenameProject(renamed, "renamed-project")]);
        Require(!cleared.ProjectLabels.ContainsKey(renamed) && cleared.ProjectName(renamed) == "renamed-project", "The folder name clears the label.");
        await store.ChangeAsync([HostStateChange.CloseProject(renamed)]);
        Console.WriteLine("PASS upstream projects.test.ts (#764): renameProject normalises, rejects empty, long and control names, and survives close/reopen");

        // Upstream #770 on the A18 unread model: stronger reasons win, Mark as read keeps a waiting agent, seeing clears all.
        var unreadPath = Path.Combine(directory, "unread-workspace");
        await store.ChangeAsync([HostStateChange.MarkUnread(unreadPath, "manual")]);
        await store.ChangeAsync([HostStateChange.MarkUnread(unreadPath, "blocked")]);
        var held = await store.ChangeAsync([HostStateChange.MarkUnread(unreadPath, "done")]);
        Require(held.UnreadWorkspaces.GetValueOrDefault(unreadPath) == "blocked", "A stronger unread reason must not be downgraded.");
        Require((await store.ChangeAsync([HostStateChange.MarkRead(unreadPath)])).UnreadWorkspaces.ContainsKey(unreadPath), "Mark as read keeps a waiting agent.");
        Require(new HostStateStore(directory).Current.UnreadWorkspaces.GetValueOrDefault(unreadPath) == "blocked", "Unread marks persist.");
        Require(!(await store.ChangeAsync([HostStateChange.MarkRead(unreadPath, seen: true)])).UnreadWorkspaces.ContainsKey(unreadPath), "Seeing the workspace clears every mark.");
        try { await store.ChangeAsync([HostStateChange.MarkUnread(unreadPath, "shiny")]); throw new InvalidOperationException("An unknown unread reason was accepted."); }
        catch (ArgumentException) { }
        Console.WriteLine("PASS upstream #770 host: unread marks rank, persist, survive Mark as read while blocked and clear when seen");

        var legacy = await store.ChangeAsync([HostStateChange.Setting("theme", "light")]);
        Require(legacy.Settings is { Theme: "light", ThemeMode: "fixed" }, "A theme change without a mode must switch the host to fixed mode.");
        var paired = await store.ChangeAsync([HostStateChange.Setting("theme-mode", "system"), HostStateChange.Setting("theme", "dark")]);
        Require(paired.Settings is { Theme: "dark", ThemeMode: "system" }, "An explicit mode in the same batch wins over the legacy rule.");

        var id = store.InstallationId;
        Require(Guid.TryParse(id, out _) && File.Exists(Path.Combine(directory, Installation.FileName)), "A state directory must receive an installation identity.");
        Require(new HostStateStore(directory).InstallationId == id && Installation.EnsureIn(directory) == id, "The installation identity must be stable.");
        Require(new HostStateStore(null).InstallationId is null, "A memory-only store has no installation identity.");
        var raced = Path.Combine(root, "state-installation-race");
        var ids = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() => Installation.EnsureIn(raced))));
        Require(ids.Distinct().Count() == 1, "Racing first launches must agree on one installation identity.");
        File.WriteAllText(Path.Combine(raced, Installation.FileName), "[]");
        try { Installation.EnsureIn(raced); throw new InvalidOperationException("A malformed installation file was replaced."); }
        catch (IOException) { }
        Console.WriteLine("PASS host state keeps unknown settings, normalizes theme changes and mints one installation identity");
        await CheckProjects(root);
    }

    private static async Task CheckProjects(string root)
    {
        using var git = new E2E.IsolatedGit(Path.Combine(root, "state-projects-git"));
        var directory = Path.Combine(root, "state-projects");
        var first = E2E.IsolatedGit.Repository(Path.Combine(root, "state-projects-repos", "My Project!"));
        var second = E2E.IsolatedGit.Repository(Path.Combine(root, "state-projects-repos", "nested", "my.project"));
        var store = new HostStateStore(directory);
        var opened = await store.ChangeAsync([HostStateChange.OpenProject(first), HostStateChange.OpenProject(second)]);
        var one = opened.ProjectRecords.Single(record => record.Path == first);
        var two = opened.ProjectRecords.Single(record => record.Path == second);
        Require(Guid.TryParse(one.Id, out _) && one.Id != two.Id && one.Slug == "my-project" && two.Slug == "my-project-2" && one.LastOpened > 0,
            "Opening a project must mint an id and a readable slug that is unique among the host's projects.");
        var closed = await store.ChangeAsync([HostStateChange.CloseProject(first)]);
        Require(closed.ProjectRecords.Single(record => record.Path == first) == one, "Closing a project must keep its identity.");
        await Task.Delay(5);
        var reopened = (await store.ChangeAsync([HostStateChange.OpenProject(first)])).ProjectRecords.Single(record => record.Path == first);
        Require(reopened.Id == one.Id && reopened.Slug == one.Slug && reopened.LastOpened > one.LastOpened, "Reopening must keep id and slug and advance lastOpened.");
        var reloaded = new HostStateStore(directory).Current;
        Require(reloaded.ProjectRecords.OrderBy(record => record.Id).SequenceEqual(store.Current.ProjectRecords.OrderBy(record => record.Id)), "Project identities must persist.");
        var forgotten = await store.ChangeAsync([HostStateChange.ForgetProject(second)]);
        Require(forgotten.ProjectRecords.Single().Path == first, "A forgotten project must lose its record.");

        var legacy = Path.Combine(root, "state-projects-legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, HostStateStore.FileName), new JsonObject { ["Projects"] = new JsonArray(first), ["RecentProjects"] = new JsonArray(second) }.ToJsonString());
        var minted = new HostStateStore(legacy).Current.ProjectRecords;
        Require(minted.Count == 2 && new HostStateStore(legacy).Current.ProjectRecords.SequenceEqual(minted), "Identities minted for an older state file must be written at once.");

        var worktree = Path.Combine(root, "state-projects-repos", "linked");
        E2E.IsolatedGit.Run(first, "worktree", "add", "-b", "linked", worktree);
        try
        {
            await store.ChangeAsync([HostStateChange.OpenProject(worktree)]);
            throw new InvalidOperationException("A worktree of an open project was opened as a second project.");
        }
        catch (HostException error) when (error.Code == HostErrorCode.AlreadyOpen) { }
        Require(!store.Current.Projects.Contains(worktree), "A refused open must leave the project list unchanged.");
        await store.ChangeAsync([HostStateChange.CloseProject(first)]);
        Require((await store.ChangeAsync([HostStateChange.OpenProject(worktree)])).Projects.Contains(worktree), "A worktree whose project is not open may be opened on its own.");
        Console.WriteLine("PASS projects keep ids, slugs and lastOpened across close and reopen, and a workspace is not opened as a project");
    }
}