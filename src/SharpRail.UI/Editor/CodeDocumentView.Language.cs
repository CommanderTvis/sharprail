using System.Text;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Controls.Documents;
using Avalonia.VisualTree;
using Avalonia.Threading;

using SharpRail.Host.Abstractions;
using SharpRail.Plugins.UI.Kit.Editor;
using SharpRail.Scintilla;

namespace SharpRail.UI.Editor;

internal sealed partial class CodeDocumentView
{
    private sealed record LanguagePreviewRow(LanguageItem Item, string Location, string Description, string Code, byte[] Styles,
        int MatchStart, int MatchLength, bool StartsFile = false, int FileCount = 0)
    {
        public bool HasDescription => Description.Length > 0;
        public string FileName => Path.GetFileName(Item.Path);
        public string Directory => Path.GetDirectoryName(Item.Path) ?? "";
        public int LineNumber => Item.Line + 1;
        public override string ToString() => $"{Location} {Description} {Code}";
    }

    private readonly EditorFrame frame;
    private bool firstColorsPending = true;

    private async Task RevealCodeColorsAsync(bool semantic = false)
    {
        if (!semantic)
        {
            frame.DeferSyntaxColors = false;
            await frame.HighlightingReady;
        }
        if (disposed) return;
        firstColorsPending = false; frame.Opacity = 1;
    }
    private readonly string languageDocument = Guid.NewGuid().ToString("N");
    private readonly DispatcherTimer languageTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private CancellationTokenSource? languageRequest;
    private long languageVersion;
    private bool languageBusy;
    private string scopedOperation = "";
    private IReadOnlyList<LanguagePreviewRow> languageRows = [];
    private bool updatingLanguageQuery;
    internal Func<LanguageItem, Task>? NavigateLanguage { get; set; }
    internal string LanguageSourceServer { get; set; } = "";

    private void WireLanguage()
    {
        languageTimer.Tick += async (_, _) => await InspectLanguageAsync();
        AttachedToVisualTree += (_, _) => { languageTimer.Start(); _ = InspectLanguageAsync(); };
        DetachedFromVisualTree += (_, _) => { languageTimer.Stop(); languageRequest?.Cancel(); this.FindControl<Popup>("LanguagePopup")!.IsOpen = false; };
        Editor.TextChanged += (_, _) =>
        {
            languageVersion++; languageRequest?.Cancel();
            Editor.SetDiagnostics([], Ui.Danger.Color, Ui.Warning.Color, Ui.Accent.Color);
            this.FindControl<TextBlock>("LanguageStatus")!.Text = "Analysis pending…";
        };
        this.FindControl<ComboBox>("LanguageScope")!.SelectionChanged += async (_, _) =>
        {
            if (this.FindControl<Popup>("LanguagePopup")!.IsOpen && scopedOperation.Length > 0) await RunLanguageAsync(scopedOperation, wholeWorkspace: scopedOperation == "workspaceSymbols" && this.FindControl<ComboBox>("LanguageScope")!.SelectedIndex != 0);
        };
        this.FindControl<Button>("LanguageHide")!.Click += (_, _) => CloseLanguagePopup();
        this.FindControl<Popup>("LanguagePopup")!.Closed += (_, _) => languageRequest?.Cancel();
        var query = this.FindControl<TextBox>("LanguageQuery")!;
        query.TextChanged += (_, _) =>
        {
            if (!updatingLanguageQuery && scopedOperation != "workspaceSymbols") FilterLanguageRows();
        };
        query.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                if (scopedOperation == "workspaceSymbols") await RunLanguageAsync("workspaceSymbols", wholeWorkspace: this.FindControl<ComboBox>("LanguageScope")!.SelectedIndex != 0);
                else await NavigateSelectedLanguageAsync();
            }
            else if (e.Key == Key.Down)
            {
                e.Handled = true;
                this.FindControl<ListBox>("LanguageItems")!.Focus();
            }
        };
        var results = this.FindControl<Border>("LanguageResults")!;
        results.AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.Escape) { e.Handled = true; CloseLanguagePopup(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var list = this.FindControl<ListBox>("LanguageItems")!;
        list.SelectionChanged += (_, _) => this.FindControl<TextBlock>("LanguageSelection")!.Text =
            list.SelectedItem is LanguagePreviewRow row ? row.Location : "";
        list.DoubleTapped += async (_, _) => await NavigateSelectedLanguageAsync();
        list.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; await NavigateSelectedLanguageAsync(); }
        };
        list.TextInput += (_, e) =>
        {
            if (scopedOperation == "workspaceSymbols" || string.IsNullOrEmpty(e.Text)) return;
            query.IsVisible = true;
            query.Text += e.Text; query.CaretIndex = query.Text.Length; query.Focus(); e.Handled = true;
        };
        Editor.ContextMenu = new ContextMenu();
        AddLanguageMenu(Editor.ContextMenu);
        Editor.AddHandler(KeyDownEvent, async (_, e) =>
        {
            var primary = CommandKeys.Modifier;
            string? operation = e.Key switch
            {
                Key.F12 => e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? "references" : "definition",
                Key.B when e.KeyModifiers == primary => "definition",
                Key.F7 when e.KeyModifiers == KeyModifiers.Alt => "references",
                _ => null
            };
            if (operation is null) return;
            e.Handled = true;
            await RunLanguageAsync(operation);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        frame.ThemeApplied += () => { foreach (var preview in this.FindControl<ListBox>("LanguageItems")!.GetVisualDescendants().OfType<TextBlock>().Where(control => control.Name == "LanguagePreview")) ColorPreview(preview); _ = InspectLanguageAsync(); };
        this.FindControl<Border>("LanguageResults")!.BorderBrush = Ui.ControlBorder;
        this.FindControl<TextBlock>("LanguageStatus")!.Foreground = Ui.Muted;
    }

    private static readonly (string Label, string Operation)[] NavigationOperations =
        [("Go to Declaration (F12)", "definition"), ("Go to Type Declaration", "typeDefinition"), ("Go to Implementation", "implementation"), ("Find Usages (Alt+F7)", "references")];

    internal void AddLanguageMenu(ContextMenu menu)
    {
        var generation = 0;
        menu.Closed += (_, _) => generation++;
        menu.Opening += async (_, e) =>
        {
            var current = ++generation;
            foreach (var item in menu.Items.OfType<Control>().Where(item => Equals(item.Tag, "lsp")).ToArray()) menu.Items.Remove(item);
            e.Cancel = false;
            try
            {
                var result = await Task.Run(async () => await host.LanguageAsync(new(workspace, LanguageSourceServer.Length > 0 ? "" : path, "status", Server: LanguageSourceServer)));
                if (current != generation || disposed || !IsEffectivelyVisible || result.Server.Length == 0 || result.State is not ("ready" or "initializing")) return;
            }
            catch { return; }
            menu.Items.Add(new Separator { Tag = "lsp" });
            foreach (var (label, operation) in NavigationOperations)
            {
                var item = Ui.Menu(label, () => _ = RunLanguageAsync(operation)); item.Tag = "lsp"; menu.Items.Add(item);
            }
            foreach (var (label, operation) in new[] { ("Document symbols", "symbols"), ("Workspace Problems", "diagnostics"), ("Language server status", "status"), ("Start / Retry language server", "retry"), ("Stop language server", "stop") })
            {
                var item = Ui.Menu(label, () => _ = RunLanguageAsync(operation, wholeWorkspace: operation == "diagnostics"));
                item.Name = "Language_" + operation; item.Tag = "lsp"; menu.Items.Add(item);
            }
        };
    }

    private async Task InspectLanguageAsync()
    {
        if (disposed || languageBusy || languageRequest is not null || !IsEffectivelyVisible || this.FindControl<Popup>("LanguagePopup")!.IsOpen) return;
        languageBusy = true;
        try
        {
            if (firstColorsPending && LanguageSourceServer.Length == 0 && Editor.Text.Length <= 1_048_576)
            {
                using var initial = new CancellationTokenSource();
                languageRequest = initial;
                try
                {
                    var mode = await Task.Run(async () => await host.LanguageAsync(new(workspace, path, "status"), initial.Token), initial.Token);
                    if (disposed || initial.IsCancellationRequested) return;
                    await RevealCodeColorsAsync(mode.SemanticColoring || mode.State is "installing" or "initializing");
                    if (initial.IsCancellationRequested) return;
                }
                finally { if (ReferenceEquals(languageRequest, initial)) languageRequest = null; }
            }
            if (!disposed) await RunLanguageAsync("inspect");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (!disposed) { report(error); await RevealCodeColorsAsync(); } }
        finally { languageBusy = false; }
    }

    private async Task RunLanguageAsync(string operation, bool wholeWorkspace = false)
    {
        if (disposed) return;
        languageRequest?.Cancel();
        if (operation != "inspect" && firstColorsPending) await RevealCodeColorsAsync(semantic: true);
        var text = Editor.Text;
        if (!wholeWorkspace && text.Length > 1_048_576)
        {
            this.FindControl<TextBlock>("LanguageStatus")!.Text = "Language analysis is limited to 1 Mi characters per document.";
            await RevealCodeColorsAsync();
            return;
        }
        if (LanguageSourceServer.Length > 0 && operation == "inspect")
        {
            this.FindControl<TextBlock>("LanguageStatus")!.Text = "Read-only dependency source";
            await RevealCodeColorsAsync();
            return;
        }
        var cancellation = new CancellationTokenSource(); languageRequest = cancellation;
        var token = cancellation.Token;
        var version = languageVersion;
        var selection = Editor.Selection;
        var request = new LanguageRequest(workspace, wholeWorkspace ? "" : path, operation,
            selection.StartLine - 1, selection.StartColumn - 1, wholeWorkspace || LanguageSourceServer.Length > 0 ? null : text, languageDocument, version,
            operation == "workspaceSymbols" ? this.FindControl<TextBox>("LanguageQuery")!.Text ?? "" : "", Editor.IsModified, Server: LanguageSourceServer,
            Scope: operation is "references" or "implementation" or "workspaceSymbols"
                ? this.FindControl<ComboBox>("LanguageScope")!.SelectedIndex switch { 0 => "file", 2 => "dependencies", _ => "workspace" } : "");
        try
        {
            if (operation is not ("inspect" or "definition" or "typeDefinition" or "implementation"))
            {
                scopedOperation = operation is "references" or "workspaceSymbols" ? operation : "";
                this.FindControl<ComboBox>("LanguageScope")!.IsVisible = scopedOperation.Length > 0;
                languageRows = []; FilterLanguageRows();
                this.FindControl<TextBlock>("LanguageHeading")!.Text = LanguageTitle(operation);
                this.FindControl<TextBlock>("LanguageStatus")!.IsVisible = true;
                this.FindControl<TextBlock>("LanguageStatus")!.Text = "Searching…";
                this.FindControl<TextBlock>("LanguageEmpty")!.Text = "Searching…";
                ShowLanguagePopup();
            }
            var result = await host.LanguageAsync(request, token);
            while (operation != "inspect" && result.State is "installing" or "initializing")
            {
                if (disposed || token.IsCancellationRequested) return;
                this.FindControl<TextBlock>("LanguageStatus")!.Text = result.Detail;
                await Task.Delay(500, token);
                result = await host.LanguageAsync(request with { Operation = operation == "retry" ? "status" : operation }, token);
            }
            if (disposed || token.IsCancellationRequested || version != languageVersion) return;
            this.FindControl<TextBlock>("LanguageStatus")!.Text = result.Server + " · " + result.Detail +
                (operation is "inspect" or "diagnostics" ? $" · {result.Diagnostics.Count} problems ({result.Freshness})" : "");
            ToolTip.SetTip(this.FindControl<TextBlock>("LanguageStatus")!, result.Detail + "\nCoverage: " + result.Coverage);

            if (operation == "inspect" && result.State is "installing" or "initializing")
                await RevealCodeColorsAsync(semantic: true);
            if (result.State != "ready")
            {
                if (result.State is not ("installing" or "initializing"))
                {
                    if (operation == "inspect") await RevealCodeColorsAsync();
                    await frame.ApplySemanticTokensAsync(text, []);
                }
                if (disposed || token.IsCancellationRequested || version != languageVersion) return;
                Editor.SetDiagnostics([], Ui.Danger.Color, Ui.Warning.Color, Ui.Accent.Color);
            }
            if (operation == "inspect" && result.State == "ready")
            {
                var semantic = result.SemanticColoring || result.Tokens.Count > 0;
                frame.DeferSyntaxColors = semantic;
                await RevealCodeColorsAsync(semantic);
                await frame.HighlightingReady;
                await frame.ApplySemanticTokensAsync(text, result.Tokens.Select(item => new SemanticToken(item.Line, item.Column, item.EndColumn - item.Column, item.Kind) { Italic = item.Kind is "enumMember" or "extensionMethod" or "extensionFunction" || item.Modifiers.Split(' ').Any(modifier => modifier is "static" or "extension") && item.Kind is "method" or "function" or "property" or "variable" }).ToArray());
                if (disposed || token.IsCancellationRequested || version != languageVersion) return;
                var underlines = await Task.Run(() => Diagnostics(text, result.Diagnostics), token);
                if (disposed || token.IsCancellationRequested || version != languageVersion) return;
                Editor.SetDiagnostics(underlines, Ui.Danger.Color, Ui.Warning.Color, Ui.Accent.Color);
            }
            else if (operation != "inspect" && (operation is "diagnostics" or "symbols" or "workspaceSymbols" or "references" or "status" or "stop" or "retry" || result.Locations.Count != 1))
            {
                var found = operation == "diagnostics" ? result.Diagnostics : result.Locations;
                var items = found.OrderBy(item => item.Path, StringComparer.Ordinal).ThenBy(item => item.Line).ThenBy(item => item.Column).Take(200).ToArray();
                var labels = await Task.Run(async () =>
                {
                    var files = new Dictionary<string, string[]>();
                    var labels = new List<LanguagePreviewRow>();
                    var highlighter = new TextMateHighlighter();
                    foreach (var item in items)
                    {
                        token.ThrowIfCancellationRequested();
                        if (!files.TryGetValue(item.Path, out var lines))
                        {
                            try { lines = (item.Path == path ? text : (await host.ReadFileAsync(item.Path, token)).Text).Split('\n'); }
                            catch (OperationCanceledException) { throw; }
                            catch { lines = []; }
                            files[item.Path] = lines;
                        }
                        var sourceLine = item.Line >= 0 && item.Line < lines.Length ? lines[item.Line].TrimEnd('\r') : "";
                        var indent = sourceLine.Length - sourceLine.TrimStart().Length;
                        var preview = sourceLine.Trim();
                        if (preview.Length > 200) preview = preview[..(char.IsHighSurrogate(preview[199]) ? 199 : 200)] + "…";
                        labels.Add(new(item, $"{item.Path}:{item.Line + 1}:{item.Column + 1}",
                            operation == "diagnostics" || preview.Length == 0 ? item.Text : "", preview,
                            highlighter.Highlight(preview, item.Path, null, token), Math.Max(0, item.Column - indent),
                            item.EndLine == item.Line ? Math.Max(0, item.EndColumn - item.Column) : 0));
                    }
                    return labels.ToArray();
                }, token);
                if (disposed || token.IsCancellationRequested || version != languageVersion) return;
                languageRows = labels;
                scopedOperation = operation is "references" or "implementation" or "workspaceSymbols" ? operation : "";
                this.FindControl<ComboBox>("LanguageScope")!.IsVisible = scopedOperation.Length > 0;
                var query = this.FindControl<TextBox>("LanguageQuery")!;
                updatingLanguageQuery = true;
                if (operation != "workspaceSymbols") query.Text = "";
                query.PlaceholderText = operation == "workspaceSymbols" ? "Search workspace symbols — Enter to search" : "Type to filter results";
                updatingLanguageQuery = false;
                FilterLanguageRows();
                var total = operation == "diagnostics" ? found.Count : Math.Max(found.Count, result.Total);
                this.FindControl<TextBlock>("LanguageHeading")!.Text = total == 0 && result.State == "ready" ? operation switch
                {
                    "definition" => "No declaration found",
                    "typeDefinition" => "No type declaration found",
                    "implementation" => "No implementation found",
                    "references" => "No usages found",
                    "diagnostics" => "No problems reported",
                    "symbols" or "workspaceSymbols" => "No symbols found",
                    _ => LanguageTitle(operation)
                } : LanguageTitle(operation) + $" · {total}" + (total > items.Length ? $" (showing {items.Length})" : "");
                this.FindControl<TextBlock>("LanguageStatus")!.IsVisible = total > 0 || result.State != "ready" || result.Freshness != "current" || operation is "status" or "retry" or "stop";
                ToolTip.SetTip(this.FindControl<TextBlock>("LanguageHeading")!, result.Coverage);
                ShowLanguagePopup();
                query.IsVisible = scopedOperation == "workspaceSymbols" || languageRows.Count > 6;
            }
            else if (result.Locations.Count == 1 && NavigateLanguage is { } navigate) await navigate(result.Locations[0]);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            if (!disposed && !token.IsCancellationRequested)
            {
                this.FindControl<TextBlock>("LanguageStatus")!.IsVisible = true;
                this.FindControl<TextBlock>("LanguageStatus")!.Text = error.Message;
                if (operation == "inspect") await RevealCodeColorsAsync();
                else ShowLanguagePopup();
            }
        }
        finally
        {
            if (operation == "inspect" && !token.IsCancellationRequested && firstColorsPending) await RevealCodeColorsAsync();
            if (ReferenceEquals(languageRequest, cancellation)) languageRequest = null;
            cancellation.Dispose();
        }
    }

    private void PreviewLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is TextBlock preview) ColorPreview(preview);
    }

    private void PreviewDataChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock preview) ColorPreview(preview);
    }

    private static void ColorPreview(TextBlock preview)
    {
        if (preview.DataContext is not LanguagePreviewRow row) return;
        preview.Inlines!.Clear();
        var bytes = Encoding.UTF8.GetBytes(row.Code);
        for (var start = 0; start < bytes.Length;)
        {
            var role = start < row.Styles.Length ? row.Styles[start] : 0;
            var matchStart = Encoding.UTF8.GetByteCount(row.Code.AsSpan(0, Math.Min(row.Code.Length, row.MatchStart)));
            var matchEnd = Encoding.UTF8.GetByteCount(row.Code.AsSpan(0, Math.Min(row.Code.Length, row.MatchStart + row.MatchLength)));
            var matched = start >= matchStart && start < matchEnd;
            var end = start + 1;
            while (end < bytes.Length && end < row.Styles.Length && row.Styles[end] == role && (end >= matchStart && end < matchEnd) == matched) end++;
            var roleName = TextMateHighlighter.Roles[Math.Min(role, TextMateHighlighter.Roles.Length - 1)];
            preview.Inlines.Add(new Run(Encoding.UTF8.GetString(bytes, start, end - start)) { Foreground = Ui.SyntaxBrush(roleName), FontWeight = matched ? Avalonia.Media.FontWeight.Bold : Avalonia.Media.FontWeight.Normal });
            start = end;
        }
    }

    private void ShowLanguagePopup()
    {
        var popup = this.FindControl<Popup>("LanguagePopup")!;
        popup.PlacementTarget = Editor;
        popup.PlacementRect = Editor.CaretBounds;
        var results = this.FindControl<Border>("LanguageResults")!;
        results.Width = Math.Min(languageRows.Count == 0 ? 360 : 620, Math.Max(240, TopLevel.GetTopLevel(this)?.ClientSize.Width - 32 ?? 620));
        results.MaxHeight = Math.Min(360, Math.Max(120, TopLevel.GetTopLevel(this)?.ClientSize.Height - 32 ?? 360));
        popup.IsOpen = true;
        var list = this.FindControl<ListBox>("LanguageItems")!;
        if (list.ItemCount > 0) list.SelectedIndex = 0;
        list.Focus();
    }

    private static string LanguageTitle(string operation) => operation switch
    {
        "references" => "Usages",
        "definition" => "Choose Declaration",
        "typeDefinition" => "Choose Type Declaration",
        "implementation" => "Choose Implementation",
        "diagnostics" => "Problems",
        "symbols" => "File Structure",
        "workspaceSymbols" => "Workspace Symbols",
        _ => "Language Server"
    };

    private void CloseLanguagePopup()
    {
        languageRequest?.Cancel();
        this.FindControl<Popup>("LanguagePopup")!.IsOpen = false;
        Editor.Focus();
    }

    private async Task NavigateSelectedLanguageAsync()
    {
        if (this.FindControl<ListBox>("LanguageItems")!.SelectedItem is not LanguagePreviewRow row || NavigateLanguage is not { } navigate) return;
        CloseLanguagePopup();
        await navigate(row.Item);
    }

    private void FilterLanguageRows()
    {
        var list = this.FindControl<ListBox>("LanguageItems")!;
        var selected = (list.SelectedItem as LanguagePreviewRow)?.Item;
        var query = scopedOperation == "workspaceSymbols" ? "" : this.FindControl<TextBox>("LanguageQuery")!.Text ?? "";
        var rows = languageRows.Where(row => row.ToString().Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var counts = rows.GroupBy(row => row.Item.Path, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        string? previous = null;
        var grouped = rows.Select(row =>
        {
            var first = row.Item.Path != previous; previous = row.Item.Path;
            return row with { StartsFile = first, FileCount = counts[row.Item.Path] };
        }).ToArray();
        list.ItemsSource = grouped;
        list.SelectedIndex = Array.FindIndex(grouped, row => row.Item == selected);
        if (list.SelectedIndex < 0 && grouped.Length > 0) list.SelectedIndex = 0;
        list.IsVisible = grouped.Length > 0;
        this.FindControl<Border>("LanguageSelectionFooter")!.IsVisible = grouped.Length > 0;
        this.FindControl<TextBlock>("LanguageKeys")!.IsVisible = grouped.Length > 0;
        this.FindControl<TextBlock>("LanguageEmpty")!.IsVisible = grouped.Length == 0 && languageRows.Count > 0;
        this.FindControl<TextBlock>("LanguageEmpty")!.Text = languageRows.Count == 0 ? "No results in this scope" : "No matching results";
    }

    private static ScintillaDiagnostic[] Diagnostics(string text, IReadOnlyList<LanguageItem> items)
    {
        var starts = new List<int> { 0 };
        var bytes = new int[text.Length + 1];
        for (var index = 0; index < text.Length;)
        {
            var count = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
            if (count == 2) bytes[index + 1] = bytes[index];
            bytes[index + count] = bytes[index] + Encoding.UTF8.GetByteCount(text.AsSpan(index, count));
            if (text[index] == '\n') starts.Add(index + 1);
            index += count;
        }
        var result = new List<ScintillaDiagnostic>();
        foreach (var item in items)
        {
            var start = Offset(item.Line, item.Column); var end = Offset(item.EndLine, item.EndColumn);
            if (start >= 0 && end >= start)
            {
                var severity = int.TryParse(item.Kind, out var value) ? value : 1;
                var label = severity switch { 2 => "Warning", 3 => "Information", 4 => "Hint", _ => "Error" };
                var source = item.Source.Length > 0 ? " · " + item.Source : "";
                result.Add(new(bytes[start], bytes[end] - bytes[start], severity, label + source + "\n" + item.Text));
            }
        }
        return result.ToArray();
        int Offset(int line, int column)
        {
            if (line < 0 || column < 0 || line >= starts.Count) return -1;
            var end = line + 1 < starts.Count ? starts[line + 1] - 1 : text.Length;
            return column <= end - starts[line] ? starts[line] + column : -1;
        }
    }

    private void StopLanguage()
    {
        languageTimer.Stop(); languageRequest?.Cancel(); this.FindControl<Popup>("LanguagePopup")!.IsOpen = false;
        if (LanguageSourceServer.Length > 0) return;
        _ = Task.Run(async () =>
        {
            try { await host.LanguageAsync(new(workspace, path, "close", DocumentId: languageDocument)); }
            catch (Exception) { }
        });
    }
}