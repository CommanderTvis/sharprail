using System.Text.RegularExpressions;

using SharpRail.Plugins.UI.Kit;

namespace SharpRail.Checks;

/// <summary>
/// Menu icon consistency (src/SharpRail.Plugins.UI.Kit/ActionIcons/SPEC.md): the registry maps each meaning to one distinct,
/// shipped icon, and no menu source assigns a menu item's icon from a raw name instead of the registry.
/// </summary>
internal static partial class ActionIconChecks
{
    [GeneratedRegex(@"(?<![A-Za-z])Icon\s*=(?!>)\s*(?<rhs>[^;,}\n]*)")]
    private static partial Regex Assignment();

    internal static void Run()
    {
        var members = Enum.GetValues<ActionIcon>();
        var assets = ActionIcons.All.Select(entry => entry.Asset).ToArray();
        Require(ActionIcons.All.Select(entry => entry.Icon).Order().SequenceEqual(members), "Every action icon semantic needs exactly one registry entry.");
        Require(assets.Distinct().Count() == assets.Length, "Two action icon semantics share an asset: " + string.Join(", ", assets.GroupBy(asset => asset).Where(group => group.Count() > 1).Select(group => group.Key)));
        if (BoundaryChecks.Checkout() is not { } checkout) { Console.WriteLine("SKIP action icon source scan: no source checkout"); return; }
        foreach (var asset in assets)
            Require(File.Exists(Path.Combine(checkout, "src/SharpRail.Plugins.UI.Kit/Assets/Icons", asset + ".svg")), $"Action icon asset '{asset}' is not shipped.");
        var violations = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(checkout, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(checkout, file).Replace('\\', '/');
            if (relative.Contains("/obj/") || relative.Contains("/bin/") || relative.Contains("/Generated/")) continue;
            var text = BoundaryChecks.Code(File.ReadAllText(file));
            if (!text.Contains("MenuItem", StringComparison.Ordinal) && !text.Contains("Ui.Menu(", StringComparison.Ordinal)) continue;
            foreach (Match match in Assignment().Matches(text))
            {
                var rhs = match.Groups["rhs"].Value.Trim();
                if (rhs.Contains("Ui.Icon(", StringComparison.Ordinal) || rhs.StartsWith('"'))
                    violations.Add($"{relative}: Icon = {rhs} (use an ActionIcon)");
            }
        }
        Require(violations.Count == 0, "Menu icons bypass the registry:\n" + string.Join('\n', violations));
        Console.WriteLine("PASS action icons: one distinct shipped icon per semantic, and no menu source assigns a raw icon name");
    }

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}