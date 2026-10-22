namespace SharpRail.Plugins.Api;

/// <summary>
/// The categories builtin plugins use, and the order Settings › Plugins lists them in: one per language (all the plugins of
/// an ecosystem together, such as a language server and the plugin that activates its environments), then the tool groups.
/// </summary>
public static class PluginCategories
{
    /// <summary>C and C++: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Cpp = "C & C++";

    /// <summary>C#: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string CSharp = "C#";

    /// <summary>CSS, SCSS and Less: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Css = "CSS";

    /// <summary>Dart and Flutter: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Dart = "Dart";

    /// <summary>Go: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Go = "Go";

    /// <summary>JavaScript and TypeScript: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string JavaScript = "JavaScript & TypeScript";

    /// <summary>Java, Kotlin and Scala: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Jvm = "JVM";

    /// <summary>PHP: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Php = "PHP";

    /// <summary>PowerShell: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string PowerShell = "PowerShell";

    /// <summary>Python: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Python = "Python";

    /// <summary>R: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string R = "R";

    /// <summary>Ruby: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Ruby = "Ruby";

    /// <summary>Rust: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Rust = "Rust";

    /// <summary>Shell scripts: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Shell = "Shell";

    /// <summary>Swift: its language server and the plugins that activate its environments, runtimes and toolchains.</summary>
    public const string Swift = "Swift";

    /// <summary>Environment managers that shape every shell, such as mise and direnv.</summary>
    public const string Environment = "Environment";

    /// <summary>AI coding agents and the context they use.</summary>
    public const string Agents = "Agents";

    /// <summary>Version control views.</summary>
    public const string VersionControl = "Version Control";

    /// <summary>Specifications, diagrams and document viewers.</summary>
    public const string Documents = "Specs & Documents";

    /// <summary>Connections to other services and machines.</summary>
    public const string Integrations = "Integrations";

    /// <summary>Where a plugin with no category is listed, after every other group.</summary>
    public const string Other = "Other";

    /// <summary>The builtin categories in listing order; any other category follows alphabetically, then <see cref="Other"/>.</summary>
    public static IReadOnlyList<string> Order { get; } =
        [Cpp, CSharp, Css, Dart, Go, JavaScript, Jvm, Php, PowerShell, Python, R, Ruby, Rust, Shell, Swift, Environment, Agents, VersionControl, Documents, Integrations];
}