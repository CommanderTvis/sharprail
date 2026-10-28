using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Chat.UI;

/// <summary>
/// The one popup family of the chat's pickers (model, effort, permission mode, past conversations): one container with a muted header, list rows with
/// the same insets and the same hover and selected wash, and a fixed check column that marks the current value without resizing the row.
/// </summary>
internal static class ChatPopup
{
    /// <summary>The height of a one-line picker row, and the width of the column that holds the check mark.</summary>
    internal const double RowHeight = 26, CheckWidth = 16;

    /// <summary>The horizontal inset of a row's content, and of every label that lines up with it.</summary>
    internal const double RowInset = 8;

    /// <summary>The same space on all four sides between a popup's border and what it holds.</summary>
    internal static readonly Thickness ContainerPadding = new(6);

    internal static readonly Thickness RowPadding = new(RowInset, 2);

    /// <summary>The one surface every popup of the chat is drawn on; lists and rows inside it have none of their own.</summary>
    internal static IBrush Surface => Ui.Surface;

    private static readonly SolidColorBrush raised = new();

    /// <summary>The composer's surface: a step lighter than the page in every theme (the page tone mixed toward the lightest of the hover and elevated tones), the page tone itself when no tone is lighter.</summary>
    internal static IBrush Raised => raised;

    static ChatPopup()
    {
        Ui.ThemeChanged += RefreshRaised;
        RefreshRaised();
    }

    private static void RefreshRaised()
    {
        static double Lum(Color c) => 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
        var page = ((ISolidColorBrush)Ui.Surface).Color;
        var lighter = new[] { ((ISolidColorBrush)Ui.Hover).Color, ((ISolidColorBrush)Ui.Elevated).Color }.MaxBy(Lum);
        if (Lum(lighter) <= Lum(page)) { raised.Color = page; return; }
        static byte Mix(byte a, byte b) => (byte)Math.Round(a + (b - a) * 0.6);
        raised.Color = Color.FromRgb(Mix(page.R, lighter.R), Mix(page.G, lighter.G), Mix(page.B, lighter.B));
    }

    /// <summary>A list on the popup surface: no fill of its own, rows as tall as their text with a horizontal inset, and the hover and selected washes (rounded like the rows) as the only fills.</summary>
    internal static ListBox Rows(ListBox list)
    {
        list.Background = Brushes.Transparent;
        list.BorderThickness = new Thickness(0);
        list.Padding = new Thickness(0);
        list.Styles.Add(new Style(x => x.OfType<ListBox>().Template().OfType<ScrollViewer>())
        {
            Setters = { new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent) }
        });
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters =
            {
                new Setter(TemplatedControl.PaddingProperty, RowPadding),
                new Setter(Layoutable.MinHeightProperty, 0d),
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(TemplatedControl.CornerRadiusProperty, new CornerRadius(4))
            }
        });
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.CornerRadiusProperty, new CornerRadius(4)) }
        });
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":pointerover").Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Ui.Hover) }
        });
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>().Class(":selected").Template().OfType<ContentPresenter>())
        {
            Setters = { new Setter(ContentPresenter.BackgroundProperty, Ui.Selected) }
        });
        return list;
    }

    /// <summary>The popup's frame: the surface, the border and the corner radius from the tokens, the same padding on every side.</summary>
    internal static Border Frame(string name, Control child) => new()
    {
        Name = name,
        Background = Surface,
        BorderBrush = Ui.BorderBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = ContainerPadding,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Child = child
    };

    /// <summary>A small muted label on the popup surface, its text lined up with the rows' text.</summary>
    internal static TextBlock Header(string name, string text) => new()
    {
        Name = name,
        Text = text,
        FontSize = 11,
        Foreground = Ui.Muted,
        Margin = new Thickness(RowInset, 0, 0, 0)
    };

    /// <summary>The popup's container: the frame with a small muted header label over the body.</summary>
    internal static Border Container(string name, string header, params Control[] body)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(Header(name + "Header", header));
        foreach (var control in body) stack.Children.Add(control);
        return Frame(name, stack);
    }

    /// <summary>A search box of a popup: its placeholder in the muted tone, whatever the control theme says.</summary>
    internal static TextBox Search(TextBox box)
    {
        box.PlaceholderForeground = Ui.Muted;
        return box;
    }

    /// <summary>Shows a flyout whose content is a <see cref="Frame"/>: the presenter's own chrome is taken away once it opens.</summary>
    internal static void Show(Flyout flyout, Control target)
    {
        flyout.Opened += (_, _) => Bare(flyout);
        shown.Add(flyout);
        flyout.Closed += (_, _) => shown.Remove(flyout);
        flyout.ShowAt(target);
    }

    private static readonly List<Flyout> shown = [];

    /// <summary>Closes every popup this chat opened that is still showing.</summary>
    internal static void CloseAll()
    {
        foreach (var flyout in shown.ToArray()) flyout.Hide();
    }

    /// <summary>The fixed-width check column of a row: a mark on the current value, blank space on the others.</summary>
    internal static TextBlock Check(bool current) => new()
    {
        Name = "ChatPopupCheck",
        Text = current ? "✓" : "",
        Width = CheckWidth,
        FontSize = 12,
        Foreground = Ui.Accent,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };

    /// <summary>Takes the flyout presenter's own frame away once it opens, so the container above is the only surface, border and padding.</summary>
    internal static void Bare(Flyout flyout)
    {
        if (flyout.Content is not Control { Parent: FlyoutPresenter presenter }) return;
        presenter.Padding = new Thickness(0);
        presenter.Background = Brushes.Transparent;
        presenter.BorderThickness = new Thickness(0);
        presenter.MinWidth = 0;
    }
}