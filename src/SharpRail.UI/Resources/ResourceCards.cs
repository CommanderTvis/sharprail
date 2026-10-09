using System.Globalization;
using System.Text.RegularExpressions;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

using SharpRail.UI.Rendering;

namespace SharpRail.UI.Resources;

/// <summary>The byte fallback and the Git LFS pointer: cards that describe content the window does not draw.</summary>
internal static partial class ResourceCards
{
    internal sealed record LfsPointer(string Oid, long Size);

    [GeneratedRegex(@"^version https://git-lfs\.github\.com/spec/v1\noid sha256:([0-9a-f]{64})\nsize (\d{1,15})\n\z")]
    private static partial Regex LfsPattern();

    internal static LfsPointer? ParseLfs(string text) => LfsPattern().Match(text) is { Success: true } match
        ? new(match.Groups[1].Value, long.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)) : null;

    internal static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        string[] units = ["KB", "MB", "GB", "TB"];
        var value = bytes / 1024.0; var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return (value >= 10 ? Math.Round(value).ToString(CultureInfo.InvariantCulture) : value.ToString("0.0", CultureInfo.InvariantCulture)) + " " + units[unit];
    }

    internal static string Bytes(long count) => count.ToString("N0", CultureInfo.InvariantCulture) + " bytes";

    internal static Border Card(string name, params Control[] rows)
    {
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.AddRange(rows);
        return new Border
        {
            Name = name,
            Background = Ui.Header,
            BorderBrush = Ui.BorderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(16),
            MaxWidth = 512,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Top,
            Child = panel
        };
    }

    internal static Control Centered(Control card) => new ScrollViewer
    {
        Content = new Border { Padding = new Thickness(24), Child = card, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center }
    };

    internal static Control BinaryView(ResourceView view)
    {
        var name = Path.GetFileName(view.Resource.Path);
        var rows = new List<Control> { Ui.Text(name, Ui.TextBrush, 13) };
        if (view.Content is ResourceContent.Bytes bytes)
        {
            rows.Add(Ui.Text($"{bytes.Mime ?? "binary data"}, {Bytes(bytes.ByteLength)}", Ui.Muted, 12));
            if (bytes.Hash.Length >= 12) rows.Add(Ui.Text("SHA-256 " + bytes.Hash[..12], Ui.Muted, 12));
            var status = Ui.Text("", Ui.Muted, 12);
            status.IsVisible = false;
            Button? save = null;
            save = Ui.Button("Save a copy…", async () =>
            {
                try
                {
                    if (TopLevel.GetTopLevel(save)?.StorageProvider is not { } storage) return;
                    if (await storage.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = name }) is not { } file) return;
                    await using var target = await file.OpenWriteAsync();
                    await target.WriteAsync(await bytes.Load(CancellationToken.None));
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or Grpc.Core.RpcException)
                {
                    status.Text = "Could not save: " + error.Message; status.Foreground = Ui.Danger; status.IsVisible = true;
                }
            });
            save.Name = "BinarySave";
            save.HorizontalAlignment = HorizontalAlignment.Left;
            save.Margin = new Thickness(0, 8, 0, 0);
            rows.Add(save); rows.Add(status);
        }
        else rows.Add(Ui.Text("Unavailable", Ui.Muted, 12));
        return Centered(Card("BinaryResource", [.. rows]));
    }

    internal static Control BinaryDiff(ResourceDiff diff)
    {
        var row = new Grid { Name = "BinaryDiff", ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 16, Margin = new Thickness(24) };
        Ui.Place(row, Side("Original", diff.Original));
        Ui.Place(row, Side("Modified", diff.Modified), 0, 1);
        return new ScrollViewer { Content = row };
    }

    internal static StackPanel Side(string title, ResourceContent content)
    {
        var card = new StackPanel { Spacing = 8, VerticalAlignment = VerticalAlignment.Top, Name = "BinarySide_" + title };
        card.Children.Add(Ui.Text(title, Ui.TextBrush, 13));
        switch (content)
        {
            case ResourceContent.Bytes bytes:
                card.Children.Add(Ui.Text($"{bytes.Mime ?? "binary data"}, {Bytes(bytes.ByteLength)}", Ui.Muted, 12));
                if (bytes.Hash.Length >= 12) card.Children.Add(Ui.Text("SHA-256 " + bytes.Hash[..12], Ui.Muted, 12));
                break;
            case ResourceContent.Text text:
                card.Children.Add(Ui.Text($"{text.Mime ?? "text"}, {Bytes(System.Text.Encoding.UTF8.GetByteCount(text.Value))}", Ui.Muted, 12));
                if (text.Hash.Length >= 12) card.Children.Add(Ui.Text("SHA-256 " + text.Hash[..12], Ui.Muted, 12));
                break;
            default:
                card.Children.Add(Ui.Text("No file", Ui.Muted, 12));
                break;
        }
        return card;
    }

    internal static Border Lfs(string name, string? label, ResourceContent content)
    {
        var pointer = content is ResourceContent.Text text ? ParseLfs(text.Value) : null;
        var rows = new List<Control>();
        if (label is not null) rows.Add(Ui.Text(label, Ui.Muted, 12));
        rows.Add(Ui.Text("Stored in Git LFS", Ui.TextBrush, 13));
        var hint = Ui.Text("This checkout holds the pointer, not the content. Run git lfs pull to fetch it.", Ui.Muted, 12);
        hint.TextWrapping = Avalonia.Media.TextWrapping.Wrap;
        rows.Add(hint);
        var size = Ui.Text("Size  " + (pointer is null ? "Unreadable pointer" : FormatSize(pointer.Size)), Ui.Muted, 12);
        size.Name = name + "Size";
        rows.Add(size);
        var oid = Ui.Text("Object  " + (pointer?.Oid[..12] ?? "—"), Ui.Muted, 12);
        oid.FontFamily = Ui.CodeFont;
        if (pointer is not null) ToolTip.SetTip(oid, pointer.Oid);
        rows.Add(oid);
        return Card(name, [.. rows]);
    }

    internal static Control LfsDiff(ResourceDiff diff)
    {
        var row = new Grid { Name = "LfsDiff", ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 12, Margin = new Thickness(24) };
        Ui.Place(row, diff.Original is ResourceContent.Absent ? Ui.Text("Old: no pointer", Ui.Muted, 12) : Lfs("LfsPointerOriginal", "Old", diff.Original));
        Ui.Place(row, diff.Modified is ResourceContent.Absent ? Ui.Text("New: no pointer", Ui.Muted, 12) : Lfs("LfsPointerModified", "New", diff.Modified), 0, 1);
        return new ScrollViewer { Content = row };
    }
}