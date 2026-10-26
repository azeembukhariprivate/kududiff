using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Kududiff.Core
{
    /// <summary>Saves the differences: as a patch other tools can apply, or as a report to read and share.</summary>
    public static class Export
    {
        // ------------------------------------------------------------------ unified patch

        /// <summary>
        /// A unified diff ("patch") that turns the left text into the right one, with <paramref name="context"/> unchanged
        /// lines around each change. It always compares exactly (ignore options would make a patch that can't be applied),
        /// keeps each line's own line break, and marks a last line without one, like GNU diff.
        /// </summary>
        public static string Patch(string left, string right, string leftName, string rightName, DiffAlgorithm algorithm, int context = 3)
        {
            var options = new CompareOptions { Algorithm = algorithm, Inline = InlineMode.Off, DetectMoves = false }.Exact();
            var cmp = Comparison.Run(left, right, options);
            var aBreaks = new List<string>();
            var bBreaks = new List<string>();
            var a = TextFile.Lines(left, aBreaks);
            var b = TextFile.Lines(right, bBreaks);
            var sb = new StringBuilder();
            if (cmp.Identical) return string.Empty;
            sb.Append("--- ").Append(leftName).Append('\n');
            sb.Append("+++ ").Append(rightName).Append('\n');

            var chunks = cmp.Chunks;
            var i = 0;
            while (i < chunks.Count)
            {
                if (!chunks[i].IsChange) { i++; continue; }
                int first = i, last = i;
                while (last + 2 < chunks.Count && !chunks[last + 1].IsChange && chunks[last + 2].IsChange && chunks[last + 1].ACount <= 2 * context)
                    last += 2;

                var body = new StringBuilder();
                int aStart, bStart, aCount = 0, bCount = 0;
                void Line(char prefix, string text, string lineBreak)
                {
                    body.Append(prefix).Append(text);
                    body.Append(lineBreak.Length == 0 ? "\n\\ No newline at end of file\n" : lineBreak);
                }

                if (first > 0)
                {
                    var e = chunks[first - 1];
                    var n = Math.Min(context, e.ACount);
                    aStart = e.AEnd - n;
                    bStart = e.BEnd - n;
                    for (var k = 0; k < n; k++) Line(' ', a[aStart + k], aBreaks[aStart + k]);
                    aCount += n;
                    bCount += n;
                }
                else
                {
                    aStart = chunks[first].A;
                    bStart = chunks[first].B;
                }

                for (var k = first; k <= last; k++)
                {
                    var c = chunks[k];
                    if (!c.IsChange)
                    {
                        for (var l = 0; l < c.ACount; l++) Line(' ', a[c.A + l], aBreaks[c.A + l]);
                        aCount += c.ACount;
                        bCount += c.BCount;
                        continue;
                    }
                    for (var l = 0; l < c.ACount; l++) Line('-', a[c.A + l], aBreaks[c.A + l]);
                    for (var l = 0; l < c.BCount; l++) Line('+', b[c.B + l], bBreaks[c.B + l]);
                    aCount += c.ACount;
                    bCount += c.BCount;
                }

                if (last + 1 < chunks.Count)
                {
                    var e = chunks[last + 1];
                    var n = Math.Min(context, e.ACount);
                    for (var k = 0; k < n; k++) Line(' ', a[e.A + k], aBreaks[e.A + k]);
                    aCount += n;
                    bCount += n;
                }

                sb.Append("@@ -").Append(Range(aStart, aCount)).Append(" +").Append(Range(bStart, bCount)).Append(" @@\n");
                sb.Append(body);
                i = last + 1;
            }
            return sb.ToString();
        }

        /// <summary>"start,count" as diff writes it: lines from 1, an empty range named by the line before it.</summary>
        private static string Range(int start, int count)
        {
            if (count == 0) return start + ",0";
            return count == 1 ? (start + 1).ToString() : (start + 1) + "," + count;
        }

        // ------------------------------------------------------------------ HTML report

        /// <summary>Unchanged runs longer than this are folded in the report, keeping a few lines on each side.</summary>
        private const int FoldAfter = 10, KeepAround = 3;

        /// <summary>A self-contained web page showing both files side by side, with the changes highlighted.</summary>
        public static string Html(Comparison cmp, string leftName, string rightName, DateTime when)
        {
            string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>").Append(E(Name(leftName))).Append(" vs ").Append(E(Name(rightName))).Append(" · KUDUDIFF</title>\n<style>\n");
            sb.Append(@":root{--bg:#f3f3f3;--surface:#fff;--text:#1b1b1b;--sub:#5c5c5c;--border:#e0e0e0;--accent:#7c3aed;
--ins:#e6f6ea;--insw:#a6ebb5;--del:#fce8ea;--delw:#ffc4c2;--chg:#e7effd;--mv:#fff3cc;--num:#8c959f}
@media (prefers-color-scheme:dark){:root{--bg:#202020;--surface:#1e1e1e;--text:#f2f2f2;--sub:#b4b4b4;--border:#3a3a3a;
--ins:#13291b;--insw:#22603a;--del:#301619;--delw:#78262d;--chg:#15243a;--mv:#31270f;--num:#6e7681}}
body{margin:0;background:var(--bg);color:var(--text);font:14px 'Segoe UI',system-ui,sans-serif}
header{padding:18px 24px;background:var(--surface);border-bottom:1px solid var(--border)}
h1{margin:0 0 4px;font-size:18px}.sub{color:var(--sub);font-size:13px}.brand{color:var(--accent);font-weight:600}
.legend span{display:inline-block;margin-right:14px}.sw{display:inline-block;width:11px;height:11px;border-radius:3px;margin-right:5px;vertical-align:-1px}
main{padding:16px 24px}.wrap{overflow-x:auto;background:var(--surface);border:1px solid var(--border);border-radius:8px}
table{border-collapse:collapse;width:100%;table-layout:fixed;font:12.5px Consolas,'Cascadia Mono',monospace}
col.n{width:52px}th{position:sticky;top:0;background:var(--surface);text-align:left;font:600 13px 'Segoe UI',sans-serif;padding:8px 10px;border-bottom:1px solid var(--border);overflow:hidden;text-overflow:ellipsis;white-space:nowrap}
td{padding:1px 8px;vertical-align:top;white-space:pre-wrap;word-break:break-all}td.n{color:var(--num);text-align:right;user-select:none;border-right:1px solid var(--border)}
td.mid{border-left:1px solid var(--border)}.del{background:var(--del)}.ins{background:var(--ins)}.chg{background:var(--chg)}.mv{background:var(--mv)}
.del i,.chg.l i{background:var(--delw);font-style:normal;border-radius:2px}.ins i,.chg.r i{background:var(--insw);font-style:normal;border-radius:2px}
tr.fold td{color:var(--sub);text-align:center;font-family:'Segoe UI',sans-serif;padding:4px;background:var(--bg)}
footer{padding:0 24px 20px;color:var(--sub);font-size:12px}
</style></head><body>
");
            sb.Append("<header><h1>").Append(E(Name(leftName))).Append(" ↔ ").Append(E(Name(rightName))).Append("</h1>\n");
            sb.Append("<div class=\"sub\">").Append(E(cmp.Summary())).Append(" · ").Append(E(OptionsText(cmp.Options))).Append("</div>\n");
            sb.Append("<div class=\"sub legend\" style=\"margin-top:8px\">")
              .Append("<span><i class=\"sw\" style=\"background:var(--ins)\"></i>Added</span>")
              .Append("<span><i class=\"sw\" style=\"background:var(--del)\"></i>Removed</span>")
              .Append("<span><i class=\"sw\" style=\"background:var(--chg)\"></i>Changed</span>")
              .Append("<span><i class=\"sw\" style=\"background:var(--mv)\"></i>Moved</span></div></header>\n");
            sb.Append("<main><div class=\"wrap\"><table><colgroup><col class=\"n\"><col><col class=\"n\"><col></colgroup>\n");
            sb.Append("<thead><tr><th colspan=\"2\" title=\"").Append(E(leftName)).Append("\">").Append(E(leftName))
              .Append("</th><th colspan=\"2\" title=\"").Append(E(rightName)).Append("\">").Append(E(rightName)).Append("</th></tr></thead><tbody>\n");

            foreach (var c in cmp.Chunks)
            {
                if (!c.IsChange)
                {
                    var rows = Math.Max(c.ACount, c.BCount);
                    for (var k = 0; k < rows; k++)
                    {
                        if (rows > FoldAfter && k == KeepAround)
                        {
                            var hidden = rows - 2 * KeepAround;
                            sb.Append("<tr class=\"fold\"><td colspan=\"4\">⋯ ").Append(hidden).Append(" unchanged lines ⋯</td></tr>\n");
                            k = rows - KeepAround - 1;
                            continue;
                        }
                        Row(sb, cmp, k < c.ACount ? c.A + k : -1, k < c.BCount ? c.B + k : -1, string.Empty, string.Empty, null, null);
                    }
                    continue;
                }
                var count = Math.Max(c.ACount, c.BCount);
                var lc = c.Kind == ChunkKind.Replace ? "chg l" : "del";
                var rc = c.Kind == ChunkKind.Replace ? "chg r" : "ins";
                for (var k = 0; k < count; k++)
                    Row(sb, cmp, k < c.ACount ? c.A + k : -1, k < c.BCount ? c.B + k : -1, lc, rc, c.ASpans, c.BSpans);
            }
            sb.Append("</tbody></table></div></main>\n<footer>Made with <span class=\"brand\">KUDUDIFF</span> on ")
              .Append(E(when.ToString("d MMMM yyyy, HH:mm"))).Append("</footer>\n</body></html>\n");
            return sb.ToString();
        }

        private static void Row(StringBuilder sb, Comparison cmp, int al, int bl, string leftClass, string rightClass, List<Span> aSpans, List<Span> bSpans)
        {
            if (al >= 0 && cmp.MovedLeft.Contains(al)) leftClass = "mv";
            if (bl >= 0 && cmp.MovedRight.Contains(bl)) rightClass = "mv";
            sb.Append("<tr>");
            Cell(sb, al, al >= 0 ? cmp.Left[al] : null, leftClass, aSpans, false);
            Cell(sb, bl, bl >= 0 ? cmp.Right[bl] : null, rightClass, bSpans, true);
            sb.Append("</tr>\n");
        }

        private static void Cell(StringBuilder sb, int line, string text, string cls, List<Span> spans, bool right)
        {
            var mid = right ? " mid" : string.Empty;
            if (line < 0)
            {
                sb.Append("<td class=\"n").Append(mid).Append("\"></td><td></td>");
                return;
            }
            var c = string.IsNullOrEmpty(cls) ? string.Empty : " " + cls;
            sb.Append("<td class=\"n").Append(mid).Append(c).Append("\">").Append(line + 1).Append("</td><td class=\"").Append(cls).Append("\">");
            var mine = spans?.Where(s => s.Line == line).OrderBy(s => s.Start).ToList();
            if (mine == null || mine.Count == 0 || cls == "mv")
            {
                sb.Append(WebUtility.HtmlEncode(text));
            }
            else
            {
                var pos = 0;
                foreach (var s in mine)
                {
                    var start = Math.Min(s.Start, text.Length);
                    var end = Math.Min(s.Start + s.Length, text.Length);
                    sb.Append(WebUtility.HtmlEncode(text.Substring(pos, Math.Max(0, start - pos))));
                    sb.Append("<i>").Append(WebUtility.HtmlEncode(text.Substring(start, end - start))).Append("</i>");
                    pos = Math.Max(pos, end);
                }
                sb.Append(WebUtility.HtmlEncode(text.Substring(Math.Min(pos, text.Length))));
            }
            sb.Append("</td>");
        }

        private static string Name(string path)
        {
            if (string.IsNullOrEmpty(path)) return "untitled";
            var i = path.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 ? path.Substring(i + 1) : path;
        }

        /// <summary>The comparison settings in words, for the report.</summary>
        public static string OptionsText(CompareOptions o)
        {
            var parts = new List<string> { o.Algorithm == DiffAlgorithm.Patience ? "patience comparison" : "standard comparison" };
            switch (o.Whitespace)
            {
                case WhitespaceMode.Ends: parts.Add("spaces at line ends ignored"); break;
                case WhitespaceMode.Amount: parts.Add("amount of spaces ignored"); break;
                case WhitespaceMode.All: parts.Add("all spaces ignored"); break;
            }
            if (o.IgnoreCase) parts.Add("case ignored");
            if (o.IgnoreBlankLines) parts.Add("blank lines ignored");
            if (o.CompareLineEndings) parts.Add("line endings compared");
            var patterns = o.IgnorePatterns.Count(p => !string.IsNullOrWhiteSpace(p));
            if (patterns > 0) parts.Add(patterns == 1 ? "1 ignore pattern" : patterns + " ignore patterns");
            return string.Join(", ", parts);
        }
    }
}
