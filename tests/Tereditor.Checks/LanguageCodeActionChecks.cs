using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tereditor.Checks;

/// <summary>
/// The fixture server's code-action answers (offer, resolve, edit), shared by the agent tool checks.
/// </summary>
internal static class LanguageCodeActionChecks
{
    private static readonly object Range = new { start = new { line = 0, character = 3 }, end = new { line = 0, character = 9 } };

    /// <summary>The fixture server's answer to textDocument/codeAction: actions only inside the "broken" diagnostic of the first line.</summary>
    internal static object Offer(string uri, string text, int version, JsonNode parameters)
    {
        var caret = parameters["range"]!["start"]!;
        if (!text.Contains("broken", StringComparison.Ordinal) || caret["line"]!.GetValue<int>() != 0 || caret["character"]!.GetValue<int>() is < 3 or > 9) return Array.Empty<object>();
        var extra = new List<object>();
        // Actions the agent checks use: an edit outside the workspace, and one with file operations.
        if (text.Contains("outside", StringComparison.Ordinal))
            extra.Add(new { title = "Edit outside", kind = "quickfix", edit = new { changes = new Dictionary<string, object[]> { [new Uri(Path.Combine(Path.GetTempPath(), "tereditor-outside.ts")).AbsoluteUri] = [new { range = Range, newText = "x" }] } } });
        if (text.Contains("fileops", StringComparison.Ordinal))
        {
            var folder = Path.GetDirectoryName(new Uri(uri).LocalPath)!;
            string Of(string name) => new Uri(Path.Combine(folder, name)).AbsoluteUri;
            extra.Add(new
            {
                title = "File operations",
                kind = "refactor",
                edit = new
                {
                    documentChanges = new object[]
                    {
                        new { kind = "create", uri = Of("created.ts") },
                        new { textDocument = new { uri = Of("created.ts"), version = (int?)null }, edits = new object[] { new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } }, newText = "hello" } } },
                        new { kind = "rename", oldUri = Of("old.ts"), newUri = Of("renamed.ts") },
                        new { kind = "delete", uri = Of("gone.ts") }
                    }
                }
            });
        }
        object[] offered =
        [
            .. extra,
            new { title = "Fix broken", kind = "quickfix", isPreferred = true, diagnostics = parameters["context"]!["diagnostics"], edit = new { documentChanges = new object[] { new { textDocument = new { uri, version }, edits = new object[] { new { range = Range, newText = "fixed" }, new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } }, newText = "// " } } } } } },
            new { title = "Disabled fix", kind = "quickfix", disabled = new { reason = "not here" }, edit = Edit(uri, version, "x") },
            new { title = "Unknown command", kind = "quickfix", command = new { title = "Unknown command", command = "fixture.undeclared" } },
            new { title = "Run fixture command", kind = "quickfix", command = new { title = "Run fixture command", command = "fixture.applyFix" } },
            new { title = "Resolve me", kind = "refactor", data = 1 }
        ];
        return offered;
    }

    /// <summary>codeAction/resolve like a real server: the action as sent, with the edit filled in only for an action that carries resolve data.</summary>
    internal static JsonNode Resolve(string uri, int version, JsonNode action)
    {
        var resolved = action.DeepClone();
        if (action["data"] is not null) resolved["edit"] = JsonSerializer.SerializeToNode(Edit(uri, version, "/*r*/"));
        return resolved;
    }

    internal static object Edit(string uri, int version, string insert) => new { documentChanges = new object[] { new { textDocument = new { uri, version }, edits = new object[] { new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } }, newText = insert } } } } };
}