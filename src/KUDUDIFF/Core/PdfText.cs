using System;
using System.IO;
using System.Linq;
using System.Text;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Exceptions;

// lang.py: only Lang.T texts
namespace Kududiff.Core
{
    /// <summary>A PDF's text, page by page ("Page 2" before each page), read with PdfPig (Apache-2.0).</summary>
    public static class PdfText
    {
        /// <summary>
        /// Every page's text; throws with a plain message when the PDF is locked, damaged or only pictures of pages.
        /// </summary>
        public static string Read(string path)
        {
            var name = Path.GetFileName(path);
            try
            {
                using (var pdf = PdfDocument.Open(path))
                {
                    var sb = new StringBuilder();
                    var any = false;
                    foreach (var page in pdf.GetPages())
                    {
                        var text = ContentOrderTextExtractor.GetText(page) ?? string.Empty;
                        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.TrimEnd()).ToList();
                        while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
                        while (lines.Count > 0 && lines[0].Length == 0) lines.RemoveAt(0);
                        if (lines.Any(l => l.Trim().Length > 0)) any = true;
                        sb.Append(Lang.T("Page {0}", page.Number)).Append('\n');
                        foreach (var l in lines) sb.Append(l).Append('\n');
                        sb.Append('\n');
                    }
                    if (!any) throw new InvalidDataException(Lang.T("\"{0}\" has no text in it, probably a scan. (KUDUCK can turn a scan into a PDF with text, which can then be compared.)", name));
                    return sb.ToString().TrimEnd() + "\n";
                }
            }
            catch (PdfDocumentEncryptedException)
            {
                throw new InvalidDataException(Lang.T("\"{0}\" is locked with a password, so its text can't be read.", name));
            }
            catch (Exception ex) when (ex is PdfDocumentFormatException || ex is InvalidOperationException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is NullReferenceException || ex is EndOfStreamException)
            {
                throw new InvalidDataException(Lang.T("\"{0}\" couldn't be read: it isn't a whole PDF file (it may be damaged).", name));
            }
        }
    }
}
