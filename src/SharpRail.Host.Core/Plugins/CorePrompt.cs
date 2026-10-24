using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>
/// The host's own text for the instructions agent plugins give sessions (H20): what belongs to a core feature rather
/// than to a plugin, each gated on that feature. It comes ahead of every plugin's contributions; each piece has an identifier
/// (<c>core.&lt;id&gt;</c>) a project can turn off in <c>.sharprail/instructions.toml</c>.
/// </summary>
internal static class CorePrompt
{
    /// <summary>
    /// The host's sections that every agent gets, in order, by identifier (<c>core.</c> plus it): what used to be each
    /// agent plugin's fixed text, so a project can turn each off in <c>.sharprail/instructions.toml</c>.
    /// </summary>
    internal static readonly IReadOnlyList<(string Id, string Title, string Description, string Text)> Fixed =
    [
        ("terminal-title", "Terminal title", "Name this terminal's tab with set_title once the task is clear.", """
            Call `set_title` once, as soon as the task is clear, with a `title` (3–6 words, in the user's language) for this terminal's tab.
            An unnamed managed workspace takes the first terminal title as its workspace name and branch slug; choose a concise task title. Manual workspace names are kept.
            Title work on a PR, issue or ticket `<Verb> #<number> <its exact title>`. Call it again only when the terminal moves to another task.
            """),
        ("pull-requests", "Pull request skill", "Use shipping-a-pr only when the user wants work on a pull request.", """
            Use `shipping-a-pr` only when the user's prompt indicates they want work on a pull request. A request to commit and/or push alone does not authorize using this skill.
            """),
        ("workspaces", "Workspaces", "Reuse the prepared workspace; create others only with workspace_create.", """
            Reuse the current workspace when the user has already prepared it for this task, including a non-Default workspace.
            Create another workspace only when the task needs additional isolation or parallel work; then you MUST use `workspace_create` (Git worktree).
            Supply a concise `description` explaining the task: the user sees it as the workspace label in Projects.
            Supply `branch` when a specific new branch is needed and `baseBranch` for the requested base; otherwise the base is the calling terminal's workspace HEAD.
            Work in the returned `path`; the tool does not switch your terminal. NEVER use `git worktree add`; use this tool so the workspace is placed and described in SharpRail.
            """),
        ("workspace-emigrate", "Workspace emigration", "Move this terminal into a workspace you created only on the user's instruction, with workspace_emigrate.", """
            `workspace_emigrate` moves this terminal tab into a workspace this same terminal created (through workspace_create) and relaunches you there, resumed with this conversation.
            Call it ONLY when the user explicitly instructed you to work in a new workspace AND you are 100% certain you will never again touch the workspace you started in; otherwise keep working in the new `path` from here.
            The user approves each move; after an approved call, end your turn at once without further tool calls. It moves only your own tab, never another terminal.
            """),
        ("parallel-tasks", "Parallel tasks", "Suggest a subagent and workspace per unrelated task.", """
            When the user gives you several unrelated tasks or bug reports, suggest parallelizing them with a subagent and a separate SharpRail workspace per independent task, even if this session did not start as an orchestrator.
            Explain that this keeps each task's context focused and lets the user follow and review the work separately in SharpRail. Make the suggestion before processing the backlog sequentially; delegate only when authorized and subagent tools are available.
            """),
        ("workspace-cleanup", "Workspace cleanup", "Remove workspaces you created with workspace_delete once their work is kept.", """
            Reduce clutter by removing workspaces you created once their work is preserved and they are no longer needed.
            Leave the checkout first, then call `workspace_delete` with its `path` rather than `git worktree remove`, so Projects updates at once; it refuses uncommitted work. Keep any workspace with an active agent.
            Removing a workspace removes its checkout, not its branch. Never remove workspaces you did not create or the Default workspace.
            """),
        ("push-and-signing", "Pushing and signing", "Never push unasked or bypass commit signing.", """
            NEVER push unless the user asks. NEVER bypass or disable commit signing: if signing or the SSH agent does not respond, the user is away; stop and report instead of working around it.
            """),
        ("warm-workspaces", "Warm workspaces", "Keep .sharprail/workspace.toml listing dependency and cache directories.", """
            New workspaces can start warm: `.sharprail/workspace.toml` in the project root (committed, yours to edit) lists git-ignored paths cloned from the Default workspace into workspaces created afterwards.
            Schema (TOML): `version = 1`, then `[clone]` with `include = ["node_modules", ".venv", "**/target"]` and optional `exclude = ["**/target/tmp"]`. Patterns are root-relative: `*`/`?` stay inside a segment, `**` spans segments, a name without `/` matches at any depth.
            Only ignored, untracked paths are cloned, and without the file nothing is. You MUST add an entry when you introduce a dependency directory or build cache worth sharing; the edit does not affect existing workspaces.
            """),
    ];

    /// <summary>
    /// The Code Intelligence guidance: the <c>lsp_*</c> rules, stated once, and <c>.sharprail/language.toml</c>.
    /// <c>{{project}}</c> becomes the workspace's servers and examples. It is left out while language services are disabled.
    /// </summary>
    internal const string LanguageServices = """
        ## Code intelligence: use `lsp_navigate` and `lsp_diagnostics`, not grep

        grep answers code questions wrongly: it misses implementations, overloads, partial classes, generated code and platform-specific variants (a grep-only agent once missed that macOS uses a different terminal renderer and shipped a fix that never ran there).
        - You MUST use `lsp_navigate` for every question about a symbol (definition, callers, implementations, shape) before you grep or Read whole files. Use grep ONLY for literals, comments, configuration, documentation and non-code files, or after `lsp_navigate` returned incomplete results; then say why in your report.
        - You MUST run `lsp_diagnostics` with `paths` listing every file you edited before you declare the work done; a build is NOT a substitute, and partial, pending or skipped coverage is not clean.
        - Pass the absolute `workspace` to EVERY `lsp_*` call: a subagent's shell `cd` does not retarget MCP.
        - The `lsp_*` tools start the language server a file needs and wait for it up to 60 s, then report `initializing`: wait and call again, NEVER treat that as "no results" and NEVER fall back silently to grep. An error names a working call shape: fix the arguments, NEVER abandon LSP because of a malformed call. Only a setting the user disabled is respected; say so with `lsp_status {"operation": "servers"}` evidence.

        {{project}}
        Name the symbol, no position needed; `Type/Member` narrows a shared name and an ambiguous result lists candidates. Explicit `line`/`column` are zero-based. `scope: "dependencies"` then `operation: "source"` reads library code.

        Subagents do NOT receive this text. When you brief one for code work, the brief MUST contain its absolute workspace path, that it passes it to every SharpRail tool, the rules above with the server ids its task needs, and that its final report MUST state how many `lsp_navigate` and `lsp_diagnostics` calls it made; reject a report with none. Give each independent task its own workspace with its servers: `workspace_create({description, languageServers: {{ids}}})`.

        Team-wide server options live in `.sharprail/language.toml` (`version = 1`, one `[servers.<id>]` table: `autoStart`, `arguments`, `buildTool`, `compilationDatabasePath`, `compilerFlags`, `includePaths`, `initializationOptions`, `settings`); edit it directly, keep comments and unknown keys, never put host paths or secrets in it. `lsp_status` `configure` changes only this host's override.
        """;

    private const string Placeholder = "<absolute path>";

    /// <summary>
    /// The Code Intelligence text for one workspace: its servers (at most <see cref="ProjectLanguageProfile.Shown"/>, by
    /// file count) and examples on a real symbol of its primary language. Without a <paramref name="profile"/> (no
    /// workspace, or before its first census) it names no server and uses placeholder examples. The full catalog is
    /// never listed; <c>lsp_status servers</c> lists it.
    /// </summary>
    internal static string Text(IReadOnlyList<LanguageServer>? catalog = null, ProjectLanguageProfile? profile = null, string workspace = "")
    {
        var servers = catalog ?? LanguageServers.Catalog;
        var shown = (profile?.Languages ?? []).Select(item => (item.Files, Server: servers.FirstOrDefault(server => server.Id == item.Server)))
            .Where(item => item.Server is not null).Take(ProjectLanguageProfile.Shown).Select(item => (item.Files, Server: item.Server!)).ToArray();
        var path = workspace.Length > 0 ? workspace : Placeholder;
        string Call(string fields) => "`{" + fields + ", \"workspace\": \"" + Escape(path) + "\"}`";
        var text = new System.Text.StringBuilder();
        if (shown.Length > 0)
        {
            text.Append("This project's language servers, by file count; ").Append(Call("\"operation\": \"retry\", \"server\": \"<id>\"").Insert(1, "lsp_status ")).Append(" starts one:\n");
            foreach (var (files, server) in shown)
                text.Append("- `").Append(server.Id).Append("` (").Append(Name(server)).Append(": ").Append(string.Join(" ", server.Extensions.Take(4))).Append("): ").Append(files).Append(files == 1 ? " file\n" : " files\n");
            text.Append("Other servers exist; `lsp_status {\"operation\": \"servers\"}` lists them, so NEVER conclude a file type has no LSP.\n\n");
        }
        else
            text.Append("Language servers start on demand; `lsp_status {\"operation\": \"servers\"}` lists them and ").Append(Call("\"operation\": \"retry\", \"server\": \"<id>\"").Insert(1, "lsp_status ")).Append(" starts one, so NEVER conclude a file type has no LSP.\n\n");
        if (profile is { Symbol.Length: > 0 } known && shown.Length > 0)
        {
            text.Append("Examples in this project (").Append(Name(shown[0].Server)).Append("):\n");
            text.Append("- ").Append(Call($"\"operation\": \"references\", \"query\": \"{Escape(known.Symbol)}\"")).Append(" (also `definition`, `implementation`, `hover`).\n");
            if (known.Member.Length > 0)
                text.Append("- ").Append(Call($"\"operation\": \"symbolBody\", \"path\": \"{Escape(known.SymbolPath)}\", \"query\": \"{Escape(known.Member)}\"")).Append(" reads one member instead of the file; `symbols` with that `path` outlines it.\n");
            else
                text.Append("- ").Append(Call($"\"operation\": \"symbols\", \"path\": \"{Escape(known.SymbolPath)}\"")).Append(" outlines a file; `symbolBody` with a `query` reads one member of it.\n");
            text.Append("- ").Append(Call($"\"operation\": \"workspaceSymbols\", \"query\": \"{Escape(Prefix(known.Symbol))}\"")).Append(" finds symbols and their files.\n");
        }
        else
        {
            text.Append("Examples:\n");
            text.Append("- ").Append(Call("\"operation\": \"references\", \"query\": \"TypeName\"")).Append(" (also `definition`, `implementation`, `hover`).\n");
            text.Append("- ").Append(Call("\"operation\": \"workspaceSymbols\", \"query\": \"Foo\"")).Append(" finds a file; `symbols` with a `path` outlines it and `symbolBody` with a `path` and `query` reads one member.\n");
        }
        var ids = shown.Length > 0 ? "[" + string.Join(", ", shown.Take(2).Select(item => "\"" + item.Server.Id + "\"")) + "]" : "[\"<id>\"]";
        return LanguageServices.TrimEnd('\n').Replace("{{project}}\n", text.ToString(), StringComparison.Ordinal).Replace("{{ids}}", ids, StringComparison.Ordinal);
    }

    /// <summary>The language a server is for, without its implementation: <c>C# (Roslyn)</c> → <c>C#</c>.</summary>
    private static string Name(LanguageServer server)
    {
        var name = server.Spec.Name.Length > 0 ? server.Spec.Name : server.Implementation.Split(" · ")[0];
        var implementation = name.IndexOf(" (", StringComparison.Ordinal);
        return implementation > 0 ? name[..implementation] : name;
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

    /// <summary>The leading word of a name (<c>WorkbenchWindow</c> → <c>Workbench</c>, <c>parse_config</c> → <c>parse</c>), a search that finds its relatives.</summary>
    private static string Prefix(string name)
    {
        for (var i = 1; i < name.Length; i++)
            if (char.IsUpper(name[i]) && char.IsLower(name[i - 1]) || name[i] == '_' && i >= 3) return name[..i];
        return name;
    }

    /// <summary>
    /// The host's contributions; <paramref name="languageServicesEnabled"/> reads the Code Intelligence setting,
    /// <paramref name="catalog"/> the servers enabled plugins provide and <paramref name="profile"/> the workspace's languages.
    /// </summary>
    internal static IReadOnlyList<PromptContribution> Contributions(Func<bool> languageServicesEnabled, Func<IReadOnlyList<LanguageServer>>? catalog = null,
        ProjectLanguageProfile? profile = null, string workspace = "") =>
    [
        .. Fixed.Select(item => new PromptContribution { Id = item.Id, Title = item.Title, Description = item.Description, Text = item.Text, Placement = PromptPlacement.Section }),
        new()
        {
            Id = "code-intelligence", Title = "Code intelligence", Description = "Use lsp_navigate and lsp_diagnostics instead of grep; this project's servers.",
            Text = Text(catalog?.Invoke(), profile, workspace), Placement = PromptPlacement.Section, When = languageServicesEnabled
        },
    ];
}