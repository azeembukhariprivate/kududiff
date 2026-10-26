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
    /// both themes. Writes self-test.log next to the exe; exits with 0 when everything passed.
    /// </summary>
    public static class SelfTest
    {
        public static bool Running { get; private set; }
        public static List<string> Errors { get; } = new List<string>();

        private static StringBuilder _log;
        private static string _temp;
        private static CompareOptions _savedOptions;

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
            Step("Right-click menu", () =>
            {
                ShellIntegration.Register();
                Expect(ShellIntegration.IsRegistered, "the menu is added");
                Expect((ShellIntegration.Command() ?? string.Empty).Contains("--pick \"%1\""), "menu command");
                ShellIntegration.Remove();
                Expect(!ShellIntegration.IsRegistered, "the menu is removed");
            });
            Step("Files picked one by one open in one comparison", Handover);

            Settings.Options = _savedOptions;
            _log.AppendLine(Errors.Count == 0 ? "SELF-TEST PASSED" : $"SELF-TEST FAILED ({Errors.Count})");
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? ".", "self-test.log"), _log.ToString()); }
            catch { /* read-only folder */ }
            try { Directory.Delete(_temp, true); }
            catch { /* ignore */ }
            return Errors.Count == 0 ? 0 : 1;
        }

        // ================================================================== steps

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
            Step("Close", () =>
            {
                main.SkipClosePrompt = true;
                main.Close();
            });
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

        private static Process Start(string args) =>
            Process.Start(new ProcessStartInfo(AppInfo.ExePath, args) { UseShellExecute = false });

        private static string Write(string name, string text, Encoding encoding)
        {
            var path = Path.Combine(_temp, name);
            File.WriteAllText(path, text, encoding);
            return path;
        }

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

        private static void Note(string text) => _log.AppendLine("      " + text);

        private static void Expect(bool condition, string what)
        {
            if (!condition) throw new Exception("expected " + what);
        }

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

        private static void Pump()
        {
            foreach (Window w in Application.Current.Windows) w.UpdateLayout();
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
            Thread.Sleep(20);
            Application.Current.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        }

        private static void OffScreen(Window w)
        {
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.WindowState = WindowState.Normal;
            w.Left = -20000;
            w.Top = -20000;
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
        }

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
