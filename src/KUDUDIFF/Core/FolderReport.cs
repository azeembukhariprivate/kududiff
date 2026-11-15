using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;

namespace Kududiff.Core
{
    /// <summary>A folder comparison as a web page: every file and folder with what it's like on each side, to read in a browser or send.</summary>
    public static class FolderReport
    {
        public static string Html(FolderComparison cmp, bool hideSame, DateTime when)
        {
            string E(string s) => WebUtility.HtmlEncode(s ?? string.Empty);
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html>\n<html lang=\"").Append(E(Lang.Current)).Append("\"").Append(Lang.RightToLeft ? " dir=\"rtl\"" : string.Empty).Append("><head><meta charset=\"utf-8\">\n");
            sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
            sb.Append("<title>").Append(E(Name(cmp.Left))).Append(" vs ").Append(E(Name(cmp.Right))).Append(" · KUDUDIFF</title>\n<style>\n");
            sb.Append(@":root{--bg:#f3f3f3;--surface:#fff;--text:#1b1b1b;--sub:#5c5c5c;--border:#e0e0e0;--accent:#7c3aed;
--ins:#e6f6ea;--del:#fce8ea;--chg:#e7effd;--bad:#fff3cc}
@media (prefers-color-scheme:dark){:root{--bg:#202020;--surface:#1e1e1e;--text:#f2f2f2;--sub:#b4b4b4;--border:#3a3a3a;
--ins:#13291b;--del:#301619;--chg:#15243a;--bad:#31270f}}
body{margin:0;background:var(--bg);color:var(--text);font:14px 'Segoe UI',system-ui,sans-serif}
header{padding:18px 24px;background:var(--surface);border-bottom:1px solid var(--border)}
h1{margin:0 0 4px;font-size:18px}.sub{color:var(--sub);font-size:13px}.brand{color:var(--accent);font-weight:600}
.legend span{display:inline-block;margin-inline-end:14px}.sw{display:inline-block;width:11px;height:11px;border-radius:3px;margin-inline-end:5px;vertical-align:-1px}
main{padding:16px 24px}.wrap{overflow-x:auto;background:var(--surface);border:1px solid var(--border);border-radius:8px}
table{border-collapse:collapse;width:100%}
th{position:sticky;top:0;background:var(--surface);text-align:start;font-weight:600;font-size:13px;padding:8px 10px;border-bottom:1px solid var(--border);white-space:nowrap}
td{padding:3px 10px;white-space:nowrap;border-bottom:1px solid var(--border)}td.num{text-align:end;font-variant-numeric:tabular-nums}td.mid{border-inline-start:1px solid var(--border)}
td.name{white-space:normal;word-break:break-all}.dir{font-weight:600}.st{color:var(--sub)}
.d td{background:var(--chg)}.l td{background:var(--del)}.r td{background:var(--ins)}.u td{background:var(--bad)}
footer{padding:0 24px 20px;color:var(--sub);font-size:12px}
</style></head><body>
");
            sb.Append("<header><h1>").Append(E(Name(cmp.Left))).Append(" ↔ ").Append(E(Name(cmp.Right))).Append("</h1>\n");
            sb.Append("<div class=\"sub\">").Append(E(cmp.Left)).Append(" ↔ ").Append(E(cmp.Right)).Append("</div>\n");
            sb.Append("<div class=\"sub\">").Append(E(cmp.Summary()));
            if (cmp.LeftOut > 0) sb.Append(" · ").Append(E(Lang.T("{0} left out", cmp.LeftOut)));
            if (hideSame && cmp.Same > 0) sb.Append(" · ").Append(E(Lang.T("files that are the same aren't listed")));
            sb.Append("</div>\n<div class=\"sub legend\" style=\"margin-top:8px\">")
              .Append("<span><i class=\"sw\" style=\"background:var(--chg)\"></i>").Append(E(Lang.T("Different"))).Append("</span>")
              .Append("<span><i class=\"sw\" style=\"background:var(--del)\"></i>").Append(E(Lang.T("Only on the left"))).Append("</span>")
              .Append("<span><i class=\"sw\" style=\"background:var(--ins)\"></i>").Append(E(Lang.T("Only on the right"))).Append("</span>")
              .Append("<span><i class=\"sw\" style=\"background:var(--bad)\"></i>").Append(E(Lang.T("Couldn't be read"))).Append("</span></div></header>\n");
            sb.Append("<main><div class=\"wrap\"><table><thead><tr><th>").Append(E(Lang.T("Name"))).Append("</th><th>").Append(E(Lang.T("What"))).Append("</th>")
              .Append("<th class=\"mid\">").Append(E(Lang.T("Left size"))).Append("</th><th>").Append(E(Lang.T("Left changed"))).Append("</th>")
              .Append("<th class=\"mid\">").Append(E(Lang.T("Right size"))).Append("</th><th>").Append(E(Lang.T("Right changed"))).Append("</th></tr></thead><tbody>\n");

            foreach (var item in cmp.Root.Below())
            {
                if (hideSame && item.State == FolderState.Same) continue;
                var cls = item.State == FolderState.Different ? "d" : item.State == FolderState.OnlyLeft ? "l" : item.State == FolderState.OnlyRight ? "r" : item.State == FolderState.Unreadable ? "u" : string.Empty;
                sb.Append("<tr").Append(cls.Length > 0 ? " class=\"" + cls + "\"" : string.Empty).Append("><td class=\"name").Append(item.IsFolder ? " dir" : string.Empty).Append("\"")
                  .Append(" style=\"padding-inline-start:").Append(10 + item.Depth * 18).Append("px\" title=\"").Append(E(item.RelativePath)).Append("\">")
                  .Append(E(item.Name)).Append(item.IsFolder ? "\\" : string.Empty).Append("</td><td class=\"st\">").Append(E(FolderComparison.StateText(item.State))).Append("</td>");
                Side(item.OnLeft, item.IsFolder, item.LeftSize, item.LeftTime);
                Side(item.OnRight, item.IsFolder, item.RightSize, item.RightTime);
                sb.Append("</tr>\n");
            }
            sb.Append("</tbody></table></div></main>\n<footer>").Append(E(Lang.T("Made with {0} on {1}", "KUDUDIFF", when.ToString("d MMMM yyyy, HH:mm"))).Replace("KUDUDIFF", "<span class=\"brand\">KUDUDIFF</span>"))
              .Append("</footer>\n</body></html>\n");
            return sb.ToString();

            void Side(bool present, bool folder, long size, DateTime time)
            {
                if (!present)
                {
                    sb.Append("<td class=\"mid\"></td><td></td>");
                    return;
                }
                sb.Append("<td class=\"num mid\">").Append(folder ? string.Empty : E(Format.Size(size))).Append("</td><td>")
                  .Append(time == default ? string.Empty : E(time.ToString("yyyy-MM-dd HH:mm"))).Append("</td>");
            }
        }

        private static string Name(string path)
        {
            var trimmed = (path ?? string.Empty).TrimEnd('\\', '/');
            var i = trimmed.LastIndexOfAny(new[] { '\\', '/' });
            return i >= 0 ? trimmed.Substring(i + 1) : trimmed;
        }
    }
}
