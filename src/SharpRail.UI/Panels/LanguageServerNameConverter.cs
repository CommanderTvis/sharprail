using System.Globalization;

using Avalonia.Data.Converters;

namespace SharpRail.UI.Panels;

internal sealed class LanguageServerNameConverter : IValueConverter
{
    internal static string Display(string id) => id switch
    {
        "typescript" => "JavaScript / TypeScript (TypeScript LS)",
        "python" => "Python (Pyright)",
        "php" => "PHP (Intelephense)",
        "css" => "CSS (VS Code)",
        "shell" => "Shell (Bash LS)",
        "csharp" => "C# (Roslyn)",
        "clangd" => "C / C++ (clangd)",
        "java" => "Java (Eclipse JDT)",
        "kotlin" => "Kotlin (Kotlin LS)",
        "intellij" => "Java / Kotlin (IntelliJ)",
        "ruby" => "Ruby (Ruby LS)",
        "go" => "Go (gopls)",
        "rust" => "Rust (rust-analyzer)",
        "scala" => "Scala (Metals)",
        "r" => "R (languageserver)",
        "powershell" => "PowerShell (Editor Services)",
        "swift" => "Swift (SourceKit-LS)",
        "dart" => "Dart (Analysis Server)",
        _ => id
    };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is string id ? Display(id) : null;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}