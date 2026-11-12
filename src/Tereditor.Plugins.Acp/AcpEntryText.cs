namespace Tereditor.Plugins.Acp;

/// <summary>How the settings page writes and reads an agent's arguments and environment as plain text.</summary>
public static class AcpEntryText
{
    /// <summary>One <c>NAME=value</c> per line; blank lines and lines without a name are dropped.</summary>
    public static IReadOnlyDictionary<string, string> ParseEnvironment(string text)
    {
        var result = new Dictionary<string, string>();
        foreach (var line in text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var at = line.IndexOf('=');
            if (at > 0) result[line[..at].Trim()] = line[(at + 1)..];
        }
        return result;
    }

    public static string FormatEnvironment(IReadOnlyDictionary<string, string> environment) =>
        string.Join('\n', environment.Select(pair => pair.Key + "=" + pair.Value));

    /// <summary>Splits a command line into arguments; double or single quotes keep a space inside one argument.</summary>
    public static IReadOnlyList<string> SplitArguments(string text)
    {
        var arguments = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        var started = false;
        foreach (var c in text)
        {
            if (quote is { } open)
            {
                if (c == open) quote = null;
                else current.Append(c);
            }
            else if (c is '"' or '\'') { quote = c; started = true; }
            else if (char.IsWhiteSpace(c))
            {
                if (started || current.Length > 0) arguments.Add(current.ToString());
                current.Clear();
                started = false;
            }
            else current.Append(c);
        }
        if (started || current.Length > 0) arguments.Add(current.ToString());
        return arguments;
    }

    /// <summary>The line the user sees for an entry: the command and its arguments, quoting arguments that hold spaces.</summary>
    public static string CommandLine(AcpAgentEntry entry) =>
        string.Join(' ', new[] { entry.LaunchCommand() }.Concat(entry.LaunchArgs()).Select(part => part.Contains(' ') ? "\"" + part + "\"" : part));

    /// <summary>One argument per line; blank lines are dropped and a trailing carriage return is not part of the argument.</summary>
    public static IReadOnlyList<string> ParseLines(string text) =>
        [.. text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Trim().Length > 0)];

    /// <summary>A stable id for a custom agent from its name.</summary>
    public static string IdFor(string name)
    {
        var slug = new string([.. name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')]).Trim('-');
        return "custom-" + (slug.Length > 0 ? slug : Guid.NewGuid().ToString("N")[..8]);
    }
}