using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Plugins.Chat.UI;

internal sealed partial class ChatView
{
    private ModelPreferences? modelPreferences;
    private readonly HashSet<string> toggledFamilies = [];
    private bool showHiddenModels;

    /// <summary>Raised after a picker popup chooses and closes; the checks watch it, as no real popup opens headless.</summary>
    internal event Action? PopupClosed;

    private void ClosePopup(Flyout flyout)
    {
        flyout.Hide();
        PopupClosed?.Invoke();
    }

    private ModelPreferences Preferences => modelPreferences ??= ModelPreferences.Parse(env.ModelPreference.Get());

    private void SavePreferences(ModelPreferences next)
    {
        modelPreferences = next;
        env.ModelPreference.Set(next.Serialize());
    }

    // A picker trigger in the footer: plain text (the model in the normal tone, the others muted), a small down arrow always, so they read as pickers.
    // A click opens the picker's menu, built fresh so it shows the values in force.
    private Button PickerButton(string kind, string label, string title, Func<Button, ContextMenu> menu, string? secondary = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var text = Ui.Text(label, kind == "model" ? Ui.TextBrush : Ui.Muted, 12);
        text.FontWeight = Ui.InterfaceWeight;
        row.Children.Add(text);
        if (secondary is { Length: > 0 }) row.Children.Add(Ui.Text(secondary, Ui.Muted, 12));
        var chevron = Ui.Icon("arrowDown", Ui.Muted, 10);
        row.Children.Add(chevron);
        Button? button = null;
        button = TerminalFacts.ChipButton("TerminalAgentFact_" + kind, row, title, () => ChatPopup.ShowMenu(menu(button!), button!));
        button.Classes.Add("plain");
        button.Background = Ui.ChipFill;
        return button;
    }

    // A flyout list as tall as its text: the theme's list rows are sized for touch.
    internal static ListBox Tight(ListBox list)
    {
        list.Styles.Add(new Style(x => x.OfType<ListBoxItem>())
        {
            Setters = { new Setter(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty, new Thickness(6, 2)), new Setter(Layoutable.MinHeightProperty, 0d) }
        });
        return list;
    }

    // ThinkRail's model selector: a filter over favourites, recents and families, a star and a hide control per model, keys to move and pick.
    internal Control ModelPanel(ChatHeader header, Flyout flyout)
    {
        var filter = ChatPopup.Search(new TextBox { Name = "ChatModelFilter", PlaceholderText = "Search models", MinWidth = 240, FontSize = 12 });
        var list = ChatPopup.Rows(new ListBox { Name = "ChatModelList", MaxHeight = 280 });
        var hiddenCount = header.Models.Count(model => Preferences.Hidden.Contains(model.Id));
        var hidden = new CheckBox { Name = "ChatModelShowHidden", Content = $"Show {hiddenCount} hidden", IsVisible = hiddenCount > 0, IsChecked = showHiddenModels };
        void Refill()
        {
            var rows = ModelPicks.Rows(header.Models, Preferences, filter.Text ?? "", header.Model, showHiddenModels, toggledFamilies);
            list.ItemsSource = rows;
            list.SelectedIndex = rows.FindIndex(row => row.Kind == PickRowKind.Model);
        }
        void Pick(PickRow row)
        {
            if (row.Kind == PickRowKind.Header)
            {
                if (row.Title is not ("Favorites" or "Recent") && !toggledFamilies.Remove(row.Title)) toggledFamilies.Add(row.Title);
                Refill();
                return;
            }
            if (row.Model is not { } model) return;
            SavePreferences(Preferences.Picked(model.Id));
            ClosePopup(flyout);
            _ = SetModel(model.Id);
        }
        list.ItemTemplate = new FuncDataTemplate<PickRow>((row, _) => row is null ? null : ModelRow(row, header, Refill), supportsRecycling: false);
        // One click picks a model or folds a section, wherever on the row it lands; the star and the hide control keep their own clicks.
        list.Tapped += (_, e) =>
        {
            if (e.Source is Visual source && source.FindAncestorOfType<Button>() is not null) return;
            if (list.SelectedItem is PickRow row) Pick(row);
        };
        filter.TextChanged += (_, _) => Refill();
        hidden.IsCheckedChanged += (_, _) => { showHiddenModels = hidden.IsChecked == true; Refill(); };
        filter.AddHandler(KeyDownEvent, (object? _, KeyEventArgs e) =>
        {
            var rows = (List<PickRow>)list.ItemsSource!;
            switch (e.Key)
            {
                case Key.Down or Key.Up:
                    var step = e.Key == Key.Down ? 1 : -1;
                    for (var at = list.SelectedIndex + step; at >= 0 && at < rows.Count; at += step)
                        if (rows[at].Kind == PickRowKind.Model || rows[at].Kind == PickRowKind.Header) { list.SelectedIndex = at; break; }
                    e.Handled = true; break;
                case Key.Enter when list.SelectedItem is PickRow row: Pick(row); e.Handled = true; break;
                case Key.Escape: ClosePopup(flyout); e.Handled = true; break;
            }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        var panel = ChatPopup.Container("ChatModelPicker", "Model", filter, list, hidden);
        flyout.Opened += (_, _) => filter.Focus();
        Refill();
        return panel;
    }

    private Control ModelRow(PickRow row, ChatHeader header, Action refill)
    {
        if (row.Kind == PickRowKind.Header)
        {
            var title = Ui.Text((row.Title is "Favorites" or "Recent" ? "" : row.Expanded ? "▾ " : "▸ ") + row.Title + (row.Count > 0 ? $"  {row.Count}" : ""), Ui.Hint, 11);
            title.Margin = new Thickness(4, 6, 4, 2);
            title.Tag = row;
            return title;
        }
        var model = row.Model!;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,Auto"), Height = ChatPopup.RowHeight };
        var star = new Button { Name = "ChatModelFavorite", Content = Ui.Text(row.Favorite ? "★" : "☆", row.Favorite ? Ui.Accent : Ui.Muted, 13), Padding = new Thickness(2, 0), MinHeight = 0, MinWidth = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        ToolTip.SetTip(star, row.Favorite ? "Remove from favorites" : "Add to favorites");
        star.Click += (_, _) => { SavePreferences(Preferences.ToggleFavorite(model.Id)); refill(); };
        var label = Ui.Text(model.Label, row.Hidden ? Ui.Hint : Ui.TextBrush, 12);
        label.VerticalAlignment = VerticalAlignment.Center;
        if (model.Description is { Length: > 0 } description) ToolTip.SetTip(label, description);
        var eye = new Button { Name = "ChatModelHide", Content = Ui.Text(row.Hidden ? "show" : "hide", Ui.Hint, 11), Padding = new Thickness(2, 0), MinHeight = 0, MinWidth = 0, Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        eye.Click += (_, _) => { SavePreferences(Preferences.ToggleHidden(model.Id)); refill(); };
        var check = ChatPopup.Check(ModelPicks.Matches(model, header.Model));
        Grid.SetColumn(star, 1); Grid.SetColumn(label, 2); Grid.SetColumn(eye, 3);
        grid.Children.Add(check); grid.Children.Add(star); grid.Children.Add(label); grid.Children.Add(eye);
        return grid;
    }

    /// <summary>
    /// The model button's menu: the flagship of each family as radio items, then Effort, a submenu of the levels, and More models, which opens the
    /// searchable list from the model button. A flagship is the first model the CLI lists for its family; the default entry and models of no known
    /// family are under More models.
    /// </summary>
    internal ContextMenu ModelMenu(ChatHeader header, Control anchor)
    {
        var menu = ChatPopup.Menu("ChatModelMenu");
        if (header.Models.Count == 0)
        {
            menu.Items.Add(Ui.Menu($"Waiting for {env.Agent.Name}\u2026", () => { }, false));
            return menu;
        }
        foreach (var model in header.Models.Where(model => !ModelPicks.IsDefault(model) && model.Family is { Length: > 0 }).GroupBy(model => model.Family).Select(group => group.First()))
            menu.Items.Add(ChatPopup.Choice("ChatModel_" + model.Id, model.Label, ModelPicks.Matches(model, header.Model), () => _ = SetModel(model.Id), model.Description));
        menu.Items.Add(new Separator());
        var effort = new MenuItem { Name = "ChatModelEffort", Header = ChatPopup.Labelled("Effort", (header.Effort ?? "default").ToLowerInvariant()) };
        Avalonia.Automation.AutomationProperties.SetName(effort, "Effort");
        foreach (var level in EffortItems(header)) effort.Items.Add(level);
        menu.Items.Add(effort);
        // The searchable list needs a text box, which a menu cannot hold (its keys move through the rows): it is its own popup, opened once the menu has closed.
        menu.Items.Add(ChatPopup.Item("ChatModelMore", "More models\u2026", () => Dispatcher.UIThread.Post(() =>
        {
            var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft };
            flyout.Content = ModelPanel(header, flyout);
            ChatPopup.Show(flyout, anchor);
        }, DispatcherPriority.Background)));
        return menu;
    }

    // ThinkRail's thinking selector: the effort levels, lowercase, the current one checked.
    internal IReadOnlyList<MenuItem> EffortItems(ChatHeader header)
    {
        // The levels the current model accepts, as the CLI reports them; every level when it reports none.
        var levels = header.Models.FirstOrDefault(model => model.Id == header.Model) is { Efforts.Count: > 0 } current ? current.Efforts : env.Agent.EffortLevels;
        return [.. levels.Select(level => ChatPopup.Choice("ChatEffort_" + level, level.ToLowerInvariant(), header.Effort == level, () => _ = SetEffort(level)))];
    }

    /// <summary>The permission modes as radio items, the current one checked, each with its description as the tooltip.</summary>
    internal ContextMenu ModeMenu(ChatHeader header)
    {
        var menu = ChatPopup.Menu("ChatModeMenu");
        foreach (var mode in ModeList(header))
        {
            var item = ChatPopup.Choice("ChatMode_" + mode.Key, mode.Label, mode.Key == header.PermissionMode, () => _ = SetMode(mode.Key));
            if (mode.Description is { Length: > 0 } description) ToolTip.SetTip(item, description);
            menu.Items.Add(item);
        }
        return menu;
    }

    // The attach chip asks what to attach first: the system picker takes a file or a folder, never both.
    private async Task<string?> PickFileOrFolder(Control anchor)
    {
        var pick = new TaskCompletionSource<string?>();
        var chosen = false;
        void Choose(Func<Task<string?>> picker)
        {
            chosen = true;
            _ = Task.Run(async () => { try { pick.TrySetResult(await picker()); } catch (Exception error) { pick.TrySetException(error); } });
        }
        var menu = AttachMenu(() => Choose(env.PickFile), () => Choose(env.PickFolder));
        // A closed menu with no choice is a cancel; the check waits for the queue, so a choice's click that lands after the close still counts.
        menu.Closed += (_, _) => Dispatcher.UIThread.Post(() => { if (!chosen) pick.TrySetResult(null); }, DispatcherPriority.Background);
        ChatPopup.ShowMenu(menu, anchor);
        return await pick.Task;
    }

    internal static ContextMenu AttachMenu(Action file, Action folder)
    {
        var menu = ChatPopup.Menu("ChatAttachMenu");
        menu.Items.Add(ChatPopup.Item("ChatAttachFile", "File\u2026", file));
        menu.Items.Add(ChatPopup.Item("ChatAttachFolder", "Folder\u2026", folder));
        return menu;
    }
}