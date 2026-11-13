using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;

using Tereditor.Plugins.Agent.UI;
using Tereditor.Plugins.UI.Kit;
using Tereditor.UI.Terminal;

using static Tereditor.Checks.E2E.E2eWorkspace;

namespace Tereditor.Checks;

/// <summary>The gear chip at the end of a terminal's chip strip and the menu that chooses which chips show.</summary>
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
            Settle(); window.UpdateLayout();
            var gear = terminal.ChipSettings;
            var twoRows = strip.Bounds.Height;
            Require(gear.IsVisible && ToolTip.GetTip(gear) as string == "Choose chips", "The gear shows once a row declares chips, and its tooltip says what it does.");
            var gearAt = gear.TranslatePoint(new Point(0, 0), terminal)!.Value;
            Require(new[] { model, effort, mise }.All(chip => chip.TranslatePoint(new Point(chip.Bounds.Width, 0), terminal)!.Value.X <= gearAt.X) &&
                Math.Abs(gearAt.Y + gear.Bounds.Height - (mise.TranslatePoint(new Point(0, mise.Bounds.Height), terminal)!.Value.Y)) < 1.5,
                $"The gear trails the strip: after every chip, beside the last row ({gear.Bounds}, mise {mise.Bounds}).");
            Require(Math.Abs(gear.Bounds.Height - model.Bounds.Height) < 2.5, $"The gear is a chip of the strip's height ({gear.Bounds.Height} against {model.Bounds.Height}).");

            var menu = terminal.BuildChipMenu();
            var items = menu.Items.OfType<MenuItem>().ToList();
            Require(items.Select(item => item.Header as string).SequenceEqual(["Model", "Effort", "Token usage", "Attach file", "mise", "Show all"]) &&
                items.SkipLast(1).All(item => item.ToggleType == MenuItemToggleType.CheckBox && item.IsChecked) && !items[^1].IsEnabled && menu.Items.OfType<Separator>().Count() == 1,
                "The menu lists every kind the rows can show, present now or not, the plugin's own chip included, all checked; Show all is idle while none is hidden.");

            Click(items.Single(item => item.Name == "TerminalChipToggle_env/env-mise"));
            Settle(); window.UpdateLayout();
            Require(!mise.IsVisible && model.IsVisible && saved[^1].SequenceEqual(["env/env-mise"]), "Unchecking a plugin chip hides it at once and saves its id.");
            Require(Math.Abs(strip.Bounds.Height - twoRows) > 1 && gear.IsVisible, $"The hidden chip's row folds away and the gear stays ({strip.Bounds.Height} against {twoRows}).");
            var oneRow = strip.Bounds.Height;

            menu = terminal.BuildChipMenu();
            Require(menu.Items.OfType<MenuItem>().Single(item => item.Name == "TerminalChipToggle_env/env-mise").IsChecked == false && menu.Items.OfType<MenuItem>().Last().IsEnabled,
                "The menu shows the hidden chip unchecked and offers Show all.");
            Click(menu.Items.OfType<MenuItem>().Single(item => item.Name == "TerminalChipToggle_model"));
            Click(terminal.BuildChipMenu().Items.OfType<MenuItem>().Single(item => item.Name == "TerminalChipToggle_effort"));
            Settle(); window.UpdateLayout();
            Require(!model.IsVisible && !effort.IsVisible && gear.IsVisible && Math.Abs(strip.Bounds.Height - oneRow) < 1,
                $"With every chip hidden the strip keeps one row, the gear's ({strip.Bounds.Height} against {oneRow}).");

            // A chip kind nobody has hidden yet, such as one a newer plugin adds, shows.
            terminal.ShowChips(["env/env-mise", "future/other"]);
            var fresh = Chip(agent, "future/new", "new");
            Settle(); window.UpdateLayout();
            Require(fresh.IsVisible && model.IsVisible && !mise.IsVisible && terminal.ChipKinds().Any(kind => kind.Id == "future/new"), "A chip whose id is not in the hidden set shows, and the menu lists it.");

            Click(terminal.BuildChipMenu().Items.OfType<MenuItem>().Single(item => item.Name == "TerminalChipShowAll"));
            Settle(); window.UpdateLayout();
            Require(mise.IsVisible && model.IsVisible && effort.IsVisible && saved[^1].Count == 0, "Show all brings every chip back and saves an empty set.");

            // The stored setting's form: comma-separated ids, empty for none.
            Require(TerminalView.HiddenChipIds("env/env-mise,model").SequenceEqual(["env/env-mise", "model"]) && TerminalView.HiddenChipIds("").Count == 0, "The stored setting reads back as its ids.");
            Console.WriteLine("PASS terminal chips: the gear trails the strip, lists every kind incl. a plugin chip, hides and restores at once, keeps one row when all are hidden, new ids show");
        }
        finally { window.Close(); }
    }

    private static void Click(MenuItem item) => item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
}