namespace SharpRail.Host.Core.Language;

internal static class ProjectJava
{
    internal static (string Path, string Source) Resolve(string workspace, string explicitPath)
    {
        if (explicitPath.Length > 0) return (explicitPath, "workspace project JDK setting");
        var sdkman = Environment.GetEnvironmentVariable("SDKMAN_CANDIDATES_DIR");
        if (string.IsNullOrWhiteSpace(sdkman))
        {
            var home = Environment.GetEnvironmentVariable("SDKMAN_DIR");
            sdkman = home is { Length: > 0 } ? System.IO.Path.Combine(home, "candidates")
                : OperatingSystem.IsMacOS() && Directory.Exists("/opt/homebrew/opt/sdkman-cli/libexec/candidates")
                    ? "/opt/homebrew/opt/sdkman-cli/libexec/candidates"
                    : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sdkman", "candidates");
        }
        return Resolve(workspace, explicitPath, sdkman, Environment.GetEnvironmentVariable("JAVA_HOME"));
    }

    internal static (string Path, string Source) Resolve(string workspace, string explicitPath, string sdkman, string? environment)
    {
        if (explicitPath.Length > 0) return (explicitPath, "workspace project JDK setting");
        var sdkFile = System.IO.Path.Combine(workspace, ".sdkmanrc");
        if (File.Exists(sdkFile))
        {
            var selected = File.ReadLines(sdkFile).Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("java=", StringComparison.Ordinal))?[5..].Trim();
            if (selected is { Length: > 0 })
            {
                if (selected.IndexOfAny(['/', '\\']) >= 0 || selected is "." or "..") throw new IOException("Invalid java candidate in .sdkmanrc.");
                var candidate = System.IO.Path.Combine(sdkman, "java", selected);
                if (!Valid(candidate)) throw new IOException("Project .sdkmanrc requires an unavailable JDK: " + candidate);
                return (candidate, "workspace .sdkmanrc");
            }
        }
        if (environment is { Length: > 0 } && Valid(environment) && LanguageProjectModel.Contains(System.IO.Path.Combine(sdkman, "java"), environment))
            return (environment, "host JAVA_HOME selecting a SDKMAN JDK");
        var current = System.IO.Path.Combine(sdkman, "java", "current");
        if (Valid(current)) return (current, "SDKMAN current JDK");
        if (environment is { Length: > 0 } && Valid(environment) && !environment.Contains("/opt/homebrew/", StringComparison.Ordinal) && !environment.Contains("kotlin-lsp", StringComparison.OrdinalIgnoreCase))
            return (environment, "host JAVA_HOME");
        return ("", "No project JDK configured; set Project JDK in workspace LSP settings.");
    }

    private static bool Valid(string path) => File.Exists(System.IO.Path.Combine(path, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java"));
}