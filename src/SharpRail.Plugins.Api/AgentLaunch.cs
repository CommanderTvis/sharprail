using System.Text;
using System.Text.RegularExpressions;

namespace SharpRail.Plugins.Api;

/// <summary>
/// What one plugin adds to another plugin's agent launch: a command that wraps or replaces the agent's executable,
/// environment variables and extra arguments. Contributions are collected with <c>AgentLaunch</c> on either context
/// and applied by the agent's own plugin with <see cref="AgentLaunchComposition.Apply"/> wherever it builds a
/// command line, so every start, revival and resume of that agent sees the same result.
/// </summary>
public sealed record AgentLaunchContribution
{
    /// <summary>
    /// Words placed before the agent's arguments. Each is one literal word and is quoted for the shell; none is
    /// interpreted. With <see cref="ReplacesExecutable"/> they stand in for the agent's executable.
    /// </summary>
    public IReadOnlyList<string> Wrapper { get; init; } = [];

    /// <summary>
    /// Whether <see cref="Wrapper"/> replaces the agent's executable (a launcher that starts the agent itself, such as
    /// <c>tool run agent --</c>) rather than prefixing it (<c>tool agent</c>).
    /// </summary>
    public bool ReplacesExecutable { get; init; }

    /// <summary>Variables set for this command only. Names outside <c>[A-Za-z_][A-Za-z0-9_]*</c> and the <c>SHARPRAIL_</c> and <c>THINKRAIL_</c> prefixes are ignored; not emitted for Windows shells.</summary>
    public IReadOnlyDictionary<string, string> Environment { get; init; } = new Dictionary<string, string>();

    /// <summary>Literal words placed directly after the executable or its replacement, before the agent's own arguments.</summary>
    public IReadOnlyList<string> Arguments { get; init; } = [];

    /// <summary>
    /// Who the agent runs as under this contribution, when it is not the agent's own account (a governed launch
    /// through a proxy or an organization's credits). The agent's plugin shows it in place of the vendor account and
    /// does not read the vendor account while one is present. Only host-side contributions carry a source, because
    /// reading it is I/O.
    /// </summary>
    public AgentAccountSource? Account { get; init; }
}

/// <summary>
/// The account an agent launch runs under when a contribution governs it. <see cref="Read"/> runs on the host and
/// returns the current reading; it never throws for an unavailable source, it reports the reason in
/// <see cref="AgentGovernedAccount.Problem"/>.
/// </summary>
/// <param name="Name">The source's name shown to the user, for example <c>Central</c>.</param>
/// <param name="Read">Reads the account now.</param>
public sealed record AgentAccountSource(string Name, Func<CancellationToken, ValueTask<AgentGovernedAccount>> Read);

/// <summary>One reading of a governed account. Credits are in the source's own unit.</summary>
/// <param name="Source">The source's name.</param>
/// <param name="Identity">Who is signed in, such as an email address.</param>
/// <param name="Plan">The plan or license name.</param>
/// <param name="Remaining">Credits left of the recurring quota, when known.</param>
/// <param name="Total">The recurring quota.</param>
/// <param name="TopUp">Credits left of the top-up quota; zero when there is none.</param>
/// <param name="RefillNext">When the quota refills, Unix milliseconds.</param>
/// <param name="Problem">A sentence the user can act on when the source cannot be read (not installed, signed out, limit read failed); then the numbers are absent.</param>
public sealed record AgentGovernedAccount(string Source, string? Identity = null, string? Plan = null, double? Remaining = null, double? Total = null,
    double TopUp = 0, long? RefillNext = null, string? Problem = null);

/// <summary>Applies <see cref="AgentLaunchContribution"/> values to a command line that starts an agent.</summary>
public static partial class AgentLaunchComposition
{
    [GeneratedRegex(@"^[A-Za-z0-9_./~+=:@,-]+$")]
    private static partial Regex SafeWord();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*$")]
    private static partial Regex VariableName();

    /// <summary>
    /// Rewrites the start of <paramref name="commandLine"/>, which must begin with the agent's executable word, using
    /// <paramref name="contributions"/> in the order given (the host orders them by plugin activation). Rules: a
    /// command whose executable is not <paramref name="executable"/> (a custom command line) is returned unchanged;
    /// wrappers that do not replace the executable go first, outermost first; only the first contribution that
    /// replaces the executable applies, innermost, and later replacers are ignored; arguments follow in order;
    /// the first contribution to set a variable wins. A contribution with a word that cannot be quoted safely for the
    /// target shell is skipped whole.
    /// </summary>
    /// <param name="commandLine">The command line, starting with the executable word.</param>
    /// <param name="executable">The agent's executable name without directory or <c>.exe</c>.</param>
    /// <param name="contributions">The contributions, outermost first.</param>
    /// <param name="windows">Whether the line is read by a Windows shell.</param>
    /// <returns>The rewritten command line.</returns>
    public static string Apply(string commandLine, string executable, IReadOnlyList<AgentLaunchContribution> contributions, bool windows = false)
    {
        var line = commandLine.TrimStart();
        if (contributions.Count == 0 || line.Length == 0) return commandLine;
        var end = WordEnd(line);
        if (end < 0 || Name(line[..end]) != executable) return commandLine;
        var usable = contributions.Where(contribution => Quotable(contribution, windows)).ToArray();
        if (usable.Length == 0) return commandLine;
        var words = new List<string>();
        foreach (var contribution in usable.Where(contribution => !contribution.ReplacesExecutable)) words.AddRange(contribution.Wrapper.Select(word => Quote(word, windows)!));
        var replacer = usable.FirstOrDefault(contribution => contribution.ReplacesExecutable && contribution.Wrapper.Count > 0);
        if (replacer is null) words.Add(line[..end]);
        else words.AddRange(replacer.Wrapper.Select(word => Quote(word, windows)!));
        foreach (var contribution in usable) words.AddRange(contribution.Arguments.Select(word => Quote(word, windows)!));
        var prefix = new StringBuilder();
        if (!windows)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, value) in usable.SelectMany(contribution => contribution.Environment))
            {
                if (!VariableName().IsMatch(name) || name.StartsWith("SHARPRAIL_", StringComparison.Ordinal) || name.StartsWith("THINKRAIL_", StringComparison.Ordinal) || !seen.Add(name)) continue;
                prefix.Append(name).Append('=').Append(Quote(value, false)).Append(' ');
            }
        }
        return prefix + string.Join(' ', words) + line[end..];
    }

    private static bool Quotable(AgentLaunchContribution contribution, bool windows) =>
        contribution.Wrapper.Concat(contribution.Arguments).All(word => word.Length > 0 && Quote(word, windows) is not null);

    private static string? Quote(string word, bool windows)
    {
        if (SafeWord().IsMatch(word)) return word;
        if (word.Any(char.IsControl)) return null;
        if (!windows) return "'" + word.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return word.IndexOfAny(['"', '%', '$', '`', '^', '&', '|', '<', '>']) >= 0 ? null : "\"" + word + "\"";
    }

    private static int WordEnd(string line)
    {
        if (line[0] is '\'' or '"')
        {
            var close = line.IndexOf(line[0], 1);
            return close < 0 ? -1 : close + 1;
        }
        var index = 0;
        while (index < line.Length && !char.IsWhiteSpace(line[index])) index++;
        return index;
    }

    private static string Name(string word)
    {
        var bare = word.Trim('\'', '"');
        var name = bare[(bare.LastIndexOfAny(['/', '\\']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}