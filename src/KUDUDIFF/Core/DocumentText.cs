using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

// lang.py: only Lang.T texts (the rest are the files' own words: XML names and the like)
namespace Kududiff.Core
{
    /// <summary>
    /// Word, Excel, PowerPoint and PDF files (and the free office suites' .odt, .ods and .odp) as plain text, so they can
    /// be compared like any text file. Only read: nothing is ever written back into them.
    /// Word: the main text and tables, then headers, footers and footnotes; tracked changes as if accepted; no comments.
    /// Excel: "Sheet: name", then a line per row, starting with its first filled cell ("A5  Rice | 42 | 3.50").
    /// PowerPoint: "Slide n", its text, then its speaker notes. PDF: "Page n" and the page's text.
    /// </summary>
    public static class DocumentText
    {
        /// <summary>Excel: add each cell's formula after its value.</summary>
        public static bool ShowFormulas { get; set; }

        /// <summary>The kinds of document read, by file ending, with the name shown for each.</summary>
        private static readonly Dictionary<string, string> Kinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".docx"] = Shown("Word document"), [".docm"] = Shown("Word document"), [".xlsx"] = Shown("Excel workbook"), [".xlsm"] = Shown("Excel workbook"),
            [".pptx"] = Shown("PowerPoint presentation"), [".pptm"] = Shown("PowerPoint presentation"), [".pdf"] = "PDF",
            [".odt"] = Shown("Text document"), [".ods"] = Shown("Spreadsheet"), [".odp"] = Shown("Presentation")
        };

        /// <summary>Marks a text that is shown later (through Lang.T), so the language files have it.</summary>
        private static string Shown(string text) => text;

        /// <summary>
        /// Older Office files (one big binary file, not XML): they can't be read, and the person is told to save them
        /// in the newer kind.
        /// </summary>
        private static readonly HashSet<string> Older = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".doc", ".xls", ".ppt" };

        /// <summary>The most one part is unpacked to, and all of them together, so a file made to swell can't fill the memory.</summary>
        public const long MaxPart = 64L << 20, MaxTotal = 256L << 20;

        /// <summary>True for the kinds of file read as text here (by the name's ending).</summary>
        public static bool CanRead(string path) => Kinds.ContainsKey(Path.GetExtension(path ?? string.Empty));

        /// <summary>"Word document", "PDF"... or null when it's not one of these.</summary>
        public static string KindOf(string path) => Kinds.TryGetValue(Path.GetExtension(path ?? string.Empty), out var k) ? k : null;

        /// <summary>The older Word, Excel and PowerPoint files (from before 2007), which aren't read.</summary>
        public static bool IsOlderOffice(string path) => Older.Contains(Path.GetExtension(path ?? string.Empty));

        /// <summary>The file as text. InvalidDataException, in plain words, when it can't be read.</summary>
        public static string Read(string path)
        {
            try { return ReadText(path); }
            catch (Exception ex) when (!(ex is InvalidDataException || ex is IOException || ex is UnauthorizedAccessException || ex is OutOfMemoryException))
            {
                // Something in the file that a well-made one doesn't have.
                throw new InvalidDataException(Lang.T("\"{0}\" couldn't be read: it isn't a whole {1} file (it may be damaged, or locked with a password).",
                    Path.GetFileName(path), Lang.T(KindOf(path))), ex);
            }
        }

        /// <summary>Reads the file's parts and turns them into text, by its kind (PDFs go to PdfText).</summary>
        private static string ReadText(string path)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext == ".pdf") return PdfText.Read(path);
            Dictionary<string, byte[]> parts;
            try { parts = Unpack(File.ReadAllBytes(path)); }
            catch (InvalidDataException)
            {
                throw new InvalidDataException(Lang.T("\"{0}\" couldn't be read: it isn't a whole {1} file (it may be damaged, or locked with a password).",
                    Path.GetFileName(path), Lang.T(KindOf(path))));
            }
            string text;
            switch (ext)
            {
                case ".docx": case ".docm": text = Word(parts); break;
                case ".xlsx": case ".xlsm": text = Excel(parts); break;
                case ".pptx": case ".pptm": text = PowerPoint(parts); break;
                case ".odt": text = OpenText(parts); break;
                case ".ods": text = OpenSheets(parts); break;
                default: text = OpenSlides(parts); break;
            }
            return text.TrimEnd() + "\n";
        }

        // ================================================================== reading the package

        /// <summary>The package's parts, each up to <see cref="MaxPart"/> and all of them up to <see cref="MaxTotal"/>.</summary>
        public static Dictionary<string, byte[]> Unpack(byte[] data)
        {
            var parts = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            long budget = MaxTotal;
            using (var zip = new ZipArchive(new MemoryStream(data), ZipArchiveMode.Read))
                foreach (var e in zip.Entries)
                {
                    // Only the parts with words in them: pictures and the like are never unpacked.
                    if (!e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) && !e.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)) continue;
                    if (budget <= 0) break;
                    var most = Math.Min(MaxPart, budget);
                    using (var s = e.Open())
                    using (var m = new MemoryStream())
                    {
                        var buffer = new byte[1 << 16];
                        int n;
                        while (m.Length < most && (n = s.Read(buffer, 0, (int)Math.Min(buffer.Length, most - m.Length))) > 0) m.Write(buffer, 0, n);
                        budget -= m.Length;
                        parts[e.FullName.Replace('\\', '/').TrimStart('/')] = m.ToArray();
                    }
                }
            return parts;
        }

        /// <summary>A part as XML: no DTDs, nothing fetched from elsewhere. Null when it's missing or not XML.</summary>
        private static XDocument Xml(Dictionary<string, byte[]> parts, string name)
        {
            if (name == null || !parts.TryGetValue(name.TrimStart('/'), out var data)) return null;
            try
            {
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true };
                using (var reader = XmlReader.Create(new MemoryStream(data), settings)) return XDocument.Load(reader);
            }
            catch (XmlException) { return null; }
        }

        /// <summary>The targets of a part's links ("_rels" file next to it), by link id.</summary>
        private static Dictionary<string, (string Type, string Target)> Links(Dictionary<string, byte[]> parts, string part)
        {
            var folder = part.Contains("/") ? part.Substring(0, part.LastIndexOf('/')) : string.Empty;
            var relsName = (folder.Length > 0 ? folder + "/" : string.Empty) + "_rels/" + part.Substring(part.LastIndexOf('/') + 1) + ".rels";
            var result = new Dictionary<string, (string, string)>();
            var rels = Xml(parts, relsName);
            if (rels?.Root == null) return result;
            foreach (var r in rels.Root.Elements().Where(e => e.Name.LocalName == "Relationship"))
            {
                var id = (string)r.Attribute("Id");
                var target = (string)r.Attribute("Target");
                if (id == null || target == null || (string)r.Attribute("TargetMode") == "External") continue;
                result[id] = ((string)r.Attribute("Type") ?? string.Empty, Resolve(folder, target));
            }
            return result;
        }

        /// <summary>
        /// The full name of a part a link points to, from the folder of the part that links to it ("../media/x" and
        /// "/x" too).
        /// </summary>
        private static string Resolve(string folder, string target)
        {
            var parts = new List<string>(target.StartsWith("/") ? new string[0] : folder.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries));
            foreach (var p in target.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); }
                else if (p != ".") parts.Add(p);
            }
            return string.Join("/", parts);
        }

        /// <summary>The elements below x with this name, whatever their namespace.</summary>
        private static IEnumerable<XElement> Named(XContainer x, string local) => x.Descendants().Where(e => e.Name.LocalName == local);

        // ================================================================== Word

        /// <summary>Word's XML namespace.</summary>
        private static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

        /// <summary>
        /// A Word document's text: the body (paragraphs and tables), then headers, footers and footnotes.
        /// </summary>
        private static string Word(Dictionary<string, byte[]> parts)
        {
            var main = MainPart(parts, "word/document.xml");
            var doc = Xml(parts, main) ?? throw new InvalidDataException(Lang.T("There's no text in this Word document."));
            var sb = new StringBuilder();
            var body = doc.Root?.Element(W + "body");
            if (body != null) WordBlocks(body, sb);
            var links = Links(parts, main);
            void Section(string heading, IEnumerable<string> names)
            {
                var texts = new List<string>();
                foreach (var name in names)
                {
                    var x = Xml(parts, name);
                    if (x?.Root == null) continue;
                    var one = new StringBuilder();
                    WordBlocks(x.Root, one);
                    var t = one.ToString().Trim();
                    if (t.Length > 0 && !texts.Contains(t)) texts.Add(t);
                }
                if (texts.Count == 0) return;
                sb.Append('\n').Append("== ").Append(Lang.T(heading)).Append(" ==\n");
                foreach (var t in texts) sb.Append(t).Append('\n');
            }
            string[] Of(string type) => links.Values.Where(l => l.Type.EndsWith("/" + type)).Select(l => l.Target).OrderBy(t => t, StringComparer.Ordinal).ToArray();
            Section(Shown("Headers"), Of("header"));
            Section(Shown("Footers"), Of("footer"));
            Section(Shown("Footnotes"), Of("footnotes"));
            Section(Shown("Endnotes"), Of("endnotes"));
            return sb.ToString();
        }

        /// <summary>The main part named by the package's own list (_rels/.rels), else the usual name.</summary>
        private static string MainPart(Dictionary<string, byte[]> parts, string usual)
        {
            var root = Xml(parts, "_rels/.rels");
            var main = root?.Root?.Elements().Where(e => e.Name.LocalName == "Relationship" && ((string)e.Attribute("Type") ?? string.Empty).EndsWith("/officeDocument"))
                .Select(e => Resolve(string.Empty, (string)e.Attribute("Target") ?? string.Empty)).FirstOrDefault();
            return main != null && parts.ContainsKey(main) ? main : usual;
        }

        /// <summary>Paragraphs and tables, in order. Footnote separators and deleted text are left out.</summary>
        private static void WordBlocks(XElement container, StringBuilder sb)
        {
            foreach (var e in container.Elements())
            {
                var name = e.Name.LocalName;
                if (name == "p") sb.Append(WordParagraph(e)).Append('\n');
                else if (name == "tbl")
                {
                    foreach (var row in e.Elements(W + "tr"))
                        sb.Append(string.Join(" | ", row.Elements(W + "tc").Select(c => string.Join(" ", c.Descendants(W + "p").Select(WordParagraph)).Trim()))).Append('\n');
                }
                else if (name == "footnote" || name == "endnote")
                {
                    var type = (string)e.Attribute(W + "type");
                    if (type == "separator" || type == "continuationSeparator" || type == "continuationNotice") continue;
                    WordBlocks(e, sb);
                }
                else if (name == "sdt" || name == "sdtContent" || name == "ins" || name == "customXml" || name == "smartTag") WordBlocks(e, sb);
            }
        }

        /// <summary>
        /// One Word paragraph's text: its runs, tabs and line breaks; deleted text left out (tracked changes as if
        /// accepted).
        /// </summary>
        private static string WordParagraph(XElement p)
        {
            var sb = new StringBuilder();
            foreach (var e in p.Descendants())
            {
                // Deleted text (w:delText) and comments' marks are not words of the document.
                switch (e.Name.LocalName)
                {
                    case "t": if (e.Name.Namespace == W && !e.Ancestors(W + "del").Any()) sb.Append(e.Value); break;
                    case "tab": if (e.Parent?.Name.LocalName == "r") sb.Append('\t'); break;
                    case "br": case "cr": if (e.Parent?.Name.LocalName == "r") sb.Append(' '); break;
                    case "noBreakHyphen": sb.Append('-'); break;
                }
            }
            return sb.ToString().TrimEnd();
        }

        // ================================================================== Excel

        /// <summary>Excel's XML namespace.</summary>
        private static readonly XNamespace S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        /// <summary>The namespace of links between parts.</summary>
        private static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        /// <summary>An Excel workbook's text: each sheet's name, then a line per row of its filled cells.</summary>
        private static string Excel(Dictionary<string, byte[]> parts)
        {
            var main = MainPart(parts, "xl/workbook.xml");
            var book = Xml(parts, main) ?? throw new InvalidDataException(Lang.T("There are no sheets in this Excel workbook."));
            var links = Links(parts, main);
            var shared = new List<string>();
            var sst = links.Values.FirstOrDefault(l => l.Type.EndsWith("/sharedStrings")).Target ?? "xl/sharedStrings.xml";
            var strings = Xml(parts, sst);
            if (strings?.Root != null)
                foreach (var si in strings.Root.Elements(S + "si"))
                    shared.Add(string.Concat(si.Descendants(S + "t").Where(t => !t.Ancestors(S + "rPh").Any()).Select(t => t.Value)));
            var dates = DateStyles(Xml(parts, links.Values.FirstOrDefault(l => l.Type.EndsWith("/styles")).Target ?? "xl/styles.xml"));
            var date1904 = (string)book.Root?.Element(S + "workbookPr")?.Attribute("date1904") is string d && (d == "1" || d == "true");

            var sb = new StringBuilder();
            foreach (var sheet in book.Root?.Element(S + "sheets")?.Elements(S + "sheet") ?? Enumerable.Empty<XElement>())
            {
                var id = (string)sheet.Attribute(R + "id");
                if (id == null || !links.TryGetValue(id, out var link)) continue;
                sb.Append(Lang.T("Sheet:")).Append(' ').Append((string)sheet.Attribute("name")).Append('\n');
                var x = Xml(parts, link.Target);
                foreach (var row in x?.Root?.Element(S + "sheetData")?.Elements(S + "row") ?? Enumerable.Empty<XElement>())
                {
                    var cells = new SortedDictionary<int, string>();
                    var rowNumber = (int?)row.Attribute("r") ?? 0;
                    var next = 0;
                    foreach (var c in row.Elements(S + "c"))
                    {
                        var (col, rowOf) = Address((string)c.Attribute("r"));
                        if (col < 0) col = next;
                        if (rowOf > 0) rowNumber = rowOf;
                        next = col + 1;
                        var value = CellValue(c, shared, dates, date1904);
                        var f = c.Element(S + "f")?.Value;
                        if (ShowFormulas && !string.IsNullOrEmpty(f)) value = (value + "  (=" + f + ")").Trim();
                        if (!string.IsNullOrEmpty(value)) cells[col] = value;
                    }
                    if (cells.Count == 0) continue;
                    sb.Append(RowLine(cells, rowNumber)).Append('\n');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>"A5  Rice | 42 | 3.50": from the row's first filled cell, empty cells in between kept so columns line up.</summary>
        private static string RowLine(SortedDictionary<int, string> cells, int row)
        {
            int first = cells.Keys.First(), last = cells.Keys.Last();
            var values = Enumerable.Range(first, Math.Min(last - first + 1, 16384)).Select(i => cells.TryGetValue(i, out var v) ? v.Replace("\r", " ").Replace("\n", " ") : string.Empty);
            return ColumnName(first) + row.ToString(CultureInfo.InvariantCulture) + "  " + string.Join(" | ", values);
        }

        /// <summary>A column's letters from its number (0 is "A", 26 is "AA").</summary>
        public static string ColumnName(int index)
        {
            var name = string.Empty;
            for (var n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name;
            return name;
        }

        /// <summary>A cell address such as "C12" as its column (from 0) and row.</summary>
        private static (int Column, int Row) Address(string r)
        {
            if (string.IsNullOrEmpty(r)) return (-1, 0);
            int col = 0, i = 0;
            for (; i < r.Length && char.IsLetter(r[i]); i++) col = col * 26 + (char.ToUpperInvariant(r[i]) - 'A' + 1);
            int.TryParse(r.Substring(i), NumberStyles.None, CultureInfo.InvariantCulture, out var row);
            return (i == 0 ? -1 : col - 1, row);
        }

        /// <summary>
        /// A cell's value as shown: shared text, true or false, a date when its style is a date, or the number (and the
        /// formula when asked).
        /// </summary>
        private static string CellValue(XElement c, List<string> shared, HashSet<int> dates, bool date1904)
        {
            var type = (string)c.Attribute("t");
            var v = c.Element(S + "v")?.Value;
            switch (type)
            {
                case "s": return int.TryParse(v, out var i) && i >= 0 && i < shared.Count ? shared[i] : string.Empty;
                case "inlineStr": return string.Concat(c.Element(S + "is")?.Descendants(S + "t").Select(t => t.Value) ?? Enumerable.Empty<string>());
                case "b": return v == "1" ? "TRUE" : v == "0" ? "FALSE" : v ?? string.Empty;
                case "str": case "e": return v ?? string.Empty;
            }
            if (v == null) return string.Empty;
            if (int.TryParse((string)c.Attribute("s"), out var style) && dates.Contains(style) &&
                double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial > -1 && serial < 2958466)
            {
                var when = (date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30)).AddDays(serial);
                return when.TimeOfDay.Ticks == 0 ? when.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                    : serial < 1 ? when.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : when.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
            return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) ? number.ToString("R", CultureInfo.InvariantCulture) : v;
        }

        /// <summary>The cell styles that show a date or time (Excel keeps dates as numbers).</summary>
        private static HashSet<int> DateStyles(XDocument styles)
        {
            var result = new HashSet<int>();
            if (styles?.Root == null) return result;
            var custom = new HashSet<int>();
            foreach (var f in styles.Root.Element(S + "numFmts")?.Elements(S + "numFmt") ?? Enumerable.Empty<XElement>())
            {
                var code = System.Text.RegularExpressions.Regex.Replace((string)f.Attribute("formatCode") ?? string.Empty, "\"[^\"]*\"|\\[[^\\]]*\\]|\\\\.", string.Empty);
                if (System.Text.RegularExpressions.Regex.IsMatch(code, "[dmyhs]", System.Text.RegularExpressions.RegexOptions.IgnoreCase) && int.TryParse((string)f.Attribute("numFmtId"), out var id))
                    custom.Add(id);
            }
            var index = 0;
            foreach (var xf in styles.Root.Element(S + "cellXfs")?.Elements(S + "xf") ?? Enumerable.Empty<XElement>())
            {
                if (int.TryParse((string)xf.Attribute("numFmtId"), out var id) && ((id >= 14 && id <= 22) || (id >= 45 && id <= 47) || custom.Contains(id))) result.Add(index);
                index++;
            }
            return result;
        }

        // ================================================================== PowerPoint

        /// <summary>PowerPoint's XML namespace.</summary>
        private static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        /// <summary>The namespace of drawings (where slide text is kept).</summary>
        private static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

        /// <summary>A PowerPoint presentation's text: "Slide n", the slide's text, then its speaker notes.</summary>
        private static string PowerPoint(Dictionary<string, byte[]> parts)
        {
            var main = MainPart(parts, "ppt/presentation.xml");
            var pres = Xml(parts, main) ?? throw new InvalidDataException(Lang.T("There are no slides in this PowerPoint presentation."));
            var links = Links(parts, main);
            var sb = new StringBuilder();
            var n = 0;
            foreach (var id in pres.Root?.Element(P + "sldIdLst")?.Elements(P + "sldId") ?? Enumerable.Empty<XElement>())
            {
                var rid = (string)id.Attribute(R + "id");
                if (rid == null || !links.TryGetValue(rid, out var link)) continue;
                n++;
                sb.Append(Lang.T("Slide {0}", n)).Append('\n');
                var slide = Xml(parts, link.Target);
                if (slide?.Root != null) SlideText(slide.Root, sb, false);
                var notes = Links(parts, link.Target).Values.FirstOrDefault(l => l.Type.EndsWith("/notesSlide")).Target;
                var notesXml = Xml(parts, notes);
                if (notesXml?.Root != null)
                {
                    var one = new StringBuilder();
                    SlideText(notesXml.Root, one, true);
                    if (one.Length > 0) sb.Append(Lang.T("Notes:")).Append('\n').Append(one);
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>The text of each shape, paragraph by paragraph. In notes, only the notes themselves (not the slide picture or its number).</summary>
        private static void SlideText(XElement root, StringBuilder sb, bool notes)
        {
            foreach (var shape in Named(root, "sp"))
            {
                if (notes)
                {
                    var type = (string)Named(shape, "ph").FirstOrDefault()?.Attribute("type");
                    if (type != "body") continue;
                }
                foreach (var p in shape.Descendants(A + "p"))
                {
                    var text = string.Concat(p.Elements().Select(e => e.Name.LocalName == "r" || e.Name.LocalName == "fld" ? (string)e.Element(A + "t") : e.Name.LocalName == "br" ? " " : null)).TrimEnd();
                    if (text.Length > 0) sb.Append(text).Append('\n');
                }
            }
            foreach (var table in Named(root, "tbl"))
                foreach (var row in table.Elements(A + "tr"))
                    sb.Append(string.Join(" | ", row.Elements(A + "tc").Select(c => string.Join(" ", c.Descendants(A + "t").Select(t => t.Value)).Trim()))).Append('\n');
        }

        // ================================================================== the free office suites (OpenDocument)

        /// <summary>
        /// The OpenDocument namespaces (.odt, .ods, .odp): text, tables, the document, drawings, slides and styles.
        /// </summary>
        private static readonly XNamespace Text = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
        private static readonly XNamespace Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
        private static readonly XNamespace Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
        private static readonly XNamespace Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
        private static readonly XNamespace Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";
        private static readonly XNamespace Style = "urn:oasis:names:tc:opendocument:xmlns:style:1.0";

        /// <summary>The OpenDocument file's content.xml, where its text is.</summary>
        private static XDocument Content(Dictionary<string, byte[]> parts) =>
            Xml(parts, "content.xml") ?? throw new InvalidDataException(Lang.T("There's no content in this file."));

        /// <summary>An .odt document's text: its paragraphs, headings, lists and tables.</summary>
        private static string OpenText(Dictionary<string, byte[]> parts)
        {
            var sb = new StringBuilder();
            var body = Content(parts).Root?.Element(Office + "body")?.Element(Office + "text");
            if (body != null) OpenBlocks(body, sb);
            var styles = Xml(parts, "styles.xml");
            foreach (var (local, heading) in new[] { ("header", Shown("Headers")), ("footer", Shown("Footers")) })
            {
                var texts = styles == null ? new List<string>() : Named(styles, local).Where(e => e.Name.Namespace == Style)
                    .Select(e => { var one = new StringBuilder(); OpenBlocks(e, one); return one.ToString().Trim(); }).Where(t => t.Length > 0).Distinct().ToList();
                if (texts.Count == 0) continue;
                sb.Append('\n').Append("== ").Append(Lang.T(heading)).Append(" ==\n");
                foreach (var t in texts) sb.Append(t).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>
        /// Writes the paragraphs, headings, lists and tables inside an OpenDocument element, a line each.
        /// </summary>
        private static void OpenBlocks(XElement container, StringBuilder sb)
        {
            foreach (var e in container.Elements())
            {
                if (e.Name == Text + "p" || e.Name == Text + "h") sb.Append(OpenParagraph(e)).Append('\n');
                else if (e.Name == Table + "table")
                {
                    foreach (var row in e.Descendants(Table + "table-row"))
                        sb.Append(string.Join(" | ", row.Elements(Table + "table-cell").Select(c => string.Join(" ", c.Elements(Text + "p").Select(OpenParagraph)).Trim()))).Append('\n');
                }
                else if (e.Name.Namespace == Text || e.Name.LocalName == "section") OpenBlocks(e, sb);
            }
        }

        /// <summary>One OpenDocument paragraph's text, with its spaces, tabs and line breaks.</summary>
        private static string OpenParagraph(XElement p)
        {
            var sb = new StringBuilder();
            void Walk(XElement e)
            {
                foreach (var node in e.Nodes())
                {
                    if (node is XText t) sb.Append(t.Value);
                    else if (node is XElement x)
                    {
                        if (x.Name == Text + "s") sb.Append(' ', Math.Max(1, Math.Min(100, (int?)x.Attribute(Text + "c") ?? 1)));
                        else if (x.Name == Text + "tab") sb.Append('\t');
                        else if (x.Name == Text + "line-break") sb.Append(' ');
                        else if (x.Name == Office + "annotation" || x.Name == Text + "note-citation" || x.Name == Text + "tracked-changes") continue;
                        else Walk(x);
                    }
                }
            }
            Walk(p);
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// An .ods spreadsheet's text: each sheet's name, then a line per filled row (a row marked as repeated is
        /// written as many times, up to 1,000).
        /// </summary>
        private static string OpenSheets(Dictionary<string, byte[]> parts)
        {
            var sb = new StringBuilder();
            var spreadsheet = Content(parts).Root?.Element(Office + "body")?.Element(Office + "spreadsheet");
            foreach (var table in spreadsheet?.Elements(Table + "table") ?? Enumerable.Empty<XElement>())
            {
                sb.Append(Lang.T("Sheet:")).Append(' ').Append((string)table.Attribute(Table + "name")).Append('\n');
                var row = 0;
                foreach (var r in table.Descendants(Table + "table-row"))
                {
                    var repeat = Math.Max(1, Math.Min(1 << 20, (int?)r.Attribute(Table + "number-rows-repeated") ?? 1));
                    var cells = new SortedDictionary<int, string>();
                    var col = 0;
                    foreach (var c in r.Elements().Where(e => e.Name == Table + "table-cell" || e.Name == Table + "covered-table-cell"))
                    {
                        var span = Math.Max(1, Math.Min(16384, (int?)c.Attribute(Table + "number-columns-repeated") ?? 1));
                        var value = string.Join(" ", c.Elements(Text + "p").Select(OpenParagraph)).Trim();
                        var f = (string)c.Attribute(Table + "formula");
                        if (ShowFormulas && !string.IsNullOrEmpty(f)) value = (value + "  (" + (f.StartsWith("of:") ? f.Substring(3) : f) + ")").Trim();
                        if (value.Length > 0)
                            for (var k = 0; k < span && col + k < 16384; k++) cells[col + k] = value;
                        col += span;
                    }
                    if (cells.Count > 0)
                        for (var k = 0; k < Math.Min(repeat, 1000); k++) sb.Append(RowLine(cells, row + k + 1)).Append('\n');
                    row += repeat;
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>An .odp presentation's text: "Slide n", the slide's text, then its notes.</summary>
        private static string OpenSlides(Dictionary<string, byte[]> parts)
        {
            var sb = new StringBuilder();
            var presentation = Content(parts).Root?.Element(Office + "body")?.Element(Office + "presentation");
            var n = 0;
            foreach (var page in presentation?.Elements(Draw + "page") ?? Enumerable.Empty<XElement>())
            {
                n++;
                sb.Append(Lang.T("Slide {0}", n)).Append('\n');
                foreach (var frame in page.Elements())
                {
                    if (frame.Name == Presentation + "notes") continue;
                    foreach (var p in frame.Descendants().Where(e => e.Name == Text + "p" || e.Name == Text + "h"))
                    {
                        var t = OpenParagraph(p);
                        if (t.Length > 0) sb.Append(t).Append('\n');
                    }
                }
                var notes = page.Element(Presentation + "notes");
                if (notes != null)
                {
                    var lines = notes.Descendants().Where(e => e.Name == Text + "p").Select(OpenParagraph).Where(t => t.Length > 0).ToList();
                    if (lines.Count > 0) sb.Append(Lang.T("Notes:")).Append('\n').Append(string.Join("\n", lines)).Append('\n');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }
    }
}
