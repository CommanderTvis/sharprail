using System.Globalization;
using System.Text.Json;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Resources;

/// <summary>An output is text, or one raster picture; markup outputs are named, never drawn.</summary>
internal sealed record NotebookOutput(string Text, byte[]? Picture, bool IsError);

internal sealed record NotebookCell(string Id, string Kind, string Source, string? ExecutionCount, IReadOnlyList<NotebookOutput> Outputs);

internal sealed record Notebook(string Language, IReadOnlyList<NotebookCell> Cells);

/// <summary>Jupyter notebooks (nbformat 4) read as cells; nothing in them is executed or rendered as markup.</summary>
internal static class NotebookModel
{
    private const int MaxPictureBytes = 8 * 1024 * 1024;
    private static readonly string[] Pictures = ["image/png", "image/jpeg", "image/gif"];

    /// <summary>The notebook, or null with the reason when the text is not one.</summary>
    internal static Notebook? Parse(string text, out string? error)
    {
        error = null;
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("cells", out var cells) || cells.ValueKind != JsonValueKind.Array)
            { error = "This file has no notebook cells."; return null; }
            var language = root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
                ? Name(metadata, "language_info") ?? Name(metadata, "kernelspec") ?? "" : "";
            return new(language, [.. cells.EnumerateArray().Where(cell => cell.ValueKind == JsonValueKind.Object).Select(Cell)]);
        }
        catch (JsonException failure) { error = "Invalid notebook JSON — " + failure.Message; return null; }
    }

    private static string? Name(JsonElement metadata, string member) =>
        metadata.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.Object && value.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
            ? name.GetString() : null;

    // nbformat stores multi-line text as a string or as a list of lines that keep their endings.
    private static string Lines(JsonElement owner, string member) => !owner.TryGetProperty(member, out var value) ? ""
        : value.ValueKind == JsonValueKind.String ? value.GetString()!
        : value.ValueKind == JsonValueKind.Array ? string.Concat(value.EnumerateArray().Where(line => line.ValueKind == JsonValueKind.String).Select(line => line.GetString()))
        : "";

    private static NotebookCell Cell(JsonElement cell, int index)
    {
        var id = cell.TryGetProperty("id", out var given) && given.ValueKind == JsonValueKind.String ? given.GetString()! : "index:" + index.ToString(CultureInfo.InvariantCulture);
        var kind = cell.TryGetProperty("cell_type", out var type) && type.ValueKind == JsonValueKind.String ? type.GetString()! : "raw";
        var count = cell.TryGetProperty("execution_count", out var executed) && executed.ValueKind == JsonValueKind.Number ? executed.GetRawText() : null;
        var outputs = cell.TryGetProperty("outputs", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Where(output => output.ValueKind == JsonValueKind.Object).Select(Output).ToArray() : [];
        return new(id, kind, Lines(cell, "source"), count, outputs);
    }

    private static NotebookOutput Output(JsonElement output)
    {
        var type = output.TryGetProperty("output_type", out var named) && named.ValueKind == JsonValueKind.String ? named.GetString() : "";
        if (type == "stream") return new(Lines(output, "text"), null, output.TryGetProperty("name", out var stream) && stream.ValueKind == JsonValueKind.String && stream.GetString() == "stderr");
        if (type == "error")
        {
            string Field(string member) => output.TryGetProperty(member, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
            return new(Field("ename") + ": " + Field("evalue"), null, true);
        }
        if (!output.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return new("", null, false);
        foreach (var media in Pictures)
            if (data.TryGetProperty(media, out var encoded) && Lines(data, media) is { Length: > 0 and <= MaxPictureBytes * 4 / 3 } base64)
                try { return new(Lines(data, "text/plain"), Convert.FromBase64String(base64.ReplaceLineEndings("")), false); }
                catch (FormatException) { }
        if (data.TryGetProperty("text/plain", out _)) return new(Lines(data, "text/plain"), null, false);
        return new("[" + string.Join(", ", data.EnumerateObject().Select(member => member.Name)) + " output not shown]", null, false);
    }
}

/// <summary>A notebook as a column of cells, and two notebooks as the cells that were added, removed or edited.</summary>
internal sealed class NotebookView : ScrollViewer, IDisposable
{
    private readonly List<Bitmap> bitmaps = [];

    private NotebookView(string name) => Name = name;

    internal static Control View(ResourceView view)
    {
        var notebook = NotebookModel.Parse((view.Content as ResourceContent.Text)?.Value ?? "", out var error);
        var result = new NotebookView("NotebookResource");
        var panel = result.Column();
        if (notebook is null) { panel.Children.Add(Notice("NotebookInvalid", error!, Ui.Danger)); return result; }
        if (notebook.Language.Length > 0) panel.Children.Add(Ui.Text(notebook.Language, Ui.Hint, 12));
        for (var index = 0; index < notebook.Cells.Count; index++) panel.Children.Add(result.CellCard(notebook.Cells[index], index + 1, null, null));
        if (notebook.Cells.Count == 0) panel.Children.Add(Notice("NotebookEmpty", "This notebook has no cells.", Ui.Hint));
        return result;
    }

    internal static Task<Control?> DiffAsync(ResourceDiff diff, CancellationToken token)
    {
        if (diff.Original is ResourceContent.Bytes || diff.Modified is ResourceContent.Bytes) return Task.FromResult<Control?>(null);
        string? originalError = null, modifiedError = null;
        var before = diff.Original is ResourceContent.Text original ? NotebookModel.Parse(original.Value, out originalError) : new("", []);
        var after = diff.Modified is ResourceContent.Text modified ? NotebookModel.Parse(modified.Value, out modifiedError) : new("", []);
        var result = new NotebookView("NotebookDiff");
        var panel = result.Column();
        if (before is null || after is null)
        {
            panel.Children.Add(Notice("NotebookInvalid", (before is null ? "Original side: " + originalError : "Modified side: " + modifiedError) + " Use the Source view.", Ui.Danger));
            return Task.FromResult<Control?>(result);
        }
        // Cells are the same cell by id when both sides have ids, otherwise by their source.
        static string Identity(NotebookCell cell) => cell.Id.StartsWith("index:", StringComparison.Ordinal) ? cell.Kind + "\n" + cell.Source : "id:" + cell.Id;
        List<NotebookCell> removed = [], added = [];
        int left = 0, right = 0, unchanged = 0, shown = 0;

        void Hidden()
        {
            if (unchanged > 0) panel.Children.Add(Ui.Text($"… {unchanged} unchanged {(unchanged == 1 ? "cell" : "cells")}", Ui.Hint, 12));
            unchanged = 0;
        }

        void Flush()
        {
            if (removed.Count + added.Count > 0) Hidden();
            if (removed.Count == added.Count)
                for (var index = 0; index < removed.Count; index++) { panel.Children.Add(result.CellCard(added[index], right - added.Count + index + 1, "changed", removed[index])); shown++; }
            else
            {
                foreach (var cell in removed) { panel.Children.Add(result.CellCard(cell, null, "removed", null)); shown++; }
                for (var index = 0; index < added.Count; index++) { panel.Children.Add(result.CellCard(added[index], right - added.Count + index + 1, "added", null)); shown++; }
            }
            removed.Clear(); added.Clear();
        }

        foreach (var (side, _) in MarkdownDiff.Sequence([.. before.Cells.Select(Identity)], [.. after.Cells.Select(Identity)], token))
        {
            if (side < 0) removed.Add(before.Cells[left++]);
            else if (side > 0) added.Add(after.Cells[right++]);
            else
            {
                Flush();
                var (old, current) = (before.Cells[left++], after.Cells[right++]);
                if (old.Source == current.Source && old.Kind == current.Kind && Same(old.Outputs, current.Outputs)) { unchanged++; continue; }
                Hidden();
                panel.Children.Add(result.CellCard(current, right, "changed", old)); shown++;
            }
        }
        Flush(); Hidden();
        if (shown == 0) panel.Children.Insert(0, Notice("NotebookDiffEmpty", "No cell changed: the two sides differ only in metadata or formatting.", Ui.Muted));
        return Task.FromResult<Control?>(result);
    }

    private static bool Same(IReadOnlyList<NotebookOutput> left, IReadOnlyList<NotebookOutput> right) => left.Count == right.Count &&
        left.Zip(right).All(pair => pair.First.Text == pair.Second.Text && pair.First.IsError == pair.Second.IsError &&
            (pair.First.Picture ?? []).AsSpan().SequenceEqual(pair.Second.Picture ?? []));

    private StackPanel Column()
    {
        var panel = new StackPanel { Spacing = 10, Margin = new Thickness(20, 12), MaxWidth = 960, HorizontalAlignment = HorizontalAlignment.Left };
        Content = panel;
        return panel;
    }

    private static TextBlock Notice(string name, string message, IBrush color)
    {
        var text = Ui.Text(message, color, 12);
        text.Name = name; text.TextWrapping = TextWrapping.Wrap;
        return text;
    }

    private static SelectableTextBlock Source(string text, IBrush color, bool code) => new()
    {
        Text = text,
        Foreground = color,
        FontFamily = code ? Ui.CodeFont : Ui.InterfaceFont,
        FontSize = code ? 12 : 13,
        TextWrapping = TextWrapping.Wrap
    };

    private Border CellCard(NotebookCell cell, int? number, string? change, NotebookCell? previous)
    {
        var rows = new StackPanel { Spacing = 6 };
        var label = cell.Kind + (number is { } ordinal ? " " + ordinal.ToString(CultureInfo.InvariantCulture) : "") + (cell.ExecutionCount is { } count ? $"  [{count}]" : "") +
            (change is null ? "" : " — " + change);
        rows.Children.Add(Ui.Text(label, change switch { "added" => Ui.Success, "removed" => Ui.Danger, "changed" => Ui.Warning, _ => Ui.Hint }, 11));
        var code = cell.Kind == "code";
        if (previous is not null && previous.Source != cell.Source)
        {
            var old = Source(previous.Source, Ui.Danger, previous.Kind == "code");
            old.TextDecorations = TextDecorations.Strikethrough;
            rows.Children.Add(old);
            rows.Children.Add(Source(cell.Source, Ui.Success, code));
        }
        else rows.Children.Add(Source(cell.Source, change == "removed" ? Ui.Danger : Ui.TextBrush, code));
        if (previous is not null && !Same(previous.Outputs, cell.Outputs)) rows.Children.Add(Ui.Text("Outputs changed", Ui.Warning, 11));
        foreach (var output in cell.Outputs)
        {
            if (output.Text.Length > 0)
                rows.Children.Add(new Border { Background = Ui.Elevated, CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 4), Child = Source(output.Text.TrimEnd('\n'), output.IsError ? Ui.Danger : Ui.Muted, true) });
            if (output.Picture is null) continue;
            try
            {
                var bitmap = new Bitmap(new MemoryStream(output.Picture));
                bitmaps.Add(bitmap);
                rows.Children.Add(new Image { Source = bitmap, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, MaxHeight = 480, HorizontalAlignment = HorizontalAlignment.Left });
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or NotSupportedException)
            { rows.Children.Add(Ui.Text("[picture output could not be decoded]", Ui.Hint, 12)); }
        }
        var card = new Border
        {
            Background = change switch { "added" => Ui.SuccessWash, "removed" => Ui.DangerWash, _ => code ? Ui.Header : Brushes.Transparent },
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(12, 8),
            Child = rows
        };
        card.Classes.Add("notebook-cell");
        if (change is not null) card.Classes.Add("notebook-" + change);
        return card;
    }

    public void Dispose()
    {
        Content = null;
        foreach (var bitmap in bitmaps) bitmap.Dispose();
        bitmaps.Clear();
    }
}