using Avalonia.Controls;

namespace SharpRail.UI.Panels;

internal static class LanguageSourceIcon
{
    internal static Control Create(string source)
    {
        var asset = source.Split('·', 2)[0].Trim() switch
        {
            "npm registry" => "npm",
            "NuGet" => "nuget",
            "Go modules" => "go",
            "RubyGems" => "ruby",
            "Maven repositories" => "maven",
            "CRAN" => "r",
            "Eclipse" => "java",
            "JetBrains" => "kotlin",
            "GitHub releases" => "folder-github",
            "Installed Swift toolchain" => "swift",
            "Installed Dart SDK" => "dart",
            _ => ""
        };
        var icon = asset.Length > 0 ? WorkbenchWindow.CoreFileIcon(asset, Ui.Muted, 16) : Ui.Icon("box", Ui.Muted, 16);
        ToolTip.SetTip(icon, source);
        return icon;
    }
}