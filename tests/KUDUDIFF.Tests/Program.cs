using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Kududiff.Core;

namespace Kududiff.Tests
{
    public static class Program
    {
        private static int _failed;
        private static int _passed;
        private static string _root;

        public static int Main()
        {
            _root = Path.Combine(Path.GetTempPath(), "kududiff-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_root);
            try
            {
                foreach (var m in typeof(Program).GetMethods(BindingFlags.NonPublic | BindingFlags.Static).Where(m => m.Name.StartsWith("Test")))
                {
                    try
                    {
                        m.Invoke(null, null);
                        _passed++;
                        Console.WriteLine("PASS " + m.Name);
                    }
                    catch (TargetInvocationException ex)
                    {
                        _failed++;
                        Console.WriteLine("FAIL " + m.Name + ": " + ex.InnerException);
                    }
                }
            }
            finally
            {
                try { Directory.Delete(_root, true); }
                catch { /* ignore */ }
            }
            Console.WriteLine($"{_passed} passed, {_failed} failed");
            return _failed == 0 ? 0 : 1;
        }

        // ================================================================== comparing folders

        /// <summary>Makes a folder with the given files ("sub/name.txt" = contents; a name ending in / is an empty folder).</summary>
        private static string MakeFolder(string name, params (string Path, string Text)[] files)
        {
            var dir = Path.Combine(_root, name + "-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(dir);
            foreach (var (path, text) in files)
            {
                var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
                if (path.EndsWith("/")) { Directory.CreateDirectory(full); continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, text);
            }
            return dir;
        }

        private static FolderItem Find(FolderComparison cmp, string relative) =>
            cmp.Root.Below().FirstOrDefault(i => i.RelativePath == relative.Replace('/', Path.DirectorySeparatorChar));

        private static void TestFoldersAreComparedFileByFile()
        {
            var left = MakeFolder("left", ("same.txt", "hello"), ("changed.txt", "one"), ("longer.txt", "short"), ("only-left.txt", "x"),
                ("src/app.cs", "class A {}"), ("src/deep/notes.md", "same"), ("gone/old.txt", "old"), ("empty/", null));
            var right = MakeFolder("right", ("same.txt", "hello"), ("changed.txt", "two"), ("longer.txt", "much longer"), ("only-right.txt", "y"),
                ("src/app.cs", "class A {}"), ("src/deep/notes.md", "same"), ("new/fresh.txt", "new"), ("empty/", null));
            var cmp = FolderComparison.Run(left, right, new FolderOptions());
            Equal(FolderState.Same, Find(cmp, "same.txt").State, "same contents");
            Equal(FolderState.Different, Find(cmp, "changed.txt").State, "same size, different bytes");
            Equal(FolderState.Different, Find(cmp, "longer.txt").State, "different sizes");
            Equal(FolderState.OnlyLeft, Find(cmp, "only-left.txt").State, "only on the left");
            Equal(FolderState.OnlyRight, Find(cmp, "only-right.txt").State, "only on the right");
            Equal(FolderState.Same, Find(cmp, "src").State, "a folder whose files are all the same");
            Equal(FolderState.Same, Find(cmp, "empty").State, "two empty folders");
            Equal(FolderState.OnlyLeft, Find(cmp, "gone").State, "a folder only on the left");
            Equal(FolderState.OnlyLeft, Find(cmp, "gone/old.txt").State, "and what's in it");
            Equal(FolderState.OnlyRight, Find(cmp, "new").State, "a folder only on the right");
            Equal(FolderState.Different, cmp.Root.State, "the folders differ");
            Equal(3, cmp.Same, "same files: same.txt, app.cs and notes.md");
            Equal(2, cmp.Different, "different files");
            Equal(2, cmp.OnlyLeft, "files only on the left");
            Equal(2, cmp.OnlyRight, "files only on the right");
            Check(!cmp.Identical, "not identical");
            Check(cmp.Summary().Contains("2 different") && cmp.Summary().Contains("2 only on the left"), cmp.Summary());
            // Folders first, then files, by name.
            Equal("empty,gone,new,src,changed.txt", string.Join(",", cmp.Root.Children.Take(5).Select(c => c.Name)), "the order");

            var twin = MakeFolder("twin", ("same.txt", "hello"), ("src/app.cs", "class A {}"));
            var twin2 = MakeFolder("twin2", ("same.txt", "hello"), ("src/app.cs", "class A {}"));
            var same = FolderComparison.Run(twin, twin2, new FolderOptions());
            Check(same.Identical && same.Root.State == FolderState.Same, "two copies are the same");
            Check(same.Summary().Contains("2 files"), same.Summary());
        }

        private static void TestFoldersSameSizeAreReadToTheEnd()
        {
            var a = new byte[200_000];
            new Random(5).NextBytes(a);
            var b = (byte[])a.Clone();
            b[b.Length - 1] ^= 1; // only the very last byte differs, after several reads
            var left = MakeFolder("big-left");
            var right = MakeFolder("big-right");
            File.WriteAllBytes(Path.Combine(left, "data.bin"), a);
            File.WriteAllBytes(Path.Combine(right, "data.bin"), b);
            File.WriteAllBytes(Path.Combine(left, "copy.bin"), a);
            File.WriteAllBytes(Path.Combine(right, "copy.bin"), a);
            var cmp = FolderComparison.Run(left, right, new FolderOptions());
            Equal(FolderState.Different, Find(cmp, "data.bin").State, "the last byte counts");
            Equal(FolderState.Same, Find(cmp, "copy.bin").State, "a true copy is the same");
        }

        private static void TestFoldersLeaveOutTheUsualNames()
        {
            var left = MakeFolder("lo-left", ("app.cs", "a"), (".git/HEAD", "ref"), ("bin/app.exe", "1"), ("obj/x.o", "1"), ("node_modules/p/index.js", "1"),
                ("Thumbs.db", "t"), ("logs/today.log", "l"), ("keep/bin.txt", "a file named like a folder on the list is kept"));
            var right = MakeFolder("lo-right", ("app.cs", "a"), (".git/HEAD", "other"), ("bin/app.exe", "2"), ("desktop.ini", "d"), ("logs/today.log", "m"),
                ("keep/bin.txt", "a file named like a folder on the list is kept"));
            var options = new FolderOptions();
            var cmp = FolderComparison.Run(left, right, options);
            Check(Find(cmp, ".git") == null && Find(cmp, "bin") == null && Find(cmp, "obj") == null && Find(cmp, "node_modules") == null, "folders on the list are left out");
            Check(Find(cmp, "Thumbs.db") == null && Find(cmp, "desktop.ini") == null, "files on the list are left out");
            Check(Find(cmp, "keep/bin.txt") != null, "only whole names match: bin.txt isn't bin");
            Equal(FolderState.Different, Find(cmp, "logs/today.log").State, "other files are compared");
            Check(cmp.LeftOut >= 7, "the left-out ones are counted: " + cmp.LeftOut);

            options.LeaveOut.Add("*.log");
            Check(Find(FolderComparison.Run(left, right, options), "logs/today.log") == null, "* stands for any letters");
            Check(options.LeavesOut("TODAY.LOG") && options.LeavesOut("BIN") && !options.LeavesOut("bin2"), "upper and lower case don't matter");

            var all = FolderComparison.Run(left, right, new FolderOptions { ShowLeftOut = true });
            Equal(FolderState.Different, Find(all, ".git/HEAD").State, "with everything shown, .git is compared");
            Equal(0, all.LeftOut, "nothing left out");

            Equal("bin|*.log|obj", string.Join("|", FolderOptions.Split(" bin, *.log;obj,, BIN ")), "the list as typed in Settings");
            Equal("bin, *.log", FolderOptions.Join(new[] { "bin", "*.log" }), "and as shown");
        }

        private static void TestFoldersThatCantBeCompared()
        {
            var outer = MakeFolder("outer", ("inner/a.txt", "a"));
            void Refused(string a, string b, string what)
            {
                try
                {
                    FolderComparison.Run(a, b, new FolderOptions());
                    Check(false, what);
                }
                catch (ArgumentException) { }
            }
            Refused(outer, outer, "the same folder on both sides");
            Refused(outer, Path.Combine(outer, "inner"), "a folder and one inside it");
            Refused(Path.Combine(outer, "inner"), outer + Path.DirectorySeparatorChar, "the other way round");
            Refused(outer, Path.Combine(_root, "no-such-folder"), "a folder that isn't there");
            Check(!FolderComparison.Inside(outer + "2", outer), "a folder with a longer name isn't inside");
            var sibling = MakeFolder("outer2", ("inner/a.txt", "a"));
            Check(FolderComparison.Run(outer, sibling, new FolderOptions()).Identical, "two folders side by side can be compared");
        }

        private static void TestFolderCopyNeverDeletesAndKeepsBak()
        {
            var left = MakeFolder("cp-left", ("changed.txt", "new text"), ("same.txt", "s"), ("only-left.txt", "l"), ("pack/a.txt", "a"), ("pack/sub/b.txt", "b"), ("pack/empty/", null));
            var right = MakeFolder("cp-right", ("changed.txt", "old text"), ("same.txt", "s"), ("only-right.txt", "r"));
            var cmp = FolderComparison.Run(left, right, new FolderOptions());

            var one = FolderCopy.Plan(cmp, Find(cmp, "changed.txt"), true);
            Equal(1, one.Count, "one file to copy");
            Check(one[0].Replaces, "it replaces the one on the right");
            Check(FolderCopy.Run(one, true).Count == 0, "copied");
            Equal("new text", File.ReadAllText(Path.Combine(right, "changed.txt")), "the right file has the left's contents");
            Equal("old text", File.ReadAllText(Path.Combine(right, "changed.txt.bak")), "the old one is kept as .bak");
            Equal("new text", File.ReadAllText(Path.Combine(left, "changed.txt")), "the left file is untouched");

            var folder = FolderCopy.Plan(cmp, Find(cmp, "pack"), true);
            Check(folder.All(s => !s.Replaces), "a folder only on the left replaces nothing");
            Check(FolderCopy.Run(folder, true).Count == 0, "the folder copied");
            Equal("b", File.ReadAllText(Path.Combine(right, "pack", "sub", "b.txt")), "with its subfolders");
            Check(Directory.Exists(Path.Combine(right, "pack", "empty")), "and its empty folder");

            var all = FolderCopy.Plan(cmp, cmp.Root, false); // everything from the right to the left
            Check(all.All(s => s.From == null || !s.From.EndsWith("same.txt")), "files that are the same aren't copied");
            FolderCopy.Run(all, false);
            Check(File.Exists(Path.Combine(left, "only-left.txt")), "nothing is deleted from the side copied to");
            Check(File.Exists(Path.Combine(left, "only-right.txt")), "what was only on the right is now on the left too");
            Check(!File.Exists(Path.Combine(left, "changed.txt.bak")), "no .bak when it's turned off");
            Check(!Directory.EnumerateFiles(left, "*.tmp", SearchOption.AllDirectories).Any() && !Directory.EnumerateFiles(right, "*.old", SearchOption.AllDirectories).Any(), "no temporary files left behind");

            // A read-only file can still be replaced, and the copy keeps the original's time.
            var ro = Path.Combine(right, "same.txt");
            File.WriteAllText(Path.Combine(left, "same.txt"), "changed on the left");
            File.SetLastWriteTime(Path.Combine(left, "same.txt"), new DateTime(2020, 5, 6, 7, 8, 9));
            File.SetAttributes(ro, FileAttributes.ReadOnly);
            var again = FolderComparison.Run(left, right, new FolderOptions());
            Check(FolderCopy.Run(FolderCopy.Plan(again, Find(again, "same.txt"), true), true).Count == 0, "read-only replaced");
            Equal("changed on the left", File.ReadAllText(ro), "the new contents");
            Equal(new DateTime(2020, 5, 6, 7, 8, 9), File.GetLastWriteTime(ro), "the time of the file copied");
        }

        private static void TestFolderReport()
        {
            var left = MakeFolder("rep-left", ("a.txt", "1"), ("b <x>.txt", "2"), ("same.txt", "s"));
            var right = MakeFolder("rep-right", ("a.txt", "3"), ("same.txt", "s"));
            var cmp = FolderComparison.Run(left, right, new FolderOptions());
            var html = FolderReport.Html(cmp, false, new DateTime(2026, 11, 15, 10, 0, 0));
            Check(html.StartsWith("<!DOCTYPE html>") && html.Contains("a.txt") && html.Contains("same.txt"), "every file is listed");
            Check(html.Contains("b &lt;x&gt;.txt") && !html.Contains("b <x>.txt"), "names are written safely");
            Check(html.Contains("Only on the left") && html.Contains("Different"), "what each is like");
            Check(html.Contains("15 November 2026"), "when it was made");
            var hidden = FolderReport.Html(cmp, true, DateTime.Now);
            Check(!hidden.Contains(">same.txt<") && hidden.Contains("a.txt"), "files that are the same can be left out of it");
        }

        // ================================================================== helpers

        // ================================================================== other languages

        private static string LangDir
        {
            get
            {
                var dir = AppContext.BaseDirectory;
                while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "KUDUDIFF", "Lang"))) dir = Path.GetDirectoryName(dir);
                return Path.Combine(dir, "src", "KUDUDIFF", "Lang");
            }
        }

        private static void UseLang(string code) =>
            Lang.Load(code, code == "en" ? null : new[] { File.ReadAllText(Path.Combine(LangDir, code + ".txt")) });

        private static void TestEveryLanguageFileIsWhole()
        {
            foreach (var (code, _) in Lang.All.Where(l => l.Code != "en"))
            {
                var lines = File.ReadAllLines(Path.Combine(LangDir, code + ".txt"));
                Check(lines.Length > 200, code + ": has the app's texts");
                foreach (var line in lines)
                {
                    var tab = line.IndexOf('\t');
                    Check(tab > 0 && tab < line.Length - 1 && line.IndexOf('\t', tab + 1) < 0, code + ": one English text and one translation: " + line);
                    var holes = new Func<string, string>(s => string.Join(",", System.Text.RegularExpressions.Regex.Matches(s, @"\{\d\}").Select(m => m.Value).Distinct().OrderBy(x => x)));
                    Equal(holes(line.Substring(0, tab)), holes(line.Substring(tab + 1)), code + ": the same places to fill in: " + line);
                }
            }
        }

        private static void TestTextsInAnotherLanguage()
        {
            try
            {
                UseLang("ml");
                Equal("ml", Lang.Current, "loaded");
                Equal("സേവ് ചെയ്യുക", Lang.T("Save"), "a word");
                Equal("ഇടത്തെ ഫയൽ ഇവിടെ ഇടുക", Lang.T("Drop the left file here"), "a sentence");
                Equal("12 മാറ്റങ്ങൾ", Lang.T("12 changes"), "a number filled in");
                Equal("\"notes.txt\" വായിക്കാൻ മാത്രമുള്ളതാണ്.", Lang.T("\"notes.txt\" is read-only."), "a name filled in");
                Equal("മാറ്റം 3 / 7", Lang.T("Change 3 of 7"), "two numbers filled in");
                Equal("Something nobody wrote down", Lang.T("Something nobody wrote down"), "unknown text stays as it is");
                Equal("ഇപ്പോൾ: സാധാരണ താരതമ്യം", Lang.T("Now: {0}", Lang.T("standard comparison")), "pieces put together");
                Equal("  ക്രമീകരണങ്ങൾ", Lang.T("  Settings"), "spaces around a known text are kept");
                var cmp = Comparison.Run("a\nb\n", "a\nc\nd\n", new CompareOptions());
                Equal("1 മാറ്റം · 2 ചേർത്തു, 1 നീക്കി", cmp.Summary(), "the summary line");
                UseLang("ar");
                Check(Lang.RightToLeft, "Arabic is right to left");
                Equal("إلغاء", Lang.T("Cancel"), "Arabic word");
            }
            finally
            {
                UseLang("en");
            }
            Equal("en", Lang.Current, "back to English");
            Equal("Save", Lang.T("Save"), "English unchanged");
            Equal("1 change · 2 added, 1 removed", Comparison.Run("a\nb\n", "a\nc\nd\n", new CompareOptions()).Summary(), "English summary unchanged");
        }

        private static void TestTheLanguageIsChosenSensibly()
        {
            Equal("ml", Lang.Choose("ml"), "the person's choice");
            Equal(Lang.Choose(""), Lang.Choose("xx"), "an unknown choice is like no choice");
            Check(Lang.Has(Lang.Choose("")), "always a language on offer");
        }

        private static void Check(bool condition, string what)
        {
            if (!condition) throw new Exception("Check failed: " + what);
        }

        private static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"{what}: expected {expected}, got {actual}");
        }

        private static string Lines(params string[] lines) => string.Join("\n", lines) + "\n";

        private static string Describe(Comparison c) => string.Join(" ", c.Chunks.Where(x => x.IsChange).Select(x => x.ToString()));

        /// <summary>The length of the longest common subsequence, the slow sure way.</summary>
        private static int BruteLcs(int[] a, int[] b)
        {
            var t = new int[a.Length + 1, b.Length + 1];
            for (var i = a.Length - 1; i >= 0; i--)
                for (var j = b.Length - 1; j >= 0; j--)
                    t[i, j] = a[i] == b[j] ? t[i + 1, j + 1] + 1 : Math.Max(t[i + 1, j], t[i, j + 1]);
            return t[0, 0];
        }

        private static void CheckMatches(int[] a, int[] b, List<(int A, int B)> m, string what)
        {
            for (var k = 0; k < m.Count; k++)
            {
                Check(a[m[k].A] == b[m[k].B], what + ": matched items are equal");
                if (k > 0) Check(m[k].A > m[k - 1].A && m[k].B > m[k - 1].B, what + ": matches go forward on both sides");
            }
        }

        // ================================================================== the comparison methods

        private static void TestMyersFindsTheLongestCommonPart()
        {
            var rnd = new Random(7);
            for (var round = 0; round < 400; round++)
            {
                var a = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => rnd.Next(0, 6)).ToArray();
                var b = Enumerable.Range(0, rnd.Next(0, 40)).Select(_ => rnd.Next(0, 6)).ToArray();
                var m = Lcs.Match(a, b, DiffAlgorithm.Standard);
                CheckMatches(a, b, m, "round " + round);
                Equal(BruteLcs(a, b), m.Count, $"round {round}: longest common part of [{string.Join(",", a)}] and [{string.Join(",", b)}]");
            }
        }

        private static void TestPatienceIsValid()
        {
            var rnd = new Random(11);
            for (var round = 0; round < 400; round++)
            {
                var a = Enumerable.Range(0, rnd.Next(0, 50)).Select(_ => rnd.Next(0, 30)).ToArray();
                var b = Enumerable.Range(0, rnd.Next(0, 50)).Select(_ => rnd.Next(0, 30)).ToArray();
                var m = Lcs.Match(a, b, DiffAlgorithm.Patience);
                CheckMatches(a, b, m, "round " + round);
                Check(m.Count <= BruteLcs(a, b), "never more than the longest common part");
            }
        }

        private static void TestPatienceKeepsFunctionsTogether()
        {
            // Moving a function: patience anchors on the unique lines and shows one block moving, not interleaved lines.
            var left = Lines("void A()", "{", "    x();", "}", "", "void B()", "{", "    y();", "}");
            var right = Lines("void B()", "{", "    y();", "}", "", "void A()", "{", "    x();", "}");
            var patience = Comparison.Run(left, right, new CompareOptions { Algorithm = DiffAlgorithm.Patience, DetectMoves = false });
            Check(patience.Chunks.Any(c => !c.IsChange && c.ACount >= 3), "the moved function's lines stay together: " + Describe(patience));
            Equal(2, patience.ChangeCount, "one function shown as removed above and added below");
        }

        // ================================================================== chunks and ignore rules

        private static void TestChunks()
        {
            var cmp = Comparison.Run(Lines("one", "two", "three", "four"), Lines("one", "2", "three", "four", "five"), new CompareOptions());
            Equal("Replace L1+1 R1+1 Insert L4+0 R4+1", Describe(cmp), "changes");
            Equal(2, cmp.ChangeCount, "change count");
            Equal(2, cmp.LinesAdded, "added");
            Equal(1, cmp.LinesRemoved, "removed");
            Check(Comparison.Run("a\nb\n", "a\nb", new CompareOptions()).Identical, "a missing final line break isn't a change");
            Check(Comparison.Run(string.Empty, string.Empty, new CompareOptions()).Identical, "two empty files");
            var all = Comparison.Run(string.Empty, Lines("x", "y"), new CompareOptions());
            Equal("Insert L0+0 R0+2", Describe(all), "everything added");
            Equal("1 change · 2 added, 0 removed", all.Summary(), "summary");
        }

        private static void TestWhitespaceModes()
        {
            var left = Lines("int x = 1;", "    call(a, b);", "end");
            var right = Lines("int  x = 1;", "call(a,b);  ", "end");
            Equal(2, Comparison.Run(left, right, new CompareOptions()).ChangeCount > 0 ? 2 : 0, "spaces count by default");
            var ends = Comparison.Run(left, right, new CompareOptions { Whitespace = WhitespaceMode.Ends });
            Equal("Replace L0+2 R0+2", Describe(ends), "line ends ignored: the inner spaces still count");
            var amount = Comparison.Run(left, right, new CompareOptions { Whitespace = WhitespaceMode.Amount });
            Equal("Replace L1+1 R1+1", Describe(amount), "amount ignored: a missing space still counts");
            Check(Comparison.Run(left, right, new CompareOptions { Whitespace = WhitespaceMode.All }).Identical, "all spaces ignored");
        }

        private static void TestCaseBlankLinesPatternsAndLineEndings()
        {
            Check(Comparison.Run(Lines("Hello World"), Lines("hello world"), new CompareOptions { IgnoreCase = true }).Identical, "case ignored");
            Check(!Comparison.Run(Lines("Hello World"), Lines("hello world"), new CompareOptions()).Identical, "case counts by default");

            var blanks = Comparison.Run(Lines("a", "", "b", "c"), Lines("a", "b", "", "", "c"), new CompareOptions { IgnoreBlankLines = true });
            Check(blanks.Identical, "blank lines ignored: " + Describe(blanks));
            var blanksChange = Comparison.Run(Lines("a", "", "b"), Lines("a", "B", "", "b"), new CompareOptions { IgnoreBlankLines = true });
            Equal(1, blanksChange.ChangeCount, "a real change among blank lines still shows");

            var stamped = new CompareOptions { IgnorePatterns = { @"\d{4}-\d{2}-\d{2} \d{2}:\d{2}" } };
            Check(Comparison.Run(Lines("built 2026-01-02 10:00 ok"), Lines("built 2026-10-25 21:13 ok"), stamped).Identical, "timestamps ignored by a pattern");
            Check(!Comparison.Run(Lines("built 2026-01-02 10:00 ok"), Lines("built 2026-10-25 21:13 OK"), stamped).Identical, "the rest still counts");
            Equal(1, CompareOptions.BadPatterns(new[] { "fine", "(broken" }).Count, "a broken pattern is reported");
            Check(Comparison.Run(Lines("x"), Lines("x"), new CompareOptions { IgnorePatterns = { "(broken" } }).Identical, "a broken pattern is skipped, not fatal");

            Check(Comparison.Run("a\r\nb\r\n", "a\nb\n", new CompareOptions()).Identical, "line endings ignored by default");
            Equal(2, Comparison.Run("a\r\nb\r\n", "a\nb\n", new CompareOptions { CompareLineEndings = true }).LinesRemoved, "and compared when asked");
        }

        // ================================================================== changes inside lines

        private static void TestChangedWords()
        {
            var cmp = Comparison.Run(Lines("var total = price * count;"), Lines("var total = cost * count + tax;"), new CompareOptions { Inline = InlineMode.Words });
            var c = cmp.Changes.Single();
            Equal(1, c.ASpans.Count, "one word changed on the left");
            Equal("price", "var total = price * count;".Substring(c.ASpans[0].Start, c.ASpans[0].Length), "the changed word");
            var right = "var total = cost * count + tax;";
            var changed = string.Join("|", c.BSpans.Select(s => right.Substring(s.Start, s.Length)));
            Equal("cost| + tax", changed, "the new parts on the right");

            var chars = Comparison.Run(Lines("colour"), Lines("color"), new CompareOptions { Inline = InlineMode.Characters }).Changes.Single();
            Equal(1, chars.ASpans.Count, "one character removed");
            Equal(4, chars.ASpans[0].Start, "the 'u'");
            Equal(0, chars.BSpans.Count, "nothing new on the right");

            var off = Comparison.Run(Lines("a b"), Lines("a c"), new CompareOptions { Inline = InlineMode.Off }).Changes.Single();
            Equal(0, off.ASpans.Count + off.BSpans.Count, "off: no changed words");

            var multi = Comparison.Run(Lines("first line", "second line"), Lines("first LINE", "second line!"), new CompareOptions()).Changes.Single();
            Check(multi.BSpans.Any(s => s.Line == 0) && multi.BSpans.Any(s => s.Line == 1), "spans on each line of a changed block");
        }

        // ================================================================== moved code

        private static void TestMovedBlocks()
        {
            var block = new[] { "void Helper()", "{", "    DoThis();", "    DoThat();", "}" };
            var left = Lines(new[] { "header" }.Concat(block).Concat(new[] { "middle 1", "middle 2", "middle 3", "footer" }).ToArray());
            var right = Lines(new[] { "header", "middle 1", "middle 2", "middle 3" }.Concat(block).Concat(new[] { "footer" }).ToArray());
            var cmp = Comparison.Run(left, right, new CompareOptions { DetectMoves = true });
            Equal(1, cmp.Moves.Count, "one moved block: " + Describe(cmp));
            Check(cmp.Moves[0].Count >= 3, "at least the block's lines");
            Check(cmp.MovedLeft.Count == cmp.MovedRight.Count && cmp.MovedLeft.Count == cmp.Moves[0].Count, "lines marked on both sides");
            Check(cmp.Summary().Contains("1 moved block"), "summary mentions it");
            Equal(0, Comparison.Run(left, right, new CompareOptions { DetectMoves = false }).Moves.Count, "off when turned off");
            var tiny = Comparison.Run(Lines("a", "x", "y"), Lines("x", "y", "a"), new CompareOptions { DetectMoves = true });
            Equal(0, tiny.Moves.Count, "one line isn't a moved block");
        }

        // ================================================================== scrolling together

        private static void TestMapLine()
        {
            var cmp = Comparison.Run(Lines("a", "b", "c", "d"), Lines("a", "x", "y", "z", "b", "c", "d"), new CompareOptions());
            Equal(0.0, cmp.MapLine(0, true), "same line before the change");
            Equal(4.0, cmp.MapLine(1, true), "after the insertion, shifted");
            Equal(6.0, cmp.MapLine(3, true), "last line");
            Equal(1.0, cmp.MapLine(2, false), "inside the insertion, back to where it was inserted");
            Equal(3.0, cmp.MapLine(6, false), "right to left");
        }

        // ================================================================== patches and reports

        private static void TestPatchAppliesWithGnuPatch()
        {
            var cases = new List<(string Name, string Left, string Right)>
            {
                ("edit", Lines(Enumerable.Range(1, 30).Select(i => "line " + i).ToArray()),
                         Lines(Enumerable.Range(1, 30).Select(i => i == 5 ? "five" : i == 20 ? "twenty" : "line " + i).Concat(new[] { "extra" }).ToArray())),
                ("crlf", "a\r\nb\r\nc\r\n", "a\r\nB\r\nc\r\nd\r\n"),
                ("no final newline", "a\nb\nc", "a\nb\nC"),
                ("add final newline", "a\nb", "a\nb\n"),
                ("from empty", string.Empty, Lines("new", "file")),
                ("to empty", Lines("old", "file"), string.Empty),
                ("unicode", Lines("café", "naïve"), Lines("cafe", "naïve", "日本語"))
            };
            var hasPatch = File.Exists("/usr/bin/patch");
            foreach (var (name, left, right) in cases)
            {
                var diff = Export.Patch(left, right, "a/file.txt", "b/file.txt", DiffAlgorithm.Standard);
                Check(diff.StartsWith("--- a/file.txt\n+++ b/file.txt\n@@ "), name + ": header");
                if (!hasPatch) continue;
                var dir = Path.Combine(_root, "patch-" + name.Replace(' ', '-'));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "file.txt"), left, new UTF8Encoding(false));
                File.WriteAllText(Path.Combine(dir, "change.patch"), diff, new UTF8Encoding(false));
                var p = Process.Start(new ProcessStartInfo("/usr/bin/patch", "--binary -s file.txt change.patch")
                    { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                Equal(0, p.ExitCode, name + ": GNU patch accepts it (" + output.Trim() + ")\n" + diff);
                Equal(right, File.ReadAllText(Path.Combine(dir, "file.txt"), Encoding.UTF8), name + ": and the result is the right file");
            }
            if (!hasPatch) Console.WriteLine("     (GNU patch not found: patches checked for their header only)");
            Equal(string.Empty, Export.Patch("same\n", "same\n", "a", "b", DiffAlgorithm.Standard), "no differences, empty patch");
        }

        private static void TestPatchHunks()
        {
            var left = Lines(Enumerable.Range(1, 40).Select(i => "l" + i).ToArray());
            var right = Lines(Enumerable.Range(1, 40).Select(i => i == 3 || i == 5 || i == 30 ? "X" + i : "l" + i).ToArray());
            var diff = Export.Patch(left, right, "a", "b", DiffAlgorithm.Standard);
            var hunks = diff.Split('\n').Where(l => l.StartsWith("@@")).ToList();
            Equal(2, hunks.Count, "nearby changes share a hunk, distant ones don't");
            Equal("@@ -1,8 +1,8 @@", hunks[0], "first hunk header");
            Equal("@@ -27,7 +27,7 @@", hunks[1], "second hunk header");
        }

        private static void TestHtmlReport()
        {
            var cmp = Comparison.Run(Lines("<b>bold</b> & co", "same", "gone"), Lines("<b>BOLD</b> & co", "same", "new"), new CompareOptions());
            var html = Export.Html(cmp, @"C:\left\a.html", @"C:\right\a.html", new DateTime(2026, 10, 25, 21, 0, 0));
            Check(html.StartsWith("<!DOCTYPE html>"), "a web page");
            Check(html.Contains("&lt;b&gt;") && !html.Contains("<b>bold"), "file text is escaped");
            Check(html.Contains("<i>BOLD</i>") || html.Contains("<i>BOLD"), "changed words marked");
            Check(html.Contains("class=\"chg l\"") && html.Contains("class=\"chg r\""), "changed lines marked");
            Check(html.Contains("2 changes"), "summary included");
            var longEqual = Comparison.Run(Lines(Enumerable.Range(0, 50).Select(i => "x" + i).ToArray()), Lines(Enumerable.Range(0, 50).Select(i => i == 49 ? "y" : "x" + i).ToArray()), new CompareOptions());
            Check(Export.Html(longEqual, "a", "b", DateTime.Now).Contains("unchanged lines"), "long unchanged runs are folded");
        }

        // ================================================================== files

        private static void TestEncodings()
        {
            Equal("UTF-8", TextFile.Decode(Encoding.UTF8.GetBytes("héllo")).EncodingName, "UTF-8 without a BOM");
            var bom = TextFile.Decode(new byte[] { 0xEF, 0xBB, 0xBF, (byte)'h', (byte)'i' });
            Equal("UTF-8 with BOM", bom.EncodingName, "UTF-8 with a BOM");
            Equal("hi", bom.Text, "BOM not part of the text");
            Equal("UTF-16 LE", TextFile.Decode(new byte[] { 0xFF, 0xFE }.Concat(Encoding.Unicode.GetBytes("hé")).ToArray()).EncodingName, "UTF-16 LE");
            Equal("hé", TextFile.Decode(new byte[] { 0xFE, 0xFF }.Concat(Encoding.BigEndianUnicode.GetBytes("hé")).ToArray()).Text, "UTF-16 BE text");
            var ansi = TextFile.Decode(new byte[] { (byte)'c', (byte)'a', (byte)'f', 0xE9 });
            Equal("café", ansi.Text, "not UTF-8: the Windows code page");
            var binary = false;
            try { TextFile.Decode(new byte[] { 1, 2, 0, 4 }); }
            catch (InvalidDataException) { binary = true; }
            Check(binary, "binary files are refused");
        }

        private static void TestSavingKeepsEncodingAndBackup()
        {
            var path = Path.Combine(_root, "save.txt");
            File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("old\r\n")).ToArray());
            var file = TextFile.Read(path);
            Check(!file.ChangedOnDisk(), "not changed yet");
            file.Save("new\r\n", true);
            var bytes = File.ReadAllBytes(path);
            Check(bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM kept");
            Equal("new\r\n", Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), "new text saved");
            Equal("old\r\n", Encoding.UTF8.GetString(File.ReadAllBytes(path + ".bak"), 3, 5), "previous version kept as .bak");
            Check(!File.Exists(path + ".kududiff-tmp"), "no temporary file left");
            File.WriteAllText(path, "changed elsewhere");
            Check(file.ChangedOnDisk(), "a change by another program is noticed");

            var plain = Path.Combine(_root, "nobak.txt");
            File.WriteAllText(plain, "x");
            TextFile.Read(plain).Save("y", false);
            Check(!File.Exists(plain + ".bak"), "no .bak when turned off");

            var latin = TextFile.Decode(new byte[] { (byte)'a', 0xE9 });
            Check(latin.CanEncode("aé") && !latin.CanEncode("日本"), "knows what the encoding can hold");
            latin.UseUtf8();
            Equal("UTF-8 with BOM", latin.EncodingName, "switched to UTF-8");
        }

        private static void TestLineEndings()
        {
            Equal(LineEndings.CrLf, TextFile.DetectLineEndings("a\r\nb\r\n"), "CRLF");
            Equal(LineEndings.Lf, TextFile.DetectLineEndings("a\nb"), "LF");
            Equal(LineEndings.Mixed, TextFile.DetectLineEndings("a\r\nb\n"), "mixed");
            Equal(LineEndings.None, TextFile.DetectLineEndings("one line"), "none");
            Equal(3, TextFile.SplitLines("a\n\nb\n").Count, "an empty line in the middle counts");
            Equal(0, TextFile.SplitLines(string.Empty).Count, "empty text has no lines");
        }

        // ================================================================== copying a change across

        private static void TestCopyLines()
        {
            string Copy(string source, int sl, int sc, string target, int tl, int tc) => TextOps.Apply(target, TextOps.CopyLines(source, sl, sc, target, tl, tc));

            Equal("a\nX\nc\n", Copy("a\nX\nc\n", 1, 1, "a\nb\nc\n", 1, 1), "replace a middle line");
            Equal("a\nX\nY\nb\n", Copy("X\nY\n", 0, 2, "a\nb\n", 1, 0), "insert in the middle");
            Equal("X\na\n", Copy("X\n", 0, 1, "a\n", 0, 0), "insert at the start");
            Equal("a\nc\n", Copy("anything", 0, 0, "a\nb\nc\n", 1, 1), "delete a middle line");
            Equal("a\nb\nX\n", Copy("X\n", 0, 1, "a\nb\n", 2, 0), "append after a final line break");
            Equal("a\nb\nX", Copy("X\n", 0, 1, "a\nb", 2, 0), "append when the text had no final break: still none");
            Equal("a", Copy(string.Empty, 0, 0, "a\nb", 1, 1), "delete the last line when there was no final break");
            Equal("a\nLAST\nc\n", Copy("x\nLAST", 1, 1, "a\nb\nc\n", 1, 1), "a copied last line without a break gets one in the middle");
            Equal("a\r\nX\r\nc\r\n", Copy("X", 0, 1, "a\r\nb\r\nc\r\n", 1, 1), "the target's own line breaks are used");
            Equal("X\nY\n", Copy("X\nY\n", 0, 2, string.Empty, 0, 0), "into an empty file");
        }

        // ================================================================== big files

        private static void TestBigFilesAreQuick()
        {
            var rnd = new Random(3);
            var left = Enumerable.Range(0, 20000).Select(i => "line " + i + " " + rnd.Next()).ToList();
            var right = new List<string>(left);
            for (var k = 0; k < 200; k++)
            {
                var at = rnd.Next(right.Count);
                if (k % 3 == 0) right.RemoveAt(at);
                else if (k % 3 == 1) right.Insert(at, "inserted " + k);
                else right[at] = "changed " + k;
            }
            var watch = Stopwatch.StartNew();
            var cmp = Comparison.Run(string.Join("\n", left), string.Join("\n", right), new CompareOptions());
            Check(watch.Elapsed.TotalSeconds < 5, "20,000 lines with 200 edits in under 5 s (took " + watch.Elapsed.TotalSeconds.ToString("0.00") + " s)");
            Check(cmp.ChangeCount >= 150 && cmp.ChangeCount <= 200, "about 200 changes: " + cmp.ChangeCount);

            // Two completely different files: the time limit keeps it from taking forever.
            var a = string.Join("\n", Enumerable.Range(0, 6000).Select(i => "a" + rnd.Next()));
            var b = string.Join("\n", Enumerable.Range(0, 6000).Select(i => "b" + rnd.Next()));
            watch.Restart();
            var different = Comparison.Run(a, b, new CompareOptions());
            Check(watch.Elapsed.TotalSeconds < 10, "nothing in common, still quick (" + watch.Elapsed.TotalSeconds.ToString("0.00") + " s)");
            Equal(1, different.ChangeCount, "one big change");
        }
    }
}
