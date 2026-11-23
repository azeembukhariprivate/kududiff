using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using Kududiff.Core;

namespace Kududiff.Tests
{
    /// <summary>Word, Excel, PowerPoint, OpenDocument and PDF files read as text (1.3).</summary>
    public static partial class Program
    {
        private static string Doc(string name, byte[] data)
        {
            var path = Path.Combine(_root, "docs-" + Guid.NewGuid().ToString("N").Substring(0, 6), name);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllBytes(path, data);
            return path;
        }

        private static void TestWordAsText()
        {
            var path = Doc("letter.docx", SampleDocuments.Word(new[] { "Dear Azeem,", "Name | Price", "The boat race is on Sunday." }, inserted: "new", deleted: "old"));
            var file = TextFile.Read(path);
            Check(file.IsDocument && file.DocumentKind == "Word document", "read as a document");
            var t = file.Text;
            Check(t.StartsWith("Dear Azeem,\nName | Price\nThe boat race is on Sunday.\n"), "paragraphs and a table row in order:\n" + t);
            Check(t.Contains("Changed words: new end."), "tracked changes as if accepted:\n" + t);
            Check(!t.Contains("old"), "deleted words left out");
            Check(!t.Contains("A comment"), "comments left out");
            Check(t.Contains("== Headers ==\nKerala Boats Ltd\n") && t.Contains("== Footers ==\nPage 1\n") && t.Contains("== Footnotes ==\nPrices without tax.\n"), "headers, footers and footnotes at the end:\n" + t);
            Check(!t.Contains("separator"), "the footnote line isn't a note");

            var other = Doc("letter.docx", SampleDocuments.Word(new[] { "Dear Azeem,", "Name | Price", "The boat race is on Saturday." }));
            var cmp = Comparison.Run(t, TextFile.Read(other).Text, new CompareOptions());
            Check(!cmp.Identical && cmp.Summary().StartsWith("1 change "), "the two changed lines next to each other are one change: " + cmp.Summary());
        }

        private static void TestExcelAsText()
        {
            var data = SampleDocuments.Excel(
                ("Prices", new[] { new[] { "Item", "Price", "Qty" }, new[] { "Rice", "3.5", "12" }, new string[0], new[] { "", "", "42=SUM(C2:C3)" }, new[] { "Paid", "TRUE", "date:2026-11-23" } }),
                ("Notes", new[] { new[] { "Only one cell" } }));
            var path = Doc("prices.xlsx", data);
            DocumentText.ShowFormulas = false;
            var t = TextFile.Read(path).Text;
            Equal("Sheet: Prices\nA1  Item | Price | Qty\nA2  Rice | 3.5 | 12\nC4  42\nA5  Paid | TRUE | 2026-11-23\n\nSheet: Notes\nA1  Only one cell\n", t, "rows from their first filled cell, dates as dates");
            DocumentText.ShowFormulas = true;
            try { Check(TextFile.Read(path).Text.Contains("C4  42  (=SUM(C2:C3))"), "the formula after the value when asked"); }
            finally { DocumentText.ShowFormulas = false; }
            Equal("AA", DocumentText.ColumnName(26), "column names past Z");
        }

        private static void TestPowerPointAsText()
        {
            var path = Doc("talk.pptx", SampleDocuments.PowerPoint((new[] { "Welcome", "To the boat race" }, "Smile and say hello"), (new[] { "Thank you" }, null)));
            var t = TextFile.Read(path).Text;
            Equal("Slide 1\nWelcome\nTo the boat race\nNotes:\nSmile and say hello\n\nSlide 2\nThank you\n", t, "slides in the order shown, with their notes only");
        }

        private static void TestOpenDocumentsAsText()
        {
            var odt = TextFile.Read(Doc("a.odt", SampleDocuments.OpenText(new[] { "First  line", "A | B" }))).Text;
            Equal("First  line\nA | B\n\n== Headers ==\nKerala Boats Ltd\n", odt, "text, tables and the header");
            var ods = TextFile.Read(Doc("a.ods", SampleDocuments.OpenSheets("Prices", new[] { new[] { "Rice", "3.50" }, new[] { "", "7=A1*2" } }))).Text;
            Equal("Sheet: Prices\nA1  Rice | 3.50\nB2  7\n", ods, "cells as shown, and a million repeated empty rows cost nothing");
            var odp = TextFile.Read(Doc("a.odp", SampleDocuments.OpenSlides((new[] { "Hello" }, "Say it slowly")))).Text;
            Equal("Slide 1\nHello\nNotes:\nSay it slowly\n", odp, "slides and notes");
        }

        private static void TestPdfAsText()
        {
            var path = Doc("report.pdf", SampleDocuments.Pdf(new[] { "Boat race results", "1. Kerala Blue" }, new[] { "Second page" }));
            var file = TextFile.Read(path);
            Check(file.IsDocument && file.DocumentKind == "PDF", "a PDF");
            Equal("Page 1\nBoat race results\n1. Kerala Blue\n\nPage 2\nSecond page\n", file.Text, "page by page");

            var scan = Doc("scan.pdf", SampleDocuments.Pdf(new string[0], new string[0]));
            try { TextFile.Read(scan); throw new Exception("a scan was read"); }
            catch (InvalidDataException ex) { Check(ex.Message.Contains("no text in it, probably a scan"), "a scan said so: " + ex.Message); }
            var broken = Doc("broken.pdf", Encoding.ASCII.GetBytes("%PDF-1.4\nnot really"));
            try { TextFile.Read(broken); throw new Exception("a broken PDF was read"); }
            catch (InvalidDataException ex) { Check(ex.Message.Contains("isn't a whole PDF"), "a broken PDF said so: " + ex.Message); }
        }

        private static void TestDocumentsAreOnlyLookedAt()
        {
            var path = Doc("letter.docx", SampleDocuments.Word(new[] { "Hello" }));
            var before = File.ReadAllBytes(path);
            var file = TextFile.Read(path);
            try { file.Save("changed", true); throw new Exception("a Word file was saved"); }
            catch (InvalidOperationException) { }
            Check(File.ReadAllBytes(path).SequenceEqual(before) && !File.Exists(path + ".bak"), "the file is untouched");

            foreach (var old in new[] { "old.doc", "old.xls", "old.ppt" })
            {
                try { TextFile.Read(Doc(old, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0, 0, 0, 0 })); throw new Exception(old + " was read"); }
                catch (InvalidDataException ex) { Check(ex.Message.Contains("before 2007"), "older Office files said so: " + ex.Message); }
            }
            try { TextFile.Read(Doc("bad.docx", Encoding.ASCII.GetBytes("not a zip"))); throw new Exception("a broken .docx was read"); }
            catch (InvalidDataException ex) { Check(ex.Message.Contains("isn't a whole Word document file"), "a broken Word file said so: " + ex.Message); }
            Check(!TextFile.Read(Doc("notes.txt", Encoding.UTF8.GetBytes("plain"))).IsDocument, "text files stay text files");
        }

        private static void TestDocumentsCantSwellOrFetch()
        {
            // A part made to swell when unpacked is read only up to a limit; pictures are never unpacked.
            var output = new MemoryStream();
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
            {
                var zeros = new byte[1 << 20];
                for (var i = 0; i < 6; i++)
                    using (var s = zip.CreateEntry($"word/big{i}.xml", CompressionLevel.Optimal).Open())
                        for (var k = 0; k < 70; k++) s.Write(zeros, 0, zeros.Length);
                using (var s = zip.CreateEntry("word/media/picture.png", CompressionLevel.Optimal).Open())
                    for (var k = 0; k < 70; k++) s.Write(zeros, 0, zeros.Length);
            }
            var parts = DocumentText.Unpack(output.ToArray());
            Check(parts.Values.All(p => p.Length <= DocumentText.MaxPart), "each part has a limit");
            Check(parts.Values.Sum(p => (long)p.Length) <= DocumentText.MaxTotal, "all of them together too");
            Check(!parts.ContainsKey("word/media/picture.png"), "pictures aren't unpacked");

            // A part that asks to read another file (a DTD) is refused, not followed.
            var secret = Path.Combine(_root, "secret.txt");
            File.WriteAllText(secret, "SECRET");
            var evil = SampleDocuments.Zip(("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"/>"),
                ("word/document.xml", $"<?xml version=\"1.0\"?><!DOCTYPE d [<!ENTITY x SYSTEM \"file:///{secret.Replace('\\', '/')}\">]><w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body><w:p><w:r><w:t>&x;</w:t></w:r></w:p></w:body></w:document>"));
            try
            {
                var t = TextFile.Read(Doc("evil.docx", evil)).Text;
                Check(!t.Contains("SECRET"), "another file was read into it");
            }
            catch (InvalidDataException) { /* refused: fine */ }
        }
    }
}
