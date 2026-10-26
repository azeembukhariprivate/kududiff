using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace Kududiff.Core
{
    public enum WhitespaceMode
    {
        /// <summary>Every space and tab counts.</summary>
        Compare,
        /// <summary>Spaces and tabs at the start and end of lines don't count (indentation changes).</summary>
        Ends,
        /// <summary>Any run of spaces and tabs counts the same as one, and those at line ends don't count.</summary>
        Amount,
        /// <summary>Spaces and tabs don't count at all.</summary>
        All
    }

    public enum InlineMode { Off, Words, Characters }

    public sealed class CompareOptions
    {
        public DiffAlgorithm Algorithm { get; set; } = DiffAlgorithm.Standard;
        public WhitespaceMode Whitespace { get; set; } = WhitespaceMode.Compare;
        public bool IgnoreCase { get; set; }
        public bool IgnoreBlankLines { get; set; }
        /// <summary>Count a line ending in "\r\n" on one side and "\n" on the other as a change.</summary>
        public bool CompareLineEndings { get; set; }
        /// <summary>Regular expressions: what they match is left out of the comparison (for example timestamps).</summary>
        public List<string> IgnorePatterns { get; set; } = new List<string>();
        public InlineMode Inline { get; set; } = InlineMode.Words;
        public bool DetectMoves { get; set; } = true;
        /// <summary>The fewest non-blank lines a block needs to be shown as moved.</summary>
        public int MinMoveLines { get; set; } = 3;

        public CompareOptions Clone()
        {
            var copy = (CompareOptions)MemberwiseClone();
            copy.IgnorePatterns = new List<string>(IgnorePatterns);
            return copy;
        }

        /// <summary>Options that compare everything exactly, as a patch needs.</summary>
        public CompareOptions Exact()
        {
            var copy = Clone();
            copy.Whitespace = WhitespaceMode.Compare;
            copy.IgnoreCase = false;
            copy.IgnoreBlankLines = false;
            copy.CompareLineEndings = true;
            copy.IgnorePatterns = new List<string>();
            return copy;
        }

        /// <summary>The patterns that aren't valid regular expressions, with why.</summary>
        public static List<string> BadPatterns(IEnumerable<string> patterns)
        {
            var bad = new List<string>();
            foreach (var p in patterns.Where(p => !string.IsNullOrWhiteSpace(p)))
            {
                try { _ = new Regex(p); }
                catch (ArgumentException ex) { bad.Add($"{p}: {ex.Message}"); }
            }
            return bad;
        }
    }

    public enum ChunkKind { Equal, Insert, Delete, Replace }

    /// <summary>A part changed inside a line: from Start, Length characters.</summary>
    public struct Span
    {
        public int Line;
        public int Start;
        public int Length;

        public Span(int line, int start, int length)
        {
            Line = line;
            Start = start;
            Length = length;
        }
    }

    /// <summary>
    /// A run of lines on each side: equal, or added on the right (Insert), removed from the left (Delete), or changed
    /// (Replace). Lines are numbered from 0. An "equal" chunk can have different line counts when blank lines are ignored.
    /// </summary>
    public sealed class Chunk
    {
        public ChunkKind Kind;
        public int A, ACount, B, BCount;
        /// <summary>Changed words or characters inside the lines of a Replace chunk.</summary>
        public List<Span> ASpans = new List<Span>(), BSpans = new List<Span>();

        public int AEnd => A + ACount;
        public int BEnd => B + BCount;
        public bool IsChange => Kind != ChunkKind.Equal;

        public override string ToString() => $"{Kind} L{A}+{ACount} R{B}+{BCount}";
    }

    /// <summary>A block removed in one place on the left and added in another on the right, unchanged.</summary>
    public sealed class Move
    {
        public int A, B, Count;

        public override string ToString() => $"Move L{A} -> R{B} ({Count})";
    }

    public sealed class Comparison
    {
        public List<string> Left { get; private set; }
        public List<string> Right { get; private set; }
        public List<Chunk> Chunks { get; private set; }
        public List<Move> Moves { get; private set; } = new List<Move>();
        public CompareOptions Options { get; private set; }
        /// <summary>Lines on the left (and right) that are part of a moved block, by line number.</summary>
        public HashSet<int> MovedLeft { get; } = new HashSet<int>();
        public HashSet<int> MovedRight { get; } = new HashSet<int>();

        public IEnumerable<Chunk> Changes => Chunks.Where(c => c.IsChange);
        public int ChangeCount => Chunks.Count(c => c.IsChange);
        public int LinesAdded => Chunks.Where(c => c.Kind == ChunkKind.Insert || c.Kind == ChunkKind.Replace).Sum(c => c.BCount);
        public int LinesRemoved => Chunks.Where(c => c.Kind == ChunkKind.Delete || c.Kind == ChunkKind.Replace).Sum(c => c.ACount);
        public bool Identical => Chunks.All(c => !c.IsChange);

        /// <summary>Compares two texts. Safe to run on a background thread.</summary>
        public static Comparison Run(string left, string right, CompareOptions options)
        {
            options = options ?? new CompareOptions();
            var aBreaks = new List<string>();
            var bBreaks = new List<string>();
            var a = TextFile.Lines(left, aBreaks);
            var b = TextFile.Lines(right, bBreaks);
            var normalize = Normalizer(options);

            // Each distinct (normalised) line gets a number; equal numbers, equal lines.
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int Id(string line, string lineBreak)
            {
                var key = normalize(line);
                if (options.CompareLineEndings) key += "\u0001" + lineBreak;
                if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count;
                return id;
            }
            var aIds = a.Select((l, i) => Id(l, aBreaks[i])).ToArray();
            var bIds = b.Select((l, i) => Id(l, bBreaks[i])).ToArray();

            // Blank lines can be left out of the matching entirely.
            bool Blank(string line) => normalize(line).Trim().Length == 0;
            int[] aIndex, bIndex;
            if (options.IgnoreBlankLines)
            {
                aIndex = Enumerable.Range(0, a.Count).Where(i => !Blank(a[i])).ToArray();
                bIndex = Enumerable.Range(0, b.Count).Where(i => !Blank(b[i])).ToArray();
            }
            else
            {
                aIndex = Enumerable.Range(0, a.Count).ToArray();
                bIndex = Enumerable.Range(0, b.Count).ToArray();
            }
            var matches = Lcs.Match(aIndex.Select(i => aIds[i]).ToArray(), bIndex.Select(i => bIds[i]).ToArray(), options.Algorithm)
                .Select(m => (A: aIndex[m.A], B: bIndex[m.B])).ToList();

            var comparison = new Comparison { Left = a, Right = b, Options = options, Chunks = BuildChunks(a, b, matches, options.IgnoreBlankLines ? Blank : (Func<string, bool>)null) };
            if (options.Inline != InlineMode.Off)
                foreach (var c in comparison.Chunks.Where(c => c.Kind == ChunkKind.Replace))
                    InlineDiff(a, b, c, options);
            if (options.DetectMoves) comparison.FindMoves(normalize);
            return comparison;
        }

        /// <summary>Turns matched line pairs into runs: equal lines, and the changes between them.</summary>
        private static List<Chunk> BuildChunks(List<string> a, List<string> b, List<(int A, int B)> matches, Func<string, bool> ignoredBlank)
        {
            var chunks = new List<Chunk>();
            void Add(ChunkKind kind, int ai, int ac, int bi, int bc)
            {
                if (ac == 0 && bc == 0) return;
                var last = chunks.Count > 0 ? chunks[chunks.Count - 1] : null;
                if (last != null && last.Kind == kind && last.AEnd == ai && last.BEnd == bi && kind == ChunkKind.Equal)
                {
                    last.ACount += ac;
                    last.BCount += bc;
                    return;
                }
                chunks.Add(new Chunk { Kind = kind, A = ai, ACount = ac, B = bi, BCount = bc });
            }
            void Gap(int ai, int aEnd, int bi, int bEnd)
            {
                int ac = aEnd - ai, bc = bEnd - bi;
                if (ac == 0 && bc == 0) return;
                // With blank lines ignored, a gap of only blank lines isn't a change.
                if (ignoredBlank != null && Enumerable.Range(ai, ac).All(i => ignoredBlank(a[i])) && Enumerable.Range(bi, bc).All(i => ignoredBlank(b[i])))
                    Add(ChunkKind.Equal, ai, ac, bi, bc);
                else
                    Add(ac == 0 ? ChunkKind.Insert : bc == 0 ? ChunkKind.Delete : ChunkKind.Replace, ai, ac, bi, bc);
            }

            int pa = 0, pb = 0;
            foreach (var (ma, mb) in matches)
            {
                Gap(pa, ma, pb, mb);
                Add(ChunkKind.Equal, ma, 1, mb, 1);
                pa = ma + 1;
                pb = mb + 1;
            }
            Gap(pa, a.Count, pb, b.Count);
            return chunks;
        }

        /// <summary>The text of a line as it's compared: ignored patterns, case and spaces taken out.</summary>
        public static Func<string, string> Normalizer(CompareOptions options)
        {
            var patterns = options.IgnorePatterns
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .Select(p => { try { return new Regex(p, RegexOptions.CultureInvariant); } catch (ArgumentException) { return null; } })
                .Where(r => r != null).ToList();
            var whitespace = options.Whitespace;
            var ignoreCase = options.IgnoreCase;
            if (patterns.Count == 0 && whitespace == WhitespaceMode.Compare && !ignoreCase) return line => line;
            return line =>
            {
                foreach (var r in patterns) line = r.Replace(line, string.Empty);
                switch (whitespace)
                {
                    case WhitespaceMode.Ends: line = line.Trim(' ', '\t'); break;
                    case WhitespaceMode.Amount: line = Regex.Replace(line.Trim(' ', '\t'), "[ \t]+", " "); break;
                    case WhitespaceMode.All: line = Regex.Replace(line, "[ \t]+", string.Empty); break;
                }
                return ignoreCase ? line.ToLowerInvariant() : line;
            };
        }

        // ------------------------------------------------------------------ changes inside lines

        /// <summary>Above this many characters a changed block isn't compared word by word (it would only be slow and noisy).</summary>
        public const int MaxInlineChars = 40000;

        private static void InlineDiff(List<string> a, List<string> b, Chunk chunk, CompareOptions options)
        {
            var aTokens = Tokens(a, chunk.A, chunk.ACount, options.Inline);
            var bTokens = Tokens(b, chunk.B, chunk.BCount, options.Inline);
            if (aTokens.Sum(t => t.Text.Length) > MaxInlineChars || bTokens.Sum(t => t.Text.Length) > MaxInlineChars) return;

            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            int Id(Token t)
            {
                var key = TokenKey(t.Text, options);
                if (!ids.TryGetValue(key, out var id)) ids[key] = id = ids.Count;
                return id;
            }
            var matches = Lcs.Match(aTokens.Select(Id).ToArray(), bTokens.Select(Id).ToArray(), DiffAlgorithm.Standard);
            var matchedA = new HashSet<int>(matches.Select(m => m.A));
            var matchedB = new HashSet<int>(matches.Select(m => m.B));
            chunk.ASpans = SpansOf(aTokens, matchedA, options);
            chunk.BSpans = SpansOf(bTokens, matchedB, options);
        }

        private struct Token
        {
            public int Line;
            public int Start;
            public string Text;
        }

        private static List<Token> Tokens(List<string> lines, int start, int count, InlineMode mode)
        {
            var tokens = new List<Token>();
            for (var l = start; l < start + count; l++)
            {
                var s = lines[l];
                var i = 0;
                while (i < s.Length)
                {
                    var j = i + 1;
                    if (mode == InlineMode.Words)
                    {
                        if (char.IsLetterOrDigit(s[i]) || s[i] == '_') while (j < s.Length && (char.IsLetterOrDigit(s[j]) || s[j] == '_')) j++;
                        else if (s[i] == ' ' || s[i] == '\t') while (j < s.Length && (s[j] == ' ' || s[j] == '\t')) j++;
                    }
                    tokens.Add(new Token { Line = l, Start = i, Text = s.Substring(i, j - i) });
                    i = j;
                }
                tokens.Add(new Token { Line = l, Start = s.Length, Text = "\n" }); // keeps words on different lines apart
            }
            return tokens;
        }

        private static string TokenKey(string text, CompareOptions options)
        {
            if (options.Whitespace != WhitespaceMode.Compare && text.Trim(' ', '\t').Length == 0 && text != "\n") return " ";
            return options.IgnoreCase ? text.ToLowerInvariant() : text;
        }

        /// <summary>Unmatched tokens, joined into spans per line. Line breaks and (when ignored) spaces aren't shown as changes.</summary>
        private static List<Span> SpansOf(List<Token> tokens, HashSet<int> matched, CompareOptions options)
        {
            var spans = new List<Span>();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (matched.Contains(i) || tokens[i].Text == "\n") continue;
                if (options.Whitespace != WhitespaceMode.Compare && tokens[i].Text.Trim(' ', '\t').Length == 0) continue;
                var t = tokens[i];
                var last = spans.Count > 0 ? spans[spans.Count - 1] : default(Span);
                if (spans.Count > 0 && last.Line == t.Line && last.Start + last.Length == t.Start)
                    spans[spans.Count - 1] = new Span(t.Line, last.Start, last.Length + t.Text.Length);
                else
                    spans.Add(new Span(t.Line, t.Start, t.Text.Length));
            }
            return spans;
        }

        // ------------------------------------------------------------------ moved code

        /// <summary>
        /// Finds blocks of lines removed in one place and added unchanged somewhere else (at least
        /// <see cref="CompareOptions.MinMoveLines"/> non-blank lines), so they can be shown as moved rather than as a
        /// removal and an unrelated addition.
        /// </summary>
        private void FindMoves(Func<string, string> normalize)
        {
            var leftChanged = new HashSet<int>();
            var rightChanged = new HashSet<int>();
            foreach (var c in Chunks.Where(c => c.IsChange))
            {
                for (var i = c.A; i < c.AEnd; i++) leftChanged.Add(i);
                for (var j = c.B; j < c.BEnd; j++) rightChanged.Add(j);
            }
            if (leftChanged.Count == 0 || rightChanged.Count == 0) return;

            string Key(string line) => normalize(line).Trim();
            var byKey = new Dictionary<string, List<int>>(StringComparer.Ordinal);
            foreach (var j in rightChanged.OrderBy(j => j))
            {
                var key = Key(Right[j]);
                if (key.Length == 0) continue;
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<int>();
                if (list.Count < 64) list.Add(j);
            }

            var usedRight = new HashSet<int>();
            var leftLines = leftChanged.OrderBy(i => i).ToList();
            var pos = 0;
            while (pos < leftLines.Count)
            {
                var i = leftLines[pos];
                var key = Key(Left[i]);
                int bestStart = -1, bestLength = 0, bestSolid = 0;
                if (key.Length > 0 && byKey.TryGetValue(key, out var candidates))
                {
                    foreach (var j in candidates)
                    {
                        if (usedRight.Contains(j)) continue;
                        int length = 0, solid = 0;
                        while (leftChanged.Contains(i + length) && rightChanged.Contains(j + length) && !usedRight.Contains(j + length)
                               && i + length < Left.Count && j + length < Right.Count && Key(Left[i + length]) == Key(Right[j + length]))
                        {
                            if (Key(Left[i + length]).Length > 0) solid++;
                            length++;
                        }
                        if (solid > bestSolid) { bestStart = j; bestLength = length; bestSolid = solid; }
                    }
                }
                if (bestSolid >= Math.Max(1, Options.MinMoveLines))
                {
                    Moves.Add(new Move { A = i, B = bestStart, Count = bestLength });
                    for (var k = 0; k < bestLength; k++)
                    {
                        MovedLeft.Add(i + k);
                        MovedRight.Add(bestStart + k);
                        usedRight.Add(bestStart + k);
                    }
                    pos += bestLength;
                    while (pos < leftLines.Count && leftLines[pos] < i + bestLength) pos++;
                }
                else
                {
                    pos++;
                }
            }
        }

        // ------------------------------------------------------------------ positions

        /// <summary>
        /// The line on the right that lines up with a line on the left (and the other way round), for scrolling both
        /// sides together: inside a change, proportionally.
        /// </summary>
        public double MapLine(double line, bool fromLeft)
        {
            foreach (var c in Chunks)
            {
                double start = fromLeft ? c.A : c.B, count = fromLeft ? c.ACount : c.BCount;
                double otherStart = fromLeft ? c.B : c.A, otherCount = fromLeft ? c.BCount : c.ACount;
                if (line < start) return otherStart;
                if (line < start + count) return otherStart + (line - start) / count * otherCount;
            }
            var lastChunk = Chunks.LastOrDefault();
            if (lastChunk == null) return line;
            return (fromLeft ? lastChunk.BEnd : lastChunk.AEnd) + (line - (fromLeft ? lastChunk.AEnd : lastChunk.BEnd));
        }

        /// <summary>The number of the first change after a line on one side, or -1.</summary>
        public int NextChange(int afterLine, bool left)
        {
            var changes = Changes.ToList();
            for (var i = 0; i < changes.Count; i++)
                if ((left ? changes[i].A : changes[i].B) > afterLine) return i;
            return -1;
        }

        public string Summary()
        {
            if (Identical) return Options.IgnoreCase || Options.Whitespace != WhitespaceMode.Compare || Options.IgnoreBlankLines || Options.IgnorePatterns.Count > 0
                ? "No differences (with the ignore options on)"
                : "The files are identical";
            var sb = new StringBuilder();
            sb.Append(ChangeCount == 1 ? "1 change" : ChangeCount + " changes");
            sb.Append($" · {LinesAdded} added, {LinesRemoved} removed");
            if (Moves.Count > 0) sb.Append($" · {Moves.Count} moved block{(Moves.Count == 1 ? string.Empty : "s")}");
            return sb.ToString();
        }
    }
}
