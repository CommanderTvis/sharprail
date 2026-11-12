namespace SharpRail.Plugins.ClaudeCode.Host;

/// <summary>
/// The launch flags that add context layers to a Claude session, read off its process command line. A process listing
/// has already consumed the shell's quoting, so a path with spaces arrives as several words, and the shortest run of
/// words that names a file is taken.
/// </summary>
internal static class SessionContext
{
    private const string AppendFileFlag = "--append-system-prompt-file";

    public static IReadOnlyList<string> AppendedPromptFiles(string command, string cwd)
    {
        var words = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var files = new List<string>();
        for (var index = 0; index < words.Length; index++)
        {
            var word = words[index];
            string? head = word == AppendFileFlag ? "" : word.StartsWith(AppendFileFlag + "=", StringComparison.Ordinal) ? word[(AppendFileFlag.Length + 1)..] : null;
            if (head is null) continue;
            var run = new List<string>();
            if (head.Length > 0) run.Add(head);
            for (var next = index + 1; next <= words.Length; next++)
            {
                if (run.Count > 0)
                {
                    var candidate = Path.GetFullPath(string.Join(' ', run), cwd);
                    if (File.Exists(candidate))
                    {
                        if (!files.Contains(candidate)) files.Add(candidate);
                        break;
                    }
                }
                if (next >= words.Length || words[next].StartsWith('-')) break;
                run.Add(words[next]);
            }
        }
        return files;
    }
}