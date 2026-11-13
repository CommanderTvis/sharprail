using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Styling;
using Avalonia.VisualTree;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.UI.Terminal;

/// <summary>
/// The gear chip that trails the chip strip and chooses which chips show. The rows declare the chip kinds they can show and
/// tag their chips (<see cref="TerminalChips"/>); the strip hides a tagged chip whose id the user unchecked, so a kind no one
/// hid, a new one included, shows.
/// </summary>
public sealed partial class TerminalView
{
    private static bool watchingChipKinds;
    private IReadOnlyList<string> hiddenChips = [];

    /// <summary>The gear chip; shown while some row of this terminal declares a chip kind.</summary>
    internal Button ChipSettings { get; }

    /// <summary>Saves the chip ids the user hid, for every terminal; set by the workbench.</summary>
    internal Action<IReadOnlyList<string>>? SaveHiddenChips { get; set; }

    internal bool ChipsWired { get; set; }

    /// <summary>Whether the host can keep a chip choice; a host that predates the setting gets no gear.</summary>
    internal bool ChipChoiceSupported
    {
        get => chipChoiceSupported;
        set { chipChoiceSupported = value; RefreshChipSettings(); }
    }

    private bool chipChoiceSupported = true;

    /// <summary>The menu the gear opened last.</summary>
    internal ContextMenu? ChipMenu { get; private set; }

    /// <summary>The ids of a stored hidden-chips setting: comma-separated, empty for none.</summary>
    internal static IReadOnlyList<string> HiddenChipIds(string value) => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private Button WireChipSettings(ContentControl slot)
    {
        if (!watchingChipKinds)
        {
            watchingChipKinds = true;
            TerminalChips.KindsProperty.Changed.AddClassHandler<Control, IReadOnlyList<TerminalChipKind>?>((control, _) => control.FindAncestorOfType<TerminalView>()?.RefreshChipSettings());
            // A chip the hidden-chips style hides keeps its last size in a wrap panel's measure; its row is measured again so it folds or grows at once.
            IsVisibleProperty.Changed.AddClassHandler<Control, bool>((control, _) =>
            {
                if (control.GetValue(TerminalChips.IdProperty) is null) return;
                control.InvalidateMeasure();
                (control.GetVisualParent() as Layoutable)?.InvalidateMeasure();
            });
        }
        // A blank label beside the icon gives the gear the height of a chip with text, so a strip of the gear alone keeps a row's height.
        var strut = Chips.Label(" ");
        strut.Width = 0;
        var content = new Panel { Children = { strut, Ui.Icon("settings", Ui.Muted, 14) } };
        var gear = Chips.Button("TerminalChipSettings", content, "Choose chips", OpenChipMenu);
        gear.Margin = new Thickness(0, 0, 8, LayoutRoles.ChipGap);
        gear.IsVisible = false;
        slot.Content = gear;
        Accessories.Children.CollectionChanged += (_, _) => RefreshChipSettings();
        return gear;
    }

    /// <summary>Every chip kind the rows can show, in row order: what each row declares, then any tagged chip none declared.</summary>
    internal IReadOnlyList<TerminalChipKind> ChipKinds()
    {
        var kinds = new List<TerminalChipKind>();
        foreach (var control in Accessories.GetLogicalDescendants().OfType<Control>())
        {
            if (control.GetValue(TerminalChips.KindsProperty) is { } declared)
                foreach (var kind in declared) Add(kind);
            if (control.GetValue(TerminalChips.IdProperty) is { Length: > 0 } id) Add(new(id, id));
        }
        return kinds;

        void Add(TerminalChipKind kind)
        {
            if (kinds.All(known => known.Id != kind.Id)) kinds.Add(kind);
        }
    }

    /// <summary>Hides the chips with these ids; the rest show.</summary>
    internal void ShowChips(IReadOnlyList<string> hidden)
    {
        if (!hidden.SequenceEqual(hiddenChips))
        {
            hiddenChips = hidden;
            Accessories.Styles.Clear();
            foreach (var id in hidden)
                Accessories.Styles.Add(new Style(selector => selector.Is<Control>().PropertyEquals(TerminalChips.IdProperty, id)) { Setters = { new Setter(IsVisibleProperty, false) } });
        }
        RefreshChipSettings();
    }

    // A terminal with nothing to choose (a plain shell with no environment) shows no gear, so it grows no strip.
    private void RefreshChipSettings() => ChipSettings.IsVisible = chipChoiceSupported && ChipKinds().Count > 0;

    private void OpenChipMenu()
    {
        ChipMenu = BuildChipMenu();
        ChipMenu.Open(ChipSettings);
    }

    /// <summary>One check row per chip kind of this terminal, then Show all.</summary>
    internal ContextMenu BuildChipMenu()
    {
        var menu = new ContextMenu { Name = "TerminalChipMenu", Placement = Avalonia.Controls.PlacementMode.TopEdgeAlignedRight, PlacementTarget = ChipSettings };
        foreach (var kind in ChipKinds())
        {
            var shown = !hiddenChips.Contains(kind.Id);
            var item = Ui.Menu(kind.Label, () => ChooseChips(shown ? [.. hiddenChips, kind.Id] : [.. hiddenChips.Where(id => id != kind.Id)]));
            item.Name = "TerminalChipToggle_" + kind.Id;
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = shown;
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = Ui.Menu("Show all", () => ChooseChips([]), hiddenChips.Count > 0);
        all.Name = "TerminalChipShowAll";
        menu.Items.Add(all);
        return menu;
    }

    // This terminal follows at once; the others follow the shared setting.
    private void ChooseChips(IReadOnlyList<string> hidden)
    {
        ShowChips(hidden);
        SaveHiddenChips?.Invoke(hidden);
    }
}