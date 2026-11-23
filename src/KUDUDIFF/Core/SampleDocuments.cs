using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace Kududiff.Core
{
    /// <summary>
    /// Small but real Word, Excel, PowerPoint, OpenDocument and PDF files, made for the tests and the self-test (the way
    /// Office and other programs lay them out, with only the parts that matter here).
    /// </summary>
    public static class SampleDocuments
    {
        private const string Rels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
        private const string Pkg = "http://schemas.openxmlformats.org/package/2006/relationships";

        private static string E(string s) => SecurityElement.Escape(s ?? string.Empty);

        public static byte[] Zip(params (string Name, string Text)[] parts)
        {
            var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                foreach (var (name, text) in parts)
                    using (var s = zip.CreateEntry(name).Open())
                    {
                        var b = Encoding.UTF8.GetBytes(text);
                        s.Write(b, 0, b.Length);
                    }
            return output.ToArray();
        }

        private static string RelsXml(params (string Id, string Type, string Target)[] links) =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"" + Pkg + "\">" +
            string.Concat(links.Select(l => $"<Relationship Id=\"{l.Id}\" Type=\"{Rels}/{l.Type}\" Target=\"{l.Target}\"/>")) + "</Relationships>";

        // ================================================================== Word

        /// <summary>A Word document: paragraphs, a table row per "a|b" line, an inserted and a deleted word, header, footer and a footnote.</summary>
        public static byte[] Word(IEnumerable<string> lines, string header = "Kerala Boats Ltd", string footer = "Page 1", string footnote = "Prices without tax.",
            string inserted = null, string deleted = null)
        {
            const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            string Run(string t) => $"<w:r><w:t xml:space=\"preserve\">{E(t)}</w:t></w:r>";
            var body = new StringBuilder();
            foreach (var line in lines)
            {
                if (line.Contains("|"))
                    body.Append("<w:tbl><w:tr>").Append(string.Concat(line.Split('|').Select(c => $"<w:tc><w:p>{Run(c.Trim())}</w:p></w:tc>"))).Append("</w:tr></w:tbl>");
                else body.Append("<w:p>").Append(Run(line)).Append("</w:p>");
            }
            body.Append("<w:p>").Append(Run("Changed words:"));
            if (inserted != null) body.Append($"<w:ins w:id=\"1\" w:author=\"A\">{Run(" " + inserted)}</w:ins>");
            if (deleted != null) body.Append($"<w:del w:id=\"2\" w:author=\"A\"><w:r><w:delText xml:space=\"preserve\"> {E(deleted)}</w:delText></w:r></w:del>");
            body.Append("<w:commentRangeStart w:id=\"0\"/>").Append(Run(" end.")).Append("<w:commentRangeEnd w:id=\"0\"/><w:r><w:commentReference w:id=\"0\"/></w:r>");
            body.Append("<w:r><w:footnoteReference w:id=\"1\"/></w:r></w:p>");
            string Part(string root, string inner) => $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><w:{root} xmlns:w=\"{w}\" xmlns:r=\"{Rels}\">{inner}</w:{root}>";
            return Zip(
                ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
                ("_rels/.rels", RelsXml(("rId1", "officeDocument", "word/document.xml"))),
                ("word/document.xml", Part("document", "<w:body>" + body + "</w:body>")),
                ("word/_rels/document.xml.rels", RelsXml(("rId1", "header", "header1.xml"), ("rId2", "footer", "footer1.xml"), ("rId3", "footnotes", "footnotes.xml"), ("rId4", "comments", "comments.xml"))),
                ("word/header1.xml", Part("hdr", $"<w:p>{Run(header)}</w:p>")),
                ("word/footer1.xml", Part("ftr", $"<w:p>{Run(footer)}</w:p>")),
                ("word/footnotes.xml", Part("footnotes", $"<w:footnote w:type=\"separator\" w:id=\"-1\"><w:p><w:r><w:separator/></w:r></w:p></w:footnote><w:footnote w:id=\"1\"><w:p>{Run(footnote)}</w:p></w:footnote>")),
                ("word/comments.xml", Part("comments", $"<w:comment w:id=\"0\" w:author=\"B\"><w:p>{Run("A comment that isn't compared")}</w:p></w:comment>")));
        }

        // ================================================================== Excel

        /// <summary>A workbook: each sheet is rows of cells ("=..." after a value in a cell, as "42=SUM(A1:A2)", is a formula).</summary>
        public static byte[] Excel(params (string Name, string[][] Rows)[] sheets)
        {
            const string s = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            var strings = new List<string>();
            int Shared(string t)
            {
                var i = strings.IndexOf(t);
                if (i < 0) { strings.Add(t); i = strings.Count - 1; }
                return i;
            }
            var parts = new List<(string, string)>
            {
                ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
                ("_rels/.rels", RelsXml(("rId1", "officeDocument", "xl/workbook.xml")))
            };
            var book = new StringBuilder($"<?xml version=\"1.0\"?><workbook xmlns=\"{s}\" xmlns:r=\"{Rels}\"><sheets>");
            var links = new List<(string, string, string)>();
            for (var n = 0; n < sheets.Length; n++)
            {
                book.Append($"<sheet name=\"{E(sheets[n].Name)}\" sheetId=\"{n + 1}\" r:id=\"rId{n + 1}\"/>");
                links.Add(($"rId{n + 1}", "worksheet", $"worksheets/sheet{n + 1}.xml"));
                var data = new StringBuilder();
                for (var r = 0; r < sheets[n].Rows.Length; r++)
                {
                    data.Append($"<row r=\"{r + 1}\">");
                    for (var c = 0; c < sheets[n].Rows[r].Length; c++)
                    {
                        var cell = sheets[n].Rows[r][c];
                        if (string.IsNullOrEmpty(cell)) continue;
                        var at = DocumentText.ColumnName(c) + (r + 1);
                        var eq = cell.IndexOf('=');
                        var value = eq > 0 ? cell.Substring(0, eq) : cell;
                        var formula = eq > 0 ? $"<f>{E(cell.Substring(eq + 1))}</f>" : string.Empty;
                        if (value.StartsWith("date:"))
                            data.Append($"<c r=\"{at}\" s=\"1\">{formula}<v>{DateTime.Parse(value.Substring(5), CultureInfo.InvariantCulture).ToOADate().ToString(CultureInfo.InvariantCulture)}</v></c>");
                        else if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                            data.Append($"<c r=\"{at}\">{formula}<v>{value}</v></c>");
                        else if (value == "TRUE" || value == "FALSE")
                            data.Append($"<c r=\"{at}\" t=\"b\">{formula}<v>{(value == "TRUE" ? 1 : 0)}</v></c>");
                        else data.Append($"<c r=\"{at}\" t=\"s\">{formula}<v>{Shared(value)}</v></c>");
                    }
                    data.Append("</row>");
                }
                parts.Add(($"xl/worksheets/sheet{n + 1}.xml", $"<?xml version=\"1.0\"?><worksheet xmlns=\"{s}\"><sheetData>{data}</sheetData></worksheet>"));
            }
            book.Append("</sheets></workbook>");
            links.Add(("rIdS", "sharedStrings", "sharedStrings.xml"));
            links.Add(("rIdT", "styles", "styles.xml"));
            parts.Add(("xl/workbook.xml", book.ToString()));
            parts.Add(("xl/_rels/workbook.xml.rels", RelsXml(links.ToArray())));
            parts.Add(("xl/sharedStrings.xml", $"<?xml version=\"1.0\"?><sst xmlns=\"{s}\">" + string.Concat(strings.Select(t => $"<si><t xml:space=\"preserve\">{E(t)}</t></si>")) + "</sst>"));
            parts.Add(("xl/styles.xml", $"<?xml version=\"1.0\"?><styleSheet xmlns=\"{s}\"><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\"/></cellXfs></styleSheet>"));
            return Zip(parts.ToArray());
        }

        // ================================================================== PowerPoint

        /// <summary>A presentation: each slide's lines, and its speaker notes. The slides are stored out of order, as Office does after moving them.</summary>
        public static byte[] PowerPoint(params (string[] Lines, string Notes)[] slides)
        {
            const string p = "http://schemas.openxmlformats.org/presentationml/2006/main";
            const string a = "http://schemas.openxmlformats.org/drawingml/2006/main";
            string Shape(IEnumerable<string> lines, string placeholder = null) =>
                "<p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"t\"/><p:cNvSpPr/><p:nvPr>" + (placeholder != null ? $"<p:ph type=\"{placeholder}\"/>" : string.Empty) + "</p:nvPr></p:nvSpPr>" +
                "<p:txBody><a:bodyPr/>" + string.Concat(lines.Select(l => $"<a:p><a:r><a:t>{E(l)}</a:t></a:r></a:p>")) + "</p:txBody></p:sp>";
            string Slide(string inner, string root = "sld") =>
                $"<?xml version=\"1.0\"?><p:{root} xmlns:p=\"{p}\" xmlns:a=\"{a}\" xmlns:r=\"{Rels}\"><p:cSld><p:spTree>{inner}</p:spTree></p:cSld></p:{root}>";
            var parts = new List<(string, string)>
            {
                ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"/>"),
                ("_rels/.rels", RelsXml(("rId1", "officeDocument", "ppt/presentation.xml")))
            };
            var ids = new StringBuilder();
            var links = new List<(string, string, string)>();
            for (var n = 0; n < slides.Length; n++)
            {
                var file = slides.Length - n; // stored in the other order: slide3.xml is shown first
                ids.Append($"<p:sldId id=\"{256 + n}\" r:id=\"rId{n + 1}\"/>");
                links.Add(($"rId{n + 1}", "slide", $"slides/slide{file}.xml"));
                parts.Add(($"ppt/slides/slide{file}.xml", Slide(Shape(slides[n].Lines))));
                if (slides[n].Notes != null)
                {
                    parts.Add(($"ppt/slides/_rels/slide{file}.xml.rels", RelsXml(("rId1", "notesSlide", $"../notesSlides/notesSlide{file}.xml"))));
                    parts.Add(($"ppt/notesSlides/notesSlide{file}.xml", Slide(Shape(new[] { "slide picture" }, "sldImg") + Shape(new[] { slides[n].Notes }, "body") + Shape(new[] { "7" }, "sldNum"), "notes")));
                }
            }
            parts.Add(("ppt/presentation.xml", $"<?xml version=\"1.0\"?><p:presentation xmlns:p=\"{p}\" xmlns:r=\"{Rels}\"><p:sldIdLst>{ids}</p:sldIdLst></p:presentation>"));
            parts.Add(("ppt/_rels/presentation.xml.rels", RelsXml(links.ToArray())));
            return Zip(parts.ToArray());
        }

        // ================================================================== OpenDocument (the free office suites)

        private const string OdfNs = "xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\" " +
                                     "xmlns:table=\"urn:oasis:names:tc:opendocument:xmlns:table:1.0\" xmlns:draw=\"urn:oasis:names:tc:opendocument:xmlns:drawing:1.0\" " +
                                     "xmlns:presentation=\"urn:oasis:names:tc:opendocument:xmlns:presentation:1.0\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\"";

        private static byte[] Odf(string mime, string body, string styles = "") => Zip(
            ("mimetype", mime),
            ("content.xml", $"<?xml version=\"1.0\"?><office:document-content {OdfNs}><office:body>{body}</office:body></office:document-content>"),
            ("styles.xml", $"<?xml version=\"1.0\"?><office:document-styles {OdfNs}><office:master-styles>{styles}</office:master-styles></office:document-styles>"));

        public static byte[] OpenText(IEnumerable<string> lines, string header = "Kerala Boats Ltd") => Odf("application/vnd.oasis.opendocument.text",
            "<office:text>" + string.Concat(lines.Select(l => l.Contains("|")
                ? "<table:table><table:table-row>" + string.Concat(l.Split('|').Select(c => $"<table:table-cell><text:p>{E(c.Trim())}</text:p></table:table-cell>")) + "</table:table-row></table:table>"
                : $"<text:p>{E(l).Replace("  ", " <text:s/>")}</text:p>")) + "</office:text>",
            $"<style:master-page style:name=\"Standard\"><style:header><text:p>{E(header)}</text:p></style:header></style:master-page>");

        public static byte[] OpenSheets(string name, string[][] rows) => Odf("application/vnd.oasis.opendocument.spreadsheet",
            $"<office:spreadsheet><table:table table:name=\"{E(name)}\">" + string.Concat(rows.Select(r => "<table:table-row>" + string.Concat(r.Select(c =>
            {
                var eq = c.IndexOf('=');
                var formula = eq > 0 ? $" table:formula=\"of:={E(c.Substring(eq + 1))}\"" : string.Empty;
                var v = eq > 0 ? c.Substring(0, eq) : c;
                return v.Length == 0 ? "<table:table-cell/>" : $"<table:table-cell{formula}><text:p>{E(v)}</text:p></table:table-cell>";
            })) + "</table:table-row>")) + "<table:table-row table:number-rows-repeated=\"1048000\"><table:table-cell table:number-columns-repeated=\"1024\"/></table:table-row></table:table></office:spreadsheet>");

        public static byte[] OpenSlides(params (string[] Lines, string Notes)[] slides) => Odf("application/vnd.oasis.opendocument.presentation",
            "<office:presentation>" + string.Concat(slides.Select((s, i) =>
                $"<draw:page draw:name=\"page{i + 1}\"><draw:frame><draw:text-box>" + string.Concat(s.Lines.Select(l => $"<text:p>{E(l)}</text:p>")) + "</draw:text-box></draw:frame>" +
                (s.Notes != null ? $"<presentation:notes><draw:frame><draw:text-box><text:p>{E(s.Notes)}</text:p></draw:text-box></draw:frame></presentation:notes>" : string.Empty) +
                "</draw:page>")) + "</office:presentation>");

        // ================================================================== PDF

        /// <summary>A PDF with a page per list of lines (Helvetica). A page with no lines has only a drawn box, like a scan has only a picture.</summary>
        public static byte[] Pdf(params string[][] pages)
        {
            var objects = new List<string>();
            var pageIds = new List<int>();
            // 1 catalog, 2 pages, 3 font; then a page and its content for each page.
            objects.Add(null);
            objects.Add(null);
            objects.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
            foreach (var lines in pages)
            {
                var content = new StringBuilder();
                if (lines.Length == 0) content.Append("0.5 g 72 300 450 400 re f\n");
                var y = 760;
                foreach (var line in lines)
                {
                    var t = line.Replace("\\", "\\\\").Replace("(", "\\(").Replace(")", "\\)");
                    content.Append($"BT /F1 12 Tf 72 {y} Td ({t}) Tj ET\n");
                    y -= 18;
                }
                var c = content.ToString();
                objects.Add($"<< /Length {Encoding.GetEncoding(28591).GetByteCount(c)} >>\nstream\n{c}endstream");
                var contentId = objects.Count;
                objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Resources << /Font << /F1 3 0 R >> >> /Contents {contentId} 0 R >>");
                pageIds.Add(objects.Count);
            }
            objects[0] = "<< /Type /Catalog /Pages 2 0 R >>";
            objects[1] = "<< /Type /Pages /Kids [" + string.Join(" ", pageIds.Select(i => i + " 0 R")) + $"] /Count {pageIds.Count} >>";
            var latin = Encoding.GetEncoding(28591);
            var output = new MemoryStream();
            void Write(string s) { var b = latin.GetBytes(s); output.Write(b, 0, b.Length); }
            Write("%PDF-1.4\n");
            var offsets = new List<long>();
            for (var i = 0; i < objects.Count; i++)
            {
                offsets.Add(output.Position);
                Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
            }
            var xref = output.Position;
            Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) Write(o.ToString("D10", CultureInfo.InvariantCulture) + " 00000 n \n");
            Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            return output.ToArray();
        }
    }
}
