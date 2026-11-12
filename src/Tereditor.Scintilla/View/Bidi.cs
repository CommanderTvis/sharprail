using System.Text;

using Avalonia.Media.TextFormatting.Unicode;

namespace SharpRail.Scintilla.View;

/// <summary>A run of one embedding level, in byte offsets of the line's UTF-8 text.</summary>
internal readonly record struct BidiRun(int Start, int End, bool RightToLeft);

/// <summary>
/// Unicode Bidirectional Algorithm (UAX #9) over one line, returning level runs in visual order.
/// Character data comes from Avalonia's public Codepoint.BiDiClass / PairedBracketType; replaces SheenBidi.
/// </summary>
internal static class Bidi
{
    private const int MaxDepth = 125, MaxBrackets = 63;

    /// <summary>Level runs in visual order; line-end bytes stay last in the paragraph direction.</summary>
    public static IReadOnlyList<BidiRun> VisualRuns(ReadOnlySpan<byte> utf8, ScintillaTextDirection direction)
    {
        var body = utf8.IndexOfAny((byte)'\r', (byte)'\n');
        if (body < 0) body = utf8.Length;
        var rtl = direction == ScintillaTextDirection.RightToLeft;
        var runs = new List<BidiRun>();
        // Only RTL characters or an RTL base can reorder; ASCII has neither.
        if (!Ascii.IsValid(utf8[..body]) || rtl)
        {
            var text = Decode(utf8[..body]);
            // Paragraph separators such as U+2029 split the line into consecutive paragraphs.
            for (var start = 0; start < text.Count;)
            {
                var end = start;
                while (end < text.Count && text.Classes[end++] != BidiClass.ParagraphSeparator) { }
                var level = new Paragraph(text, start, end, direction).Resolve(runs);
                if (start == 0) rtl = (level & 1) != 0;
                start = end;
            }
        }
        else if (body > 0) runs.Add(new(0, body, false));
        if (body < utf8.Length) runs.Add(new(body, utf8.Length, rtl));
        return runs;
    }

    private sealed class Text(int capacity)
    {
        public readonly List<int> Codepoints = new(capacity);
        public readonly List<BidiClass> Classes = new(capacity);
        // Byte offset of each code point, plus the end.
        public readonly List<int> Offsets = new(capacity + 1);
        public int Count => Codepoints.Count;
    }

    private static Text Decode(ReadOnlySpan<byte> utf8)
    {
        var text = new Text(utf8.Length);
        for (var offset = 0; offset < utf8.Length;)
        {
            Rune.DecodeFromUtf8(utf8[offset..], out var rune, out var consumed);
            text.Codepoints.Add(rune.Value);
            text.Classes.Add(new Codepoint((uint)rune.Value).BiDiClass);
            text.Offsets.Add(offset);
            offset += Math.Max(consumed, 1);
        }
        text.Offsets.Add(utf8.Length);
        return text;
    }

    private sealed class Paragraph
    {
        private readonly Text text;
        private readonly int start, count;
        private readonly BidiClass[] original, types;
        private readonly byte[] levels;
        private readonly int[] matchingPdi;
        private readonly bool[] matchedPdi;
        private readonly byte baseLevel;

        public Paragraph(Text text, int start, int end, ScintillaTextDirection direction)
        {
            this.text = text; this.start = start; count = end - start;
            original = new BidiClass[count];
            for (var i = 0; i < count; i++) original[i] = text.Classes[start + i];
            types = (BidiClass[])original.Clone();
            levels = new byte[count];
            matchingPdi = new int[count];
            matchedPdi = new bool[count];
            MatchIsolates();
            // P2-P3: the first strong character outside isolates, defaulting to LTR.
            baseLevel = direction switch
            {
                ScintillaTextDirection.LeftToRight => 0,
                ScintillaTextDirection.RightToLeft => 1,
                _ => FirstStrong(0, count) == 1 ? (byte)1 : (byte)0,
            };
        }

        private static bool IsIsolateInitiator(BidiClass type) =>
            type is BidiClass.LeftToRightIsolate or BidiClass.RightToLeftIsolate or BidiClass.FirstStrongIsolate;

        // X9 removes embeddings, overrides, PDF and boundary neutrals.
        private static bool IsRemoved(BidiClass type) => type is BidiClass.LeftToRightEmbedding or BidiClass.RightToLeftEmbedding
            or BidiClass.LeftToRightOverride or BidiClass.RightToLeftOverride or BidiClass.PopDirectionalFormat or BidiClass.BoundaryNeutral;

        // BD9: isolate initiators and PDIs match by depth.
        private void MatchIsolates()
        {
            Array.Fill(matchingPdi, -1);
            var open = new Stack<int>();
            for (var i = 0; i < count; i++)
            {
                if (IsIsolateInitiator(original[i])) open.Push(i);
                else if (original[i] == BidiClass.PopDirectionalIsolate && open.Count > 0)
                {
                    matchingPdi[open.Pop()] = i;
                    matchedPdi[i] = true;
                }
            }
        }

        // Direction of the first strong character in [from, to), skipping isolates: 0, 1, or -1 when none.
        private int FirstStrong(int from, int to)
        {
            for (var i = from; i < to; i++)
            {
                var type = original[i];
                if (type == BidiClass.LeftToRight) return 0;
                if (type is BidiClass.RightToLeft or BidiClass.ArabicLetter) return 1;
                if (IsIsolateInitiator(type))
                {
                    if (matchingPdi[i] < 0) return -1;
                    i = matchingPdi[i];
                }
            }
            return -1;
        }

        /// <summary>Appends this paragraph's runs in visual order and returns its base level.</summary>
        public byte Resolve(List<BidiRun> runs)
        {
            Explicit();
            foreach (var sequence in Sequences()) ResolveSequence(sequence);
            Whitespace();
            Reorder(runs);
            return baseLevel;
        }

        private record struct Entry(byte Level, BidiClass Override, bool Isolate);

        // X1-X8.
        private void Explicit()
        {
            var stack = new Stack<Entry>();
            stack.Push(new(baseLevel, BidiClass.OtherNeutral, false));
            int overflowIsolates = 0, overflowEmbeddings = 0, validIsolates = 0;
            for (var i = 0; i < count; i++)
            {
                var type = original[i];
                var top = stack.Peek();
                switch (type)
                {
                    case BidiClass.RightToLeftEmbedding or BidiClass.LeftToRightEmbedding
                        or BidiClass.RightToLeftOverride or BidiClass.LeftToRightOverride:
                        {
                            levels[i] = top.Level;
                            var rtl = type is BidiClass.RightToLeftEmbedding or BidiClass.RightToLeftOverride;
                            var level = (byte)(rtl ? (top.Level + 1) | 1 : (top.Level + 2) & ~1);
                            if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                            {
                                var over = type switch
                                {
                                    BidiClass.RightToLeftOverride => BidiClass.RightToLeft,
                                    BidiClass.LeftToRightOverride => BidiClass.LeftToRight,
                                    _ => BidiClass.OtherNeutral,
                                };
                                stack.Push(new(level, over, false));
                            }
                            else if (overflowIsolates == 0) overflowEmbeddings++;
                            break;
                        }
                    case BidiClass.RightToLeftIsolate or BidiClass.LeftToRightIsolate or BidiClass.FirstStrongIsolate:
                        {
                            levels[i] = top.Level;
                            if (top.Override != BidiClass.OtherNeutral) types[i] = top.Override;
                            var rtl = type == BidiClass.RightToLeftIsolate
                                || type == BidiClass.FirstStrongIsolate && FirstStrong(i + 1, matchingPdi[i] < 0 ? count : matchingPdi[i]) == 1;
                            var level = (byte)(rtl ? (top.Level + 1) | 1 : (top.Level + 2) & ~1);
                            if (level <= MaxDepth && overflowIsolates == 0 && overflowEmbeddings == 0)
                            {
                                validIsolates++;
                                stack.Push(new(level, BidiClass.OtherNeutral, true));
                            }
                            else overflowIsolates++;
                            break;
                        }
                    case BidiClass.PopDirectionalIsolate:
                        if (overflowIsolates > 0) overflowIsolates--;
                        else if (validIsolates > 0)
                        {
                            overflowEmbeddings = 0;
                            while (!stack.Peek().Isolate) stack.Pop();
                            stack.Pop();
                            validIsolates--;
                        }
                        top = stack.Peek();
                        levels[i] = top.Level;
                        if (top.Override != BidiClass.OtherNeutral) types[i] = top.Override;
                        break;
                    case BidiClass.PopDirectionalFormat:
                        levels[i] = top.Level;
                        if (overflowIsolates > 0) { }
                        else if (overflowEmbeddings > 0) overflowEmbeddings--;
                        else if (!top.Isolate && stack.Count >= 2) stack.Pop();
                        break;
                    case BidiClass.ParagraphSeparator:
                        levels[i] = baseLevel;
                        break;
                    case BidiClass.BoundaryNeutral:
                        levels[i] = top.Level;
                        break;
                    default:
                        levels[i] = top.Level;
                        if (top.Override != BidiClass.OtherNeutral) types[i] = top.Override;
                        break;
                }
            }
        }

        // X10: level runs of kept characters, chained across matched isolates.
        private List<List<int>> Sequences()
        {
            var levelRuns = new List<List<int>>();
            var runOf = new int[count];
            List<int>? current = null;
            for (var i = 0; i < count; i++)
            {
                if (IsRemoved(original[i])) continue;
                if (current is null || levels[current[^1]] != levels[i]) levelRuns.Add(current = []);
                current.Add(i);
                runOf[i] = levelRuns.Count - 1;
            }
            var sequences = new List<List<int>>();
            foreach (var run in levelRuns)
            {
                if (original[run[0]] == BidiClass.PopDirectionalIsolate && matchedPdi[run[0]]) continue;
                var sequence = new List<int>(run);
                var last = run;
                while (IsIsolateInitiator(original[last[^1]]) && matchingPdi[last[^1]] >= 0)
                {
                    last = levelRuns[runOf[matchingPdi[last[^1]]]];
                    sequence.AddRange(last);
                }
                sequences.Add(sequence);
            }
            return sequences;
        }

        private int Kept(int from, int step)
        {
            for (var i = from; i >= 0 && i < count; i += step)
                if (!IsRemoved(original[i])) return i;
            return -1;
        }

        private static BidiClass Strong(byte level) => (level & 1) != 0 ? BidiClass.RightToLeft : BidiClass.LeftToRight;

        private static bool IsNeutral(BidiClass type) => type is BidiClass.ParagraphSeparator or BidiClass.SegmentSeparator
            or BidiClass.WhiteSpace or BidiClass.OtherNeutral or BidiClass.FirstStrongIsolate or BidiClass.LeftToRightIsolate
            or BidiClass.RightToLeftIsolate or BidiClass.PopDirectionalIsolate;

        // Strong direction for N0-N2: numbers count as R.
        private static BidiClass StrongOf(BidiClass type) => type switch
        {
            BidiClass.LeftToRight => BidiClass.LeftToRight,
            BidiClass.RightToLeft or BidiClass.ArabicLetter or BidiClass.EuropeanNumber or BidiClass.ArabicNumber => BidiClass.RightToLeft,
            _ => BidiClass.OtherNeutral,
        };

        private void ResolveSequence(List<int> sequence)
        {
            var level = levels[sequence[0]];
            var before = Kept(sequence[0] - 1, -1);
            var sos = Strong(Math.Max(level, before < 0 ? baseLevel : levels[before]));
            var lastIndex = sequence[^1];
            var after = IsIsolateInitiator(original[lastIndex]) ? -1 : Kept(lastIndex + 1, 1);
            var eos = Strong(Math.Max(level, after < 0 ? baseLevel : levels[after]));
            var n = sequence.Count;
            BidiClass Get(int k) => types[sequence[k]];
            void Set(int k, BidiClass type) => types[sequence[k]] = type;

            // W1
            for (var k = 0; k < n; k++)
            {
                if (Get(k) != BidiClass.NonspacingMark) continue;
                var previous = k == 0 ? sos : Get(k - 1);
                Set(k, IsIsolateInitiator(previous) || previous == BidiClass.PopDirectionalIsolate ? BidiClass.OtherNeutral : previous);
            }
            // W2, W3
            var lastStrong = sos;
            for (var k = 0; k < n; k++)
            {
                var type = Get(k);
                if (type is BidiClass.LeftToRight or BidiClass.RightToLeft or BidiClass.ArabicLetter) lastStrong = type;
                if (type == BidiClass.EuropeanNumber && lastStrong == BidiClass.ArabicLetter) Set(k, BidiClass.ArabicNumber);
            }
            for (var k = 0; k < n; k++)
                if (Get(k) == BidiClass.ArabicLetter) Set(k, BidiClass.RightToLeft);
            // W4
            for (var k = 1; k + 1 < n; k++)
            {
                var (previous, type, next) = (Get(k - 1), Get(k), Get(k + 1));
                if (type == BidiClass.EuropeanSeparator && previous == BidiClass.EuropeanNumber && next == BidiClass.EuropeanNumber)
                    Set(k, BidiClass.EuropeanNumber);
                else if (type == BidiClass.CommonSeparator && previous == next && previous is BidiClass.EuropeanNumber or BidiClass.ArabicNumber)
                    Set(k, previous);
            }
            // W5
            for (var k = 0; k < n; k++)
            {
                if (Get(k) != BidiClass.EuropeanTerminator) continue;
                var end = k;
                while (end < n && Get(end) == BidiClass.EuropeanTerminator) end++;
                if (k > 0 && Get(k - 1) == BidiClass.EuropeanNumber || end < n && Get(end) == BidiClass.EuropeanNumber)
                    for (var j = k; j < end; j++) Set(j, BidiClass.EuropeanNumber);
                k = end - 1;
            }
            // W6
            for (var k = 0; k < n; k++)
                if (Get(k) is BidiClass.EuropeanSeparator or BidiClass.EuropeanTerminator or BidiClass.CommonSeparator) Set(k, BidiClass.OtherNeutral);
            // W7
            lastStrong = sos;
            for (var k = 0; k < n; k++)
            {
                var type = Get(k);
                if (type is BidiClass.LeftToRight or BidiClass.RightToLeft) lastStrong = type;
                if (type == BidiClass.EuropeanNumber && lastStrong == BidiClass.LeftToRight) Set(k, BidiClass.LeftToRight);
            }

            var embedding = Strong(level);
            // N0: bracket pairs take the embedding direction, or the context's when only it occurs inside.
            foreach (var (open, close) in BracketPairs(sequence))
            {
                var found = BidiClass.OtherNeutral;
                for (var k = open + 1; k < close; k++)
                {
                    var strong = StrongOf(Get(k));
                    if (strong == BidiClass.OtherNeutral) continue;
                    found = strong;
                    if (strong == embedding) break;
                }
                if (found == BidiClass.OtherNeutral) continue;
                if (found != embedding)
                {
                    var context = sos;
                    for (var k = open - 1; k >= 0; k--)
                    {
                        var strong = StrongOf(Get(k));
                        if (strong != BidiClass.OtherNeutral) { context = strong; break; }
                    }
                    found = context == found ? found : embedding;
                }
                foreach (var k in (ReadOnlySpan<int>)[open, close])
                {
                    Set(k, found);
                    for (var j = k + 1; j < n && original[sequence[j]] == BidiClass.NonspacingMark; j++) Set(j, found);
                }
            }
            // N1, N2
            for (var k = 0; k < n; k++)
            {
                if (!IsNeutral(Get(k))) continue;
                var end = k;
                while (end < n && IsNeutral(Get(end))) end++;
                var leading = k == 0 ? sos : StrongOf(Get(k - 1));
                var trailing = end == n ? eos : StrongOf(Get(end));
                var resolved = leading == trailing ? leading : embedding;
                for (var j = k; j < end; j++) Set(j, resolved);
                k = end - 1;
            }
            // I1, I2
            foreach (var i in sequence)
            {
                var type = types[i];
                if ((levels[i] & 1) == 0)
                    levels[i] += type switch
                    {
                        BidiClass.RightToLeft => 1,
                        BidiClass.ArabicNumber or BidiClass.EuropeanNumber => 2,
                        _ => 0,
                    };
                else if (type is BidiClass.LeftToRight or BidiClass.EuropeanNumber or BidiClass.ArabicNumber) levels[i]++;
            }
        }

        // BD16: sequence indices of bracket pairs, sorted by opening bracket.
        private List<(int Open, int Close)> BracketPairs(List<int> sequence)
        {
            var pairs = new List<(int, int)>();
            var open = new List<(int Closer, int Index)>();
            for (var k = 0; k < sequence.Count; k++)
            {
                var i = sequence[k];
                if (types[i] != BidiClass.OtherNeutral) continue;
                var codepoint = new Codepoint((uint)text.Codepoints[start + i]);
                if (codepoint.PairedBracketType == BidiPairedBracketType.Open)
                {
                    if (open.Count == MaxBrackets) break;
                    if (codepoint.TryGetPairedBracket(out var closer)) open.Add((Canonical((int)closer.Value), k));
                }
                else if (codepoint.PairedBracketType == BidiPairedBracketType.Close)
                {
                    var value = Canonical((int)codepoint.Value);
                    for (var j = open.Count - 1; j >= 0; j--)
                    {
                        if (open[j].Closer != value) continue;
                        pairs.Add((open[j].Index, k));
                        open.RemoveRange(j, open.Count - j);
                        break;
                    }
                }
            }
            pairs.Sort();
            return pairs;
        }

        // U+2329/U+232A are canonically equivalent to U+3008/U+3009.
        private static int Canonical(int codepoint) => codepoint switch { 0x2329 => 0x3008, 0x232A => 0x3009, _ => codepoint };

        // L1, with removed characters taking their neighbour's level.
        private void Whitespace()
        {
            for (var i = 0; i < count; i++)
                if (IsRemoved(original[i])) levels[i] = i == 0 ? baseLevel : levels[i - 1];
            var trailing = true;
            for (var i = count - 1; i >= 0; i--)
            {
                var type = original[i];
                if (type is BidiClass.SegmentSeparator or BidiClass.ParagraphSeparator)
                {
                    levels[i] = baseLevel;
                    trailing = true;
                }
                else if (type is BidiClass.WhiteSpace || IsIsolateInitiator(type) || type == BidiClass.PopDirectionalIsolate || IsRemoved(type))
                {
                    if (trailing) levels[i] = baseLevel;
                }
                else trailing = false;
            }
        }

        // L2: reverse every maximal sequence of runs at or above each odd level, highest first.
        private void Reorder(List<BidiRun> output)
        {
            var runs = new List<(int Start, int End, byte Level)>();
            for (var i = 0; i < count; i++)
            {
                if (runs.Count > 0 && runs[^1].Level == levels[i]) runs[^1] = runs[^1] with { End = i + 1 };
                else runs.Add((i, i + 1, levels[i]));
            }
            if (runs.Count == 0) return;
            var highest = runs.Max(run => run.Level);
            var lowestOdd = runs.Min(run => run.Level) | 1;
            for (var level = highest; level >= lowestOdd; level--)
            {
                for (var r = 0; r < runs.Count; r++)
                {
                    if (runs[r].Level < level) continue;
                    var end = r;
                    while (end < runs.Count && runs[end].Level >= level) end++;
                    runs.Reverse(r, end - r);
                    r = end;
                }
            }
            foreach (var run in runs)
                output.Add(new(text.Offsets[start + run.Start], text.Offsets[start + run.End], (run.Level & 1) != 0));
        }
    }
}