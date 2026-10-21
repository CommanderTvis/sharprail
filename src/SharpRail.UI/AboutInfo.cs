using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpRail.UI;

/// <summary>
/// What the About screen says about this build, read from in-memory assembly attributes and the runtime only:
/// no file, process or network access, so it is safe on the UI thread.
/// </summary>
public static class AboutInfo
{
    public const string Name = "SharpRail";
    public const string Description = "A native C# and Avalonia workbench for projects, worktrees, terminals and Git";
    public const string Repository = "https://github.com/CommanderTvis/sharprail";
    public const string Upstream = "https://github.com/JetBrains/thinkrail";
    public const string License = "Derived from ThinkRail, licensed under the Apache License 2.0. Bundled components keep their own licenses.";
    public const string NoticesFile = "THIRD-PARTY-NOTICES.md";
    public const string NoticesUrl = Repository + "/blob/main/" + NoticesFile;

    /// <summary>The product version stamped by the packaging tasks, or "dev" for a build without a stamp.</summary>
    public static string Version => ParseVersion(typeof(AboutInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    public static string ParseVersion(string? informational) => string.IsNullOrWhiteSpace(informational) ? "dev" : informational.Trim();

    /// <summary>The runtime lines, label then value, in display order.</summary>
    public static IReadOnlyList<(string Label, string Value)> Runtime() =>
    [
        ("Runtime", RuntimeInformation.FrameworkDescription),
        ("OS", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
        ("Avalonia", ParseVersion(typeof(Avalonia.Application).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion).Split('+')[0]),
        ("Renderer", "Skia")
    ];

    /// <summary>Plain text for a bug report: the name and version, then the runtime lines.</summary>
    public static string Details(string version, IEnumerable<(string Label, string Value)> runtime) =>
        string.Join('\n', runtime.Select(line => $"{line.Label}: {line.Value}").Prepend($"{Name} {version}"));

    public static string Details() => Details(Version, Runtime());

    /// <summary>The shipped notices file beside the packaged app, or null; reads the file system, so call it off the UI thread.</summary>
    public static string? FindNotices()
    {
        var @base = AppContext.BaseDirectory;
        foreach (var candidate in new[]
        {
            Path.Combine(@base, "..", "Resources", NoticesFile), Path.Combine(@base, NoticesFile),
            Path.Combine(@base, "..", NoticesFile), Path.Combine(@base, "..", "..", "share", "doc", "sharprail", NoticesFile)
        })
            if (File.Exists(candidate)) return Path.GetFullPath(candidate);
        return null;
    }
}