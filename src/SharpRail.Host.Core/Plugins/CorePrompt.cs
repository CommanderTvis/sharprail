using SharpRail.Host.Abstractions;
using SharpRail.Host.Core.Language;
using SharpRail.Plugins.Api.Host;

namespace SharpRail.Host.Core.Plugins;

/// <summary>
/// The host's own text for the instructions agent plugins give sessions (H20): what belongs to a core feature rather
/// than to a plugin, each gated on that feature. It comes ahead of every plugin's contributions.
/// </summary>
internal static class CorePrompt
{
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
        new() { Id = "language-services", Text = Text(catalog?.Invoke(), profile, workspace), Placement = PromptPlacement.Section, When = languageServicesEnabled },
    ];
}