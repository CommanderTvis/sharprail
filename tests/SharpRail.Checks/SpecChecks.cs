using System.Net;

using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

using SharpRail.Host.Abstractions;
using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Host.Remote;

namespace SharpRail.Checks;

/// <summary>The spec catalog: traversal order, frontmatter dialect, graph and validation, incremental index, durable-spec query and authoring.</summary>
internal static class SpecChecks
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Write(string root, string path, string text)
    {
        var full = Path.Combine(root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private static string Describe(SpecGraph graph) => string.Join("\n",
        string.Join(";", graph.Specs.Select(spec => spec.ToString())),
        string.Join(";", graph.Edges.Select(edge => edge.ToString())),
        string.Join(";", graph.DanglingLinks.Select(edge => edge.ToString())),
        string.Join(";", graph.DuplicateIds.Select(duplicate => duplicate.Id + "=" + string.Join(",", duplicate.Paths))),
        string.Join(";", graph.ParentCycles.Select(cycle => string.Join(">", cycle))));

    internal static async Task Run(string root)
    {
        var workspace = Path.Combine(root, "spec-checks");
        Write(workspace, "SPEC.md", "---\nid: goal\ntype: goal-and-requirements\ntitle: \"Goal: the product\"\nstatus: active\n---\n# Goal\n");
        Write(workspace, "src/core.md", "\uFEFF---\r\nid: core\r\ntype: module-design\r\ntitle: Core\r\nparent: goal\r\ndepends-on: [shared, 'missing one']\r\nreferences:\r\n  - goal\r\n  - shared\r\nimplements: goal\r\n---\r\nBody\r\n");
        Write(workspace, "src/shared.md", "---\nid: shared\ntype: module-design\ntitle: Shared # trailing comment\nparent: [goal]\n---\n");
        Write(workspace, "a/dup.md", "---\nid: twice\ntype: submodule-design\ntitle: First\nparent: ring\n---\n");
        Write(workspace, "b.md", "---\nid: twice\ntype: submodule-design\ntitle: Second\n---\n");
        Write(workspace, "c/dup.md", "---\nid: twice\ntype: submodule-design\ntitle: Third\n---\n");
        Write(workspace, "ring.md", "---\nid: ring\ntype: submodule-design\ntitle: Ring\nparent: twice\n---\n");
        Write(workspace, "docs/SPEC.md", "# Plain heading\n\nNo frontmatter.\n");
        Write(workspace, "notes.md", "---\ntitle: Not a spec\n---\n");
        Write(workspace, "node_modules/pkg/SPEC.md", "---\nid: hidden\ntype: module-design\ntitle: Hidden\n---\n");

        var core = new ProjectServices(workspace);
        IProjectServices local = new LocalProjectAdapter(core);
        var graph = await local.GetSpecGraphAsync();
        var byId = graph.Specs.ToDictionary(spec => spec.Id);
        Require(byId.Keys.Order().SequenceEqual(new[] { "core", "docs/SPEC.md", "goal", "ring", "shared", "twice" }.Order()), "The catalog must hold exactly the visible specs: " + string.Join(",", byId.Keys));
        Require(byId["goal"] is { Title: "Goal: the product", Status: "active" } && byId["core"] is { Title: "Core", Parent: "goal", Type: "module-design" } &&
            byId["shared"] is { Title: "Shared", Parent: "goal" } && byId["docs/SPEC.md"] is { Title: "Plain heading", Type: "spec" },
            "Frontmatter must tolerate a byte order mark, CRLF, quotes, comments and a bracketed parent.");
        Require(byId["twice"] is { Title: "First", Path: var winner } && winner == Path.Combine("a", "dup.md") &&
            graph.DuplicateIds.Single() is { Id: "twice" } duplicate && duplicate.Paths.SequenceEqual([Path.Combine("a", "dup.md"), "b.md", Path.Combine("c", "dup.md")]),
            "Directories and files sort in one list, so the duplicate-id winner is the first name in code-unit order.");
        Require(graph.Targets("core", SpecLinks.DependsOn).SequenceEqual(["shared", "missing one"]) && graph.Targets("core", SpecLinks.References).SequenceEqual(["goal", "shared"]) &&
            graph.Targets("core", SpecLinks.Implements).SequenceEqual(["goal"]) && graph.Sources("goal", SpecLinks.Parent).Order().SequenceEqual(["core", "shared"]) &&
            graph.Sources("shared", SpecLinks.DependsOn).SequenceEqual(["core"]), "The graph must carry every link kind forwards and backwards.");
        Require(graph.DanglingLinks.Single() == new SpecEdge("core", "missing one", SpecLinks.DependsOn) &&
            graph.ParentCycles.Single().Order().SequenceEqual(["ring", "twice"]) && !graph.IsValid, "Validation must report dangling links and parent cycles.");
        Require((await local.ListSpecsAsync()).SequenceEqual(graph.Specs) && graph.Specs.Select(spec => spec.Title).SequenceEqual(graph.Specs.Select(spec => spec.Title).Order(StringComparer.Ordinal)),
            "The spec list is the graph's specs ordered by title.");

        var index = SpecCatalog.For(workspace);
        var (parses, builds) = (index.Parses, index.Builds);
        Require(ReferenceEquals(await local.GetSpecGraphAsync(), graph) && index.Parses == parses && index.Builds == builds, "An unchanged workspace must be served without parsing or rebuilding.");
        Write(workspace, "src/shared.md", "---\nid: shared\ntype: module-design\ntitle: Shared module\nparent: goal\n---\n");
        var edited = await local.GetSpecGraphAsync();
        Require(index.Parses == parses + 1 && index.Builds == builds + 1 && edited.Specs.Single(spec => spec.Id == "shared").Title == "Shared module", "Only the changed file must be parsed again.");
        File.Delete(Path.Combine(workspace, "ring.md"));
        var removed = await local.GetSpecGraphAsync();
        Require(index.Parses == parses + 1 && index.Builds == builds + 2 && removed.ParentCycles.Count == 0 && removed.DanglingLinks.Count == 2, "A removed file must leave the index and the graph.");

        await using var server = RemoteServer.Create(workspace, IPAddress.Loopback, 0, "spec-test");
        await server.StartAsync();
        try
        {
            var address = server.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var remote = new RemoteProjectAdapter(new Uri(address), "spec-test");
            Require(Describe(await remote.GetSpecGraphAsync()) == Describe(removed), "Remote spec graph differs.");
            Require((await remote.ListSpecsAsync()).SequenceEqual(removed.Specs), "Remote specs differ.");
            Require(await local.HasDurableSpecsAsync() && await remote.HasDurableSpecsAsync(), "A workspace with durable specs must say so on both hosts.");
        }
        finally { await server.StopAsync(); }

        var scratch = Path.Combine(root, "spec-scratch");
        Write(scratch, ".thinkrail/context/task.md", "---\nid: task\ntype: task-spec\ntitle: Scratch\n---\n");
        var scratchHost = new ProjectServices(scratch);
        Require(!await scratchHost.HasDurableSpecsAsync(), "An ephemeral task spec is not a durable spec.");
        Write(scratch, "SPEC.md", "---\nid: goal\ntype: goal-and-requirements\ntitle: Goal\n---\n");
        Require(await scratchHost.HasDurableSpecsAsync(), "A durable spec must be found on the next read.");
        Require(!await new ProjectServices(Path.Combine(root, "spec-absent")).HasDurableSpecsAsync(), "An unreadable project has no durable specs rather than an error.");
        try { await new ProjectServices(Path.Combine(root, "spec-absent")).ListSpecsAsync(); throw new InvalidOperationException("A missing workspace listed specs."); }
        catch (DirectoryNotFoundException) { }
        Console.WriteLine("PASS spec catalog: deterministic traversal, tolerant frontmatter, full graph and validation, incremental index and durable-spec query, equal over gRPC");

        using (var git = new E2E.IsolatedGit(Path.Combine(root, "spec-git")))
        {
            var repository = E2E.IsolatedGit.Repository(Path.Combine(root, "spec-repo"));
            var worktree = Path.Combine(root, "spec-worktree");
            var session = new ProjectServices(repository);
            await session.ApplyGitActionAsync(new("create-worktree", worktree, "spec-branch"));
            await new ProjectServices(worktree).ListSpecsAsync();
            Require(SpecCatalog.IsIndexed(worktree), "A read workspace must keep its index.");
            await session.ApplyGitActionAsync(new("remove-worktree", worktree));
            Require(!SpecCatalog.IsIndexed(worktree), "Removing a workspace must drop its index.");
        }
        Console.WriteLine("PASS spec index is dropped with its workspace");
        Authoring(root, workspace);
    }

    private static void Authoring(string root, string workspace)
    {
        Directory.CreateDirectory(Path.Combine(workspace, "real"));
        Directory.CreateSymbolicLink(Path.Combine(workspace, "linked"), Path.Combine(workspace, "real"));
        foreach (var path in new[] { "", "  ", Path.Combine(workspace, "x.md"), "notes.txt", "../out.md", "a/../../out.md", "node_modules/x.md", "src/.git/x.md", "linked/x.md", "src" + Path.DirectorySeparatorChar, "real.md/" })
            try { SpecAuthoring.ResolvePath(workspace, path); throw new InvalidOperationException($"The path rule accepted {path}."); }
            catch (ArgumentException) { }
        Require(SpecAuthoring.ResolvePath(workspace, "./new/dir//spec.md") == "new/dir/spec.md" && SpecAuthoring.ResolvePath(workspace, "build.md") == "build.md",
            "The path rule must normalise an acceptable path and refuse only ignored directories, not names that resemble them.");

        var created = SpecAuthoring.Create(workspace, new("new/dir/spec.md", "fresh", "submodule-design", "Fresh: a spec")
        { Parent = "goal", DependsOn = ["core", "shared"], Tags = ["x, y"], Body = "\n# Fresh\n" });
        var graph = SpecCatalog.For(workspace).Graph(default);
        Require(created == "new/dir/spec.md" && graph.Specs.Single(spec => spec.Id == "fresh") is { Title: "Fresh: a spec", Parent: "goal" } &&
            graph.Targets("fresh", SpecLinks.DependsOn).SequenceEqual(["core", "shared"]), "A created spec must be read back by the catalog.");
        foreach (var draft in new SpecDraft[] { new("other.md", "fresh", "module-design", "Same id"), new("new/dir/spec.md", "another", "module-design", "Same path"), new("x.md", "x", "", "No type") })
            try { SpecAuthoring.Create(workspace, draft); throw new InvalidCastException($"Create accepted {draft.Path}."); }
            catch (Exception error) when (error is ArgumentException || error is InvalidOperationException && error.Message.Contains("already exists", StringComparison.Ordinal)) { }

        const string body = "\r\n# Core\r\n\r\n---\r\nnot: frontmatter\r\n";
        var original = "\uFEFF---\r\nid: core\r\n# keep this comment\r\ntype: module-design\r\ntitle: Core\r\ncustom: value\r\ndepends-on:\r\n  - shared\r\n  - goal\r\n---" + body;
        var updated = SpecAuthoring.UpdateText(original, new()
        {
            Set = new Dictionary<string, string> { ["status"] = "active", ["title"] = "Core: host" },
            Remove = ["absent"],
            AddList = new Dictionary<string, string[]> { [SpecLinks.DependsOn] = ["fresh", "goal"], ["tags"] = ["one"] },
            RemoveList = new Dictionary<string, string[]> { [SpecLinks.DependsOn] = ["shared"] }
        });
        Require(updated == "\uFEFF---\r\nid: core\r\n# keep this comment\r\ntype: module-design\r\ntitle: \"Core: host\"\r\ncustom: value\r\ndepends-on: [goal, fresh]\r\nstatus: active\r\ntags: [one]\r\n---" + body,
            "An update must rewrite only the touched frontmatter lines and keep the byte order mark, CRLF, comments and body: " + updated.Replace("\r", "\\r").Replace("\n", "\\n"));
        Require(SpecAuthoring.UpdateText(original, new()) == original, "An empty edit must return the file unchanged.");
        foreach (var edit in new FrontmatterEdit[]
        {
            new() { Set = new Dictionary<string, string> { ["id"] = "renamed" } }, new() { Set = new Dictionary<string, string> { ["tags"] = "a" } },
            new() { Remove = ["type"] }, new() { Set = new Dictionary<string, string> { ["type"] = "" } }
        })
            try { SpecAuthoring.UpdateText(original, edit); throw new InvalidCastException("An edit that breaks identity was accepted."); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException) { }
        try { SpecAuthoring.UpdateText("# No frontmatter\n", new()); throw new InvalidCastException("A file without frontmatter was updated."); }
        catch (InvalidOperationException) { }

        var before = File.ReadAllBytes(Path.Combine(workspace, "src", "core.md"));
        SpecAuthoring.Update(workspace, "core", new() { Set = new Dictionary<string, string> { ["status"] = "done" } });
        var after = File.ReadAllBytes(Path.Combine(workspace, "src", "core.md"));
        Require(after.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) && after.AsSpan().EndsWith("---\r\nBody\r\n"u8) && after.Length == before.Length + "status: done\r\n".Length &&
            SpecCatalog.For(workspace).Graph(default).Specs.Single(spec => spec.Id == "core").Status == "done", "A file update must keep the bytes it does not edit and be visible on the next read.");
        Require(SpecAuthoring.Delete(workspace, "fresh") == "new/dir/spec.md" && !File.Exists(Path.Combine(workspace, "new", "dir", "spec.md")) &&
            SpecCatalog.For(workspace).Graph(default).Specs.All(spec => spec.Id != "fresh"), "A deleted spec must leave the catalog.");
        foreach (var act in new Action[] { () => SpecAuthoring.Delete(workspace, "fresh"), () => SpecAuthoring.Update(workspace, "nobody", new()) })
            try { act(); throw new InvalidCastException("An unknown spec id was accepted."); }
            catch (InvalidOperationException) { }
        Console.WriteLine("PASS spec authoring: one path rule, create, frontmatter-only lossless update and delete");
    }
}