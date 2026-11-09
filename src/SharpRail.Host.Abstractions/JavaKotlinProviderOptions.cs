namespace SharpRail.Host.Abstractions;

/// <summary>Which Java / Kotlin provider choices exist while their plugins are enabled, and which one is in effect.</summary>
public static class JavaKotlinProviderOptions
{
    public const string Standard = "standard";

    /// <summary>The offered choices: <c>standard</c> only while every server the replacing ones replace is present, then each replacing server present.</summary>
    public static string[] Available(IEnumerable<string> presentServers, IEnumerable<(string Id, IReadOnlyList<string> Replaces)> replacing)
    {
        var present = presentServers.ToHashSet();
        var offered = replacing.Where(server => present.Contains(server.Id)).ToArray();
        var replaced = offered.SelectMany(server => server.Replaces).Distinct().ToArray();
        var standard = replaced.Length > 0 && replaced.All(present.Contains);
        return [.. standard ? [Standard] : Array.Empty<string>(), .. offered.Select(server => server.Id)];
    }

    /// <summary>The saved choice when it is offered, else the first offered one (the saved value is never rewritten); empty when none is offered.</summary>
    public static string Effective(string saved, string[] options) => options.Contains(saved) ? saved : options.FirstOrDefault() ?? "";
}
