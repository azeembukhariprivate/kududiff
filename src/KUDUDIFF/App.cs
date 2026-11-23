using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Kududiff.Core;
using Kududiff.Views;

namespace Kududiff
{
    public static class Program
    {
        /// <summary>
        /// KUDUDIFF.exe                       open
        /// KUDUDIFF.exe left.txt right.txt    compare two files
        /// KUDUDIFF.exe left-folder right-folder   compare two folders
        /// KUDUDIFF.exe --pick file           the right-click menu: Windows starts one copy per selected file, and the
        ///                                    files picked together (or one now and one later) open in one comparison
        /// KUDUDIFF.exe --register-shell | --unregister   used by the installer
        /// KUDUDIFF.exe --self-test           automated checks for the build
        /// </summary>
        [STAThread]
        public static int Main(string[] args)
        {
            bool Has(string name) => args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

            var app = new App();
            if (Has("--self-test")) return app.RunSelfTest();
            if (Has("--register-shell"))
            {
                Settings.ShellMenu = true;
                ShellIntegration.Register();
                return 0;
            }
            if (Has("--unregister"))
            {
                ShellIntegration.Remove();
                return 0;
            }
            return app.Run(App.Normalize(args));
        }
    }

    public sealed class App : Application
    {
        private readonly List<string> _picks = new List<string>();
        private readonly List<string[]> _early = new List<string[]>();
        private readonly DispatcherTimer _pickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        private Hub _hub;
        private bool _started;
        private string _report;
        private bool _watchingReport;

        /// <summary>Makes file names full paths, so the running copy (which may have another current folder) finds them.</summary>
        public static string[] Normalize(string[] args)
        {
            var result = new string[args.Length];
            for (var i = 0; i < args.Length; i++)
            {
                result[i] = args[i];
                if (args[i].StartsWith("--")) continue;
                try { result[i] = Path.GetFullPath(args[i]); }
                catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is PathTooLongException) { }
            }
            return result;
        }

        public int Run(string[] args)
        {
            // The copy that gets the files may bring its window to the front.
            AllowSetForegroundWindow(-1);
            _hub = Hub.Start(args, received => Dispatcher.BeginInvoke(new Action(() => Handle(received))));
            if (_hub == null) return 0; // handed to the copy that's already running

            _pickTimer.Tick += (s, e) =>
            {
                _pickTimer.Stop();
                FlushPicks();
            };
            Startup += (s, e) =>
            {
                Init();
                ShellIntegration.Refresh();
                _started = true;
                Handle(args);
                foreach (var early in _early) Handle(early);
                _early.Clear();
            };
            Exit += (s, e) => _hub?.Dispose();
            return Run();
        }

        public int RunSelfTest()
        {
            var code = 1;
            Startup += (s, e) =>
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                DispatcherUnhandledException += OnUnhandledException;
                code = SelfTest.Run();
                Shutdown(code);
            };
            Run();
            return code;
        }

        internal void Init()
        {
            ShutdownMode = ShutdownMode.OnLastWindowClose;
            DispatcherUnhandledException += OnUnhandledException;
            SafetyNet.Install(); // says so if an error behind the window closes the app
            LangUi.Start(); // the app in the chosen language
            ThemeManager.Init(Settings.Theme);
            DocumentText.ShowFormulas = Settings.CompareFormulas;
            ThemeManager.WatchSystem();
        }

        /// <summary>A start of KUDUDIFF: this one, or a later one that handed its command over.</summary>
        private void Handle(string[] args)
        {
            if (!_started)
            {
                _early.Add(args);
                return;
            }
            var opens = new List<string>();
            var picks = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                var a = args[i];
                if (string.Equals(a, "--pick", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) picks.Add(args[++i]);
                else if (string.Equals(a, "--report", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length) _report = args[++i];
                else if (!a.StartsWith("--")) opens.Add(a);
            }

            if (opens.Count >= 2 && Directory.Exists(opens[0]) && Directory.Exists(opens[1]))
            {
                var f = ShowFolderWindow();
                f.Open(opens[0], opens[1]);
                if (opens.Count > 2) f.ShowMessage(Lang.T("Only the first two folders are compared."));
            }
            else if (opens.Count >= 2)
            {
                var w = NewWindow();
                w.Open(opens[0], opens[1]);
                if (opens.Count > 2) w.ShowMessage("Only the first two files are compared.");
            }
            else picks.AddRange(opens);

            if (picks.Count > 0)
            {
                // Windows starts one copy per selected file, a moment apart: wait for the rest before opening.
                _picks.AddRange(picks);
                _pickTimer.Stop();
                _pickTimer.Start();
            }
            else if (opens.Count == 0) NewWindow();

            if (_report != null && !_watchingReport)
            {
                _watchingReport = true;
                WatchForReport();
            }
        }

        private void FlushPicks()
        {
            var all = _picks.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            _picks.Clear();
            var folders = all.Where(Directory.Exists).ToList();
            var files = all.Where(p => !Directory.Exists(p)).ToList();
            if (folders.Count > 0) FlushFolders(folders);
            if (files.Count == 0) return;
            if (files.Count >= 2)
            {
                var w = NewWindow();
                w.Open(files[0], files[1]);
                if (files.Count > 2) w.ShowMessage($"{files.Count} files were chosen; the first two are compared.");
                return;
            }
            // One file: it completes a comparison waiting for its second file, or starts a new one.
            var waiting = Windows.OfType<MainWindow>().FirstOrDefault(w => w.LeftSide.HasFile && !w.RightSide.HasFile);
            if (waiting != null)
            {
                waiting.LoadSide(waiting.RightSide, files[0]);
                Bring(waiting);
                return;
            }
            var window = NewWindow();
            if (window.LoadSide(window.LeftSide, files[0])) window.ShowMessage("Now choose the second file: right-click it → Compare with KUDUDIFF, drop it on the right, or use Open.");
        }

        /// <summary>Folders picked from the right-click menu: two are compared; one waits on the left for the second.</summary>
        private void FlushFolders(List<string> folders)
        {
            if (folders.Count >= 2)
            {
                var f = ShowFolderWindow();
                f.Open(folders[0], folders[1]);
                if (folders.Count > 2) f.ShowMessage(Lang.T("{0} folders were chosen; the first two are compared.", folders.Count));
                return;
            }
            var waiting = Windows.OfType<FolderWindow>().FirstOrDefault(w => w.LeftFolder != null && w.RightFolder == null);
            if (waiting != null)
            {
                waiting.SetSide(false, folders[0]);
                Bring(waiting);
                return;
            }
            var window = ShowFolderWindow();
            window.SetSide(true, folders[0]);
            window.ShowMessage(Lang.T("Now choose the second folder: right-click it → Compare with KUDUDIFF, drop it here, or use Choose."));
        }

        /// <summary>A window for comparing two files (an empty one if there is one), shown and in front.</summary>
        public static MainWindow ShowFileWindow() => ((App)Current).NewWindow();

        /// <summary>A window for comparing two folders (reusing one that has no folders yet), shown and in front.</summary>
        public static FolderWindow ShowFolderWindow()
        {
            var app = (App)Current;
            var window = app.Windows.OfType<FolderWindow>().FirstOrDefault(w => w.LeftFolder == null && w.RightFolder == null);
            if (window == null)
            {
                window = new FolderWindow();
                var last = app.Windows.OfType<Window>().LastOrDefault(w => w != window && w.IsVisible && w.WindowState == WindowState.Normal && (w is MainWindow || w is FolderWindow));
                if (last != null)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = last.Left + 28;
                    window.Top = last.Top + 28;
                }
                if (app.MainWindow == null) app.MainWindow = window;
                window.Show();
            }
            Bring(window);
            return window;
        }

        /// <summary>An empty comparison window (reusing one that has no files yet), shown and in front.</summary>
        private MainWindow NewWindow()
        {
            var window = Windows.OfType<MainWindow>().FirstOrDefault(w => !w.LeftSide.HasFile && !w.RightSide.HasFile);
            if (window == null)
            {
                window = new MainWindow();
                var last = Windows.OfType<Window>().LastOrDefault(w => w != window && w.IsVisible && w.WindowState == WindowState.Normal && (w is MainWindow || w is FolderWindow));
                if (last != null)
                {
                    window.WindowStartupLocation = WindowStartupLocation.Manual;
                    window.Left = last.Left + 28;
                    window.Top = last.Top + 28;
                }
                if (MainWindow == null) MainWindow = window;
                window.Show();
            }
            Bring(window);
            return window;
        }

        private static void Bring(Window window)
        {
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        }

        /// <summary>For the self-test: once a comparison is on screen, writes which files it has and exits.</summary>
        private void WatchForReport()
        {
            var until = DateTime.UtcNow.AddSeconds(30);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (s, e) =>
            {
                var w = Windows.OfType<MainWindow>().FirstOrDefault(x => x.HasBoth && !x.IsComparing && x.Comparison != null);
                var f = Windows.OfType<FolderWindow>().FirstOrDefault(x => x.HasBoth && !x.IsComparing && x.Comparison != null);
                if (w == null && f == null && DateTime.UtcNow < until) return;
                timer.Stop();
                var text = w != null ? $"{w.LeftSide.File.Path}\n{w.RightSide.File.Path}\n{w.Changes.Count}"
                    : f != null ? $"{f.LeftFolder}\n{f.RightFolder}\n{f.Comparison.Different + f.Comparison.OnlyLeft + f.Comparison.OnlyRight}" : "timeout";
                try { File.WriteAllText(_report, text); }
                catch (IOException) { }
                foreach (var x in Windows.OfType<MainWindow>()) x.SkipClosePrompt = true;
                Shutdown(w == null && f == null ? 1 : 0);
            };
            timer.Start();
        }

        private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
        {
            if (SelfTest.Running)
            {
                SelfTest.Errors.Add(e.Exception.ToString());
                e.Handled = true;
                return;
            }
            Dialogs.Error(MainWindow?.IsVisible == true ? MainWindow : null, "Something went wrong:\n\n" + e.Exception.Message);
            e.Handled = true;
        }

        [DllImport("user32.dll")]
        private static extern bool AllowSetForegroundWindow(int processId);
    }
}
