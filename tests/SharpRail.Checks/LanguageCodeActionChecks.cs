using System.Text.Json;
using System.Text.Json.Nodes;

using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;

using SharpRail.Host.Client;
using SharpRail.Host.Core;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Checks;

/// <summary>
/// Quick fixes through the fixture language server: Alt+Enter lists the server's code actions at the caret, Enter applies the chosen
/// edit to the buffer as one undo step (directly, after codeAction/resolve and through a command's workspace/applyEdit), an empty answer
/// shows no list, and an answer for an older revision is dropped.
/// </summary>
internal static class LanguageCodeActionChecks
{
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static readonly object Range = new { start = new { line = 0, character = 3 }, end = new { line = 0, character = 9 } };

    /// <summary>The fixture server's answer to textDocument/codeAction: actions only inside the "broken" diagnostic of the first line.</summary>
    internal static object Offer(string uri, string text, int version, JsonNode parameters)
    {
        var caret = parameters["range"]!["start"]!;
        if (!text.Contains("broken", StringComparison.Ordinal) || caret["line"]!.GetValue<int>() != 0 || caret["character"]!.GetValue<int>() is < 3 or > 9) return Array.Empty<object>();
        return new object[]
        {
            new { title = "Fix broken", kind = "quickfix", isPreferred = true, diagnostics = parameters["context"]!["diagnostics"], edit = new { documentChanges = new object[] { new { textDocument = new { uri, version }, edits = new object[] { new { range = Range, newText = "fixed" }, new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } }, newText = "// " } } } } } },
            new { title = "Disabled fix", kind = "quickfix", disabled = new { reason = "not here" }, edit = Edit(uri, version, "x") },
            new { title = "Unknown command", kind = "quickfix", command = new { title = "Unknown command", command = "fixture.undeclared" } },
            new { title = "Run fixture command", kind = "quickfix", command = new { title = "Run fixture command", command = "fixture.applyFix" } },
            new { title = "Resolve me", kind = "refactor", data = 1 }
        };
    }

    /// <summary>codeAction/resolve like a real server: the action as sent, with the edit filled in only for an action that carries resolve data.</summary>
    internal static JsonNode Resolve(string uri, int version, JsonNode action)
    {
        var resolved = action.DeepClone();
        if (action["data"] is not null) resolved["edit"] = JsonSerializer.SerializeToNode(Edit(uri, version, "/*r*/"));
        return resolved;
    }

    internal static object Edit(string uri, int version, string insert) => new { documentChanges = new object[] { new { textDocument = new { uri, version }, edits = new object[] { new { range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 0 } }, newText = insert } } } } };

    internal static void Run(string fixture)
    {
        var root = Path.Combine(fixture, "language-code-actions"); Directory.CreateDirectory(root);
        var statePath = Path.Combine(root, ".tools", "state"); Directory.CreateDirectory(Path.Combine(statePath, "language"));
        var executable = Environment.ProcessPath!;
        string[] arguments = Path.GetFileNameWithoutExtension(executable) == "dotnet" ? [typeof(LanguageChecks).Assembly.Location, "--fake-lsp"] : ["--fake-lsp"];
        File.WriteAllText(Path.Combine(statePath, "language", "servers.json"), System.Text.Json.JsonSerializer.Serialize(new { typescript = new { command = executable, arguments } }));
        File.WriteAllText(Path.Combine(root, "code.ts"), "😀 broken");
        var disabled = Environment.GetEnvironmentVariable("SHARPRAIL_LSP_DISABLE"); Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", "0");
        var state = new HostStateStore(statePath);
        var view = new SharpRail.UI.Editor.CodeDocumentView(new("code.ts", "😀 broken"), root, new LocalProjectAdapter(new ProjectServices(root, state)),
            () => { }, _ => { }, () => { }, error => throw error);
        var window = new Window { Width = 800, Height = 400, Content = view };
        window.Show();
        try
        {
            var status = view.FindControl<TextBlock>("LanguageStatus")!;
            var popup = view.FindControl<Popup>("QuickFixPopup")!;
            var list = view.FindControl<ListBox>("QuickFixItems")!;
            var message = view.FindControl<TextBlock>("QuickFixStatus")!;
            void Key(Control target, Key key, KeyModifiers modifiers = KeyModifiers.None)
                => target.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, KeyModifiers = modifiers });
            void Settle() { var until = DateTime.UtcNow.AddSeconds(1.5); E2E.E2eWorkspace.Until(() => DateTime.UtcNow > until); }
            void Open(string title)
            {
                Key(view.Editor, Avalonia.Input.Key.Enter, KeyModifiers.Alt);
                E2E.E2eWorkspace.Until(() => popup.IsOpen && list.IsVisible && list.ItemCount == 3);
                list.SelectedIndex = Array.FindIndex(list.ItemsSource!.Cast<object>().Select(row => row.ToString()!).ToArray(), row => row.Contains(title, StringComparison.Ordinal));
                Require(list.SelectedIndex >= 0, title + " was not listed.");
            }
            void Applied(string expected)
            {
                try { E2E.E2eWorkspace.Until(() => view.Editor.Text == expected || popup.IsOpen && message.IsVisible, "the quick fix to apply or explain itself: " + expected); }
                catch (InvalidOperationException)
                {
                    Console.WriteLine(Task.Run(async () => await state.Languages.ExecuteAsync(new(root, Operation: "logs", Server: "typescript"))).GetAwaiter().GetResult().Detail);
                    throw;
                }
                Require(view.Editor.Text == expected, $"The quick fix did not apply (text {view.Editor.Text}); message: {message.Text}");
            }
            string[] Titles() => list.ItemsSource!.Cast<object>().Select(row => row.GetType().GetProperty("Title")!.GetValue(row)!.ToString()!).ToArray();

            E2E.E2eWorkspace.Until(() => status.Text?.Contains("1 problems", StringComparison.Ordinal) == true);
            view.Editor.SelectRange(4, 0);

            // Alt+Enter lists the usable actions, preferred first; disabled actions and commands the server never declared are hidden.
            Key(view.Editor, Avalonia.Input.Key.Enter, KeyModifiers.Alt);
            E2E.E2eWorkspace.Until(() => popup.IsOpen && (list.IsVisible && list.ItemCount == 3 || message.IsVisible), "the quick-fix list or its message");
            Require(list.IsVisible && list.ItemCount == 3, $"The quick-fix list had {list.ItemCount} rows; message: {message.Text}");
            Require(Titles().SequenceEqual(["Fix broken", "Run fixture command", "Resolve me"]), "Quick fixes were listed as: " + string.Join(", ", Titles()));

            // Enter applies the edits of the preferred action as one undo step.
            list.SelectedIndex = 0;
            Key(list, Avalonia.Input.Key.Enter);
            Applied("// 😀 fixed");
            Require(!popup.IsOpen, "Applying a quick fix left the list open.");
            view.Editor.Undo();
            Require(view.Editor.Text == "😀 broken", "One Undo did not revert the quick fix: " + view.Editor.Text);
            E2E.E2eWorkspace.Until(() => status.Text?.Contains("1 problems", StringComparison.Ordinal) == true);

            // An action without an edit is resolved first; a command's workspace/applyEdit supplies the edit of a command-only action.
            view.Editor.SelectRange(4, 0);
            Open("Resolve me");
            Key(list, Avalonia.Input.Key.Enter);
            Applied("/*r*/😀 broken");
            view.Editor.Undo();
            E2E.E2eWorkspace.Until(() => status.Text?.Contains("1 problems", StringComparison.Ordinal) == true);
            view.Editor.SelectRange(4, 0);
            Open("Run fixture command");
            Key(list, Avalonia.Input.Key.Enter);
            Applied("/*cmd*/😀 broken");
            view.Editor.Undo();
            E2E.E2eWorkspace.Until(() => status.Text?.Contains("1 problems", StringComparison.Ordinal) == true);

            // Escape closes the list without changing the text.
            view.Editor.SelectRange(4, 0);
            Open("Fix broken");
            Key(list, Avalonia.Input.Key.Escape);
            Require(!popup.IsOpen && view.Editor.Text == "😀 broken", "Escape did not close the list untouched.");

            // The context menu offers the same action with its registry icon.
            view.Editor.RaiseEvent(new ContextRequestedEventArgs());
            E2E.E2eWorkspace.Until(() => view.Editor.ContextMenu!.IsOpen && view.Editor.ContextMenu.Items.OfType<MenuItem>().Any(item => item.Name == "Language_quickFix"));
            var item = view.Editor.ContextMenu!.Items.OfType<MenuItem>().Single(entry => entry.Name == "Language_quickFix");
            Require(item.Icon is not null && item.Header!.ToString()!.Contains("Alt+Enter", StringComparison.Ordinal), "The context menu's quick-fix item lacked its icon or chord.");
            view.Editor.ContextMenu.Close();

            // Away from the diagnostic the server has nothing: a message, no list.
            view.Editor.SelectRange(0, 0);
            Key(view.Editor, Avalonia.Input.Key.Enter, KeyModifiers.Alt);
            E2E.E2eWorkspace.Until(() => popup.IsOpen && message.IsVisible && message.Text == "No quick fixes");
            Require(!list.IsVisible, "An empty answer showed a list.");
            popup.IsOpen = false;

            // An edit made before the answer arrives makes the answer stale: no list.
            view.Editor.SelectRange(4, 0);
            Key(view.Editor, Avalonia.Input.Key.Enter, KeyModifiers.Alt);
            view.Editor.ReplaceRange(view.Editor.Text.Length, 0, " ");
            Settle();
            Require(!popup.IsOpen, "A quick-fix answer for an older revision was shown.");

            // An edit made while the list is open drops the choice instead of applying it to moved text.
            view.Editor.SelectRange(4, 0);
            Open("Fix broken");
            view.Editor.ReplaceRange(view.Editor.Text.Length, 0, " ");
            var edited = view.Editor.Text;
            Key(list, Avalonia.Input.Key.Enter);
            Settle();
            Require(view.Editor.Text == edited, "A quick fix chosen for an older revision changed the text: " + view.Editor.Text);
            Console.WriteLine("PASS quick fixes: Alt+Enter list, preferred first, hidden disabled and undeclared, apply as one undo step, resolve, command applyEdit, empty and stale answers");
        }
        finally
        {
            window.Close();
            Task.Run(state.StopLanguagesAsync).Wait(TimeSpan.FromSeconds(10));
            Environment.SetEnvironmentVariable("SHARPRAIL_LSP_DISABLE", disabled);
        }
    }
}