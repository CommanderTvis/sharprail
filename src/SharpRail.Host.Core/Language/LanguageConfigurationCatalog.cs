using System.Text.Json;

using SharpRail.Host.Abstractions;

namespace SharpRail.Host.Core.Language;

internal static class LanguageConfigurationCatalog
{
    private sealed record Field(string Label, string Section, string Path, string Type = "string", string[]? Choices = null, string Help = "", string PathKind = "", bool AllowCustomChoices = false, decimal? Minimum = null);
    internal static LanguageResult Read(string id)
    {
        var server = LanguageServers.Catalog.SingleOrDefault(item => item.Id == id) ?? throw new ArgumentException("Unknown language server.");
        Field Setting(string label, string path, string type = "string", string[]? choices = null, string help = "", string pathKind = "", bool allowCustomChoices = false, decimal? minimum = null) => new(label, "settings", path, type, choices, help, pathKind, allowCustomChoices, minimum);
        Field Init(string label, string path, string type = "string", string[]? choices = null, string help = "", string pathKind = "", bool allowCustomChoices = false, decimal? minimum = null) => new(label, "initializationOptions", path, type, choices, help, pathKind, allowCustomChoices, minimum);
        var fields = id switch
        {
            "typescript" => new[] { Init("TypeScript server memory (MiB)", "maxTsServerMemory", "integer", minimum: 1), Init("TypeScript engine path", "tsserver.path", pathKind: "fileOrFolder", help: "Path to tsserver.js on the workspace host; blank uses the managed engine or project installation."), Init("Server log level", "tsserver.logVerbosity", choices: ["off", "terse", "normal", "requestTime", "verbose"]) },
            "python" => [Setting("Python interpreter", "python.pythonPath", pathKind: "file"), Setting("Additional import directories", "python.analysis.extraPaths", "lines", pathKind: "folder"), Setting("Type checking", "python.analysis.typeCheckingMode", choices: ["off", "basic", "standard", "strict"]), Setting("Diagnostic scope", "python.analysis.diagnosticMode", choices: ["openFilesOnly", "workspace"]), Setting("Read library code for types", "python.analysis.useLibraryCodeForTypes", "boolean"), Setting("Custom type stubs", "python.analysis.stubPath", pathKind: "folder")],
            "csharp" => [Init("Automatic project loading limit", "autoLoadProjects", "integer", minimum: 0, help: "Maximum projects to load automatically; 0 disables automatic loading. SharpRail defaults to 500.")],
            "java" => [Setting("Project JDK home", "java.home", pathKind: "folder"), Setting("Gradle importer JDK", "java.import.gradle.java.home", pathKind: "folder"), Setting("Additional library JARs", "java.project.referencedLibraries", "lines", pathKind: "file", help: "JAR paths and glob patterns. Advanced JSON also accepts an include/exclude/sources object."), Setting("Build configuration updates", "java.configuration.updateBuildConfiguration", choices: ["automatic", "interactive", "disabled"]), Setting("Import Maven projects", "java.import.maven.enabled", "boolean"), Setting("Import Gradle projects", "java.import.gradle.enabled", "boolean")],
            "go" => [Setting("Build flags", "gopls.buildFlags", "lines", help: "One flag per line, for example -tags=integration."), Setting("Directory filters", "gopls.directoryFilters", "lines", help: "Ordered +include / -exclude directory patterns."), Setting("Staticcheck diagnostics", "gopls.staticcheck", "boolean"), Setting("Semantic tokens", "gopls.semanticTokens", "boolean")],
            "rust" => [Setting("Cargo features", "rust-analyzer.cargo.features", "features", help: "Select all features or list individual Cargo feature names."), Setting("Check command", "rust-analyzer.check.command", choices: ["check", "clippy"], allowCustomChoices: true, help: "Cargo subcommand; check and clippy are common, but custom commands are supported."), Setting("Run checks on save", "rust-analyzer.checkOnSave", "boolean"), Setting("Enable procedural macros", "rust-analyzer.procMacro.enable", "boolean"), Setting("Run build scripts", "rust-analyzer.cargo.buildScripts.enable", "boolean")],
            "php" => [Setting("Library include directories", "intelephense.environment.includePaths", "lines", pathKind: "folder"), Setting("PHP language version", "intelephense.environment.phpVersion", help: "Semantic version, for example 8.3.0; provider supports PHP 5.3.0 and later."), Setting("Excluded file patterns", "intelephense.files.exclude", "lines"), Setting("Enable diagnostics", "intelephense.diagnostics.enable", "boolean")],
            "kotlin" => [Init("Project JDK home", "defaultSdk", pathKind: "folder", help: "JDK for analysis and Maven/Gradle import. Blank follows the workspace .sdkmanrc or SDKMAN current; the bundled server runtime is separate.")],
            "ruby" => [Init("Formatter", "formatter", choices: ["auto", "rubocop", "rubocop_internal", "syntax_tree", "none"], allowCustomChoices: true, help: "Formatter name; installed add-ons may supply additional formatters.")],
            "scala" => [Setting("Build import", "metals.autoImportBuilds", choices: ["off", "initial", "all"]), Setting("Excluded symbol packages", "metals.excludedPackages", "lines")],
            "shell" => [Setting("Workspace scan pattern", "bashIde.globPattern"), Setting("Background analysis file limit", "bashIde.backgroundAnalysisMaxFiles", "integer", minimum: 0), Setting("Include workspace symbols", "bashIde.includeAllWorkspaceSymbols", "boolean"), Setting("ShellCheck executable", "bashIde.shellcheckPath", pathKind: "file")],
            "css" => [Setting("Validate CSS", "css.validate", "boolean"), Setting("Validate SCSS", "scss.validate", "boolean"), Setting("Validate Less", "less.validate", "boolean")],
            "powershell" => [Setting("Enable script analysis", "powershell.scriptAnalysis.enable", "boolean"), Setting("Script analyzer settings file", "powershell.scriptAnalysis.settingsPath", pathKind: "file")],
            "r" => [Setting("Enable diagnostics", "r.lsp.diagnostics", "boolean"), Setting("Debug logging", "r.lsp.debug", "boolean"), Setting("Rich documentation", "r.lsp.rich_documentation", "boolean")],
            "swift" => [Init("Build configuration", "swiftPM.configuration", choices: ["debug", "release"]), Init("Swift compiler flags", "swiftPM.swiftCompilerFlags", "lines"), Init("Compilation database search paths", "compilationDatabase.searchPaths", "lines", pathKind: "folder")],
            "dart" => [Init("Analyze only projects with open files", "onlyAnalyzeProjectsWithOpenFiles", "boolean"), Setting("Excluded analysis folders", "dart.analysisExcludedFolders", "lines", pathKind: "folder"), Setting("Include dependencies in symbol searches", "dart.includeDependenciesInWorkspaceSymbols", "boolean"), Setting("Report TODO comments", "dart.showTodos", "boolean")],
            _ => []
        };
        var source = server.Package.Length > 0 ? "npm · " + server.Package : id switch
        {
            "csharp" => "NuGet · roslyn-language-server (.NET tool)",
            "go" => "Go modules · golang.org/x/tools/gopls",
            "ruby" => "RubyGems · ruby-lsp",
            "scala" => "Maven repositories · org.scalameta:metals (Coursier)",
            "r" => "CRAN · languageserver",
            "java" => "Eclipse · download.eclipse.org",
            "kotlin" or "intellij" => "JetBrains · download.jetbrains.com",
            "clangd" => "GitHub releases · clangd/clangd",
            "rust" => "GitHub releases · rust-lang/rust-analyzer",
            "powershell" => "GitHub releases · PowerShell/PowerShellEditorServices",
            "swift" => "Installed Swift toolchain · sourcekit-lsp",
            "dart" => "Installed Dart SDK · analysis server",
            _ => "Host installation"
        };
        var (website, repository) = id switch
        {
            "typescript" => ("https://github.com/typescript-language-server/typescript-language-server#readme", "https://github.com/typescript-language-server/typescript-language-server"),
            "python" => ("https://microsoft.github.io/pyright/", "https://github.com/microsoft/pyright"),
            "php" => ("https://intelephense.com/", "https://github.com/bmewburn/intelephense-docs"),
            "css" => ("https://code.visualstudio.com/docs/languages/css", "https://github.com/microsoft/vscode-css-languageservice"),
            "shell" => ("https://github.com/bash-lsp/bash-language-server#readme", "https://github.com/bash-lsp/bash-language-server"),
            "csharp" => ("https://www.nuget.org/packages/roslyn-language-server", "https://github.com/dotnet/roslyn"),
            "clangd" => ("https://clangd.llvm.org/", "https://github.com/clangd/clangd"),
            "java" => ("https://projects.eclipse.org/projects/eclipse.jdt.ls", "https://github.com/eclipse-jdtls/eclipse.jdt.ls"),
            "kotlin" => ("https://github.com/Kotlin/kotlin-lsp#readme", "https://github.com/Kotlin/kotlin-lsp"),
            "intellij" => ("https://www.jetbrains.com/help/intellij-vscode/About-instance.html#features", "https://github.com/JetBrains/intellij-lsp.nvim"),
            "ruby" => ("https://shopify.github.io/ruby-lsp/", "https://github.com/Shopify/ruby-lsp"),
            "go" => ("https://go.dev/gopls/", "https://github.com/golang/tools/tree/master/gopls"),
            "rust" => ("https://rust-analyzer.github.io/", "https://github.com/rust-lang/rust-analyzer"),
            "scala" => ("https://scalameta.org/metals/", "https://github.com/scalameta/metals"),
            "r" => ("https://cran.r-project.org/package=languageserver", "https://github.com/REditorSupport/languageserver"),
            "powershell" => ("https://github.com/PowerShell/PowerShellEditorServices#readme", "https://github.com/PowerShell/PowerShellEditorServices"),
            "swift" => ("https://www.swift.org/documentation/", "https://github.com/swiftlang/sourcekit-lsp"),
            "dart" => ("https://dart.dev/tools/analysis", "https://github.com/dart-lang/sdk/tree/main/pkg/analysis_server"),
            _ => ("", "")
        };
        var repositoryLabel = id switch { "intellij" => "Official client repository", "php" => "Documentation repository", _ => "Source repository" };
        return new("ready", "SharpRail pins managed LSP versions so these configuration controls match the server’s configuration structure. Custom executables and servers on PATH may use another version. SDK-bundled servers follow the installed toolchain. Unset fields retain server defaults.", id)
        {
            Configuration = JsonSerializer.Serialize(new { provider = server.Implementation, version = server.Version, source, website, repository, repositoryLabel, fields }, JsonSerializerOptions.Web)
        };
    }
}