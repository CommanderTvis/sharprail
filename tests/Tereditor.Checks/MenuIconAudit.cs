using System.Runtime.CompilerServices;

using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

using Tereditor.Plugins.UI.Kit;

namespace Tereditor.Checks;

/// <summary>
/// The menu icon rule (src/Tereditor.Plugins.UI.Kit/ActionIcons/SPEC.md, "Icon slot"): in every open menu each icon lies fully inside its
/// slot, the slot has the shared size, the icon is centred in it, no clipping ancestor cuts it, and every row's text starts on one edge.
/// <see cref="Audit"/> checks one open menu; the module initializer below audits every context menu that any headless check opens,
/// so a new menu that breaks the rule fails the check that opens it.
/// </summary>
internal static class MenuIconAudit
{
    private const double Tolerance = 0.5;

    /// <summary>How many menus have been audited, so a mode can require that its sweep reached the menus it opens.</summary>
    internal static int Audited { get; private set; }

    /// <summary>Set while a check opens a deliberately broken menu, so the ambient audit leaves it to that check.</summary>
    internal static bool Probing { get; set; }

    [ModuleInitializer]
    internal static void Install() =>
        MenuBase.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) => Dispatcher.UIThread.Post(() =>
        {
            if (Probing || !menu.IsOpen || TopLevel.GetTopLevel(menu) is not Layoutable root) return;
            root.UpdateLayout();
            Audit("menu " + string.Join("|", menu.Items.OfType<MenuItem>().Select(item => item.Header as string ?? item.Name).Take(3)), menu);
        }, DispatcherPriority.Background));

    /// <summary>Throws unless every icon of the open <paramref name="menu"/> is drawn inside its slot, centred, unclipped, with the rows' text on one edge.</summary>
    internal static void Audit(string name, ContextMenu menu)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var rows = menu.Items.OfType<MenuItem>().Where(row => row.IsVisible && row.Bounds.Height > 0).ToArray();
        if (rows.Length == 0) return;
        Audited++;
        var edges = new List<(string Row, double X)>();
        foreach (var row in rows)
        {
            var label = row.Header as string ?? row.Name ?? row.Header?.GetType().Name ?? "?";
            var header = row.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(part => part.Name == "PART_HeaderPresenter");
            if (header is { Bounds.Width: > 0 }) edges.Add((label, header.TranslatePoint(default, menu)!.Value.X));
            if (row.Icon is not Control icon) continue;
            var slot = row.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault(part => part.Name == "PART_IconPresenter");
            Require(slot is not null, $"{name}: '{label}' has an icon but no icon slot.");
            var size = LayoutRoles.MenuIconSlot;
            Require(Math.Abs(slot!.Bounds.Width - size) <= Tolerance && Math.Abs(slot.Bounds.Height - size) <= Tolerance,
                $"{name}: the icon slot of '{label}' is {slot.Bounds.Width:0.#}x{slot.Bounds.Height:0.#}, not the shared {size}x{size} (MenuIconSlot).");
            Require(icon.Bounds.Width > 0 && icon.Bounds.Height > 0, $"{name}: the icon of '{label}' has no size ({icon.Bounds}).");
            Require(icon.DesiredSize.Width <= icon.Bounds.Width + Tolerance && icon.DesiredSize.Height <= icon.Bounds.Height + Tolerance,
                $"{name}: the icon of '{label}' wants {icon.DesiredSize.Width:0.#}x{icon.DesiredSize.Height:0.#} but is laid out {icon.Bounds.Width:0.#}x{icon.Bounds.Height:0.#}, so layout trims it.");
            var inSlot = Box(icon, slot);
            Require(inSlot.X >= -Tolerance && inSlot.Y >= -Tolerance && inSlot.Right <= slot.Bounds.Width + Tolerance && inSlot.Bottom <= slot.Bounds.Height + Tolerance,
                $"{name}: the icon of '{label}' spans {inSlot} outside its {size}x{size} slot.");
            var offX = inSlot.Center.X - slot.Bounds.Width / 2;
            var offY = inSlot.Center.Y - slot.Bounds.Height / 2;
            Require(Math.Abs(offX) <= Tolerance && Math.Abs(offY) <= Tolerance, $"{name}: the icon of '{label}' is {offX:0.##}, {offY:0.##} px off the centre of its slot.");
            var inRow = Box(slot, row);
            Require(Math.Abs(inRow.Center.Y - row.Bounds.Height / 2) <= 1, $"{name}: the icon slot of '{label}' is centred at y={inRow.Center.Y:0.##} in a row {row.Bounds.Height:0.##} high.");
            // Everything the icon draws, not only its outer box: a child cut by its own parent is a trimmed glyph too.
            foreach (var part in icon.GetSelfAndVisualDescendants().Where(part => part.Bounds.Width > 0 && part.Bounds.Height > 0))
                foreach (var ancestor in part.GetVisualAncestors())
                {
                    var clips = ancestor is Control { ClipToBounds: true } || ancestor is ScrollContentPresenter;
                    if (!clips || ancestor.Bounds.Width <= 0) continue;
                    var box = Box(part, ancestor);
                    // A scrolled menu legitimately cuts rows vertically; only the sideways clip is a defect there.
                    var vertical = ancestor is not ScrollContentPresenter;
                    Require(box.X >= -Tolerance && box.Right <= ancestor.Bounds.Width + Tolerance && (!vertical || box.Y >= -Tolerance && box.Bottom <= ancestor.Bounds.Height + Tolerance),
                        $"{name}: the icon of '{label}' draws {part.GetType().Name} at {box}, past {ancestor.GetType().Name} {(ancestor as Control)?.Name} which clips it ({ancestor.Bounds.Width:0.#}x{ancestor.Bounds.Height:0.#}).");
                }
        }
        if (edges.Count > 1)
            Require(edges.All(edge => Math.Abs(edge.X - edges[0].X) <= Tolerance), $"{name}: row text does not start on one edge: " + string.Join(", ", edges.Select(edge => $"{edge.Row}={edge.X:0.#}")));
    }

    // Where the child is drawn in the ancestor's space, scaling and offsets of the Viewbox and render transforms included.
    private static Rect Box(Visual child, Visual ancestor) => new Rect(child.Bounds.Size).TransformToAABB(child.TransformToVisual(ancestor)!.Value);

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}