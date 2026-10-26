using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Kududiff.Controls;
using Kududiff.Core;

namespace Kududiff.Views
{
    /// <summary>
    /// A comparison of two files: both files side by side, the bands between them that join each change with copy
    /// arrows, a map of all the changes, and the tools to go through them, copy them across, save, and export.
    /// </summary>
    public sealed class MainWindow : Window
    {
        private readonly Button _update = Ui.Link(string.Empty, (s, e) => AppInfo.OpenUrl(AppInfo.ReleasesUrl + "/latest"));
        private readonly TextBlock _summary = Ui.Text("Choose two files to compare", 12.5);
        private readonly TextBlock _position = Ui.Subtle(string.Empty, 12.5, false);
        private readonly TextBlock _message = Ui.Subtle(string.Empty, 12.5, false);
        private readonly StackPanel _recent = new StackPanel { Margin = new Thickness(0, 26, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
        private readonly DispatcherTimer _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        private readonly DispatcherTimer _messageTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        private readonly LinkPane _link;
        private readonly Overview _overview;
        private readonly Button _options;
        private readonly List<Button> _needBoth = new List<Button>();
        private CompareOptions _compareOptions = Settings.Options;
        private List<Chunk> _changes = new List<Chunk>();
        private int _current = -1;
        private int _version;
        private bool _running;
        private string _comparedLeft, _comparedRight;
        private SidePanel _drivenSide;
        private DateTime _drivenUntil;
        private bool _reloading;

        public MainWindow()
        {
            Width = 1320;
            Height = 860;
            MinWidth = 900;
            MinHeight = 560;
            Dialogs.Prepare(this, AppInfo.Name);
            RefreshRecent();

            LeftSide = new SidePanel(this, Side.Left);
            RightSide = new SidePanel(this, Side.Right);
            LastFocused = RightSide;
            _link = new LinkPane(this);
            _overview = new Overview(this);

            var root = new Grid();
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // Header: the name, a new version, recent comparisons, settings and about.
            var header = Ui.Columns(Ui.Auto, Ui.Star(), Ui.Auto, Ui.Auto, Ui.Auto, Ui.Auto);
            header.Margin = new Thickness(16, 10, 12, 10);
            header.Children.Add(Ui.Row(LogoTile.Create(26), Ui.Text(AppInfo.Name, 15, bold: true).Margin(10, 0, 0, 0)).At(0, 0));
            _update.Visibility = Visibility.Collapsed;
            header.Children.Add(_update.Margin(0, 0, 14, 0).At(0, 2));
            Button recent = null;
            recent = Ui.Button("Recent", "IconHistory", "GhostButton", (s, e) => ShowRecentMenu(recent));
            header.Children.Add(recent.At(0, 3));
            header.Children.Add(Ui.Button("Settings", "IconSettings", "GhostButton", (s, e) => new SettingsWindow(this).ShowDialog()).At(0, 4));
            header.Children.Add(Ui.Button("About", "IconInfo", "GhostButton", (s, e) => About.Show(this)).At(0, 5));
            root.Children.Add(new Border { Child = header, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(0));

            // Toolbar.
            var tools = new WrapPanel { Margin = new Thickness(12, 6, 12, 6) };
            Button Tool(string text, string icon, string tip, Action action, bool needBoth = true)
            {
                var b = Ui.Small(Ui.Button(text, icon, "GhostButton", (s, e) => action()));
                b.ToolTip = tip;
                b.Margin = new Thickness(0, 0, 2, 0);
                if (needBoth) _needBoth.Add(b);
                tools.Children.Add(b);
                return b;
            }
            void Gap() => tools.Children.Add(Ui.Divider(true));
            Tool("Previous", "IconUp", "Go to the previous change (Alt+Up)", Previous);
            Tool("Next", "IconDown", "Go to the next change (Alt+Down)", Next);
            Gap();
            Tool("Copy to left", "IconArrowLeft", "Copy the current change from the right file to the left one (Alt+Left)", () => CopyCurrent(false));
            Tool("Copy to right", "IconArrowRight", "Copy the current change from the left file to the right one (Alt+Right)", () => CopyCurrent(true));
            Gap();
            Tool(string.Empty, "IconUndo", "Undo (Ctrl+Z) in the file you last edited", () => LastFocused?.Editor.Undo(), false);
            Tool(string.Empty, "IconRedo", "Redo (Ctrl+Y) in the file you last edited", () => LastFocused?.Editor.Redo(), false);
            Gap();
            _options = Tool("Options", "IconSettings", "What to ignore, how to compare, and how changes are shown", ShowOptions, false);
            Tool("Swap sides", "IconSwap", "Put the left file on the right and the right file on the left", Swap, false);
            Tool("Reload", "IconRefresh", "Read both files from disk again (F5)", Reload, false);
            Button export = null;
            export = Tool("Export", "IconExport", "Save the differences as a patch file or a web page report", () => ShowExportMenu(export));
            root.Children.Add(new Border { Child = tools, BorderThickness = new Thickness(0, 0, 0, 1) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(1));

            // The two files, the bands between them and the map of changes.
            var body = Ui.Columns(Ui.Star(), Ui.Px(64), Ui.Star(), Ui.Px(16));
            body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            body.RowDefinitions.Add(new RowDefinition());
            body.Margin = new Thickness(0, 0, 0, 0);
            body.Children.Add(LeftSide.Header.At(0, 0));
            body.Children.Add(RightSide.Header.At(0, 2, 2));
            body.Children.Add(LeftSide.Body.At(1, 0));
            body.Children.Add(_link.At(1, 1));
            body.Children.Add(RightSide.Body.At(1, 2));
            body.Children.Add(_overview.At(1, 3));
            root.Children.Add(body.At(2));

            // Status: the summary, where you are, a message, and what the colours mean.
            var status = Ui.Columns(Ui.Auto, Ui.Auto, Ui.Star(), Ui.Auto);
            status.Children.Add(_summary.At(0, 0));
            status.Children.Add(_position.Margin(18, 0, 0, 0).At(0, 1));
            _message.TextTrimming = TextTrimming.CharacterEllipsis;
            status.Children.Add(_message.Margin(18, 0, 12, 0).At(0, 2));
            var legend = Ui.Row(Swatch("DiffInsertLine", "Added"), Swatch("DiffDeleteLine", "Removed"), Swatch("DiffChangeLine", "Changed"), Swatch("DiffMoveLine", "Moved"));
            status.Children.Add(legend.At(0, 3));
            root.Children.Add(new Border { Child = status, Padding = new Thickness(16, 8, 16, 8), BorderThickness = new Thickness(0, 1, 0, 0) }
                .Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "BorderBrush").At(3));
            Content = root;

            foreach (var side in new[] { LeftSide, RightSide })
            {
                var s = side;
                s.View.ScrollOffsetChanged += (o, e) => Scrolled(s);
                s.View.VisualLinesChanged += (o, e) => RedrawLinks();
                s.Editor.TextArea.Caret.PositionChanged += (o, e) => CaretMoved(s);
            }
            _debounce.Tick += (s, e) =>
            {
                _debounce.Stop();
                StartCompare();
            };
            _messageTimer.Tick += (s, e) =>
            {
                _messageTimer.Stop();
                _message.Text = string.Empty;
            };
            // Follows the theme (also when Windows switches it): the syntax colours have a light and a dark set.
            SetResourceReference(TagProperty, "EditorBg");
            PreviewKeyDown += OnKey;
            Loaded += async (s, e) => await CheckForUpdates();
            Activated += (s, e) => ReloadChangedFiles();
            Closing += (s, e) =>
            {
                if (!SkipClosePrompt && !ConfirmDiscard()) e.Cancel = true;
            };
            UpdateState();
        }

        public SidePanel LeftSide { get; }
        public SidePanel RightSide { get; }
        public SidePanel LastFocused { get; set; }
        public Comparison Comparison { get; private set; }
        public Chunk CurrentChunk => _current >= 0 && _current < _changes.Count ? _changes[_current] : null;
        public int CurrentIndex => _current;
        public IReadOnlyList<Chunk> Changes => _changes;

        /// <summary>True while a comparison is waiting or running.</summary>
        public bool IsComparing => _debounce.IsEnabled || _running;

        /// <summary>For the self-test: close without asking about unsaved changes.</summary>
        public bool SkipClosePrompt { get; set; }

        public bool HasBoth => LeftSide.HasFile && RightSide.HasFile;

        public CompareOptions CompareOptions
        {
            get => _compareOptions.Clone();
            set
            {
                _compareOptions = value.Clone();
                Settings.Options = _compareOptions;
                UpdateOptionsButton();
                StartCompare();
            }
        }

        private static UIElement Swatch(string brush, string text)
        {
            var box = new Border { Width = 10, Height = 10, CornerRadius = new CornerRadius(2), Margin = new Thickness(14, 0, 5, 0) }.Res(Border.BackgroundProperty, brush);
            return Ui.Row(box, Ui.Subtle(text, 12, false));
        }

        private async Task CheckForUpdates()
        {
            if (!Settings.CheckForUpdates || SelfTest.Running) return;
            try
            {
                var info = await UpdateChecker.CheckAsync();
                if (info == null || !info.IsNewer || Settings.SkippedVersion == info.Latest.ToString(3)) return;
                _update.Content = $"{AppInfo.Name} {info.Latest.ToString(3)} is available";
                _update.Visibility = Visibility.Visible;
            }
            catch
            {
                // No internet: try again next time.
            }
        }

        protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
        {
            base.OnPropertyChanged(e);
            if (e.Property == TagProperty && LeftSide != null) ApplySettings();
        }

        /// <summary>Applies the editor settings and the theme's syntax colours to every open comparison.</summary>
        public static void RefreshAll()
        {
            foreach (var w in Application.Current.Windows.OfType<MainWindow>()) w.ApplySettings();
        }

        public void ApplySettings()
        {
            foreach (var side in new[] { LeftSide, RightSide })
            {
                side.ApplyViewSettings();
                side.ApplySyntax();
                side.Redraw();
            }
            RedrawLinks();
        }

        public void ShowMessage(string text)
        {
            _message.Text = text;
            _message.ToolTip = text;
            _messageTimer.Stop();
            _messageTimer.Start();
        }

        // ================================================================== files

        public void PickFile(SidePanel side)
        {
            var path = Dialogs.PickFile(this, side.Side == Side.Left ? "Choose the left file" : "Choose the right file", "All files|*.*");
            if (path != null) LoadSide(side, path);
        }

        /// <summary>Opens a file on one side. Returns false if it couldn't be read, or the user kept unsaved changes.</summary>
        public bool LoadSide(SidePanel side, string path)
        {
            if (Directory.Exists(path))
            {
                Dialogs.Error(this, $"\"{path}\" is a folder. {AppInfo.Name} compares two files.");
                return false;
            }
            if (side.IsDirty && !Dialogs.Confirm(this, $"\"{side.DisplayName}\" has changes that aren't saved. Throw them away and open another file?"))
                return false;
            TextFile file;
            try
            {
                file = TextFile.Read(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException || ex is ArgumentException || ex is NotSupportedException)
            {
                Dialogs.Error(this, ex.Message);
                return false;
            }
            side.Load(file);
            Settings.LastFolder = Path.GetDirectoryName(path);
            if (HasBoth)
            {
                Settings.AddRecent(LeftSide.File.Path, RightSide.File.Path);
                RefreshRecent();
            }
            _current = -1;
            StartCompare();
            return true;
        }

        public void Open(string left, string right)
        {
            if (LoadSide(LeftSide, left)) LoadSide(RightSide, right);
        }

        public void Dropped(SidePanel side, string[] files)
        {
            if (files.Length >= 2) Open(files[0], files[1]);
            else LoadSide(side, files[0]);
            Activate();
        }

        public void TextEdited(SidePanel side)
        {
            if (_reloading) return;
            UpdateTitle();
            _debounce.Stop();
            _debounce.Start();
        }

        /// <summary>Saves one side. Returns true when it was saved.</summary>
        public bool Save(SidePanel side, bool ask)
        {
            if (!side.HasFile || side.File.Path == null) return false;
            var name = side.DisplayName;
            if (ask && side.File.ChangedOnDisk() && File.Exists(side.File.Path) &&
                !Dialogs.Confirm(this, $"\"{name}\" was changed by another program after it was opened here. Save over those changes?"))
                return false;
            var text = side.Text;
            if (!side.File.CanEncode(text))
            {
                if (ask && !Dialogs.Confirm(this, $"Some characters can't be saved in {side.File.EncodingName}, the encoding \"{name}\" uses. Save it as UTF-8 instead?"))
                    return false;
                side.File.UseUtf8();
            }
            try
            {
                side.File.Save(text, Settings.KeepBackup);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidOperationException)
            {
                Dialogs.Error(this, $"Couldn't save \"{name}\".\n\n{ex.Message}");
                return false;
            }
            side.MarkSaved();
            UpdateTitle();
            ShowMessage(Settings.KeepBackup ? $"Saved {name} (the previous version is in {name}.bak)" : $"Saved {name}");
            return true;
        }

        private void SaveFocused()
        {
            var side = LastFocused != null && LastFocused.IsDirty ? LastFocused : new[] { LeftSide, RightSide }.FirstOrDefault(s => s.IsDirty);
            if (side != null) Save(side, true);
        }

        /// <summary>Asks about unsaved changes before closing. False to keep the window open.</summary>
        private bool ConfirmDiscard()
        {
            foreach (var side in new[] { LeftSide, RightSide }.Where(s => s.IsDirty))
            {
                var answer = MessageBox.Show(this, $"Save the changes to \"{side.DisplayName}\"?", AppInfo.Name, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (answer == MessageBoxResult.Cancel) return false;
                if (answer == MessageBoxResult.Yes && !Save(side, true)) return false;
            }
            return true;
        }

        /// <summary>Reads both files from disk again (asking first about unsaved changes).</summary>
        public void Reload()
        {
            foreach (var side in new[] { LeftSide, RightSide }.Where(s => s.HasFile && s.File.Path != null))
            {
                if (side.IsDirty && !Dialogs.Confirm(this, $"Throw away the unsaved changes to \"{side.DisplayName}\" and read it from disk again?")) continue;
                ReloadSide(side);
            }
            StartCompare();
        }

        /// <summary>For the self-test: reads both files again, throwing away unsaved changes.</summary>
        public void ReloadForTest()
        {
            foreach (var side in new[] { LeftSide, RightSide }.Where(s => s.HasFile)) ReloadSide(side);
            StartCompare();
        }

        private void ReloadSide(SidePanel side)
        {
            TextFile file;
            try
            {
                file = TextFile.Read(side.File.Path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is InvalidDataException)
            {
                ShowMessage(ex.Message);
                return;
            }
            var offset = side.Editor.VerticalOffset;
            _reloading = true;
            try { side.Load(file); }
            finally { _reloading = false; }
            side.Editor.ScrollToVerticalOffset(offset);
        }

        /// <summary>When another program changed a file that has no unsaved changes here, it is read again.</summary>
        private void ReloadChangedFiles()
        {
            var any = false;
            foreach (var side in new[] { LeftSide, RightSide })
            {
                if (!side.HasFile || side.IsDirty || side.File.Path == null || !File.Exists(side.File.Path) || !side.File.ChangedOnDisk()) continue;
                ReloadSide(side);
                ShowMessage($"\"{side.DisplayName}\" was changed by another program and has been read again.");
                any = true;
            }
            if (any) StartCompare();
        }

        public void Swap()
        {
            LeftSide.SwapWith(RightSide);
            _current = -1;
            StartCompare();
        }

        // ================================================================== comparing

        /// <summary>Compares the two texts in the background; a newer comparison replaces an older one still running.</summary>
        public void StartCompare()
        {
            _debounce.Stop();
            if (!HasBoth)
            {
                _version++;
                _running = false;
                Apply(null, null, null);
                return;
            }
            var version = ++_version;
            var left = LeftSide.Text;
            var right = RightSide.Text;
            var options = _compareOptions.Clone();
            _running = true;
            UpdateState();
            Task.Run(() => Comparison.Run(left, right, options)).ContinueWith(t =>
            {
                if (version != _version) return;
                _running = false;
                if (t.IsFaulted)
                {
                    ShowMessage("Couldn't compare: " + (t.Exception?.InnerException?.Message ?? "unknown error"));
                    UpdateState();
                    return;
                }
                Apply(t.Result, left, right);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        /// <summary>Makes sure the comparison shown is of the text as it is now (comparing right away if not).</summary>
        private void EnsureFresh()
        {
            if (!HasBoth) return;
            var left = LeftSide.Text;
            var right = RightSide.Text;
            if (Comparison != null && !_debounce.IsEnabled && left == _comparedLeft && right == _comparedRight) return;
            _debounce.Stop();
            _version++;
            _running = false;
            Apply(Comparison.Run(left, right, _compareOptions.Clone()), left, right);
        }

        private void Apply(Comparison result, string left, string right)
        {
            var previous = CurrentChunk;
            Comparison = result;
            _comparedLeft = left;
            _comparedRight = right;
            _changes = result?.Changes.ToList() ?? new List<Chunk>();

            var l = new LineLook[result?.Left.Count ?? 0];
            var r = new LineLook[result?.Right.Count ?? 0];
            if (result != null)
            {
                foreach (var c in _changes)
                {
                    var look = c.Kind == ChunkKind.Replace ? LineLook.Changed : c.Kind == ChunkKind.Delete ? LineLook.Removed : LineLook.Added;
                    for (var i = c.A; i < c.AEnd; i++) l[i] = look == LineLook.Added ? LineLook.Removed : look;
                    for (var i = c.B; i < c.BEnd; i++) r[i] = look == LineLook.Removed ? LineLook.Added : look;
                }
                foreach (var i in result.MovedLeft.Where(i => i < l.Length)) l[i] = LineLook.Moved;
                foreach (var i in result.MovedRight.Where(i => i < r.Length)) r[i] = LineLook.Moved;
            }
            LeftSide.Looks = l;
            RightSide.Looks = r;

            // Stay at the same place: the change that starts where the current one did, or the next one.
            if (previous != null && _changes.Count > 0)
            {
                var index = _changes.FindIndex(c => c.A >= previous.A);
                _current = index >= 0 ? index : _changes.Count - 1;
            }
            else if (_current >= _changes.Count) _current = _changes.Count - 1;

            LeftSide.Redraw();
            RightSide.Redraw();
            UpdateState();
            // Line up the other side with the one being worked in.
            if (result != null)
            {
                var from = LastFocused ?? LeftSide;
                Sync(from, from == LeftSide ? RightSide : LeftSide);
            }
        }

        private void UpdateState()
        {
            UpdateTitle();
            UpdateOptionsButton();
            foreach (var b in _needBoth) b.IsEnabled = HasBoth;
            if (!LeftSide.HasFile && !RightSide.HasFile) _summary.Text = "Choose two files to compare, or drop them on the two sides";
            else if (!HasBoth) _summary.Text = LeftSide.HasFile ? "Now choose the right file" : "Now choose the left file";
            else if (Comparison == null) _summary.Text = "Comparing…";
            else _summary.Text = Comparison.Summary();
            _summary.Res(TextBlock.ForegroundProperty, Comparison != null && Comparison.Identical ? "OkText" : "Text");
            _position.Text = _changes.Count == 0 ? string.Empty : _current >= 0 ? $"Change {_current + 1} of {_changes.Count}" : $"{_changes.Count} to go through";
            RedrawLinks();
        }

        private void UpdateTitle()
        {
            string Name(SidePanel s) => (s.IsDirty ? "• " : string.Empty) + s.DisplayName;
            Title = LeftSide.HasFile || RightSide.HasFile ? $"{Name(LeftSide)} ↔ {Name(RightSide)} - {AppInfo.Name}" : AppInfo.Name;
        }

        private void UpdateOptionsButton()
        {
            var o = _compareOptions;
            var on = (o.Whitespace != WhitespaceMode.Compare ? 1 : 0) + (o.IgnoreCase ? 1 : 0) + (o.IgnoreBlankLines ? 1 : 0) + (o.CompareLineEndings ? 1 : 0)
                     + (o.IgnorePatterns.Any(p => !string.IsNullOrWhiteSpace(p)) ? 1 : 0) + (o.Algorithm == DiffAlgorithm.Patience ? 1 : 0);
            var text = on == 0 ? "Options" : $"Options ({on} on)";
            _options.Content = Ui.Label("IconSettings", text);
            System.Windows.Automation.AutomationProperties.SetName(_options, text);
            _options.ToolTip = "What to ignore, how to compare, and how changes are shown\n\nNow: " + Export.OptionsText(o)
                               + (o.Inline == InlineMode.Off ? ", changed lines only" : o.Inline == InlineMode.Words ? ", changed words shown" : ", changed characters shown")
                               + (o.DetectMoves ? ", moved code found" : string.Empty);
        }

        private void ShowOptions()
        {
            var chosen = OptionsWindow.Show(this, _compareOptions);
            if (chosen != null) CompareOptions = chosen;
        }

        // ================================================================== going through changes

        public void Next()
        {
            if (_changes.Count == 0) return;
            if (_current >= 0) GoTo(Math.Min(_current + 1, _changes.Count - 1));
            else
            {
                var top = (int)Math.Floor(LeftSide.TopLine);
                var index = _changes.FindIndex(c => c.A >= top);
                GoTo(index >= 0 ? index : _changes.Count - 1);
            }
        }

        public void Previous()
        {
            if (_changes.Count == 0) return;
            if (_current >= 0) GoTo(Math.Max(_current - 1, 0));
            else
            {
                var top = (int)Math.Floor(LeftSide.TopLine);
                var index = _changes.FindLastIndex(c => c.A < top);
                GoTo(Math.Max(0, index));
            }
        }

        /// <summary>Makes a change the current one and shows it a third of the way down on both sides.</summary>
        public void GoTo(int index)
        {
            if (index < 0 || index >= _changes.Count) return;
            _current = index;
            var c = _changes[index];
            var third = RightSide.Editor.ViewportHeight / 3;
            Drive(LeftSide, RightSide);
            LeftSide.Editor.ScrollToVerticalOffset(Math.Max(0, LeftSide.OffsetOf(c.A) - third));
            RightSide.Editor.ScrollToVerticalOffset(Math.Max(0, RightSide.OffsetOf(c.B) - third));
            LeftSide.Redraw();
            RightSide.Redraw();
            UpdateState();
        }

        /// <summary>Clicking or moving in a change makes it the current one.</summary>
        private void CaretMoved(SidePanel side)
        {
            if (_changes.Count == 0 || !side.Editor.TextArea.IsKeyboardFocusWithin) return;
            var line = side.Editor.TextArea.Caret.Line - 1;
            var left = side.Side == Side.Left;
            var index = _changes.FindIndex(c =>
            {
                var start = left ? c.A : c.B;
                var count = left ? c.ACount : c.BCount;
                return count > 0 ? line >= start && line < start + count : line == start;
            });
            if (index < 0 || index == _current) return;
            _current = index;
            LeftSide.Redraw();
            RightSide.Redraw();
            UpdateState();
        }

        public void CopyCurrent(bool toRight)
        {
            if (_current < 0) Next();
            if (_current >= 0) CopyChange(_current, toRight);
        }

        /// <summary>Copies a change from one side to the other, as one step that Undo takes back.</summary>
        public void CopyChange(int index, bool toRight)
        {
            if (!HasBoth) return;
            var before = Comparison;
            EnsureFresh();
            if (!ReferenceEquals(before, Comparison))
            {
                ShowMessage("The files changed since the comparison was shown. Look again, then copy.");
                return;
            }
            if (index < 0 || index >= _changes.Count) return;
            var c = _changes[index];
            var source = toRight ? LeftSide : RightSide;
            var target = toRight ? RightSide : LeftSide;
            var edit = toRight
                ? TextOps.CopyLines(source.Text, c.A, c.ACount, target.Text, c.B, c.BCount)
                : TextOps.CopyLines(source.Text, c.B, c.BCount, target.Text, c.A, c.ACount);
            _current = index;
            target.Editor.Document.Replace(edit.Start, edit.Length, edit.Insert);
            LastFocused = target;
            StartCompare();
        }

        // ================================================================== scrolling together

        private void Drive(params SidePanel[] sides)
        {
            // Scrolling set by the app shouldn't be mirrored back.
            _drivenSide = sides.Length == 1 ? sides[0] : null;
            _drivenUntil = DateTime.UtcNow.AddMilliseconds(250);
            _drivenBoth = sides.Length > 1;
        }

        private bool _drivenBoth;

        private void Scrolled(SidePanel side)
        {
            RedrawLinks();
            if (DateTime.UtcNow < _drivenUntil && (_drivenBoth || ReferenceEquals(side, _drivenSide))) return;
            Sync(side, side == LeftSide ? RightSide : LeftSide);
        }

        /// <summary>Scrolls one side so the line in the middle of the other lines up with it.</summary>
        private void Sync(SidePanel from, SidePanel to)
        {
            if (Comparison == null || !from.HasFile || !to.HasFile) return;
            var half = from.Editor.ViewportHeight / 2;
            var line = from.LineAt(from.Editor.VerticalOffset + half);
            var other = Comparison.MapLine(line, from.Side == Side.Left);
            var target = Math.Max(0, to.OffsetOf(other) - half);
            Drive(to);
            if (Math.Abs(to.Editor.VerticalOffset - target) > 0.5) to.Editor.ScrollToVerticalOffset(target);
            if (Math.Abs(to.Editor.HorizontalOffset - from.Editor.HorizontalOffset) > 0.5) to.Editor.ScrollToHorizontalOffset(from.Editor.HorizontalOffset);
        }

        private void RedrawLinks()
        {
            _link?.InvalidateVisual();
            _overview?.InvalidateVisual();
        }

        // ================================================================== keys

        private void OnKey(object sender, KeyEventArgs e)
        {
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.Alt)
            {
                switch (key)
                {
                    case Key.Down: Next(); e.Handled = true; return;
                    case Key.Up: Previous(); e.Handled = true; return;
                    case Key.Right: CopyCurrent(true); e.Handled = true; return;
                    case Key.Left: CopyCurrent(false); e.Handled = true; return;
                }
            }
            if (mods == ModifierKeys.Control && key == Key.S)
            {
                SaveFocused();
                e.Handled = true;
            }
            else if (mods == ModifierKeys.None && key == Key.F5)
            {
                Reload();
                e.Handled = true;
            }
            else if (mods == ModifierKeys.None && key == Key.F7)
            {
                Next();
                e.Handled = true;
            }
            else if (mods == ModifierKeys.Shift && key == Key.F7)
            {
                Previous();
                e.Handled = true;
            }
        }

        // ================================================================== recent and export

        public UIElement RecentPanel() => _recent;

        private void RefreshRecent()
        {
            _recent.Children.Clear();
            var list = Settings.Recent.Take(6).ToList();
            _recent.Visibility = list.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (list.Count == 0) return;
            var heading = Ui.Subtle("Compared recently", 12, false).Margin(0, 0, 0, 6);
            heading.HorizontalAlignment = HorizontalAlignment.Center;
            _recent.Children.Add(heading);
            foreach (var (left, right) in list)
            {
                var (l, r) = (left, right);
                var link = Ui.Link(Path.GetFileName(l) + "  ↔  " + Path.GetFileName(r), (s, e) => Open(l, r));
                link.ToolTip = l + "\n" + r;
                link.HorizontalAlignment = HorizontalAlignment.Center;
                link.Margin = new Thickness(0, 2, 0, 2);
                _recent.Children.Add(link);
            }
        }

        private void ShowRecentMenu(FrameworkElement at)
        {
            var menu = new ContextMenu { PlacementTarget = at, Placement = PlacementMode.Bottom };
            var list = Settings.Recent;
            foreach (var (left, right) in list)
            {
                var (l, r) = (left, right);
                var item = new MenuItem { Header = Path.GetFileName(l) + "  ↔  " + Path.GetFileName(r), ToolTip = l + "\n" + r };
                item.Click += (s, e) => Open(l, r);
                menu.Items.Add(item);
            }
            if (list.Count == 0) menu.Items.Add(new MenuItem { Header = "Nothing compared yet", IsEnabled = false });
            else
            {
                menu.Items.Add(new Separator());
                var clear = new MenuItem { Header = "Clear this list" };
                clear.Click += (s, e) =>
                {
                    Settings.ClearRecent();
                    RefreshRecent();
                };
                menu.Items.Add(clear);
            }
            menu.IsOpen = true;
        }

        private void ShowExportMenu(FrameworkElement at)
        {
            var menu = new ContextMenu { PlacementTarget = at, Placement = PlacementMode.Bottom };
            var patch = new MenuItem { Header = "Save as a patch file…", ToolTip = "A unified diff that turns the left file into the right one (for patch or git apply)" };
            patch.Click += (s, e) => SaveExport(false, null);
            var html = new MenuItem { Header = "Save as a web page report…", ToolTip = "Both files side by side with the changes coloured, to read in a browser or send to someone" };
            html.Click += (s, e) => SaveExport(true, null);
            menu.Items.Add(patch);
            menu.Items.Add(html);
            menu.IsOpen = true;
        }

        public string PatchText() =>
            Export.Patch(LeftSide.Text, RightSide.Text, "a/" + LeftSide.DisplayName, "b/" + RightSide.DisplayName, _compareOptions.Algorithm);

        public string ReportText()
        {
            EnsureFresh();
            return Export.Html(Comparison, LeftSide.File.Path ?? LeftSide.DisplayName, RightSide.File.Path ?? RightSide.DisplayName, DateTime.Now);
        }

        /// <summary>Saves the patch or the report. With no path, asks where. Returns the path saved to, or null.</summary>
        public string SaveExport(bool html, string path)
        {
            if (!HasBoth) return null;
            var baseName = Path.GetFileNameWithoutExtension(RightSide.DisplayName);
            if (html)
            {
                path = path ?? Dialogs.SaveFile(this, "Save the report", baseName + " differences.html", "Web page|*.html");
                if (path == null) return null;
                Write(path, ReportText());
            }
            else
            {
                var text = PatchText();
                if (text.Length == 0)
                {
                    if (!SelfTest.Running) Dialogs.Info(this, "The files are exactly the same, so there's nothing to put in a patch.");
                    return null;
                }
                path = path ?? Dialogs.SaveFile(this, "Save the patch", baseName + ".patch", "Patch file|*.patch;*.diff|All files|*.*");
                if (path == null) return null;
                Write(path, text);
            }
            return path;

            void Write(string file, string content)
            {
                try
                {
                    File.WriteAllText(file, content, new UTF8Encoding(false));
                    ShowMessage("Saved " + file);
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
                {
                    Dialogs.Error(this, $"Couldn't save \"{file}\".\n\n{ex.Message}");
                    path = null;
                }
            }
        }
    }
}
