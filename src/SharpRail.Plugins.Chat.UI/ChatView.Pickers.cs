using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

using SharpRail.Plugins.Agent.UI;
using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Plugins.Chat.UI;

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
    private Button PickerButton(string kind, string label, string title, Func<Flyout, Control> content, string? secondary = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
        var text = Ui.Text(label, kind == "model" ? Ui.TextBrush : Ui.Muted, 12);
        text.FontWeight = Ui.InterfaceWeight;
        row.Children.Add(text);
        if (secondary is { Length: > 0 }) row.Children.Add(Ui.Text(secondary, Ui.Muted, 12));
        var chevron = Ui.Icon("arrowDown", Ui.Muted, 10);
        row.Children.Add(chevron);
        Button? button = null;
        button = TerminalFacts.ChipButton("TerminalAgentFact_" + kind, row, title, () =>
        {
            var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft };
            flyout.Content = content(flyout);
            ChatPopup.Show(flyout, button!);
        });
        button.Classes.Add("plain");
        button.Background = Brushes.Transparent;
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

    private const string EffortKey = "~effort", MoreKey = "~more";

    /// <summary>
    /// The model button's popup: the flagship of each family, then Effort and More models, which open their own lists in the same popup. A flagship
    /// is the first model the CLI lists for its family; the default entry and models of no known family are under More models.
    /// </summary>
    internal Control ModelMenu(ChatHeader header, Flyout flyout)
    {
        var flagships = header.Models.Where(model => !ModelPicks.IsDefault(model) && model.Family is { Length: > 0 }).GroupBy(model => model.Family).Select(group => group.First()).ToList();
        var choices = flagships.Select(model => new PopupChoice(model.Id, model.Label, model.Description, ModelPicks.Matches(model, header.Model))).ToList();
        choices.Add(new PopupChoice(EffortKey, "Effort", (header.Effort ?? "default").ToLowerInvariant() + "  \u203A", false, false, true));
        choices.Add(new PopupChoice(MoreKey, "More models", "\u203A", false, false, true));
        Control? panel = null;
        // The two rows open a list beside the popup, from the row itself; picking in it closes both.
        void Child(Func<Flyout, Control> build)
        {
            var row = panel?.GetVisualDescendants().OfType<ListBox>().FirstOrDefault() is { } list ? list.ContainerFromIndex(list.SelectedIndex) : null;
            var child = new Flyout { Placement = PlacementMode.RightEdgeAlignedTop };
            child.Content = build(child);
            child.Closed += (_, _) => ClosePopup(flyout);
            ChatPopup.Show(child, row ?? panel!);
        }
        panel = ChoicePanel("ChatModelMenu", "ChatModelMenuList", "Model", choices, flyout, choice =>
        {
            if (choice.Key == EffortKey) Child(child => EffortPanel(header, child));
            else if (choice.Key == MoreKey) Child(child => ModelPanel(header, child));
            else _ = SetModel(choice.Key);
        }, choice => choice.Key is EffortKey or MoreKey);
        return panel;
    }

    // ThinkRail's thinking selector: the effort levels, lowercase, the current one checked, reachable by keys.
    internal Control EffortPanel(ChatHeader header, Flyout flyout)
    {
        // The levels the current model accepts, as the CLI reports them; every level when it reports none.
        var levels = header.Models.FirstOrDefault(model => model.Id == header.Model) is { Efforts.Count: > 0 } current ? current.Efforts : env.Agent.EffortLevels;
        return ChoicePanel("ChatEffortPicker", "ChatEffortList", "Effort",
            [.. levels.Select(level => new PopupChoice(level, level.ToLowerInvariant(), null, header.Effort == level))], flyout, choice => _ = SetEffort(choice.Key));
    }

    /// <summary>The permission modes: each with its description as a muted second part, the current one checked.</summary>
    internal Control ModePanel(ChatHeader header, Flyout flyout) =>
        ChoicePanel("ChatModePicker", "ChatModeList", "Permission mode",
            [.. ModeList(header).Select(mode => new PopupChoice(mode.Key, mode.Label, mode.Description, mode.Key == header.PermissionMode, true))], flyout, choice => _ = SetMode(choice.Key));

    // The attach chip asks what to attach first: the system picker takes a file or a folder, never both.
    private async Task<string?> PickFileOrFolder(Control anchor)
    {
        var pick = new TaskCompletionSource<string?>();
        var chosen = false;
        var flyout = new Flyout { Placement = PlacementMode.TopEdgeAlignedLeft };
        flyout.Content = ChoicePanel("ChatAttachPicker", "ChatAttachList", "Attach",
            [new PopupChoice("file", "File\u2026", null, false), new PopupChoice("folder", "Folder\u2026", null, false)], flyout, choice =>
            {
                chosen = true;
                _ = Task.Run(async () => { try { pick.TrySetResult(await (choice.Key == "folder" ? env.PickFolder() : env.PickFile())); } catch (Exception error) { pick.TrySetException(error); } });
            });
        flyout.Closed += (_, _) => { if (!chosen) pick.TrySetResult(null); };
        ChatPopup.Show(flyout, anchor);
        return await pick.Task;
    }

    private sealed record PopupChoice(string Key, string Label, string? Detail, bool Current, bool DetailInTip = false, bool Submenu = false);

    // The plain popup: a titled list of choices, a check on the current one, one click or Enter chooses and closes, Esc closes.
    private Control ChoicePanel(string name, string listName, string title, IReadOnlyList<PopupChoice> choices, Flyout flyout, Action<PopupChoice> choose, Func<PopupChoice, bool>? stays = null)
    {
        var list = ChatPopup.Rows(new ListBox { Name = listName, MinWidth = 200, MaxWidth = 440 });
        list.ItemsSource = choices;
        list.SelectedIndex = Math.Max(0, choices.ToList().FindIndex(choice => choice.Current));
        list.ItemTemplate = new FuncDataTemplate<PopupChoice>((choice, _) => choice is null ? null : ChoiceRow(choice), supportsRecycling: false);
        void Choose()
        {
            if (list.SelectedItem is not PopupChoice picked) return;
            if (stays?.Invoke(picked) != true) ClosePopup(flyout);
            choose(picked);
        }
        list.Tapped += (_, _) => Choose();
        list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Choose(); e.Handled = true; }
            else if (e.Key == Key.Escape) { ClosePopup(flyout); e.Handled = true; }
        };
        flyout.Opened += (_, _) => list.Focus();
        return ChatPopup.Container(name, title, list);
    }

    private static Control ChoiceRow(PopupChoice choice)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 8, Height = ChatPopup.RowHeight };
        var label = Ui.Text(choice.Label, Ui.TextBrush, 12);
        label.VerticalAlignment = VerticalAlignment.Center;
        grid.Children.Add(ChatPopup.Check(choice.Current));
        Grid.SetColumn(label, 1);
        grid.Children.Add(label);
        if (choice.Detail is { Length: > 0 } tip && choice.DetailInTip) ToolTip.SetTip(grid, tip);
        else if (choice.Detail is { Length: > 0 } detail)
        {
            var text = Ui.Text(detail.ReplaceLineEndings(" "), Ui.Muted, 11);
            text.Name = "ChatPopupDetail";
            text.MaxLines = 1;
            text.TextWrapping = TextWrapping.NoWrap;
            text.TextTrimming = TextTrimming.CharacterEllipsis;
            text.VerticalAlignment = VerticalAlignment.Center;
            if (choice.Submenu) text.HorizontalAlignment = HorizontalAlignment.Right;
            Grid.SetColumn(text, 2);
            grid.Children.Add(text);
            ToolTip.SetTip(grid, detail);
        }
        return grid;
    }
}