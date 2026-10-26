using System.Collections.Generic;

namespace Kududiff.Core
{
    /// <summary>Line-based edits on text, as plain character positions, so the editor can apply them as one undoable change.</summary>
    public static class TextOps
    {
        /// <summary>Where each line starts, plus one entry for the end of the text.</summary>
        public static List<int> LineStarts(string text)
        {
            var starts = new List<int>();
            var breaks = new List<string>();
            var lines = TextFile.Lines(text, breaks);
            var pos = 0;
            for (var i = 0; i < lines.Count; i++)
            {
                starts.Add(pos);
                pos += lines[i].Length + breaks[i].Length;
            }
            starts.Add(text.Length);
            return starts;
        }

        /// <summary>The characters of lines [line, line + count), including their line breaks.</summary>
        public static (int Start, int Length) LineRange(string text, int line, int count)
        {
            var starts = LineStarts(text);
            var last = starts.Count - 1;
            var start = starts[System.Math.Min(line, last)];
            var end = starts[System.Math.Min(line + count, last)];
            return (start, end - start);
        }

        private static bool EndsWithBreak(string s) => s.Length > 0 && (s[s.Length - 1] == '\n' || s[s.Length - 1] == '\r');

        private static string TrimBreak(string s)
        {
            if (s.EndsWith("\r\n")) return s.Substring(0, s.Length - 2);
            return EndsWithBreak(s) ? s.Substring(0, s.Length - 1) : s;
        }

        /// <summary>
        /// The edit that replaces lines [targetLine, targetLine + targetCount) of the target with lines
        /// [sourceLine, sourceLine + sourceCount) of the source: copying a change from one side to the other. Line breaks
        /// are kept sensible at the end of the text (a copied last line without a break gets one when it lands in the
        /// middle, and a target without a final break keeps not having one).
        /// </summary>
        public static (int Start, int Length, string Insert) CopyLines(string source, int sourceLine, int sourceCount, string target, int targetLine, int targetCount)
        {
            var (srcStart, srcLength) = LineRange(source, sourceLine, sourceCount);
            var insert = source.Substring(srcStart, srcLength);
            var (start, length) = LineRange(target, targetLine, targetCount);
            var end = start + length;
            var newline = TextFile.NewLineOf(target.Length > 0 ? target : source);
            var atEnd = end == target.Length;
            var targetHasFinalBreak = EndsWithBreak(target);
            if (target.Length == 0) return (0, 0, insert);

            if (!atEnd)
            {
                if (insert.Length > 0 && !EndsWithBreak(insert)) insert += newline;
                return (start, length, insert);
            }

            if (insert.Length == 0)
            {
                // Removing the last lines: if the text had no final line break, the line before loses its break too.
                if (!targetHasFinalBreak && start > 0)
                {
                    var before = target.Substring(0, start);
                    var trimmed = TrimBreak(before);
                    var cut = before.Length - trimmed.Length;
                    return (start - cut, length + cut, string.Empty);
                }
                return (start, length, string.Empty);
            }

            if (length == 0 && target.Length > 0 && !targetHasFinalBreak)
                return (start, 0, newline + TrimBreak(insert)); // adding after a last line that had no break

            if (targetHasFinalBreak && !EndsWithBreak(insert)) insert += newline;
            else if (!targetHasFinalBreak && EndsWithBreak(insert)) insert = TrimBreak(insert);
            return (start, length, insert);
        }

        public static string Apply(string text, (int Start, int Length, string Insert) edit) =>
            text.Substring(0, edit.Start) + edit.Insert + text.Substring(edit.Start + edit.Length);
    }
}
