using System.Reflection;
using System.Text.RegularExpressions;

namespace SharpRail.Checks;

/// <summary>
/// Holds an enrolled spec to its assembly: the identifiers under its "Public surface" heading must be
/// exactly the assembly's public top-level types. A spec enrols with the <c>public-surface-checked</c> tag.
/// </summary>
internal static partial class SurfaceChecks
{
    private const string Tag = "public-surface-checked", Heading = "## Public surface";

    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }

    internal static void Run(string root)
    {
        Fixtures(Path.Combine(root, "public-surface"));
        if (BoundaryChecks.Checkout() is not { } checkout) { Console.WriteLine("SKIP public surface: no source checkout"); return; }
        var (enrolled, violations) = Scan(checkout);
        Require(violations.Count == 0, "Declared public surface differs from the assembly:\n" + string.Join('\n', violations));
        Require(enrolled > 0, "No spec is enrolled in the public-surface check.");
        Console.WriteLine($"PASS public surface: {enrolled} enrolled spec(s) match their assemblies");
    }

    internal static (int Enrolled, List<string> Violations) Scan(string root)
    {
        var violations = new List<string>();
        var enrolled = 0;
        foreach (var spec in new[] { "src", "tests", "scripts" }.Select(area => Path.Combine(root, area)).Where(Directory.Exists)
            .SelectMany(area => Directory.EnumerateFiles(area, "*.md", SearchOption.AllDirectories)).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(spec).ReplaceLineEndings("\n");
            if (!text.StartsWith("---\n", StringComparison.Ordinal) || text.IndexOf("\n---", 4, StringComparison.Ordinal) is not (> 0 and var end)) continue;
            if (!text[..end].Split('\n').Any(line => line.StartsWith("tags:", StringComparison.Ordinal) && Tags().IsMatch(line))) continue;
            enrolled++;
            var name = Path.GetRelativePath(root, spec);
            var project = Directory.EnumerateFiles(Path.GetDirectoryName(spec)!, "*.csproj").SingleOrDefault();
            var section = text.IndexOf("\n" + Heading + "\n", StringComparison.Ordinal);
            if (project is null) { violations.Add($"{name}: enrolled, but no project sits beside it"); continue; }
            if (section < 0) { violations.Add($"{name}: enrolled, but has no \"{Heading}\" section"); continue; }
            var body = text[(section + Heading.Length + 2)..];
            if (body.IndexOf("\n## ", StringComparison.Ordinal) is >= 0 and var next) body = body[..next];
            var declared = Backticked().Matches(body).Select(match => match.Groups[1].Value).ToArray();
            if (declared.Length == 0) { violations.Add($"{name}: enrolled, but declares no identifiers"); continue; }
            foreach (var token in declared.Where(token => !Identifier().IsMatch(token))) violations.Add($"{name}: `{token}` is not a bare identifier");
            Assembly assembly;
            try { assembly = Assembly.Load(Path.GetFileNameWithoutExtension(project)); }
            catch (Exception error) when (error is FileNotFoundException or FileLoadException or BadImageFormatException)
            {
                violations.Add($"{name}: assembly {Path.GetFileNameWithoutExtension(project)} cannot be loaded");
                continue;
            }
            var exported = assembly.GetExportedTypes().Where(type => !type.IsNested).Select(type => type.Name.Split('`')[0])
                .Where(name => Identifier().IsMatch(name)).ToHashSet(StringComparer.Ordinal);
            foreach (var missing in exported.Except(declared).Order(StringComparer.Ordinal)) violations.Add($"{name}: public `{missing}` is not declared");
            foreach (var stale in declared.Where(token => Identifier().IsMatch(token)).Except(exported).Order(StringComparer.Ordinal)) violations.Add($"{name}: declared `{stale}` is not public in {assembly.GetName().Name}");
            foreach (var repeated in declared.GroupBy(token => token).Where(group => group.Count() > 1)) violations.Add($"{name}: `{repeated.Key}` is declared twice");
        }
        return (enrolled, violations);
    }

    private static void Fixtures(string directory)
    {
        var count = 0;
        var real = typeof(SharpRail.Host.Abstractions.IWorkspaceHost).Assembly.GetExportedTypes().Where(type => !type.IsNested)
            .Select(type => type.Name.Split('`')[0]).Distinct().Order(StringComparer.Ordinal).ToArray();
        (int Enrolled, List<string> Violations) Spec(string tags, string body, string project = "SharpRail.Host.Abstractions")
        {
            var root = Path.Combine(directory, "case-" + ++count);
            var module = Path.Combine(root, "src", project);
            Directory.CreateDirectory(module);
            File.WriteAllText(Path.Combine(module, project + ".csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            File.WriteAllText(Path.Combine(module, "SPEC.md"), $"---\nid: fixture\ntags: [{tags}]\n---\n\n# Fixture\n\n{body}\n\n## Later\n\n`NotPartOfTheSurface`\n");
            return Scan(root);
        }
        string List(IEnumerable<string> names) => Heading + "\n\n" + string.Join(", ", names.Select(name => $"`{name}`"));
        void Fails((int Enrolled, List<string> Violations) result, string expected)
            => Require(result.Enrolled == 1 && result.Violations.Count == 1 && result.Violations[0].Contains(expected, StringComparison.Ordinal),
                $"Expected one violation containing '{expected}', found: {string.Join("; ", result.Violations)}");

        Require(Spec("host, " + Tag, List(real)) is { Enrolled: 1, Violations.Count: 0 }, "A spec declaring exactly the public types must pass.");
        Require(Spec("host", "No list here.") is { Enrolled: 0, Violations.Count: 0 }, "An unenrolled spec must stay descriptive.");
        Fails(Spec(Tag, List(real.Skip(1))), $"public `{real[0]}` is not declared");
        Fails(Spec(Tag, List(real.Append("RemovedType"))), "declared `RemovedType` is not public");
        Fails(Spec(Tag, List(real.Append(real[0]))), "is declared twice");
        Fails(Spec(Tag, "The surface is described in prose."), "has no \"" + Heading + "\" section");
        Fails(Spec(Tag, Heading + "\n\nNothing listed."), "declares no identifiers");
        Fails(Spec(Tag, List(real) + ", `IWorkspaceHost.GetWorkspaceAsync`"), "is not a bare identifier");
        Fails(Spec(Tag, List(real), "SharpRail.Host.Missing"), "cannot be loaded");
        Console.WriteLine($"PASS public-surface fixtures: {count} specs through the repository scan");
    }

    [GeneratedRegex(@"[\[,\s]public-surface-checked[\],\s]")] private static partial Regex Tags();
    [GeneratedRegex("`([^`\n]+)`")] private static partial Regex Backticked();
    [GeneratedRegex(@"^[A-Za-z_]\w*$")] private static partial Regex Identifier();
}