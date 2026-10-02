using System.Text.Json;
using System.Text.Json.Nodes;

using SharpRail.Host.Core;
using SharpRail.Host.Core.Plugins;
using SharpRail.Plugins.Api;
using SharpRail.Plugins.SpecDialect;

namespace SharpRail.Checks;

/// <summary>Observable contracts translated from the fork's spec-graph core and tools tests.</summary>
internal static class SpecToolChecks
{
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    public static async Task Run(string root)
    {
        var project = Path.Combine(root, "spec-tools");
        Directory.CreateDirectory(project);
        var state = new HostStateStore(Path.Combine(root, "spec-tools-state"));
        await using var runtime = new PluginRuntime(new() { StateDirectory = null, State = state, Builtins = [(SpecDialectManifest.Manifest, new SpecDialectHost())] });
        await runtime.Start();
        var tools = runtime.McpTools(null, project).ToDictionary(tool => tool.Name);
        async Task<(string Text, bool Error)> Call(string name, object parameters) =>
            await tools[name].Call(JsonSerializer.SerializeToNode(parameters, PluginJson.Options)!.AsObject(), CancellationToken.None);
        async Task<string> Success(string name, object parameters)
        {
            var reply = await Call(name, parameters);
            Require(!reply.Error, name + ": " + reply.Text);
            return reply.Text;
        }
        async Task Fails(string name, object parameters, string contains)
        {
            var reply = await Call(name, parameters);
            Require(reply.Error && reply.Text.Contains(contains, StringComparison.Ordinal), name + " should refuse the input: " + reply.Text);
        }

        var createSchema = tools["spec_create"].InputSchema;
        Require(createSchema["properties"]?["type"]?.ToJsonString().Contains("module-design", StringComparison.Ordinal) == true,
            "The create schema advertises the dialect's type enum: " + createSchema);
        foreach (var (type, firstHeading) in new[] { ("goal-and-requirements", "Goal"), ("architecture-design", "Drivers"),
            ("module-design", "Responsibility"), ("submodule-design", "Responsibility"), ("task-spec", "Purpose") })
        {
            await Success("spec_create", new { path = type + ".md", id = type, type, title = "Title: " + type, status = "draft", tags = new[] { "v1" } });
            var text = await File.ReadAllTextAsync(Path.Combine(project, type + ".md"));
            Require(text.Contains("## " + firstHeading, StringComparison.Ordinal), "Create chooses the heading scaffold by type.");
            Require((await Success("spec_get", new { id = type })).Contains(" — Title: " + type, StringComparison.Ordinal), "Quoted YAML titles round trip.");
        }
        await Fails("spec_create", new { path = "module-design.md", id = "collision", type = "module-design", title = "Collision" }, "already exists");
        await Fails("spec_create", new { path = "another.md", id = "module-design", type = "module-design", title = "Duplicate" }, "already in use");
        await Fails("spec_create", new { path = "empty.md", id = "", type = "module-design", title = "Empty" }, "non-empty");
        await Fails("spec_create", new { path = "type.md", id = "invalid", type = "invalid-type", title = "Invalid" }, "Invalid arguments");
        foreach (var path in new[] { "../escape.md", "/absolute.md", "wrong.txt", ".git/spec.md", "node_modules/spec.md", "DiSt/spec.md", " " })
            await Fails("spec_create", new { path, id = "unsafe", type = "module-design", title = "Unsafe" }, "Error:");
        var outside = Path.Combine(root, "outside-spec-tools");
        Directory.CreateDirectory(outside);
        Directory.CreateSymbolicLink(Path.Combine(project, "linked"), outside);
        await Fails("spec_create", new { path = "linked/spec.md", id = "symlink", type = "module-design", title = "Unsafe" }, "symlink");
        Require(!File.Exists(Path.Combine(outside, "spec.md")), "Create never follows a symlink.");
        Console.WriteLine("PASS fork spec_create: all five scaffolds, enums, collision and duplicate refusal, root-relative indexable paths and symlink refusal");

        var body = "\r\n## Body\r\n\r\nProse stays byte-for-byte.\r\n";
        var original = "\uFEFF---\r\n# dialect comment\r\nid: edit-me # identity\r\ntype: module-design\r\ntitle: Before # title comment\r\nstatus: draft\r\ntags:\r\n  - old # list comment\r\n  - keep\r\ncustom:\r\n  nested: value # untouched\r\nreferences: [goal-and-requirements]\r\n---\r\n" + body;
        await File.WriteAllTextAsync(Path.Combine(project, "edit.md"), original);
        await Success("spec_update", new
        {
            id = "edit-me",
            set = new { title = "After: punctuation", status = "active", parent = "goal-and-requirements" },
            addList = new { tags = new[] { "keep", "new" }, covers = new[] { "requirement" } },
            removeList = new { tags = new[] { "old" } }
        });
        var edited = System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(project, "edit.md")));
        Require(edited.StartsWith("\uFEFF---\r\n", StringComparison.Ordinal) && edited.EndsWith(body, StringComparison.Ordinal) &&
            edited.Contains("# title comment", StringComparison.Ordinal) && edited.Contains("custom:\r\n  nested: value # untouched", StringComparison.Ordinal) &&
            edited.Contains("# dialect comment", StringComparison.Ordinal) && edited.Contains("id: edit-me # identity", StringComparison.Ordinal) && edited.Contains("# list comment", StringComparison.Ordinal),
            "Update preserves BOM, CRLF, prose, comments and nested non-dialect YAML: " + edited);
        Require(!edited.Replace("\r\n", "", StringComparison.Ordinal).Contains('\n'), "Update preserves CRLF on inserted fields.");
        Require((await Success("spec_grep", new { pattern = "byte-for-byte", tag = "new", dependsOn = "absent" })) == "No matches.", "Dependency filter applies with tags.");
        Require((await Success("spec_grep", new { pattern = "byte-for-byte", tag = "new", parent = "goal-and-requirements" })).Contains("edit.md:", StringComparison.Ordinal), "Updated tags and parent are searchable.");
        await Fails("spec_update", new { id = "edit-me", set = new { id = "renamed" } }, "Cannot rename");
        await Fails("spec_update", new { id = "edit-me", set = new { tags = "wrong" } }, "addList/removeList");
        await Fails("spec_update", new { id = "edit-me", remove = new[] { "type" } }, "protected");
        await Fails("spec_update", new { id = "edit-me", set = new { type = "" } }, "valid id and type");
        Require(System.Text.Encoding.UTF8.GetString(await File.ReadAllBytesAsync(Path.Combine(project, "edit.md"))) == edited, "Rejected edits don't write the file.");
        await Success("spec_update", new { id = "edit-me", remove = new[] { "status" }, removeList = new { references = new[] { "goal-and-requirements" } } });
        Require(!(await File.ReadAllTextAsync(Path.Combine(project, "edit.md"))).Contains("status:", StringComparison.Ordinal), "A removable field leaves the YAML.");
        Console.WriteLine("PASS fork spec_update: scalar/list edits, protected fields, comments and custom YAML, BOM/CRLF and unchanged prose");

        async Task Write(string path, string frontmatter) => await File.WriteAllTextAsync(Path.Combine(project, path), "---\n" + frontmatter + "\n---\n\nNeedle\n");
        await Write("a.md", "id: a\ntype: module-design\ntitle: A\nparent: b\ndepends-on: [goal-and-requirements, missing]\nreferences: [edit-me]\nimplements: [architecture-design]\ntags: [selected]");
        await Write("b.md", "id: b\ntype: module-design\nparent: a");
        await Write("duplicate.md", "id: a\ntype: module-design\ntitle: Duplicate");
        await Write("not-spec.md", "id: incomplete");
        await Write("invalid-yaml.md", "id: [broken\ntype: module-design");
        var get = await Success("spec_get", new { id = "a" });
        Require(get.Contains("depends-on -> missing (missing)", StringComparison.Ordinal) && get.Contains("implements -> architecture-design", StringComparison.Ordinal) &&
            get.Contains("references -> edit-me", StringComparison.Ordinal) && get.Contains("parent -> b", StringComparison.Ordinal), "Get resolves all forward/reverse edge kinds: " + get);
        var neighbors = await Success("spec_graph", new { root = "a", direction = "neighbors", edge = "depends-on", depth = 1 });
        Require(neighbors.Contains("missing targets: missing", StringComparison.Ordinal) && neighbors.Contains("a --depends-on--> goal-and-requirements", StringComparison.Ordinal), "Graph reports missing targets: " + neighbors);
        var ancestors = await Success("spec_graph", new { root = "a", direction = "ancestors", depth = 100 });
        Require(ancestors.Contains("nodes (2)", StringComparison.Ordinal) && ancestors.Contains("edges (2)", StringComparison.Ordinal), "Cycle traversal terminates and keeps both edges.");
        Require((await Success("spec_graph", new { root = "a", direction = "subtree", depth = 0 })).Contains("nodes (1)", StringComparison.Ordinal), "Depth zero keeps only the root.");
        Require((await Success("spec_graph", new { root = "a", direction = "subtree", depth = 1 })).Contains("b --parent--> a", StringComparison.Ordinal), "Subtree follows reverse parent links.");
        await Fails("spec_graph", new { root = "unknown", direction = "neighbors" }, "No spec");
        var validation = await Success("spec_validate", new { });
        Require(validation.Contains("Duplicate ids (1)", StringComparison.Ordinal) && validation.Contains("Dangling links (1)", StringComparison.Ordinal) &&
            validation.Contains("Parent cycles (1)", StringComparison.Ordinal), "Validation reports every issue category: " + validation);
        var grep = await Success("spec_grep", new { pattern = "needle", tag = "selected", dependsOn = "goal-and-requirements" });
        Require(grep.StartsWith("1 match(es):", StringComparison.Ordinal) && grep.Contains("a.md:", StringComparison.Ordinal), "Grep applies both list metadata filters.");
        Require((await Success("spec_grep", new { pattern = "Needle", limit = 1 })).Contains("(truncated)", StringComparison.Ordinal), "Grep signals a bounded result.");
        await Fails("spec_grep", new { pattern = "[", regex = true }, "Invalid search pattern");
        Require(await Success("spec_grep", new { pattern = "incomplete" }) == "No matches.", "An id without a type isn't indexed.");
        Console.WriteLine("PASS fork spec graph/get/grep/validate: all edge kinds, graph depth and cycles, duplicates and dangling links, metadata filters and invalid YAML");

        await Success("spec_delete", new { id = "edit-me" });
        Require(!File.Exists(Path.Combine(project, "edit.md")), "Delete removes the spec file.");
        await Fails("spec_get", new { id = "edit-me" }, "No spec");
        Require((await Success("spec_validate", new { })).Contains("--references--> edit-me [missing]", StringComparison.Ordinal), "Delete keeps inbound links for validation to report.");
        await Fails("spec_delete", new { id = "edit-me" }, "No spec");
        Console.WriteLine("PASS fork spec_delete: removes the file, rejects an unknown id and retains other specs' links");
    }
}