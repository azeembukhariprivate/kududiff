using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Kududiff.Core;

namespace Kududiff.Views
{
    /// <summary>
    /// KUDUDIFF.exe --self-test: checks the comparison engine, then opens real comparison windows on test files and
    /// drives them (going through changes, copying a change across and undoing it, saving with a .bak copy, the
    /// options, exporting a patch and a report, other encodings, a big file, swapping, reloading), the right-click
    /// menu, handing files from one start of the app to another, every language's colours, and the other windows in
    /// both themes; and the folder window (two folders compared, a file opened side by side, copying across, the report). Writes self-test.log next to the exe; exits with 0 when everything passed.
    /// </summary>
    public static class SelfTest
    {
        /// <summary>True while the self-test runs (errors are kept instead of shown).</summary>
        public static bool Running { get; private set; }
        /// <summary>Errors that weren't expected during the self-test.</summary>
        public static List<string> Errors { get; } = new List<string>();

        /// <summary>The self-test's log, written to a file at the end.</summary>
        private static StringBuilder _log;
        /// <summary>The folder for the test's files.</summary>
        private static string _temp;
        /// <summary>The person's options, put back after the test.</summary>
        private static CompareOptions _savedOptions;

        /// <summary>The left test file: a little C# program.</summary>
        private const string LeftCode = @"using System;

namespace Demo
{
    public static class Greeter
    {
        public static string Hello(string name)
        {
            return ""Hello, "" + name;
        }

        public static int Add(int a, int b)
        {
            return a + b;
        }

        public static void Old()
        {
            Console.WriteLine(""remove me"");
        }

        // A helper that moves down below.
        public static int Twice(int x)
        {
            var y = x * 2;
            return y;
        }
    }
}
";

        /// <summary>The right test file: the same program with changes, a removed method and an added one.</summary>
        private const string RightCode = @"using System;
using System.Linq;

namespace Demo
{
    public static class Greeter
    {
        public static string Hello(string name)
        {
            return ""Hi there, "" + name + ""!"";
        }

        public static int Add(int a, int b)
        {
            return a + b;
        }

        public static int Sum(params int[] values) => values.Sum();

        // A helper that moves down below.
        public static int Twice(int x)
        {
            var y = x * 2;
            return y;
        }
    }
}
";

        /// <summary>Runs every step, writes the log, and returns 0 when all passed.</summary>
        public static int Run()
        {
            Running = true;
            _log = new StringBuilder();
            _temp = Path.Combine(Path.GetTempPath(), "kududiff-selftest-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_temp);
            _savedOptions = Settings.Options;
            Settings.Options = new CompareOptions();

            Step("Comparison engine", Engine);
            Step("Load themes and styles", () => ThemeManager.Init(ThemeMode.Light));
            Step("Theme keys exist", () =>
            {
                foreach (var key in new[] { "WindowBg", "SurfaceBg", "SurfaceAltBg", "Text", "SubtleText", "Accent", "BorderBrush", "InputBorder", "SelectionBg", "OkText", "BadText",
                                            "EditorBg", "GutterText", "DiffInsertBg", "DiffInsertLine", "DiffDeleteBg", "DiffDeleteLine", "DiffChangeBg", "DiffChangeLine",
                                            "DiffInlineInsert", "DiffInlineDelete", "DiffMoveBg", "DiffMoveLine", "DiffCurrent",
                                            "PrimaryButton", "SecondaryButton", "GhostButton", "LinkButton", "ChipRadio", "MonoBox", "Card",
                                            "IconFolder", "IconFile", "IconSave", "IconUp", "IconDown", "IconArrowLeft", "IconArrowRight", "IconSwap", "IconUndo", "IconRedo",
                                            "IconRefresh", "IconExport", "IconHistory", "IconSettings", "IconInfo" })
                    Application.Current.FindResource(key);
            });
            Step("Every language's colours load (light and dark)", () =>
            {
                foreach (var language in Syntax.Languages)
                {
                    Expect(Syntax.Get(language, false) != null, language + " colours (light)");
                    Expect(Syntax.Get(language, true) != null, language + " colours (dark)");
                }
                Expect(Syntax.LanguageFor(@"C:\x\a.cs") == "C#" && Syntax.LanguageFor("b.yml") == "YAML" && Syntax.LanguageFor("c.bat") == "Batch"
                       && Syntax.LanguageFor("d.sh") == "Bash" && Syntax.LanguageFor("e.ini") == "INI" && Syntax.LanguageFor("f.tsx") == "JavaScript", "file types");
            });
            UiRound();
            FolderRound();
            Step("Right-click menu", () =>
            {
                ShellIntegration.Register();
                Expect(ShellIntegration.IsRegistered, "the menu is added");
                Expect(ShellIntegration.FolderMenuRegistered, "the menu is added for folders too");
                Expect((ShellIntegration.Command() ?? string.Empty).Contains("--pick \"%1\""), "menu command");
                ShellIntegration.Remove();
                Expect(!ShellIntegration.IsRegistered && !ShellIntegration.FolderMenuRegistered, "the menu is removed");
            });
            Step("Files picked one by one open in one comparison", Handover);
            Step("Folders picked one by one open in one comparison", FolderHandover);

            Settings.Options = _savedOptions;
            _log.AppendLine(Errors.Count == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({Errors.Count})");
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "self-test.log"), _log.ToString()); }
            catch { /* read-only folder */ }
            try { Directory.Delete(_temp, true); }
            catch { /* ignore */ }
            return Errors.Count == 0 ? 0 : 1;
        }

        // ================================================================== steps

        /// <summary>Checks the comparison engine on the test files.</summary>
        private static void Engine()
        {
            var cmp = Comparison.Run(LeftCode, RightCode, new CompareOptions());
            Note(cmp.Summary());
            Expect(!cmp.Identical, "differences");
            Expect(cmp.Changes.Any(c => c.Kind == ChunkKind.Insert), "an added line (using System.Linq)");
            Expect(cmp.Changes.Any(c => c.Kind == ChunkKind.Replace && c.ASpans.Count > 0 && c.BSpans.Count > 0), "changed words in a changed line");
            var same = Comparison.Run("a\r\nB  c\r\n", "a\nb c\n", new CompareOptions { IgnoreCase = true, Whitespace = WhitespaceMode.Amount });
            Expect(same.Identical, "ignore case, spaces and line endings");
            var patch = Export.Patch(LeftCode, RightCode, "a/x.cs", "b/x.cs", DiffAlgorithm.Standard);
            Expect(patch.StartsWith("--- a/x.cs\n+++ b/x.cs\n@@ "), "a patch");
            var patience = Comparison.Run(LeftCode, RightCode, new CompareOptions { Algorithm = DiffAlgorithm.Patience });
            Expect(!patience.Identical, "the patience method");
        }

        /// <summary>
        /// Opens real windows and drives them: changes, copying, undo, saving, options, documents, folders, settings
        /// and About.
        /// </summary>
        private static void UiRound()
        {
            var left = Write("Greeter old.cs", LeftCode, new UTF8Encoding(false));
            var right = Write("Greeter.cs", RightCode, new UTF8Encoding(false));
            MainWindow main = null;

            Step("Empty window", () =>
            {
                main = new MainWindow();
                OffScreen(main);
                main.Show();
                Pump();
                Expect(!main.HasBoth && main.Comparison == null, "no comparison yet");
            });
            if (main == null) return;

            Step("Compare two files", () =>
            {
                main.Open(left, right);
                Idle(main);
                Expect(main.HasBoth, "both files open");
                Expect(main.Comparison != null && !main.Comparison.Identical, "differences found");
                Expect(main.Changes.Count >= 3, "at least 3 changes, got " + main.Changes.Count);
                Expect(main.LeftSide.Looks.Any(l => l != LineLook.Same) && main.RightSide.Looks.Any(l => l != LineLook.Same), "changed lines coloured");
                Expect(main.Title.Contains("Greeter old.cs") && main.Title.Contains("Greeter.cs"), "the title names both files");
                Expect(main.LeftSide.Editor.SyntaxHighlighting != null, "C# colours");
                Note(main.Comparison.Summary());
            });

            Step("Go through the changes", () =>
            {
                Expect(main.CurrentIndex == 0, "the first change shown when the files open");
                main.Next();
                Expect(main.CurrentIndex == 1, "second change");
                main.Previous();
                Expect(main.CurrentIndex == 0, "back to the first");
                main.GoTo(main.Changes.Count - 1);
                Expect(main.CurrentIndex == main.Changes.Count - 1, "last change");
                Pump();
                main.GoTo(0);
            });

            Step("Copy a change to the right, then undo", () =>
            {
                var count = main.Changes.Count;
                var original = main.RightSide.Text;
                main.CopyChange(0, true);
                Idle(main);
                Expect(main.Changes.Count == count - 1, $"one change fewer ({count} → {main.Changes.Count})");
                Expect(main.RightSide.IsDirty, "the right file shows unsaved changes");
                Expect(main.Title.Contains("•"), "the title marks unsaved changes");
                main.RightSide.Editor.Undo();
                Idle(main);
                Expect(main.RightSide.Text == original, "undo restores the text");
                Expect(main.Changes.Count == count, "undo restores the change");
                Expect(!main.RightSide.IsDirty, "back to unchanged after undo");
            });

            Step("Copy every change to the left: the files match", () =>
            {
                var guard = 0;
                while (main.Changes.Count > 0 && guard++ < 50)
                {
                    main.CopyChange(0, false);
                    Idle(main);
                }
                Expect(main.Comparison.Identical, "identical after copying everything");
                Expect(main.LeftSide.Text == main.RightSide.Text, "the same text");
                main.ReloadForTest();
                Idle(main);
                Expect(!main.Comparison.Identical && !main.LeftSide.IsDirty, "read from disk again");
            });

            Step("Save keeps a .bak copy", () =>
            {
                Settings.KeepBackup = true;
                main.CopyChange(0, true);
                Idle(main);
                var text = main.RightSide.Text;
                Expect(main.Save(main.RightSide, false), "saved");
                Expect(File.ReadAllText(right) == text, "the file has the new text");
                Expect(File.Exists(right + ".bak") && File.ReadAllText(right + ".bak") == RightCode, "the .bak has the previous version");
                Expect(!main.RightSide.IsDirty && !main.Title.Contains("•"), "no unsaved changes after saving");
                File.WriteAllText(right, RightCode);
                File.Delete(right + ".bak");
                main.ReloadForTest();
                Idle(main);
            });

            Step("Comparison options", () =>
            {
                var normal = main.Changes.Count;
                main.CompareOptions = new CompareOptions { Inline = InlineMode.Characters };
                Idle(main);
                Expect(main.Changes.Count == normal, "characters: the same changes");
                main.CompareOptions = new CompareOptions { Algorithm = DiffAlgorithm.Patience, DetectMoves = false };
                Idle(main);
                Expect(main.Comparison.Options.Algorithm == DiffAlgorithm.Patience, "patience used");
                main.CompareOptions = new CompareOptions { IgnorePatterns = new List<string> { "return \"H.*" } };
                Idle(main);
                Expect(main.Changes.Count < normal, "an ignore pattern removes a change");
                main.CompareOptions = new CompareOptions();
                Idle(main);
                Expect(main.Changes.Count == normal, "back to normal");
            });

            Step("Export a patch and a report", () =>
            {
                var patch = main.SaveExport(false, Path.Combine(_temp, "out.patch"));
                Expect(patch != null && File.ReadAllText(patch).StartsWith("--- a/Greeter old.cs"), "a patch file");
                var html = main.SaveExport(true, Path.Combine(_temp, "out.html"));
                var page = html != null ? File.ReadAllText(html) : string.Empty;
                Expect(page.StartsWith("<!DOCTYPE html>") && page.Contains("Greeter.cs"), "a report");
            });

            Step("Swap sides", () =>
            {
                main.Swap();
                Idle(main);
                Expect(main.LeftSide.File.Path == right && main.RightSide.File.Path == left, "swapped");
                main.Swap();
                Idle(main);
            });

            Step("Other encodings", () =>
            {
                var utf16 = Write("unicode.txt", "Grüße\r\nZeile zwei\r\n", new UnicodeEncoding(false, true));
                var ansi = Write("ansi.txt", "Gr\u00fc\u00dfe\r\nZeile 2\r\n", Encoding.GetEncoding(1252));
                main.Open(utf16, ansi);
                Idle(main);
                Expect(main.LeftSide.File.EncodingName == "UTF-16 LE", "UTF-16 read, got " + main.LeftSide.File.EncodingName);
                Expect(main.RightSide.Text.StartsWith("Grüße"), "Windows code page read");
                Expect(main.Changes.Count == 1, "one change");
                main.CopyChange(0, true);
                Idle(main);
                Expect(main.Save(main.RightSide, false), "saved");
                Expect(TextFile.Read(ansi).Text == "Grüße\r\nZeile zwei\r\n", "saved in its own encoding");
                Expect(File.ReadAllBytes(ansi).Length == "Grüße\r\nZeile zwei\r\n".Length, "one byte per character (Windows code page kept)");
            });

            Step("A big file", () =>
            {
                var a = new StringBuilder();
                var b = new StringBuilder();
                for (var i = 0; i < 60000; i++)
                {
                    a.Append("line ").Append(i).Append(" of the big test file\r\n");
                    if (i % 5000 == 0) b.Append("changed line ").Append(i).Append("\r\n");
                    else b.Append("line ").Append(i).Append(" of the big test file\r\n");
                }
                var started = DateTime.UtcNow;
                main.Open(Write("big1.log", a.ToString(), Encoding.UTF8), Write("big2.log", b.ToString(), Encoding.UTF8));
                Idle(main, 60);
                Note($"60000 lines compared in {(DateTime.UtcNow - started).TotalSeconds:0.0}s");
                Expect(main.Changes.Count == 12, "12 changes, got " + main.Changes.Count);
                main.GoTo(6);
                Pump();
            });

            Step("Word, Excel, PowerPoint and PDF files, shown as text", () =>
            {
                var docs = Path.Combine(_temp, "Documents");
                Directory.CreateDirectory(docs);
                string Put(string name, byte[] data) { var p = Path.Combine(docs, name); File.WriteAllBytes(p, data); return p; }
                var a = Put("Letter old.docx", SampleDocuments.Word(new[] { "Dear Azeem,", "Name | Price", "The boat race is on Sunday.", "See you there." }));
                var b = Put("Letter.docx", SampleDocuments.Word(new[] { "Dear Azeem,", "Name | Price | Seats", "The boat race is on Saturday.", "See you there." }, inserted: "new"));
                var before = File.ReadAllBytes(b);
                main.Open(a, b);
                Idle(main);
                Expect(main.LeftSide.File.IsDocument && main.RightSide.File.IsDocument, "both read as Word documents");
                Expect(main.LeftSide.Editor.IsReadOnly && main.RightSide.Editor.IsReadOnly, "look only");
                Expect(main.Changes.Count >= 2, "changes found, got " + main.Changes.Count);
                var right = main.RightSide.Text;
                main.CopyChange(0, false);
                main.CopyChange(0, true);
                Idle(main);
                Expect(main.RightSide.Text == right && !main.LeftSide.IsDirty && !main.RightSide.IsDirty, "nothing copied into them");
                Expect(!main.Save(main.RightSide, false), "never saved");
                Expect(File.ReadAllBytes(b).SequenceEqual(before) && !File.Exists(b + ".bak"), "the file untouched");
                Screenshots.One(main, "Two Word documents");

                var x1 = Put("Prices old.xlsx", SampleDocuments.Excel(("Prices", new[] { new[] { "Item", "Price" }, new[] { "Rice", "3.5" }, new[] { "Total", "3.5=SUM(B2:B2)" } })));
                var x2 = Put("Prices.xlsx", SampleDocuments.Excel(("Prices", new[] { new[] { "Item", "Price" }, new[] { "Rice", "3.75" }, new[] { "Total", "3.75=SUM(B2:B3)" } })));
                main.Open(x1, x2);
                Idle(main);
                Expect(main.RightSide.Text.Contains("A2  Rice | 3.75"), "Excel rows: " + main.RightSide.Text);
                DocumentText.ShowFormulas = true;
                try
                {
                    main.ReloadForTest();
                    Idle(main);
                    Expect(main.RightSide.Text.Contains("(=SUM(B2:B3))"), "formulas when asked");
                }
                finally
                {
                    DocumentText.ShowFormulas = false;
                    main.ReloadForTest();
                    Idle(main);
                }
                Screenshots.One(main, "Two Excel workbooks");

                var p1 = Put("Results old.pdf", SampleDocuments.Pdf(new[] { "Boat race results", "1. Kerala Blue", "2. River Queen" }));
                var p2 = Put("Results.pdf", SampleDocuments.Pdf(new[] { "Boat race results", "1. River Queen", "2. Kerala Blue" }));
                main.Open(p1, p2);
                Idle(main);
                Expect(main.LeftSide.File.DocumentKind == "PDF" && main.Changes.Count >= 1, "PDF text compared");
                Expect(main.LeftSide.Text.StartsWith("Page 1\nBoat race results"), "page by page: " + main.LeftSide.Text);
                Screenshots.One(main, "Two PDF files");
            });

            Step("Back to the code files", () =>
            {
                main.Open(left, right);
                Idle(main);
                main.GoTo(1);
            });

            Step("Dark theme", () =>
            {
                ThemeManager.Apply(ThemeMode.Dark);
                Pump();
                Expect(ReferenceEquals(main.LeftSide.Editor.SyntaxHighlighting, Syntax.Get("C#", true)), "dark colours for code");
                Screenshots.All("Dark theme");
                Show(OptionsWindow.Build(main, main.CompareOptions, o => { }));
                Show(new SettingsWindow(main));
                ThemeManager.Apply(ThemeMode.Light);
                Pump();
                Expect(ReferenceEquals(main.LeftSide.Editor.SyntaxHighlighting, Syntax.Get("C#", false)), "light colours for code");
            });

            Step("Editor settings", () =>
            {
                Settings.ShowInvisibles = true;
                Settings.WordWrap = true;
                Settings.FontSize = 15;
                MainWindow.RefreshAll();
                Pump();
                Expect(main.LeftSide.Editor.WordWrap && main.LeftSide.Editor.FontSize == 15, "applied");
                Screenshots.All("Editor settings");
                Settings.ShowInvisibles = false;
                Settings.WordWrap = false;
                Settings.FontSize = 13;
                MainWindow.RefreshAll();
            });

            Step("Options window", () => Show(OptionsWindow.Build(main, main.CompareOptions, o => { })));
            Step("Settings window", () => Show(new SettingsWindow(main)));
            Step("About window", () => Show(About.Build(main)));
            foreach (var (code, settings, compare) in new[] { ("ml", "ക്രമീകരണങ്ങൾ", "താരതമ്യ ക്രമീകരണങ്ങൾ"), ("hi", "सेटिंग्स", "तुलना के विकल्प"), ("ar", "الإعدادات", "خيارات المقارنة") })
                Step("In another language: " + code, () =>
                {
                    LangUi.Use(code);
                    try
                    {
                        Expect(Lang.Current == code, "the language to be loaded from the app");
                        var w = new SettingsWindow(main);
                        OffScreen(w);
                        w.Show();
                        Pump();
                        var texts = Texts(w);
                        Expect(texts.Contains(settings), "the settings heading in the language: " + string.Join(" | ", texts.Take(8)));
                        Expect(!texts.Contains("Settings") && !texts.Contains("Appearance"), "no English left on the settings window");
                        Expect(w.FlowDirection == (code == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight), "Arabic windows mirrored, others not");
                        Screenshots.One(w, "Settings in " + code);
                        w.Close();
                        var o = OptionsWindow.Build(main, main.CompareOptions, x => { });
                        OffScreen(o);
                        o.Show();
                        Pump();
                        Expect(Texts(o).Contains(compare), "the options window in the language");
                        Screenshots.One(o, "Options in " + code);
                        o.Close();
                        Expect(main.Comparison == null || !main.Comparison.Summary().Contains("change") || code == "en", "the summary in the language");
                    }
                    finally
                    {
                        LangUi.Use("en");
                    }
                });
            Step("Close", () =>
            {
                main.SkipClosePrompt = true;
                main.Close();
            });
        }

        /// <summary>Two folders compared in the folder window: the tree, hiding what's the same, opening a file side by side,
        /// copying a file and a folder across (with .bak, nothing deleted), the report, the left-out names, and the window in another language.</summary>
        private static void FolderRound()
        {
            var left = MakeFolder("folder-left", ("same.txt", "the same\n"), ("changed.cs", LeftCode), ("only-left.txt", "only here\n"),
                ("src/app.cs", "class App {}\n"), ("src/deep/notes.md", "notes\n"), ("pack/a.txt", "a\n"), ("pack/sub/b.txt", "b\n"), (".git/HEAD", "left\n"));
            var right = MakeFolder("folder-right", ("same.txt", "the same\n"), ("changed.cs", RightCode), ("only-right.txt", "only there\n"),
                ("src/app.cs", "class App {}\n"), ("src/deep/notes.md", "notes\n"), (".git/HEAD", "right\n"));
            var saved = (Settings.FolderLeaveOut, Settings.FolderShowLeftOut, Settings.FolderHideSame, Settings.KeepBackup);
            Settings.FolderLeaveOut = FolderOptions.UsualLeaveOut.ToList();
            Settings.FolderShowLeftOut = false;
            Settings.FolderHideSame = false;
            Settings.KeepBackup = true;
            FolderWindow w = null;
            try
            {
                Step("Folder window", () =>
                {
                    w = new FolderWindow { SkipQuestions = true };
                    OffScreen(w);
                    w.Show();
                    Pump();
                    Expect(!w.HasBoth && w.Comparison == null, "no folders yet");
                });
                if (w == null) return;

                Step("Compare two folders", () =>
                {
                    w.Open(left, right);
                    IdleFolders(w);
                    var c = w.Comparison;
                    Expect(c != null, "a comparison");
                    Note(c.Summary());
                    Expect(c.Different == 1 && c.OnlyLeft == 3 && c.OnlyRight == 1 && c.Same == 3, $"1 different, 3 only left, 1 only right, 3 same (got {c.Different}, {c.OnlyLeft}, {c.OnlyRight}, {c.Same})");
                    Expect(!w.Rows.Any(r => r.Item.Name == ".git"), ".git is left out");
                    Expect(w.Rows.Any(r => r.Item.RelativePath == "pack") && w.Rows.Any(r => r.Name == "b.txt"), "a folder only on the left, open");
                    Expect(!w.Rows.Any(r => r.Name == "notes.md"), "a folder that's the same starts closed");
                    Expect(w.Title.Contains("folder-left") && w.Title.Contains("folder-right"), "the title names both folders");
                });

                Step("Hide what's the same", () =>
                {
                    w.HideSame = true;
                    Pump();
                    Expect(!w.Rows.Any(r => r.Item.State == FolderState.Same), "nothing that's the same is listed");
                    Expect(w.Rows.Any(r => r.Name == "changed.cs"), "the differences still are");
                    w.HideSame = false;
                });

                Step("Open a changed file side by side", () =>
                {
                    Expect(w.Choose("changed.cs"), "the changed file is listed");
                    var main = w.OpenChosen();
                    Expect(main != null, "a comparison window");
                    Idle(main);
                    Expect(main.HasBoth && main.Changes.Count >= 3, "the two copies compared line by line");
                    Screenshots.One(main, "Changed file from a folder comparison");
                    main.SkipClosePrompt = true;
                    main.Close();
                });

                Step("Copy a file across keeps a .bak", () =>
                {
                    Expect(w.Choose("changed.cs"), "chosen");
                    Wait(w.CopyChosen(true));
                    IdleFolders(w);
                    Expect(File.ReadAllText(Path.Combine(right, "changed.cs")) == LeftCode, "the right file has the left's text");
                    Expect(File.ReadAllText(Path.Combine(right, "changed.cs.bak")) == RightCode, "the old one is kept as .bak");
                    Expect(w.Comparison.Different == 0, "now the same");
                });

                Step("Copy a folder across", () =>
                {
                    Expect(w.Choose("pack"), "the folder only on the left is listed");
                    Wait(w.CopyChosen(true));
                    IdleFolders(w);
                    Expect(File.Exists(Path.Combine(right, "pack", "sub", "b.txt")), "with what's inside it");
                    Expect(File.Exists(Path.Combine(right, "only-right.txt")), "nothing is deleted");
                    Expect(w.Comparison.Rows(FolderState.OnlyLeft).All(i => !i.RelativePath.StartsWith("pack")), "the folder is on both sides now");
                });

                Step("Save a folder report", () =>
                {
                    var path = w.SaveReport(Path.Combine(_temp, "folders.html"));
                    Expect(path != null && File.ReadAllText(path).Contains("only-left.txt"), "the report lists the files");
                });

                Step("Show left-out files", () =>
                {
                    Settings.FolderShowLeftOut = true;
                    w.Compare();
                    IdleFolders(w);
                    Expect(w.Rows.Any(r => r.Item.Name == ".git"), ".git is compared when asked");
                    Settings.FolderShowLeftOut = false;
                    w.Compare();
                    IdleFolders(w);
                });

                Step("Folders that can't be compared", () =>
                {
                    w.Open(left, Path.Combine(left, "src"));
                    IdleFolders(w);
                    Expect(w.Comparison == null, "a folder and one inside it aren't compared");
                    w.Open(left, right);
                    IdleFolders(w);
                });

                Step("A folder dropped on the file window", () =>
                {
                    var main = new MainWindow();
                    OffScreen(main);
                    main.Show();
                    Pump();
                    main.Open(left, right);
                    Pump();
                    var f = Application.Current.Windows.OfType<FolderWindow>().FirstOrDefault(x => x != w && x.HasBoth);
                    Expect(f != null, "two folders open a folder comparison");
                    IdleFolders(f);
                    f.Close();
                    main.SkipClosePrompt = true;
                    main.Close();
                });

                Step("Folder window, dark", () =>
                {
                    ThemeManager.Apply(ThemeMode.Dark);
                    Pump();
                    Screenshots.One(w, "Folder comparison (dark)");
                    ThemeManager.Apply(ThemeMode.Light);
                });

                foreach (var (code, hide) in new[] { ("ml", "ഒരുപോലെയുള്ളവ മറയ്ക്കുക"), ("ar", "إخفاء المتطابق") })
                    Step("Folder window in another language: " + code, () =>
                    {
                        LangUi.Use(code);
                        try
                        {
                            var f = new FolderWindow();
                            OffScreen(f);
                            f.Show();
                            f.Open(left, right);
                            IdleFolders(f);
                            var texts = Texts(f);
                            Expect(texts.Contains(hide), "the window in the language: " + string.Join(" | ", texts.Take(12)));
                            Expect(!texts.Contains("Hide what's the same") && !texts.Contains("Copy to right"), "no English left on it");
                            Expect(f.Rows.All(r => r.StateText != "Same" && r.StateText != "Different"), "the rows in the language");
                            Screenshots.One(f, "Folder comparison in " + code);
                            f.Close();
                        }
                        finally
                        {
                            LangUi.Use("en");
                        }
                    });
            }
            finally
            {
                if (w != null) w.Close();
                (Settings.FolderLeaveOut, Settings.FolderShowLeftOut, Settings.FolderHideSame, Settings.KeepBackup) = saved;
            }
        }

        /// <summary>Starts KUDUDIFF twice, each with one folder, as File Explorer does for two selected folders: they must end up in one comparison.</summary>
        private static void FolderHandover()
        {
            var a = MakeFolder("pick-folder-a", ("x.txt", "1\n"), ("y.txt", "same\n"));
            var b = MakeFolder("pick-folder-b", ("x.txt", "2\n"), ("y.txt", "same\n"));
            var report = Path.Combine(_temp, "folder-handover.txt");
            using (var first = Start($"--pick \"{a}\" --report \"{report}\""))
            {
                Thread.Sleep(400);
                using (var second = Start($"--pick \"{b}\""))
                {
                    Expect(second.WaitForExit(20000), "the second start hands its folder over and exits");
                    Expect(second.ExitCode == 0, "the second start exits cleanly");
                }
                if (!first.WaitForExit(45000))
                {
                    first.Kill();
                    throw new Exception("the first start didn't open the folder comparison");
                }
            }
            var lines = File.Exists(report) ? File.ReadAllLines(report) : new string[0];
            Note("report: " + string.Join(" | ", lines));
            Expect(lines.Length == 3, "a report");
            Expect(string.Equals(lines[0], a, StringComparison.OrdinalIgnoreCase) && string.Equals(lines[1], b, StringComparison.OrdinalIgnoreCase), "both folders in one comparison, in the order picked");
            Expect(lines[2] == "1", "one file differs");
        }

        /// <summary>Starts KUDUDIFF twice, each with one file, as File Explorer does for two selected files: they must end up in one comparison.</summary>
        private static void Handover()
        {
            var a = Write("pick-a.txt", "one\ntwo\nthree\n", new UTF8Encoding(false));
            var b = Write("pick-b.txt", "one\n2\nthree\n", new UTF8Encoding(false));
            var report = Path.Combine(_temp, "handover.txt");
            using (var first = Start($"--pick \"{a}\" --report \"{report}\""))
            {
                Thread.Sleep(400);
                using (var second = Start($"--pick \"{b}\""))
                {
                    Expect(second.WaitForExit(20000), "the second start hands its file over and exits");
                    Expect(second.ExitCode == 0, "the second start exits cleanly");
                }
                if (!first.WaitForExit(45000))
                {
                    first.Kill();
                    throw new Exception("the first start didn't open the comparison");
                }
            }
            var lines = File.Exists(report) ? File.ReadAllLines(report) : new string[0];
            Note("report: " + string.Join(" | ", lines));
            Expect(lines.Length == 3, "a report");
            Expect(string.Equals(lines[0], a, StringComparison.OrdinalIgnoreCase) && string.Equals(lines[1], b, StringComparison.OrdinalIgnoreCase), "both files in one comparison, in the order picked");
            Expect(lines[2] == "1", "one change");
        }

        // ================================================================== helpers

        /// <summary>Starts another copy of KUDUDIFF with these arguments.</summary>
        private static Process Start(string args) =>
            Process.Start(new ProcessStartInfo(AppInfo.ExePath, args) { UseShellExecute = false });

        /// <summary>Writes a test file in this encoding and returns its path.</summary>
        private static string Write(string name, string text, Encoding encoding)
        {
            var path = Path.Combine(_temp, name);
            File.WriteAllText(path, text, encoding);
            return path;
        }

        /// <summary>Runs one step, writing to the log whether it passed.</summary>
        private static void Step(string name, Action action)
        {
            var before = Errors.Count;
            var started = DateTime.UtcNow;
            try
            {
                action();
                Pump();
                Screenshots.All(name);
                if (Errors.Count > before) throw new Exception(string.Join(" | ", Errors.Skip(before)));
                _log.AppendLine($"ok    {name} ({(DateTime.UtcNow - started).TotalSeconds:0.0}s)");
            }
            catch (Exception ex)
            {
                if (Errors.Count == before) Errors.Add(name + ": " + ex.Message);
                _log.AppendLine("FAIL  " + name + ": " + ex);
            }
        }

        /// <summary>Adds a note to the log under the step.</summary>
        private static void Note(string text) => _log.AppendLine("      " + text);

        /// <summary>Fails the step when the condition isn't true.</summary>
        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

        /// <summary>Lets the window finish its work (up to some seconds).</summary>
        private static void Idle(MainWindow w, int seconds = 20)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            Pump();
            while (w.IsComparing || (w.HasBoth && w.Comparison == null))
            {
                if (DateTime.UtcNow > until) throw new Exception("timed out waiting for the comparison");
                Pump();
                Thread.Sleep(30);
            }
            Pump();
        }

        /// <summary>Makes a test folder with these files.</summary>
        private static string MakeFolder(string name, params (string Path, string Text)[] files)
        {
            var dir = Path.Combine(_temp, name);
            Directory.CreateDirectory(dir);
            foreach (var (path, text) in files)
            {
                var full = Path.Combine(dir, path.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full));
                File.WriteAllText(full, text, new UTF8Encoding(false));
            }
            return dir;
        }

        /// <summary>Lets the folder window finish comparing.</summary>
        private static void IdleFolders(FolderWindow w, int seconds = 30)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            Pump();
            while (w.IsComparing)
            {
                if (DateTime.UtcNow > until) throw new Exception("timed out waiting for the folder comparison");
                Pump();
                Thread.Sleep(30);
            }
            Pump();
        }

        /// <summary>Waits for a task while the window keeps working.</summary>
        private static void Wait(System.Threading.Tasks.Task task, int seconds = 30)
        {
            var until = DateTime.UtcNow.AddSeconds(seconds);
            while (!task.IsCompleted)
            {
                if (DateTime.UtcNow > until) throw new Exception("timed out");
                Pump();
                Thread.Sleep(20);
            }
            if (task.IsFaulted) throw task.Exception.InnerException;
        }

        /// <summary>Lets the window handle what's waiting (drawing, clicks).</summary>
        private static void Pump()
        {
            foreach (Window w in Application.Current.Windows) w.UpdateLayout();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(20);
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        /// <summary>Puts a window where it can't be seen, so the test doesn't get in the way.</summary>
        private static void OffScreen(Window w)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowState = WindowState.Normal;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }

        /// <summary>Every text a window shows (its title too).</summary>
        private static List<string> Texts(Window w)
        {
            var list = new List<string> { w.Title };
            void Walk(DependencyObject o)
            {
                if (o is System.Windows.Controls.TextBlock t) list.Add(t.Text);
                else if (o is System.Windows.Controls.ContentControl c && c.Content is string s) list.Add(s);
                foreach (var child in LogicalTreeHelper.GetChildren(o))
                    if (child is DependencyObject d) Walk(d);
            }
            Walk(w);
            return list;
        }

        /// <summary>Shows a window (off screen) and lets it draw.</summary>
        private static void Show(Window w)
        {
            OffScreen(w);
            w.Show();
            Pump();
            Screenshots.One(w, w.Title);
            w.Close();
        }
    }
}
