using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Panels;

public sealed partial class WorkspacePromptFields : UserControl
{
    private readonly List<(byte[] Data, Bitmap Preview)> images = [];
    private int pendingPastes;
    public bool IsPasting => pendingPastes > 0;
    public event Action? StateChanged;
    public IReadOnlyList<byte[]> Images => images.Select(image => image.Data).ToArray();

    public WorkspacePromptFields()
    {
        AvaloniaXamlLoader.Load(this);
        this.FindControl<TextBox>("WsPrompt")!.AddHandler(KeyDownEvent, (_, e) =>
        {
            var modifier = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
            if (e.Key != Key.V || !e.KeyModifiers.HasFlag(modifier)) return;
            e.Handled = true;
            _ = PasteAsync();
        }, RoutingStrategies.Tunnel);
        this.FindControl<TextBox>("WsPrompt")!.PastingFromClipboard += (_, e) =>
        {
            e.Handled = true;
            _ = PasteAsync();
        };
        DetachedFromVisualTree += (_, _) => { foreach (var image in images) image.Preview.Dispose(); images.Clear(); };
    }

    private async Task PasteAsync()
    {
        pendingPastes++; StateChanged?.Invoke();
        try
        {
            this.FindControl<TextBlock>("WsPromptError")!.IsVisible = false;
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
            if (await clipboard.TryGetBitmapAsync() is { } bitmap)
            {
                using (bitmap)
                {
                    var attachment = await Task.Run(() =>
                    {
                        using var stream = new MemoryStream();
                        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
                        var data = stream.ToArray();
                        stream.Position = 0;
                        return (Data: data, Preview: new Bitmap(stream));
                    });
                    if (TopLevel.GetTopLevel(this) is not null) AddImage(attachment.Data, attachment.Preview);
                    else attachment.Preview.Dispose();
                }
            }
            else if (await clipboard.TryGetTextAsync() is { } text && TopLevel.GetTopLevel(this) is not null)
                this.FindControl<TextBox>("WsPrompt")!.SelectedText = text;
        }
        catch (Exception error)
        {
            var message = this.FindControl<TextBlock>("WsPromptError")!;
            message.Text = error.Message; message.IsVisible = true;
        }
        finally { pendingPastes--; StateChanged?.Invoke(); }
    }

    public void AddImage(byte[] data)
    {
        using var stream = new MemoryStream(data);
        AddImage(data, new Bitmap(stream));
    }

    private void AddImage(byte[] data, Bitmap bitmap)
    {
        if (data.Length > FileLimits.PreviewBytes || images.Count >= 8)
        {
            bitmap.Dispose();
            throw new IOException("Attach at most eight images, each no larger than 8 MB.");
        }
        images.Add((data, bitmap));
        var panel = this.FindControl<WrapPanel>("WsImages")!;
        var card = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        card.Children.Add(new Image { Source = bitmap, Width = 80, Height = 64 });
        card.Children.Add(Ui.Button("Remove", () =>
        {
            images.RemoveAll(image => image.Preview == bitmap); bitmap.Dispose();
            panel.Children.Remove(card); panel.IsVisible = images.Count > 0;
            this.FindControl<TextBox>("WsPrompt")!.Focus();
        }));
        panel.Children.Add(card); panel.IsVisible = true;
    }
}