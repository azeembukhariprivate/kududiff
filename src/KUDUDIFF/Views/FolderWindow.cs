using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using Kududiff.Controls;
using Kududiff.Core;

namespace Kududiff.Views
{
    /// <summary>
    /// Two folders compared: one tree of every file and folder from both sides, each marked the same, different or on
    /// one side only. A changed text file opens side by side; the arrows copy a file or folder across (asking before
    /// anything is replaced, keeping the old file as .bak, never deleting); the comparison can be saved as a web page.
    /// </summary>
    public sealed class FolderWindow : Window
    {
        private readonly RowList<FolderRow> _rows = new RowList<FolderRow>();
        private readonly ListBox _list;
        private readonly TextBlock _leftPath = Ui.Text(string.Empty, 13.5, bold: true);
        private readonly TextBlock _rightPath = Ui.Text(string.Empty, 13.5, bold: true);
        private readonly TextBlock _summary = Ui.Text(string.Empty, 12.5);
        private readonly TextBlock _message = Ui.Subtle(string.Empty, 12.5, false);
        private readonly Button _stop;
        private readonly Border _placeholder;
        private readonly CheckBox _hideSame;
        private readonly CheckBox _showLeftOut;
        private readonly List<Button> _needResult = new List<Button>();
        private readonly HashSet<string> _expanded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource _cancel;
        private int _version;

        public FolderWindow()
        {
            Width = 1180;
            Height = 820;
            MinWidth = 860;
            MinHeight = 520;
            Dialogs.Prepare(this, Lang.T("Compare folders") + " - " + AppInfo.Name);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header: the name, comparing files, settings and about.
            var header = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto);
            header.Margin = new Thickness(16, 10, 12, 10);
            header.Children.Add(Ui.Row(LogoTile.Create(26), Ui.Text(AppInfo.Name, 15, bold: true).Margin(10, 0, 0, 0), Ui.Subtle("Compare folders", 13, false).Margin(12, 2, 0, 0)).At(0, 0));
            var files = Ui.Button("Compare files", "IconFile", "GhostButton", (s, e) => App.ShowFileWindow());
            files.ToolTip = "Compare two files side by side";
            header.Children.Add(files.At(0, 2));
            header.Children.Add(Ui.Button("Settings", "IconSettings", "GhostButton", (s, e) =>
            {
                new SettingsWindow(this).ShowDialog();
                // A changed list of names to leave out is used straight away.
                if (Comparison != null && FolderOptions.Join(Settings.FolderLeaveOut) != FolderOptions.Join(Comparison.Options.LeaveOut)) Compare();
            }).At(0, 3));
            header.Children.Add(Ui.Button("About", "IconInfo", "GhostButton", (s, e) => About.Show(this)).At(0, 4));
            root.Children.Add(new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(0));

            // Toolbar.
            var tools = new WrapPanel { Margin = new Thickness(12, 6, 12, 6) };
            Button Tool(string text, string icon, string tip, Action action, bool needResult = true)
            {
                var b = Ui.Small(Ui.Button(text, icon, "GhostButton", (s, e) => action()));
                b.ToolTip = tip;
                b.Margin = new Thickness(0, 0, 2, 0);
                if (needResult) _needResult.Add(b);
                tools.Children.Add(b);
                return b;
            }
            void Gap() => tools.Children.Add(Ui.Divider(true));
            Tool("Open side by side", "IconCompare", "Compare the chosen file's two copies line by line (Enter, or double-click)", () => OpenChosen());
            Gap();
            Tool("Copy to left", "IconArrowLeft", "Copy the chosen file or folder from the right to the left (Alt+Left)", () => _ = CopyChosen(false));
            Tool("Copy to right", "IconArrowRight", "Copy the chosen file or folder from the left to the right (Alt+Right)", () => _ = CopyChosen(true));
            Gap();
            Tool("Previous", "IconUp", "Go to the previous difference (Alt+Up)", () => Step(-1));
            Tool("Next", "IconDown", "Go to the next difference (Alt+Down)", () => Step(1));
            Gap();
            Tool("Swap sides", "IconSwap", "Put the left folder on the right and the right folder on the left", Swap, false);
            Tool("Compare again", "IconRefresh", "Look at both folders again (F5)", Compare, false);
            Tool("Save report", "IconExport", "Save the comparison as a web page, to read in a browser or send to someone", () => SaveReport(null));
            Gap();
            _hideSame = new CheckBox { Content = "Hide what's the same", IsChecked = Settings.FolderHideSame, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 12, 0) };
            _hideSame.Click += (s, e) =>
            {
                Settings.FolderHideSame = _hideSame.IsChecked == true;
                Fill();
            };
            tools.Children.Add(_hideSame);
            _showLeftOut = new CheckBox { Content = "Show left-out files", IsChecked = Settings.FolderShowLeftOut, VerticalAlignment = VerticalAlignment.Center };
            _showLeftOut.ToolTip = "Also compare the files and folders that are usually left out (.git, node_modules, bin, obj… change the list in Settings)";
            _showLeftOut.Click += (s, e) =>
            {
                Settings.FolderShowLeftOut = _showLeftOut.IsChecked == true;
                Compare();
            };
            tools.Children.Add(_showLeftOut);
            root.Children.Add(new Border { Child = tools, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(1));

            // The two folders, each with a button to choose it.
            var folders = Ui.Columns(Ui.Star(), Ui.Px(16), Ui.Star());
            folders.Margin = new Thickness(14, 8, 14, 8);
            folders.Children.Add(FolderHeader(_leftPath, true).At(0, 0));
            folders.Children.Add(FolderHeader(_rightPath, false).At(0, 2));
            root.Children.Add(folders.At(2));

            // The list, with its column headings, and a drop area until two folders are chosen.
            var body = new Grid { AllowDrop = true, Margin = new Thickness(14, 0, 14, 8) };
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition());
            var heads = FolderTemplates.Columns();
            heads.Margin = new Thickness(6, 0, 6, 4);
            string[] titles = { "Name", "What", "Left size", "Left changed", "Right size", "Right changed" };
            for (var i = 0; i < titles.Length; i++)
            {
                var t = Ui.Subtle(titles[i], 12, false);
                if (i == 2 || i == 4) t.HorizontalAlignment = HorizontalAlignment.Right;
                t.Margin = new Thickness(i == 0 ? 22 : 0, 0, i == 2 || i == 4 ? 12 : 0, 0);
                heads.Children.Add(t.At(0, i));
            }
            body.Children.Add(heads.At(0));
            _list = new ListBox { ItemsSource = _rows, ItemTemplate = FolderTemplates.Row }.Styled("TableList");
            VirtualizingPanel.SetIsVirtualizing(_list, true);
            VirtualizingPanel.SetVirtualizationMode(_list, VirtualizationMode.Recycling);
            ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
            System.Windows.Automation.AutomationProperties.SetName(_list, "Files and folders");
            _list.MouseDoubleClick += (s, e) =>
            {
                if (!(e.OriginalSource is DependencyObject d)) return;
                if (ItemsControl.ContainerFromElement(_list, d) is ListBoxItem item && item.DataContext is FolderRow row)
                {
                    if (row.Item.IsFolder) Toggle(row);
                    else OpenChosen();
                }
            };
            _list.PreviewMouseLeftButtonDown += (s, e) =>
            {
                // A click on the ▸ arrow opens or closes the folder.
                if (e.OriginalSource is TextBlock t && t.Name == "Exp" && t.DataContext is FolderRow row && row.Item.IsFolder)
                {
                    Toggle(row);
                    e.Handled = true;
                }
            };
            _list.SelectionChanged += (s, e) => UpdateState();
            var frame = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Child = _list, Padding = new Thickness(0, 2, 0, 2) }
                .Res(Border.BorderBrushProperty, "BorderBrush").Res(Border.BackgroundProperty, "SurfaceBg");
            body.Children.Add(frame.At(1));

            var drop = Ui.Column(
                Ui.Icon("IconFolder", 34).Res(System.Windows.Documents.TextElement.ForegroundProperty, "SubtleText"),
                Ui.Text("Drop two folders here", 16, bold: true).Margin(0, 12, 0, 4),
                Ui.Subtle("or choose them one at a time", 12.5).Margin(0, 0, 0, 10),
                Ui.Row(Ui.Button("Choose the left folder…", "IconFolder", "PrimaryButton", (s, e) => Pick(true)),
                       Ui.Button("Choose the right folder…", "IconFolder", "SecondaryButton", (s, e) => Pick(false)).Margin(8, 0, 0, 0)),
                Ui.Subtle("Files with the same name are compared by what's inside them. Nothing is changed unless you copy something across.", 12).Margin(0, 16, 0, 0));
            foreach (FrameworkElement child in drop.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
            ((TextBlock)drop.Children[4]).TextAlignment = TextAlignment.Center;
            ((TextBlock)drop.Children[4]).MaxWidth = 460;
            drop.HorizontalAlignment = HorizontalAlignment.Center;
            drop.VerticalAlignment = VerticalAlignment.Center;
            _placeholder = new Border { BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10), Child = drop }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "InputBorder");
            Grid.SetRowSpan(_placeholder, 2);
            body.Children.Add(_placeholder);
            body.PreviewDragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            body.PreviewDrop += (s, e) =>
            {
                if (!(e.Data.GetData(DataFormats.FileDrop) is string[] dropped) || dropped.Length == 0) return;
                e.Handled = true;
                Dropped(dropped);
            };
            root.Children.Add(body.At(3));

            // Status: the summary, a message, Stop while looking, and what the colours mean.
            var status = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto);
            _summary.TextTrimming = TextTrimming.CharacterEllipsis;
            status.Children.Add(_summary.At(0, 0));
            _message.TextTrimming = TextTrimming.CharacterEllipsis;
            status.Children.Add(_message.Margin(18, 0, 12, 0).At(0, 1));
            _stop = Ui.Small(Ui.Button("Stop", "IconStop", "GhostButton", (s, e) => _cancel?.Cancel()));
            _stop.Visibility = Visibility.Collapsed;
            status.Children.Add(_stop.Margin(0, 0, 10, 0).At(0, 2));
            status.Children.Add(Ui.Row(Swatch("DiffChangeLine", "Different"), Swatch("DiffDeleteLine", "Only on the left"), Swatch("DiffInsertLine", "Only on the right")).At(0, 3));
            root.Children.Add(new Border { Child = status, Padding = new Thickness(16, 8, 16, 8), BorderThickness = new Thickness(0, 1, 0, 0) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(4));
            Content = root;

            PreviewKeyDown += OnKey;
            Closed += (s, e) => _cancel?.Cancel();
            UpdateState();
        }

        public string LeftFolder { get; private set; }
        public string RightFolder { get; private set; }
        public FolderComparison Comparison { get; private set; }
        public bool IsComparing => _cancel != null;
        public IReadOnlyList<FolderRow> Rows => _rows;
        public FolderRow Chosen => _list.SelectedItem as FolderRow;
        public bool HasBoth => LeftFolder != null && RightFolder != null;

        /// <summary>For the self-test: copy without asking.</summary>
        public bool SkipQuestions { get; set; }

        /// <summary>Hides (or shows again) the files and folders that are the same on both sides.</summary>
        public bool HideSame
        {
            get => _hideSame.IsChecked == true;
            set
            {
                _hideSame.IsChecked = value;
                Settings.FolderHideSame = value;
                Fill();
            }
        }

        /// <summary>Chooses the row of a file or folder (by its path below the two folders). False when it isn't shown.</summary>
        public bool Choose(string relativePath)
        {
            var row = _rows.FirstOrDefault(r => string.Equals(r.Item.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (row == null) return false;
            _list.SelectedItem = row;
            _list.ScrollIntoView(row);
            return true;
        }

        private UIElement FolderHeader(TextBlock path, bool left)
        {
            var grid = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto);
            grid.Children.Add(Ui.Icon("IconFolder", 16).Res(System.Windows.Documents.TextElement.ForegroundProperty, "SubtleText").Margin(0, 0, 8, 0).At(0, 0));
            path.TextTrimming = TextTrimming.CharacterEllipsis;
            path.VerticalAlignment = VerticalAlignment.Center;
            grid.Children.Add(path.At(0, 1));
            var choose = Ui.Small(Ui.Button("Choose…", "IconFolder", "GhostButton", (s, e) => Pick(left)));
            choose.ToolTip = left ? "Choose the left folder" : "Choose the right folder";
            grid.Children.Add(choose.Margin(8, 0, 0, 0).At(0, 2));
            return grid;
        }

        private static UIElement Swatch(string brush, string text)
        {
            var box = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(14, 0, 5, 0) }.Res(Border.BackgroundProperty, brush);
            return Ui.Row(box, Ui.Subtle(text, 12, false));
        }

        public void ShowMessage(string text)
        {
            _message.Text = text;
            _message.ToolTip = text;
        }

        // ================================================================== choosing the folders

        private void Pick(bool left)
        {
            var start = left ? LeftFolder : RightFolder;
            var path = Dialogs.PickFolder(this, left ? "Choose the left folder" : "Choose the right folder", start ?? Settings.LastFolder);
            if (path == null) return;
            if (left) LeftFolder = path;
            else RightFolder = path;
            Changed();
        }

        /// <summary>Folders dropped on the window: two fill both sides, one fills the empty side (else the right).</summary>
        public void Dropped(string[] paths)
        {
            var dirs = paths.Where(Directory.Exists).ToList();
            if (dirs.Count == 0)
            {
                Dialogs.Error(this, Lang.T("Drop folders here. To compare two files, use Compare files."));
                return;
            }
            if (dirs.Count >= 2) Open(dirs[0], dirs[1]);
            else
            {
                if (LeftFolder == null) LeftFolder = dirs[0];
                else RightFolder = dirs[0];
                Changed();
            }
            Activate();
        }

        public void Open(string left, string right)
        {
            LeftFolder = left;
            RightFolder = right;
            _expanded.Clear();
            _collapsed.Clear();
            Changed();
        }

        /// <summary>Sets one side (the right-click menu: the first folder waits on the left for the second).</summary>
        public void SetSide(bool left, string path)
        {
            if (left) LeftFolder = path;
            else RightFolder = path;
            Changed();
        }

        private void Changed()
        {
            _leftPath.Text = LeftFolder ?? Lang.T("Choose the left folder");
            _rightPath.Text = RightFolder ?? Lang.T("Choose the right folder");
            _leftPath.ToolTip = LeftFolder;
            _rightPath.ToolTip = RightFolder;
            Title = HasBoth ? $"{FolderName(LeftFolder)} ↔ {FolderName(RightFolder)} - {AppInfo.Name}" : Lang.T("Compare folders") + " - " + AppInfo.Name;
            if (LeftFolder != null) Settings.LastFolder = LeftFolder;
            if (HasBoth)
            {
                Settings.AddRecent(LeftFolder, RightFolder);
                Compare();
            }
            else
            {
                Comparison = null;
                _rows.Replace(new FolderRow[0]);
                UpdateState();
            }
        }

        private static string FolderName(string path)
        {
            var name = Path.GetFileName(path.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        private void Swap()
        {
            (LeftFolder, RightFolder) = (RightFolder, LeftFolder);
            Changed();
        }

        // ================================================================== comparing

        /// <summary>Compares the two folders in the background; a newer comparison replaces one still running.</summary>
        public void Compare()
        {
            if (!HasBoth) return;
            _cancel?.Cancel();
            var cancel = new CancellationTokenSource();
            _cancel = cancel;
            var version = ++_version;
            var options = new FolderOptions { LeaveOut = Settings.FolderLeaveOut, ShowLeftOut = Settings.FolderShowLeftOut };
            string left = LeftFolder, right = RightFolder;
            ShowMessage(string.Empty);
            UpdateState();
            Action<string> progress = path => Dispatcher.BeginInvoke(new Action(() =>
            {
                if (version == _version) _summary.Text = Lang.T("Comparing… {0}", path);
            }));
            Task.Run(() => FolderComparison.Run(left, right, options, cancel.Token, progress), cancel.Token).ContinueWith(t =>
            {
                if (version != _version) return;
                _cancel = null;
                if (t.IsCanceled || t.Exception?.InnerException is OperationCanceledException)
                {
                    ShowMessage(Lang.T("Stopped. Use Compare again to look at the folders again."));
                    UpdateState();
                    return;
                }
                if (t.IsFaulted)
                {
                    Comparison = null;
                    _rows.Replace(new FolderRow[0]);
                    var problem = t.Exception?.InnerException;
                    if (!SelfTest.Running) Dialogs.Error(this, problem is ArgumentException ? problem.Message : Lang.T("Couldn't compare the folders.\n\n{0}", problem?.Message));
                    ShowMessage(problem?.Message);
                    UpdateState();
                    return;
                }
                Comparison = t.Result;
                Fill();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Makes the rows: the tree as it's open now, without what's the same when that's hidden.</summary>
        private void Fill()
        {
            var chosen = Chosen?.Item.RelativePath;
            var rows = new List<FolderRow>();
            var hide = _hideSame.IsChecked == true;
            void Add(FolderItem folder)
            {
                foreach (var item in folder.Children)
                {
                    if (hide && item.State == FolderState.Same) continue;
                    var open = item.IsFolder && IsOpen(item);
                    rows.Add(new FolderRow(item, open));
                    if (open) Add(item);
                }
            }
            if (Comparison != null) Add(Comparison.Root);
            _rows.Replace(rows);
            var again = chosen == null ? null : rows.FirstOrDefault(r => string.Equals(r.Item.RelativePath, chosen, StringComparison.OrdinalIgnoreCase));
            if (again != null)
            {
                _list.SelectedItem = again;
                _list.ScrollIntoView(again);
            }
            UpdateState();
        }

        /// <summary>Folders with differences start open, the same ones closed; after that they stay as the person left them.</summary>
        private bool IsOpen(FolderItem folder) =>
            _expanded.Contains(folder.RelativePath) || (!_collapsed.Contains(folder.RelativePath) && folder.State != FolderState.Same);

        private void Toggle(FolderRow row)
        {
            var path = row.Item.RelativePath;
            if (row.Expanded)
            {
                _expanded.Remove(path);
                _collapsed.Add(path);
            }
            else
            {
                _collapsed.Remove(path);
                _expanded.Add(path);
            }
            _list.SelectedItem = row;
            Fill();
        }

        private void UpdateState()
        {
            _placeholder.Visibility = HasBoth ? Visibility.Collapsed : Visibility.Visible;
            _stop.Visibility = IsComparing ? Visibility.Visible : Visibility.Collapsed;
            foreach (var b in _needResult) b.IsEnabled = Comparison != null && !IsComparing;
            if (!HasBoth) _summary.Text = LeftFolder == null && RightFolder == null ? Lang.T("Choose two folders to compare, or drop them here") : LeftFolder == null ? Lang.T("Now choose the left folder") : Lang.T("Now choose the right folder");
            else if (IsComparing) { if (!_summary.Text.StartsWith(Lang.T("Comparing…"))) _summary.Text = Lang.T("Comparing…"); }
            else if (Comparison == null) _summary.Text = string.Empty;
            else
            {
                var text = Comparison.Summary();
                if (Comparison.LeftOut > 0) text += "  ·  " + Lang.T("{0} left out", Comparison.LeftOut);
                _summary.Text = text;
            }
            _summary.Res(TextBlock.ForegroundProperty, Comparison != null && Comparison.Identical && !IsComparing ? "OkText" : "Text");
        }

        // ================================================================== going through the differences

        private void Step(int direction)
        {
            if (_rows.Count == 0) return;
            var start = _list.SelectedIndex;
            for (var i = start + direction; i >= 0 && i < _rows.Count; i += direction)
            {
                var row = _rows[i];
                if (row.Item.IsChange && !row.Item.IsFolder || row.Item.IsFolder && !row.Item.OnBoth)
                {
                    _list.SelectedIndex = i;
                    _list.ScrollIntoView(row);
                    return;
                }
            }
            ShowMessage(Lang.T(direction > 0 ? "No more differences below." : "No more differences above."));
        }

        /// <summary>Opens the chosen file's two copies side by side in a comparison of their own.</summary>
        public MainWindow OpenChosen()
        {
            var row = Chosen;
            if (row == null || Comparison == null) return null;
            var item = row.Item;
            if (item.IsFolder)
            {
                Toggle(row);
                return null;
            }
            if (!item.OnBoth)
            {
                ShowMessage(Lang.T("\"{0}\" is only on one side. Copy it across, or open it from its folder.", item.Name));
                return null;
            }
            var window = App.ShowFileWindow();
            window.Open(Comparison.LeftPath(item), Comparison.RightPath(item));
            return window;
        }

        // ================================================================== copying across

        /// <summary>Copies the chosen file or folder to the other side. Returns the problems (null when nothing was done).</summary>
        public async Task<List<string>> CopyChosen(bool toRight)
        {
            var row = Chosen;
            if (row == null || Comparison == null || IsComparing) return null;
            var steps = FolderCopy.Plan(Comparison, row.Item, toRight);
            var files = steps.Count(s => !s.MakeFolder);
            if (steps.Count == 0)
            {
                ShowMessage(Lang.T(row.Item.OnBoth || !(toRight ? row.Item.OnLeft : row.Item.OnRight)
                    ? (toRight ? "Nothing to copy: it's the same on the right already, or isn't on the left." : "Nothing to copy: it's the same on the left already, or isn't on the right.")
                    : "Nothing to copy."));
                return null;
            }
            var replaces = steps.Count(s => s.Replaces);
            var target = toRight ? Lang.T("the right") : Lang.T("the left");
            if (!SkipQuestions && (replaces > 0 || files > 1))
            {
                var question = new StringBuilder(files == 1 ? Lang.T("Copy \"{0}\" to {1}?", row.Item.Name, target) : Lang.T("Copy {0} files to {1}?", files, target));
                if (replaces > 0)
                {
                    question.Append("\n\n").Append(replaces == 1 ? Lang.T("1 file there will be replaced.") : Lang.T("{0} files there will be replaced.", replaces));
                    question.Append(' ').Append(Lang.T(Settings.KeepBackup ? "The old ones are kept as .bak files next to them." : "The old ones won't be kept (keeping .bak files is turned off in Settings)."));
                }
                question.Append("\n\n").Append(Lang.T("Nothing is deleted on either side."));
                if (!Dialogs.Confirm(this, question.ToString())) return null;
            }
            IsEnabled = false;
            Cursor = Cursors.Wait;
            List<string> problems;
            try
            {
                var keep = Settings.KeepBackup;
                problems = await Task.Run(() => FolderCopy.Run(steps, keep));
            }
            finally
            {
                IsEnabled = true;
                Cursor = null;
            }
            if (problems.Count > 0)
            {
                var shown = string.Join("\n", problems.Take(8)) + (problems.Count > 8 ? "\n" + Lang.T("…and {0} more", problems.Count - 8) : string.Empty);
                if (!SelfTest.Running) Dialogs.Error(this, Lang.T("Some files couldn't be copied:\n\n{0}", shown));
            }
            else ShowMessage(files == 1 ? Lang.T("Copied \"{0}\" to {1}.", row.Item.Name, target) : Lang.T("Copied {0} files to {1}.", files, target));
            Compare();
            return problems;
        }

        // ================================================================== report and keys

        /// <summary>Saves the comparison as a web page. With no path, asks where. Returns the path saved to, or null.</summary>
        public string SaveReport(string path)
        {
            if (Comparison == null) return null;
            path = path ?? Dialogs.SaveFile(this, "Save the report", FolderName(LeftFolder) + " vs " + FolderName(RightFolder) + ".html", "Web page|*.html");
            if (path == null) return null;
            try
            {
                File.WriteAllText(path, FolderReport.Html(Comparison, _hideSame.IsChecked == true, DateTime.Now), new UTF8Encoding(false));
                ShowMessage(Lang.T("Saved {0}", path));
                return path;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Dialogs.Error(this, Lang.T("Couldn't save \"{0}\".\n\n{1}", path, ex.Message));
                return null;
            }
        }

        private void OnKey(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.Alt)
            {
                switch (key)
                {
                    case Key.Down: Step(1); e.Handled = true; return;
                    case Key.Up: Step(-1); e.Handled = true; return;
                    case Key.Right: _ = CopyChosen(true); e.Handled = true; return;
                    case Key.Left: _ = CopyChosen(false); e.Handled = true; return;
                }
            }
            if (mods != ModifierKeys.None) return;
            if (key == Key.F5) { Compare(); e.Handled = true; }
            else if (key == Key.F7) { Step(1); e.Handled = true; }
            else if (key == Key.Enter && Chosen != null) { OpenChosen(); e.Handled = true; }
            else if ((key == Key.Right || key == Key.Left) && Chosen != null && Chosen.Item.IsFolder && _list.IsKeyboardFocusWithin && Chosen.Expanded == (key == Key.Left))
            {
                Toggle(Chosen);
                e.Handled = true;
            }
        }
    }

    /// <summary>A list that can be refilled in one go (one "reset" instead of a change per row), for big folders.</summary>
    public sealed class RowList<T> : ObservableCollection<T>
    {
        public void Replace(IEnumerable<T> items)
        {
            Items.Clear();
            foreach (var i in items) Items.Add(i);
            OnPropertyChanged(new PropertyChangedEventArgs("Count"));
            OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
            OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
        }
    }

    /// <summary>One row of the folder list.</summary>
    public sealed class FolderRow
    {
        public FolderRow(FolderItem item, bool expanded)
        {
            Item = item;
            Expanded = expanded;
        }

        public FolderItem Item { get; }
        public bool Expanded { get; }

        public Thickness Indent => new Thickness(Item.Depth * 18, 0, 0, 0);
        public string Expander => Item.IsFolder && Item.Children.Count > 0 ? (Expanded ? "▾" : "▸") : string.Empty;
        public Geometry Icon => Application.Current.TryFindResource(Item.IsFolder ? "IconFolder" : "IconFile") as Geometry;
        public string Name => Item.Name;
        public string StateText => FolderComparison.StateText(Item.State);
        public FontWeight Weight => Item.IsFolder ? FontWeights.SemiBold : FontWeights.Normal;

        /// <summary>What it's like, in the subtle colour when it's the same and the text colour when it isn't.</summary>
        public Brush StateBrush => Brush(Item.State == FolderState.Same ? "SubtleText" : "Text");
        public Brush Mark => Brush(Item.State == FolderState.Different ? "DiffChangeLine" : Item.State == FolderState.OnlyLeft ? "DiffDeleteLine"
            : Item.State == FolderState.OnlyRight ? "DiffInsertLine" : Item.State == FolderState.Unreadable ? "DiffMoveLine" : null);

        public string LeftSize => Item.OnLeft && !Item.IsFolder ? Format.Size(Item.LeftSize) : string.Empty;
        public string RightSize => Item.OnRight && !Item.IsFolder ? Format.Size(Item.RightSize) : string.Empty;
        public string LeftTime => Item.OnLeft ? When(Item.LeftTime) : string.Empty;
        public string RightTime => Item.OnRight ? When(Item.RightTime) : string.Empty;

        public string Tip
        {
            get
            {
                var tip = Item.RelativePath;
                if (Item.Problem != null) tip += "\n" + Item.Problem;
                if (Item.IsFolder && Item.State != FolderState.Same)
                {
                    var files = Item.Below().Where(i => !i.IsFolder).ToList();
                    var changed = files.Count(i => i.IsChange);
                    if (changed > 0) tip += "\n" + Format.Count(changed, "file that differs", "files that differ");
                }
                return tip;
            }
        }

        private static string When(DateTime t) => t == default ? string.Empty : t.ToString("yyyy-MM-dd HH:mm");

        private static Brush Brush(string key) => key == null ? Brushes.Transparent : Application.Current.TryFindResource(key) as Brush ?? Brushes.Transparent;
    }

    public static class FolderTemplates
    {
        private const string Ns = "xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\"";
        private static readonly double[] Widths = { -1, 130, 90, 130, 90, 130 };

        /// <summary>The columns of a row, also used for the headings above the list.</summary>
        public static Grid Columns()
        {
            var g = new Grid();
            foreach (var w in Widths) g.ColumnDefinitions.Add(new ColumnDefinition { Width = w < 0 ? new GridLength(1, GridUnitType.Star) : new GridLength(w) });
            return g;
        }

        private static string ColumnXaml => "<Grid.ColumnDefinitions>" + string.Concat(Widths.Select(w => "<ColumnDefinition Width=\"" + (w < 0 ? "*" : w.ToString(System.Globalization.CultureInfo.InvariantCulture)) + "\"/>")) + "</Grid.ColumnDefinitions>";

        public static DataTemplate Row => (DataTemplate)XamlReader.Parse(
            "<DataTemplate " + Ns + "><Grid Height=\"26\" ToolTip=\"{Binding Tip}\" Background=\"Transparent\">" + ColumnXaml +
            "<Border Width=\"4\" Margin=\"-6,2,0,2\" CornerRadius=\"2\" HorizontalAlignment=\"Left\" Background=\"{Binding Mark}\"/>" +
            "<StackPanel Orientation=\"Horizontal\" Margin=\"{Binding Indent}\">" +
            "<TextBlock x:Name=\"Exp\" Text=\"{Binding Expander}\" Width=\"18\" FontSize=\"12\" Foreground=\"{DynamicResource SubtleText}\" VerticalAlignment=\"Center\" TextAlignment=\"Center\" Cursor=\"Hand\" Background=\"Transparent\"/>" +
            "<Viewbox Width=\"14\" Height=\"14\" Margin=\"0,0,8,0\" VerticalAlignment=\"Center\"><Canvas Width=\"24\" Height=\"24\"><Path Data=\"{Binding Icon}\" Stroke=\"{DynamicResource SubtleText}\" StrokeThickness=\"1.8\" StrokeLineJoin=\"Round\"/></Canvas></Viewbox>" +
            "<TextBlock Text=\"{Binding Name}\" FontWeight=\"{Binding Weight}\" VerticalAlignment=\"Center\" TextTrimming=\"CharacterEllipsis\"/></StackPanel>" +
            "<TextBlock Grid.Column=\"1\" Text=\"{Binding StateText}\" VerticalAlignment=\"Center\" Foreground=\"{Binding StateBrush}\" TextTrimming=\"CharacterEllipsis\"/>" +
            "<TextBlock Grid.Column=\"2\" Text=\"{Binding LeftSize}\" HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\" Margin=\"0,0,12,0\"/>" +
            "<TextBlock Grid.Column=\"3\" Text=\"{Binding LeftTime}\" VerticalAlignment=\"Center\" Foreground=\"{DynamicResource SubtleText}\"/>" +
            "<TextBlock Grid.Column=\"4\" Text=\"{Binding RightSize}\" HorizontalAlignment=\"Right\" VerticalAlignment=\"Center\" Margin=\"0,0,12,0\"/>" +
            "<TextBlock Grid.Column=\"5\" Text=\"{Binding RightTime}\" VerticalAlignment=\"Center\" Foreground=\"{DynamicResource SubtleText}\"/>" +
            "</Grid></DataTemplate>");
    }
}
