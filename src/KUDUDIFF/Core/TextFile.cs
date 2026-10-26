using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Kududiff.Core
{
    public enum LineEndings { None, CrLf, Lf, Cr, Mixed }

    /// <summary>
    /// A text file as it was read: its text, the encoding to save it back with (including whether it had a byte order
    /// mark), and when it was last changed on disk, so saving can tell when something else changed it meanwhile.
    /// </summary>
    public sealed class TextFile
    {
        /// <summary>Files bigger than this aren't opened (a comparison holds both files in memory, twice).</summary>
        public const long MaxBytes = 200L * 1024 * 1024;

        public string Path { get; private set; }
        public string Text { get; private set; }
        public Encoding Encoding { get; private set; }
        public bool HasBom { get; private set; }
        public DateTime StampUtc { get; private set; }
        public long StampLength { get; private set; }

        public string EncodingName =>
            Encoding is UTF8Encoding ? (HasBom ? "UTF-8 with BOM" : "UTF-8")
            : Encoding is UnicodeEncoding ? (Encoding.CodePage == 1201 ? "UTF-16 BE" : "UTF-16 LE")
            : Encoding is UTF32Encoding ? "UTF-32"
            : Encoding.CodePage == 1252 ? "Windows-1252"
            : Encoding.CodePage == 28591 ? "Latin-1"
            : Encoding.WebName;

        public static TextFile Read(string path)
        {
            var info = new FileInfo(path);
            if (!info.Exists) throw new FileNotFoundException("The file wasn't found: " + path, path);
            if (info.Length > MaxBytes) throw new IOException($"\"{info.Name}\" is too big to compare ({info.Length / 1048576} MB; the limit is {MaxBytes / 1048576} MB).");
            var bytes = File.ReadAllBytes(path);
            var file = Decode(bytes);
            file.Path = path;
            file.StampUtc = info.LastWriteTimeUtc;
            file.StampLength = info.Length;
            return file;
        }

        /// <summary>Works out the encoding: a byte order mark if there is one, else UTF-8 if the bytes are valid UTF-8, else the Windows code page.</summary>
        public static TextFile Decode(byte[] bytes)
        {
            Encoding enc;
            var skip = 0;
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) { enc = new UTF8Encoding(true); skip = 3; }
            else if (bytes.Length >= 4 && bytes[0] == 0xFF && bytes[1] == 0xFE && bytes[2] == 0 && bytes[3] == 0) { enc = new UTF32Encoding(false, true); skip = 4; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) { enc = new UnicodeEncoding(false, true); skip = 2; }
            else if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) { enc = new UnicodeEncoding(true, true); skip = 2; }
            else
            {
                if (LooksBinary(bytes)) throw new InvalidDataException("This looks like a binary file, not text. KUDUDIFF compares text and code files.");
                enc = IsValidUtf8(bytes) ? (Encoding)new UTF8Encoding(false) : Ansi;
            }
            return new TextFile { Encoding = enc, HasBom = skip > 0, Text = enc.GetString(bytes, skip, bytes.Length - skip) };
        }

        /// <summary>A file made from text that isn't on disk yet (saved as UTF-8).</summary>
        public static TextFile FromText(string text, string path = null) =>
            new TextFile { Text = text ?? string.Empty, Encoding = new UTF8Encoding(false), Path = path };

        private static Encoding Ansi
        {
            get
            {
#if NETFRAMEWORK
                return Encoding.Default;
#else
                return Encoding.Latin1;
#endif
            }
        }

        private static bool LooksBinary(byte[] bytes)
        {
            var n = Math.Min(bytes.Length, 8000);
            for (var i = 0; i < n; i++)
                if (bytes[i] == 0) return true;
            return false;
        }

        private static bool IsValidUtf8(byte[] bytes)
        {
            try
            {
                new UTF8Encoding(false, true).GetCharCount(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>True when the file on disk changed since it was read (by another program).</summary>
        public bool ChangedOnDisk()
        {
            if (string.IsNullOrEmpty(Path)) return false;
            try
            {
                var info = new FileInfo(Path);
                return !info.Exists || info.LastWriteTimeUtc != StampUtc || info.Length != StampLength;
            }
            catch (IOException)
            {
                return false;
            }
        }

        /// <summary>False when the text has characters the file's encoding can't hold (they'd be saved as "?").</summary>
        public bool CanEncode(string text)
        {
            var strict = (Encoding)Encoding.Clone();
            strict.EncoderFallback = EncoderFallback.ExceptionFallback;
            try
            {
                strict.GetByteCount(text);
                return true;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }

        /// <summary>Saves from now on as UTF-8 (with a byte order mark, so other programs recognise it).</summary>
        public void UseUtf8()
        {
            Encoding = new UTF8Encoding(true);
            HasBom = true;
        }

        /// <summary>
        /// Saves new text in the file's own encoding. With <paramref name="backup"/> the previous version is kept next to
        /// it as "name.bak" (replacing an older .bak). The new text is written to a temporary file first, so a failed save
        /// never leaves a half-written file.
        /// </summary>
        public void Save(string text, bool backup)
        {
            if (string.IsNullOrEmpty(Path)) throw new InvalidOperationException("The file has no name yet.");
            var preamble = HasBom ? Encoding.GetPreamble() : new byte[0];
            var body = Encoding.GetBytes(text);
            var temp = Path + ".kududiff-tmp";
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(preamble, 0, preamble.Length);
                fs.Write(body, 0, body.Length);
            }
            try
            {
                if (File.Exists(Path))
                {
                    var bak = Path + ".bak";
                    if (backup) File.Copy(Path, bak, true);
                    var attributes = File.GetAttributes(Path);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                        throw new UnauthorizedAccessException($"\"{System.IO.Path.GetFileName(Path)}\" is read-only.");
                    File.Copy(temp, Path, true);
                    File.Delete(temp);
                }
                else
                {
                    File.Move(temp, Path);
                }
            }
            catch
            {
                try { File.Delete(temp); } catch { /* best effort */ }
                throw;
            }
            Text = text;
            var info = new FileInfo(Path);
            StampUtc = info.LastWriteTimeUtc;
            StampLength = info.Length;
        }

        // ------------------------------------------------------------------ lines

        /// <summary>
        /// Splits text into lines without their line breaks. A line break at the very end doesn't start another line:
        /// "a\nb\n" and "a\nb" both have two lines.
        /// </summary>
        public static List<string> SplitLines(string text) => Lines(text, null);

        /// <summary>The lines, plus each one's line break ("\r\n", "\n", "\r" or "" for a last line without one).</summary>
        public static List<string> Lines(string text, List<string> breaks)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text)) return lines;
            var start = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c != '\n' && c != '\r') continue;
                lines.Add(text.Substring(start, i - start));
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
                {
                    breaks?.Add("\r\n");
                    i++;
                }
                else
                {
                    breaks?.Add(c.ToString());
                }
                start = i + 1;
            }
            if (start < text.Length)
            {
                lines.Add(text.Substring(start));
                breaks?.Add(string.Empty);
            }
            return lines;
        }

        public static LineEndings DetectLineEndings(string text)
        {
            var breaks = new List<string>();
            Lines(text, breaks);
            LineEndings found = LineEndings.None;
            foreach (var b in breaks)
            {
                var kind = b == "\r\n" ? LineEndings.CrLf : b == "\n" ? LineEndings.Lf : b == "\r" ? LineEndings.Cr : LineEndings.None;
                if (kind == LineEndings.None) continue;
                if (found == LineEndings.None) found = kind;
                else if (found != kind) return LineEndings.Mixed;
            }
            return found;
        }

        public static string Describe(LineEndings endings)
        {
            switch (endings)
            {
                case LineEndings.CrLf: return "Windows (CRLF)";
                case LineEndings.Lf: return "Unix (LF)";
                case LineEndings.Cr: return "Old Mac (CR)";
                case LineEndings.Mixed: return "Mixed line endings";
                default: return "One line";
            }
        }

        /// <summary>The line break to use when a line has to be added: the text's own, or Windows' when it has none.</summary>
        public static string NewLineOf(string text)
        {
            switch (DetectLineEndings(text))
            {
                case LineEndings.Lf: return "\n";
                case LineEndings.Cr: return "\r";
                default: return "\r\n";
            }
        }
    }
}
