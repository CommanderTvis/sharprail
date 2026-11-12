using System.Security.Cryptography;
using System.Text;

using SharpRail.Host.Abstractions;

namespace SharpRail.UI.Rendering;

internal sealed record DiffTriage(Func<string, bool> IsKept, Action<string, bool, IReadOnlyList<string>> SetKept,
    Action<IReadOnlyList<string>> ReportKeys);

internal sealed partial class DiffView
{
    internal static IReadOnlyDictionary<RevertTarget, string> ContentKeys(string diff)
    {
        var (_, hunks) = Parse(diff);
        var contents = new List<(RevertTarget Target, string Hash)>();
        foreach (var hunk in hunks)
            foreach (var (index, target) in Blocks(hunk, includeWhole: true))
            {
                var removed = hunk.Lines.Skip(index).TakeWhile(line => line is ['-' or '+' or '\\', ..])
                    .Where(line => line.StartsWith('-')).Select(line => line[1..]);
                var added = hunk.Lines.Skip(index).TakeWhile(line => line is ['-' or '+' or '\\', ..])
                    .Where(line => line.StartsWith('+')).Select(line => line[1..]);
                var original = string.Join('\n', removed);
                var modified = string.Join('\n', added);
                var bytes = Encoding.UTF8.GetBytes(original.Length + ":" + original + modified.Length + ":" + modified);
                contents.Add((target, Convert.ToHexString(SHA256.HashData(bytes))));
            }
        var counts = contents.GroupBy(item => item.Hash).ToDictionary(group => group.Key, group => group.Count());
        return contents.ToDictionary(item => item.Target, item => counts[item.Hash] == 1 ? item.Hash
            : item.Hash + "#" + counts[item.Hash] + "@" + item.Target.Original!.Start);
    }
}