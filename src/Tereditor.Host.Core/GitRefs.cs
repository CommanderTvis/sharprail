namespace SharpRail.Host.Core;

/// <summary>The shape every ref passes before it reaches Git, whether a client sent it or the repository named it.</summary>
/// <remarks>
/// <c>check-ref-format</c>'s rules decided in process, so a door costs no child: no option shape, range,
/// reflog or revision syntax, control character, glob or empty, dotted or <c>.lock</c> component.
/// </remarks>
internal static class GitRefs
{
    internal static bool IsSafe(string reference)
    {
        if (reference.Length == 0 || reference[0] == '-' || reference == "@") return false;
        if (reference.Contains("..", StringComparison.Ordinal) || reference.Contains("@{", StringComparison.Ordinal)) return false;
        if (reference[^1] is '.' or '/') return false;
        foreach (var value in reference)
            if (value <= ' ' || value == '\u007f' || value is '~' or '^' or ':' or '?' or '*' or '[' or '\\') return false;
        foreach (var component in reference.Split('/'))
            if (component.Length == 0 || component[0] == '.' || component.EndsWith(".lock", StringComparison.Ordinal)) return false;
        return true;
    }

    internal static string Require(string reference)
    {
        if (!IsSafe(reference)) throw new ArgumentException("Not a usable git ref: " + reference);
        return reference;
    }
}