using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using Kududiff.Controls;
using Kududiff.Core;

namespace Kududiff.Views
{
    public sealed class SettingsWindow : Window
    {
        public SettingsWindow(Window owner)
        {
            Width = 580;
            SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Dialogs.Prepare(this, "Settings · KUDUDIFF", owner);

            var root = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
            root.Children.Add(Ui.Heading("Settings", 20).Margin(0, 0, 0, 14));

            root.Children.Add(Ui.Heading("Appearance", 14).Margin(0, 0, 0, 8));
            var themes = new WrapPanel();
            foreach (var mode in new[] { ThemeMode.System, ThemeMode.Light, ThemeMode.Dark })
            {
                var chip = new RadioButton { Content = mode == ThemeMode.System ? "Same as Windows" : mode.ToString(), GroupName = "theme", IsChecked = Settings.Theme == mode }
                    .Styled("ChipRadio");
                chip.Checked += (s, e) =>
                {
                    Settings.Theme = mode;
                    ThemeManager.Apply(mode);
                };
                themes.Children.Add(chip);
            }
            root.Children.Add(themes);

            root.Children.Add(Ui.Heading("Editor", 14).Margin(0, 16, 0, 6));
            var size = new ComboBox { Width = 90, HorizontalAlignment = HorizontalAlignment.Left };
            for (var i = 9; i <= 24; i++) size.Items.Add(i);
            size.SelectedItem = Settings.FontSize;
            size.SelectionChanged += (s, e) =>
            {
                if (size.SelectedItem is int v)
                {
                    Settings.FontSize = v;
                    MainWindow.RefreshAll();
                }
            };
            var sizeRow = Ui.Row(Ui.Text("Text size", 13).Margin(0, 0, 12, 0), size);
            root.Children.Add(sizeRow.Margin(0, 0, 0, 6));
            root.Children.Add(Check("Wrap long lines", () => Settings.WordWrap, v => Settings.WordWrap = v));
            root.Children.Add(Check("Show spaces, tabs and line ends", () => Settings.ShowInvisibles, v => Settings.ShowInvisibles = v));
            root.Children.Add(Check("Colour code by language (C#, Python, JSON, HTML and more)", () => Settings.SyntaxColours, v => Settings.SyntaxColours = v));

            root.Children.Add(Ui.Heading("Saving", 14).Margin(0, 16, 0, 4));
            root.Children.Add(Check("Keep the previous version as a .bak file when saving", () => Settings.KeepBackup, v => Settings.KeepBackup = v));
            root.Children.Add(Ui.Subtle("Saving \"app.cs\" first copies the old one to \"app.cs.bak\" next to it, replacing an older .bak.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Heading("File Explorer", 14).Margin(0, 16, 0, 4));
            var menu = new CheckBox { Content = "Add \"Compare with KUDUDIFF\" to files' right-click menu", IsChecked = Settings.ShellMenu };
            menu.Click += (s, e) =>
            {
                Settings.ShellMenu = menu.IsChecked == true;
                ShellIntegration.Refresh();
            };
            root.Children.Add(menu);
            root.Children.Add(Ui.Subtle("Select two files and right-click, or pick one file now and the other later. For your Windows account only; on Windows 11 it's under \"Show more options\".", 12)
                .Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Heading("Updates", 14).Margin(0, 16, 0, 4));
            var updates = new CheckBox { Content = "Check for a new KUDUDIFF version when it starts", IsChecked = Settings.CheckForUpdates };
            updates.Click += (s, e) => Settings.CheckForUpdates = updates.IsChecked == true;
            root.Children.Add(updates);
            root.Children.Add(Ui.Subtle("Only asks GitHub for the latest version number. Nothing is downloaded without asking.", 12).Margin(27, 0, 0, 0));

            root.Children.Add(Ui.Divider().Margin(0, 18, 0, 0));
            var bottom = Ui.Columns(Ui.Star(), Ui.Auto);
            bottom.Children.Add(Ui.Link("About KUDUDIFF", (s, e) => About.Show(this)).At(0, 0));
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.Click += (s, e) => Close();
            bottom.Children.Add(close.At(0, 1));
            root.Children.Add(bottom.Margin(0, 14, 0, 0));
            Content = root;
        }

        private static CheckBox Check(string text, Func<bool> get, Action<bool> set)
        {
            var box = new CheckBox { Content = text, IsChecked = get(), Margin = new Thickness(0, 2, 0, 2) };
            box.Click += (s, e) =>
            {
                set(box.IsChecked == true);
                MainWindow.RefreshAll();
            };
            return box;
        }
    }

    /// <summary>How to compare: the method, what to ignore, how changes inside lines are shown, and moved code.</summary>
    public static class OptionsWindow
    {
        public static Window Build(Window owner, CompareOptions current, Action<CompareOptions> done)
        {
            var o = current.Clone();
            var content = new StackPanel();
            content.Children.Add(Ui.Heading("Comparison options", 18));
            content.Children.Add(Ui.Subtle("These apply to the comparison on screen and the web page report, and are remembered for next time. A patch always compares exactly.", 12.5)
                .Margin(0, 4, 0, 12));

            WrapPanel Chips<T>(string group, (T Value, string Text, string Tip)[] items, T selected, Action<T> pick)
            {
                var panel = new WrapPanel();
                foreach (var (value, text, tip) in items)
                {
                    var v = value;
                    var chip = new RadioButton { Content = text, GroupName = group, IsChecked = Equals(value, selected), ToolTip = tip }.Styled("ChipRadio");
                    chip.Checked += (s, e) => pick(v);
                    panel.Children.Add(chip);
                }
                return panel;
            }

            void Section(string text) => content.Children.Add(Ui.Heading(text, 14).Margin(0, 12, 0, 6));

            Section("Spaces");
            content.Children.Add(Chips("ws", new[]
            {
                (WhitespaceMode.Compare, "Compare all spaces", "Every space and tab counts"),
                (WhitespaceMode.Ends, "Ignore at line ends", "Spaces and tabs at the start and end of lines don't count"),
                (WhitespaceMode.Amount, "Ignore amount", "\"a  b\" and \"a b\" are the same, and spaces at line ends don't count"),
                (WhitespaceMode.All, "Ignore all", "Spaces and tabs don't count at all: \"a b\" and \"ab\" are the same")
            }, o.Whitespace, v => o.Whitespace = v));

            Section("Also ignore");
            var caseBox = new CheckBox { Content = "Upper and lower case (\"Name\" and \"name\" are the same)", IsChecked = o.IgnoreCase };
            caseBox.Click += (s, e) => o.IgnoreCase = caseBox.IsChecked == true;
            var blank = new CheckBox { Content = "Blank lines (added or removed empty lines don't count)", IsChecked = o.IgnoreBlankLines };
            blank.Click += (s, e) => o.IgnoreBlankLines = blank.IsChecked == true;
            var endings = new CheckBox { Content = "Line endings (Windows CRLF and Unix LF are the same)", IsChecked = !o.CompareLineEndings };
            endings.Click += (s, e) => o.CompareLineEndings = endings.IsChecked != true;
            content.Children.Add(caseBox);
            content.Children.Add(blank);
            content.Children.Add(endings);

            content.Children.Add(Ui.Text("Text matching these patterns (regular expressions, one per line):", 13).Margin(0, 10, 0, 4));
            var patterns = Ui.MonoBox(false);
            patterns.AcceptsReturn = true;
            patterns.Height = 76;
            patterns.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            patterns.Text = string.Join(Environment.NewLine, o.IgnorePatterns);
            System.Windows.Automation.AutomationProperties.SetName(patterns, "Ignore patterns");
            content.Children.Add(patterns);
            content.Children.Add(Ui.Subtle(@"For example \d{4}-\d{2}-\d{2} for dates, or //.*$ for comments after //. The matching text is left out before lines are compared.", 12)
                .Margin(0, 4, 0, 0));

            Section("Changes inside lines");
            content.Children.Add(Chips("inline", new[]
            {
                (InlineMode.Words, "Show changed words", "Within changed lines, the words that differ are marked"),
                (InlineMode.Characters, "Show changed characters", "Within changed lines, the exact characters that differ are marked"),
                (InlineMode.Off, "Lines only", "Only whole lines are marked")
            }, o.Inline, v => o.Inline = v));
            var moves = new CheckBox { Content = "Find code that was moved (blocks of 3 or more lines that appear elsewhere)", IsChecked = o.DetectMoves, Margin = new Thickness(0, 8, 0, 0) };
            moves.Click += (s, e) => o.DetectMoves = moves.IsChecked == true;
            content.Children.Add(moves);

            Section("Method");
            content.Children.Add(Chips("alg", new[]
            {
                (DiffAlgorithm.Standard, "Standard", "The smallest set of changes (the Myers method, as in diff and git)"),
                (DiffAlgorithm.Patience, "Patience", "Lines up unique lines first, which keeps functions and blocks together in code (as git diff --patience)")
            }, o.Algorithm, v => o.Algorithm = v));

            var error = Ui.Text(string.Empty, 12.5, "BadText", wrap: true);
            error.Visibility = Visibility.Collapsed;
            content.Children.Add(error.Margin(0, 10, 0, 0));

            var reset = Ui.Button("Reset");
            reset.ToolTip = "Back to the standard options";
            var cancel = Ui.Button("Cancel");
            var apply = Ui.Button("Apply", null, "PrimaryButton");
            cancel.IsCancel = true;
            apply.IsDefault = false;
            var window = Dialogs.Dialog(owner, "Options · KUDUDIFF", content, 620, reset, cancel, apply);
            reset.Click += (s, e) =>
            {
                window.Close();
                done(new CompareOptions());
            };
            apply.Click += (s, e) =>
            {
                var list = patterns.Text.Replace("\r\n", "\n").Split('\n').Select(p => p.Trim('\r')).Where(p => p.Trim().Length > 0).ToList();
                var bad = CompareOptions.BadPatterns(list);
                if (bad.Count > 0)
                {
                    error.Text = "These patterns aren't valid:\n" + string.Join("\n", bad);
                    error.Visibility = Visibility.Visible;
                    return;
                }
                o.IgnorePatterns = list;
                window.Close();
                done(o);
            };
            return window;
        }

        /// <summary>Shows the options; returns the chosen ones, or null when cancelled.</summary>
        public static CompareOptions Show(Window owner, CompareOptions current)
        {
            CompareOptions chosen = null;
            Build(owner, current, o => chosen = o).ShowDialog();
            return chosen;
        }
    }

    public static class About
    {
        public static Window Build(Window owner)
        {
            var header = Ui.Row(LogoTile.Create(64), Ui.Column(Ui.Text("KUDUDIFF", 20, bold: true), Ui.Subtle("Version " + AppInfo.VersionText + " · Compare two files, see what changed", 12.5))
                .Margin(16, 0, 0, 0));
            ((FrameworkElement)header.Children[1]).VerticalAlignment = VerticalAlignment.Center;

            var rows = new StackPanel { Margin = new Thickness(0, 18, 0, 0) };
            void Row(string label, UIElement value)
            {
                var g = Ui.Columns(Ui.Px(120), Ui.Star());
                g.Children.Add(Ui.Subtle(label, 12.5, false).At(0, 0));
                g.Children.Add(value.At(0, 1));
                rows.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(0, 7, 0, 7), Child = g }.Res(Border.BorderBrushProperty, "BorderBrush"));
            }
            var madeBy = Ui.Text("Azeem, with Claude and ", 12.5);
            var heart = new Run("♥");
            heart.SetResourceReference(TextElement.ForegroundProperty, "Accent");
            madeBy.Inlines.Add(heart);
            Row("Made by", madeBy);
            Row("Licence", Ui.Row(Ui.Text("MIT · free and open source  ", 12.5), Ui.Link("View", (s, e) => OpenLocal("LICENSE.txt"))));
            Row("Source code", Ui.Link("github.com/" + AppInfo.RepoOwner + "/" + AppInfo.RepoName, (s, e) => AppInfo.OpenUrl(AppInfo.RepoUrl)));
            Row("Comparing", Ui.Text("Its own engine: the Myers and patience methods, word and character changes, moved code", 12.5, wrap: true));
            Row("Editor", Ui.Text("AvalonEdit (MIT licence)", 12.5));
            Row("Third-party", Ui.Link("Third-party notices", (s, e) => OpenLocal("THIRD-PARTY-NOTICES.txt")));
            rows.Children.Add(Ui.Divider());

            var content = Ui.Column(header, rows,
                Ui.Subtle("KUDUDIFF works on your computer only. Files are changed only when you save, and saving keeps the previous version as a .bak file unless you turn that off.", 11.5)
                    .Margin(0, 14, 0, 0));

            Window window = null;
            var updates = Ui.Button("Check for updates");
            var close = Ui.Button("Close", null, "PrimaryButton");
            close.IsCancel = true;
            close.IsDefault = true;
            window = Dialogs.Dialog(owner, "About KUDUDIFF", content, 560, updates, close);
            close.Click += (s, e) => window.Close();
            updates.Click += async (s, e) =>
            {
                updates.IsEnabled = false;
                try
                {
                    var info = await UpdateChecker.CheckAsync();
                    if (info == null) Dialogs.Info(window, "Couldn't find any published version yet.");
                    else if (info.IsNewer)
                    {
                        if (Dialogs.Confirm(window, $"KUDUDIFF {info.Latest.ToString(3)} is available. Open the download page?")) AppInfo.OpenUrl(info.Url);
                    }
                    else Dialogs.Info(window, "You have the latest version.");
                }
                catch
                {
                    Dialogs.Error(window, "Couldn't check for updates. Check your internet connection and try again.");
                }
                finally
                {
                    updates.IsEnabled = true;
                }
            };
            return window;
        }

        public static void Show(Window owner) => Build(owner).ShowDialog();

        private static void OpenLocal(string name)
        {
            var path = Path.Combine(Path.GetDirectoryName(AppInfo.ExePath) ?? string.Empty, name);
            if (File.Exists(path)) AppInfo.OpenFile(path);
            else AppInfo.OpenUrl(AppInfo.RepoUrl);
        }
    }
}
