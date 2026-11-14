using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.VisualTree;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.UI.Kit;
using Tereditor.UI.Terminal;

using static Tereditor.Checks.E2E.E2eWorkspace;

namespace Tereditor.Checks;

/// <summary>The gear chip after the last chip of a terminal's chip strip and the popup that chooses which chips show.</summary>
internal static class TerminalChipChecks
{
    internal static void Run()
    {
        using var terminal = new TerminalView(_ => new FileDropChecks.RecordingTerminal(), new("/tmp", "chip-probe", "", "chip-client"));
        var window = new Window { Width = 900, Height = 400, Content = terminal };
        window.Show();
        try
        {
            var saved = new List<IReadOnlyList<string>>();
            terminal.SaveHiddenChips = saved.Add;
            var strip = terminal.FindControl<Border>("TerminalAccessoriesStrip")!;
            // Positions are read in the chip flow's own box, inside the strip's padding.
            var flow = terminal.FindControl<Control>("TerminalChipStrip")!;
            Settle(); window.UpdateLayout();
            Require(!terminal.ChipSettings.IsVisible && strip.Bounds.Height < 0.5, "A terminal with no chip kinds (a plain shell) shows no gear and no strip.");

            // An agent row and an environment row, built the way the plugins build theirs: tagged chips and declared kinds.
            var agent = new WrapPanel { Margin = new Thickness(8, 0) };
            agent.SetValue(TerminalChips.KindsProperty, TerminalFacts.Kinds("model", "effort", "usage", "attach"));
            static Control Chip(Panel row, string id, string label)
            {
                var chip = TerminalChips.Tag(TerminalFacts.FactChip(id, label, label), id);
                chip.Margin = Chips.Gap;
                row.Children.Add(chip);
                return chip;
            }
            var model = Chip(agent, "model", "opus-5-5");
            var effort = Chip(agent, "effort", "medium effort");
            var env = new WrapPanel { Margin = new Thickness(8, 0) };
            env.SetValue(TerminalChips.KindsProperty, [new TerminalChipKind("env/env-mise", "mise")]);
            var mise = Chip(env, "env/env-mise", "mise");
            terminal.Accessories.Children.Add(new ContentControl { Content = agent });
            terminal.Accessories.Children.Add(new ContentControl { Content = env });
            terminal.ShowChips([]);
            Settle(); window.UpdateLayout(); window.UpdateLayout();
            var gear = terminal.ChipSettings;
            var twoRows = strip.Bounds.Height;
            Require(gear.IsVisible && ToolTip.GetTip(gear) as string == "Choose chips", "The gear shows once a row declares chips, and its tooltip says what it does.");
            Require(Math.Abs(gear.Bounds.Height - model.Bounds.Height) < 2.5, $"The gear is a chip of the strip's height ({gear.Bounds.Height} against {model.Bounds.Height}).");
            RequireGearAfter(mise, "The gear sits right after the last chip, on its line");

            // The gear's popup: every kind the rows can show, present now or not, the plugin's own chip included, all checked.
            gear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(); window.UpdateLayout();
            var popup = terminal.ChipMenu;
            Require(popup is { IsOpen: true } && popup.Content is Panel, "The gear opens its popup.");
            var list = (Panel)popup!.Content!;
            var toggles = list.Children.OfType<CheckBox>().ToList();
            CheckBox Toggle(string id) => toggles.Single(box => box.Name == "TerminalChipToggle_" + id);
            var showAll = list.Children.OfType<Button>().Single(button => button.Name == "TerminalChipShowAll");
            Require(toggles.Select(box => box.Content as string).SequenceEqual(["Model", "Effort", "Token usage", "Attach file", "mise"]) &&
                toggles.All(box => box.IsChecked == true) && !showAll.IsEnabled,
                "The popup lists every kind the rows can show, all checked; Show all is idle while none is hidden.");

            // Two toggles in a row: the popup stays open, keeps its rows and applies both; it stays put while the gear moves.
            var popupAt = TopLevel.GetTopLevel(list)!.PointToScreen(new Point(0, 0));
            var gearBefore = gear.TranslatePoint(new Point(0, 0), flow)!.Value;
            Toggle("env/env-mise").IsChecked = false;
            Settle(); window.UpdateLayout(); window.UpdateLayout();
            var popupNow = TopLevel.GetTopLevel(list)!.PointToScreen(new Point(0, 0));
            Require(Point.Distance(gearBefore, gear.TranslatePoint(new Point(0, 0), flow)!.Value) > 5 && Math.Abs(popupNow.X - popupAt.X) <= 1 && Math.Abs(popupNow.Y - popupAt.Y) <= 1,
                $"The row reflows and the gear moves, but the open popup stays where it opened ({popupAt} against {popupNow}).");
            Require(popup.IsOpen && !mise.IsVisible && model.IsVisible && saved[^1].SequenceEqual(["env/env-mise"]), "Unchecking a plugin chip hides it at once, saves its id and leaves the popup open.");
            Require(Math.Abs(strip.Bounds.Height - twoRows) > 1 && gear.IsVisible, $"The hidden chip's row folds away and the gear stays ({strip.Bounds.Height} against {twoRows}).");
            RequireGearAfter(effort, "With the last row gone the gear moves up after the chip that is now last");
            var oneRow = strip.Bounds.Height;
            Toggle("model").IsChecked = false;
            Settle(); window.UpdateLayout(); window.UpdateLayout();
            Require(popup.IsOpen && ReferenceEquals(terminal.ChipMenu, popup) && list.Children.OfType<CheckBox>().SequenceEqual(toggles) &&
                !model.IsVisible && !mise.IsVisible && saved[^1].SequenceEqual(["env/env-mise", "model"]) && Toggle("model").IsChecked == false && Toggle("env/env-mise").IsChecked == false && showAll.IsEnabled,
                "A second toggle in the same popup applies too: both chips hidden and saved, the same rows still open, Show all offered.");
            Toggle("effort").IsChecked = false;
            Settle(); window.UpdateLayout(); window.UpdateLayout();
            var gearAt = gear.TranslatePoint(new Point(0, 0), flow)!.Value;
            Require(popup.IsOpen && !model.IsVisible && !effort.IsVisible && gear.IsVisible && Math.Abs(strip.Bounds.Height - oneRow) < 1 && Math.Abs(gearAt.X - 8) < 1 && gearAt.Y < 1,
                $"With every chip hidden the gear is the strip's only chip, where the first chip would be, and the strip keeps one row ({strip.Bounds.Height} against {oneRow}, gear at {gearAt}).");

            // A chip kind nobody has hidden yet, such as one a newer plugin adds, shows; a change from elsewhere updates the open popup in place.
            terminal.ShowChips(["env/env-mise", "future/other"]);
            var fresh = Chip(agent, "future/new", "new");
            Settle(); window.UpdateLayout(); window.UpdateLayout();
            Require(fresh.IsVisible && model.IsVisible && !mise.IsVisible && terminal.ChipKinds().Any(kind => kind.Id == "future/new"), "A chip whose id is not in the hidden set shows, and the gear lists it.");
            Require(popup.IsOpen && Toggle("model").IsChecked == true && Toggle("effort").IsChecked == true && Toggle("env/env-mise").IsChecked == false && saved[^1].SequenceEqual(["env/env-mise", "model", "effort"]),
                "A hidden set that arrives from the host updates the open popup's checks in place and saves nothing.");
            RequireGearAfter(fresh, "A chip added to the row puts the gear after it");

            showAll.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle(); window.UpdateLayout(); window.UpdateLayout();
            Require(popup.IsOpen && mise.IsVisible && model.IsVisible && effort.IsVisible && saved[^1].Count == 0 && toggles.All(box => box.IsChecked == true) && !showAll.IsEnabled,
                "Show all brings every chip back, saves an empty set and leaves the popup open.");
            gear.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Settle();
            Require(!popup.IsOpen, "The gear closes its own popup.");

            // A last line with no room left: the gear wraps to a line of its own at the row's start.
            var before = strip.Bounds.Height;
            var wide = Chip(env, "future/wide", "wide");
            wide.Width = flow.Bounds.Width - 16 - LayoutRoles.ChipGap - gear.Bounds.Width / 2;
            Settle(); window.UpdateLayout(); window.UpdateLayout();
            gearAt = gear.TranslatePoint(new Point(0, 0), flow)!.Value;
            var wideBottom = wide.TranslatePoint(new Point(0, wide.Bounds.Height), flow)!.Value.Y;
            Require(Math.Abs(gearAt.X - 8) < 1 && gearAt.Y >= wideBottom - 0.5 && strip.Bounds.Height > before + gear.Bounds.Height - 1,
                $"With no room after the last chip the gear wraps to its own line at the row's start and the strip grows by it (gear at {gearAt}, chip bottom {wideBottom}, {strip.Bounds.Height} against {before}).");

            // The stored setting's form: comma-separated ids, empty for none.
            Require(TerminalView.HiddenChipIds("env/env-mise,model").SequenceEqual(["env/env-mise", "model"]) && TerminalView.HiddenChipIds("").Count == 0, "The stored setting reads back as its ids.");
            Console.WriteLine("PASS terminal chips: the gear is the last chip of the flow and wraps with it, its popup lists every kind incl. a plugin chip and stays open across toggles, hides and restores at once, keeps one row when all are hidden, new ids show");

            // The gear's box starts right after the chip's (its gap included), on that chip's line, and after every visible chip.
            void RequireGearAfter(Control last, string what)
            {
                var at = gear.TranslatePoint(new Point(0, 0), flow)!.Value;
                var end = last.TranslatePoint(new Point(last.Bounds.Width, 0), flow)!.Value;
                var chips = flow.GetVisualDescendants().OfType<Control>().Where(chip => chip.GetValue(TerminalChips.IdProperty) is not null && chip.IsEffectivelyVisible).ToList();
                Require(Math.Abs(at.X - (end.X + LayoutRoles.ChipGap)) < 1 && Math.Abs(at.Y - end.Y) < 1 &&
                    chips.All(chip => chip.TranslatePoint(new Point(chip.Bounds.Width, chip.Bounds.Height), flow)!.Value is var corner && (corner.Y <= at.Y + gear.Bounds.Height + 1 && (corner.X <= at.X + 0.5 || corner.Y <= at.Y + 0.5))),
                    $"{what} (gear at {at}, chip ends at {end}).");
            }
        }
        finally { window.Close(); }
    }
}