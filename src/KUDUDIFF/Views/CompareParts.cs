// The parts of the comparison window: each side's panel (its header and text editor), the painting of changes under the
// text, the strip between the sides with the copy arrows, and the map beside the scrollbar.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Document;
using ICSharpCode.AvalonEdit.Rendering;
using Kududiff.Controls;
using Kududiff.Core;

namespace Kududiff.Views
{
    /// <summary>Which side of the comparison: left or right.</summary>
    public enum Side { Left, Right }

    /// <summary>What a line looks like in the comparison.</summary>
    public enum LineLook : byte { Same, Removed, Added, Changed, Moved }

    /// <summary>
    /// One side of the comparison: the file, its header (name, folder, encoding, Open and Save) and its editor, which
    /// colours the changed lines. The comparison itself lives in the window; this only shows it.
    /// </summary>
    public sealed class SidePanel
    {
        /// <summary>The comparison window this panel belongs to.</summary>
        private readonly MainWindow _owner;
        /// <summary>The file's name, above the text.</summary>
        private readonly TextBlock _name = Ui.Text(string.Empty, 13.5, bold: true);
        /// <summary>The folder the file is in.</summary>
        private readonly TextBlock _folder = Ui.Subtle(string.Empty, 11.5, false);
        /// <summary>The encoding and line endings (or the kind of document).</summary>
        private readonly TextBlock _info = Ui.Subtle(string.Empty, 11.5, false);
        /// <summary>The dot shown when the text has changes not saved yet.</summary>
        private readonly System.Windows.Shapes.Ellipse _dirty = Ui.Dot("Accent", 8);
        /// <summary>The save button in the header.</summary>
        private readonly Button _save;
        /// <summary>What's shown before a file is chosen ("Open a file or drop one here").</summary>
        private readonly Border _placeholder;
        /// <summary>Paints the changes under this side's text.</summary>
        private readonly DiffRenderer _renderer;

        /// <summary>Which side this is.</summary>
        public Side Side { get; }
        /// <summary>The text editor (AvalonEdit) showing the file.</summary>
        public TextEditor Editor { get; }
        /// <summary>The file shown, as it was read or last saved (null before one is chosen).</summary>
        public TextFile File { get; private set; }
        /// <summary>The header above the text (name, folder, details, save button).</summary>
        public FrameworkElement Header { get; }
        /// <summary>The text editor with the placeholder over it.</summary>
        public Grid Body { get; }
        /// <summary>
        /// How each line looks in the comparison (same, removed, added, changed, moved), by line number.
        /// </summary>
        public LineLook[] Looks { get; set; } = new LineLook[0];

        /// <summary>Makes one side's panel.</summary>
        public SidePanel(MainWindow owner, Side side)
        {
            _owner = owner;
            Side = side;
            Editor = new TextEditor
            {
                ShowLineNumbers = true,
                FontFamily = new FontFamily("Consolas, Cascadia Mono, Courier New"),
                IsReadOnly = true,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = side == Side.Left ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto,
                Padding = new Thickness(4, 0, 0, 0)
            };
            Editor.SetResourceReference(Control.BackgroundProperty, "EditorBg");
            Editor.SetResourceReference(Control.ForegroundProperty, "Text");
            Editor.SetResourceReference(TextEditor.LineNumbersForegroundProperty, "GutterText");
            Editor.TextArea.SelectionCornerRadius = 0;
            Editor.TextArea.SetResourceReference(ICSharpCode.AvalonEdit.Editing.TextArea.SelectionBrushProperty, "SelectionBg");
            Editor.TextArea.SelectionForeground = null;
            Editor.TextArea.SelectionBorder = null;
            Editor.Options.EnableHyperlinks = false;
            Editor.Options.EnableEmailHyperlinks = false;
            Editor.Options.AllowScrollBelowDocument = true;
            System.Windows.Automation.AutomationProperties.SetName(Editor, side == Side.Left ? "Left file" : "Right file");
            _renderer = new DiffRenderer(this, owner);
            Editor.TextArea.TextView.BackgroundRenderers.Add(_renderer);
            ApplyViewSettings();

            // Header: name and folder, encoding and line endings, Open and Save.
            var header = Ui.Columns(Ui.Star(), Ui.Auto, Ui.Auto);
            header.Margin = new Thickness(side == Side.Left ? 12 : 4, 8, side == Side.Left ? 4 : 12, 6);
            _dirty.Visibility = Visibility.Collapsed;
            _dirty.Margin = new Thickness(0, 0, 7, 0);
            _dirty.VerticalAlignment = VerticalAlignment.Center;
            var title = Ui.Row(_dirty, _name);
            _name.TextTrimming = TextTrimming.CharacterEllipsis;
            _folder.TextTrimming = TextTrimming.CharacterEllipsis;
            header.Children.Add(Ui.Column(title, Ui.Row(_folder, _info.Margin(10, 0, 0, 0))).At(0, 0));
            var open = Ui.Small(Ui.Button("Open…", "IconFolder", "GhostButton", (s, e) => _owner.PickFile(this)));
            open.ToolTip = side == Side.Left ? "Choose the left file" : "Choose the right file";
            header.Children.Add(open.At(0, 1));
            _save = Ui.Small(Ui.Button("Save", "IconSave", "GhostButton", (s, e) => _owner.Save(this, true)));
            _save.ToolTip = "Save this file (Ctrl+S)";
            _save.IsEnabled = false;
            header.Children.Add(_save.Margin(4, 0, 0, 0).At(0, 2));
            Header = header;

            // Body: the editor, with a drop area over it until a file is chosen.
            Body = new Grid { AllowDrop = true };
            var frame = new Border { BorderThickness = new Thickness(1), Child = Editor }.Res(Border.BorderBrushProperty, "BorderBrush");
            Body.Children.Add(frame);
            var openBig = Ui.Button("Open a file…", "IconFolder", "PrimaryButton", (s, e) => _owner.PickFile(this));
            var drop = Ui.Column(
                Ui.Icon("IconFile", 34).Res(System.Windows.Documents.TextElement.ForegroundProperty, "SubtleText"),
                Ui.Text(side == Side.Left ? "Drop the left file here" : "Drop the right file here", 16, bold: true).Margin(0, 12, 0, 4),
                Ui.Subtle("or", 12.5).Margin(0, 0, 0, 10),
                openBig);
            foreach (FrameworkElement child in drop.Children) child.HorizontalAlignment = HorizontalAlignment.Center;
            drop.HorizontalAlignment = HorizontalAlignment.Center;
            drop.VerticalAlignment = VerticalAlignment.Center;
            if (side == Side.Left)
            {
                var folders = Ui.Link("Compare two folders instead", (s, e) => App.ShowFolderWindow());
                folders.Margin = new Thickness(0, 14, 0, 0);
                drop.Children.Add(folders);
                folders.HorizontalAlignment = HorizontalAlignment.Center;
            }
            var dropContent = new StackPanel();
            dropContent.Children.Add(drop);
            if (side == Side.Left) dropContent.Children.Add(owner.RecentPanel());
            _placeholder = new Border
            {
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(10),
                Margin = new Thickness(14),
                Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = new Grid { VerticalAlignment = VerticalAlignment.Center, Children = { dropContent } } }
            }.Res(Border.BackgroundProperty, "SurfaceBg").Res(Border.BorderBrushProperty, "InputBorder");
            Body.Children.Add(_placeholder);

            Body.PreviewDragOver += (s, e) =>
            {
                e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            Body.PreviewDrop += (s, e) =>
            {
                if (!(e.Data.GetData(DataFormats.FileDrop) is string[] files) || files.Length == 0) return;
                e.Handled = true;
                _owner.Dropped(this, files);
            };
            Editor.Document.Changed += OnDocumentChanged;
            Editor.TextArea.GotKeyboardFocus += (s, e) => _owner.LastFocused = this;
            UpdateHeader();
        }

        /// <summary>True once a file is shown.</summary>
        public bool HasFile => File != null;

        /// <summary>True when the editor's view can answer questions about line positions.</summary>
        public bool Ready => !Switching && View.Document != null && ReferenceEquals(View.Document, Editor.Document);
        /// <summary>True when the text has changes not saved yet.</summary>
        public bool IsDirty => File != null && !Editor.Document.UndoStack.IsOriginalFile;
        /// <summary>The text as it is now in the editor.</summary>
        public string Text => Editor.Document.Text;
        /// <summary>The file's name, or "(none)".</summary>
        public string DisplayName => File?.Path != null ? Path.GetFileName(File.Path) : "(none)";
        /// <summary>The editor's view (for scrolling and drawing).</summary>
        public TextView View => Editor.TextArea.TextView;

        /// <summary>Lines as the comparison counts them (a final line break doesn't start another line).</summary>
        public int LineCount
        {
            get
            {
                var doc = Editor.Document;
                if (doc.TextLength == 0) return 0;
                return doc.GetLineByNumber(doc.LineCount).Length == 0 ? doc.LineCount - 1 : doc.LineCount;
            }
        }

        /// <summary>True while the editor is being given another document (its view has none for a moment).</summary>
        public bool Switching { get; private set; }

        /// <summary>Shows a file in this panel.</summary>
        public void Load(TextFile file)
        {
            Switching = true;
            try { LoadDocument(file); }
            finally { Switching = false; }
            ApplySyntax();
            UpdateHeader();
        }

        /// <summary>
        /// Gives the editor a new document with the file's text (look-only for Word, Excel, PowerPoint and PDF files),
        /// its colours and settings.
        /// </summary>
        private void LoadDocument(TextFile file)
        {
            Editor.Document.Changed -= OnDocumentChanged;
            var doc = new TextDocument(file.Text);
            Editor.Document = doc;
            doc.UndoStack.ClearAll();
            doc.Changed += OnDocumentChanged;
            File = file;
            Editor.IsReadOnly = file.IsDocument; // shown as text, never changed
            Editor.ScrollToHome();
        }

        /// <summary>Swaps files, text and undo history with the other side.</summary>
        public void SwapWith(SidePanel other)
        {
            Switching = other.Switching = true;
            try { SwapDocuments(other); }
            finally { Switching = other.Switching = false; }
            foreach (var p in new[] { this, other })
            {
                p.Editor.IsReadOnly = p.File == null || p.File.IsDocument;
                p.ApplySyntax();
                p.UpdateHeader();
            }
        }

        /// <summary>
        /// Trades documents and files with the other side (for "Swap sides"), keeping each one's undo history.
        /// </summary>
        private void SwapDocuments(SidePanel other)
        {
            var (myDoc, myFile) = (Editor.Document, File);
            Editor.Document.Changed -= OnDocumentChanged;
            other.Editor.Document.Changed -= other.OnDocumentChanged;
            Editor.Document = other.Editor.Document;
            File = other.File;
            other.Editor.Document = myDoc;
            other.File = myFile;
            Editor.Document.Changed += OnDocumentChanged;
            other.Editor.Document.Changed += other.OnDocumentChanged;
        }

        /// <summary>Marks the text as saved (no dot, undo history as it is) after saving.</summary>
        public void MarkSaved()
        {
            Editor.Document.UndoStack.MarkAsOriginalFile();
            UpdateHeader();
        }

        /// <summary>Tells the window the text was edited, so it compares again shortly.</summary>
        private void OnDocumentChanged(object sender, DocumentChangeEventArgs e)
        {
            UpdateHeader();
            _owner.TextEdited(this);
        }

        /// <summary>
        /// Fills the header from the file: its name, folder, encoding and line endings, and the dot when not saved.
        /// </summary>
        public void UpdateHeader()
        {
            _placeholder.Visibility = File == null ? Visibility.Visible : Visibility.Collapsed;
            if (File == null)
            {
                _name.Text = Side == Side.Left ? "Left file" : "Right file";
                _folder.Text = "Not chosen yet";
                _info.Text = string.Empty;
                _save.IsEnabled = false;
                _dirty.Visibility = Visibility.Collapsed;
                return;
            }
            _name.Text = File.Path != null ? Path.GetFileName(File.Path) : "Untitled";
            _folder.Text = File.Path != null ? Path.GetDirectoryName(File.Path) : string.Empty;
            _info.Text = File.IsDocument
                ? Lang.T("{0}, shown as text · look only", Lang.T(File.DocumentKind))
                : File.EncodingName + " · " + TextFile.Describe(TextFile.DetectLineEndings(Text));
            Header.ToolTip = File.Path;
            _dirty.Visibility = IsDirty ? Visibility.Visible : Visibility.Collapsed;
            _dirty.ToolTip = IsDirty ? "Changed, not saved yet" : null;
            _save.IsEnabled = IsDirty;
        }

        /// <summary>Colours the text for its language (C#, XML...), in the theme's colours.</summary>
        public void ApplySyntax()
        {
            Editor.SyntaxHighlighting = Settings.SyntaxColours && File != null && !File.IsDocument
                ? Syntax.Get(Syntax.LanguageFor(File.Path), ThemeManager.IsDark)
                : null;
        }

        /// <summary>
        /// Uses the settings for the editor: the font size, line numbers, wrapping, spaces shown and tab width.
        /// </summary>
        public void ApplyViewSettings()
        {
            Editor.FontSize = Settings.FontSize;
            Editor.WordWrap = Settings.WordWrap;
            Editor.Options.ShowSpaces = Settings.ShowInvisibles;
            Editor.Options.ShowTabs = Settings.ShowInvisibles;
            Editor.Options.ShowEndOfLine = Settings.ShowInvisibles;
            Editor.TextArea.TextView.SetResourceReference(TextView.NonPrintableCharacterBrushProperty, "GutterText");
        }

        /// <summary>Paints the changes again.</summary>
        public void Redraw()
        {
            View.InvalidateLayer(KnownLayer.Background);
        }

        // ------------------------------------------------------------------ positions

        /// <summary>The top of a line (counted from 0) in the editor, relative to its visible area. The line after the last is just below it.</summary>
        public double TopOf(int line)
        {
            if (!Ready) return 0;
            var view = View;
            var doc = Editor.Document;
            var number = Math.Max(1, Math.Min(line + 1, doc.LineCount));
            var top = view.GetVisualTopByDocumentLine(number);
            if (line + 1 > doc.LineCount) top += view.DefaultLineHeight * (line + 1 - doc.LineCount);
            return top - view.VerticalOffset;
        }

        /// <summary>The line (with a fraction) at a vertical position of the whole document.</summary>
        public double LineAt(double documentY)
        {
            if (!Ready) return 0;
            var view = View;
            var doc = Editor.Document;
            if (doc.LineCount == 0) return 0;
            var line = view.GetDocumentLineByVisualTop(Math.Max(0, documentY));
            if (line == null) return 0;
            var top = view.GetVisualTopByDocumentLine(line.LineNumber);
            var next = line.LineNumber < doc.LineCount ? view.GetVisualTopByDocumentLine(line.LineNumber + 1) : top + view.DefaultLineHeight;
            var fraction = next > top ? Math.Max(0, Math.Min(1, (documentY - top) / (next - top))) : 0;
            return line.LineNumber - 1 + fraction;
        }

        /// <summary>The vertical position (of the whole document) where a line, with a fraction, starts.</summary>
        public double OffsetOf(double line)
        {
            if (!Ready) return 0;
            var view = View;
            var doc = Editor.Document;
            var whole = (int)Math.Floor(Math.Max(0, line));
            var number = Math.Min(whole + 1, doc.LineCount);
            var top = view.GetVisualTopByDocumentLine(Math.Max(1, number));
            var height = number < doc.LineCount ? view.GetVisualTopByDocumentLine(number + 1) - top : view.DefaultLineHeight;
            if (whole + 1 > doc.LineCount) return top + height * (whole + 1 - doc.LineCount) + height * (line - whole);
            return top + height * (line - whole);
        }

        /// <summary>The line at the top of the visible area, with a fraction.</summary>
        public double TopLine => LineAt(Editor.VerticalOffset);
    }

    /// <summary>Paints the comparison under the text: changed lines, the changed words in them, moved code, and where lines were added on the other side.</summary>
    internal sealed class DiffRenderer : IBackgroundRenderer
    {
        /// <summary>The panel whose text is painted.</summary>
        private readonly SidePanel _side;
        /// <summary>The comparison window (for the comparison and the change chosen).</summary>
        private readonly MainWindow _owner;

        /// <summary>Makes the painter for one side.</summary>
        public DiffRenderer(SidePanel side, MainWindow owner)
        {
            _side = side;
            _owner = owner;
        }

        /// <summary>Painted under the text, with the selection.</summary>
        public KnownLayer Layer => KnownLayer.Background;

        /// <summary>
        /// Paints the visible lines: a colour per line's look, the changed words stronger, a line where lines were
        /// added on the other side, and a frame round the change chosen.
        /// </summary>
        public void Draw(TextView textView, DrawingContext dc)
        {
            var cmp = _owner.Comparison;
            if (cmp == null || !_side.HasFile || textView.VisualLines.Count == 0) return;
            var left = _side.Side == Side.Left;
            var width = Math.Max(textView.ActualWidth, ((System.Windows.Controls.Primitives.IScrollInfo)textView).ExtentWidth) + textView.HorizontalOffset;
            var looks = _side.Looks;

            foreach (var vl in textView.VisualLines)
            {
                var index = vl.FirstDocumentLine.LineNumber - 1;
                if (index >= looks.Length) continue;
                var brush = Background(looks[index]);
                if (brush == null) continue;
                var y = vl.VisualTop - textView.VerticalOffset;
                dc.DrawRectangle(brush, null, new Rect(0, y, width, vl.Height));
            }

            // Changed words and characters.
            var first = textView.VisualLines.First().FirstDocumentLine.LineNumber - 1;
            var last = textView.VisualLines.Last().LastDocumentLine.LineNumber - 1;
            var doc = _side.Editor.Document;
            var builder = new BackgroundGeometryBuilder { AlignToWholePixels = true, CornerRadius = 2 };
            var any = false;
            foreach (var c in cmp.Chunks)
            {
                if (c.Kind != ChunkKind.Replace) continue;
                var start = left ? c.A : c.B;
                var count = left ? c.ACount : c.BCount;
                if (start > last || start + count < first) continue;
                foreach (var span in left ? c.ASpans : c.BSpans)
                {
                    if (span.Line < first || span.Line > last || span.Line >= doc.LineCount) continue;
                    if (looks.Length > span.Line && looks[span.Line] == LineLook.Moved) continue;
                    var line = doc.GetLineByNumber(span.Line + 1);
                    var s = Math.Min(span.Start, line.Length);
                    var e = Math.Min(span.Start + span.Length, line.Length);
                    if (e <= s) continue;
                    builder.AddSegment(textView, new TextSegment { StartOffset = line.Offset + s, EndOffset = line.Offset + e });
                    any = true;
                }
            }
            if (any)
            {
                var geometry = builder.CreateGeometry();
                if (geometry != null) dc.DrawGeometry(Ui.Brush(left ? "DiffInlineDelete" : "DiffInlineInsert"), null, geometry);
            }

            // Where lines were added or removed on the other side: a thin line between two lines here.
            foreach (var c in cmp.Chunks)
            {
                if (!c.IsChange || (left ? c.ACount : c.BCount) > 0) continue;
                var at = left ? c.A : c.B;
                if (at < first - 1 || at > last + 1) continue;
                var y = _side.TopOf(at);
                var pen = new Pen(Ui.Brush(left ? "DiffInsertLine" : "DiffDeleteLine"), 2);
                dc.DrawLine(pen, new Point(0, y), new Point(width, y));
            }

            // The current change: a bar down the left edge.
            var current = _owner.CurrentChunk;
            if (current != null)
            {
                var start = left ? current.A : current.B;
                var count = left ? current.ACount : current.BCount;
                var top = _side.TopOf(start);
                var bottom = count > 0 ? _side.TopOf(start + count) : top + 2;
                dc.DrawRectangle(Ui.Brush("DiffCurrent"), null, new Rect(0, top - (count > 0 ? 0 : 3), 3, Math.Max(4, bottom - top + (count > 0 ? 0 : 4))));
            }
        }

        /// <summary>The colour for a line's look, from the theme.</summary>
        private static Brush Background(LineLook look)
        {
            switch (look)
            {
                case LineLook.Removed: return Ui.Brush("DiffDeleteBg");
                case LineLook.Added: return Ui.Brush("DiffInsertBg");
                case LineLook.Changed: return Ui.Brush("DiffChangeBg");
                case LineLook.Moved: return Ui.Brush("DiffMoveBg");
                default: return null;
            }
        }
    }

    /// <summary>
    /// The strip between the two editors: a band joins each change on the left to where it is on the right, with
    /// arrows that copy the change across.
    /// </summary>
    public sealed class LinkPane : FrameworkElement
    {
        /// <summary>The comparison window.</summary>
        private readonly MainWindow _owner;
        /// <summary>Where each copy arrow was drawn, which change it copies and which way, for clicks.</summary>
        private readonly List<(Rect Area, int Change, bool ToRight)> _arrows = new List<(Rect, int, bool)>();

        /// <summary>Makes the strip between the two sides.</summary>
        public LinkPane(MainWindow owner)
        {
            _owner = owner;
            Width = 64;
            ClipToBounds = true;
            Cursor = Cursors.Arrow;
            ToolTipService.SetInitialShowDelay(this, 300);
        }

        /// <summary>
        /// Draws a band from each change on the left to where it is on the right, and the copy arrows.
        /// </summary>
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Ui.Brush("SurfaceAltBg"), null, new Rect(0, 0, ActualWidth, ActualHeight));
            _arrows.Clear();
            var cmp = _owner.Comparison;
            var l = _owner.LeftSide;
            var r = _owner.RightSide;
            if (cmp == null || !l.HasFile || !r.HasFile) return;
            var w = ActualWidth;
            var changes = cmp.Changes.ToList();
            var current = _owner.CurrentChunk;
            for (var i = 0; i < changes.Count; i++)
            {
                var c = changes[i];
                double ly1 = l.TopOf(c.A), ly2 = c.ACount > 0 ? l.TopOf(c.AEnd) : ly1;
                double ry1 = r.TopOf(c.B), ry2 = c.BCount > 0 ? r.TopOf(c.BEnd) : ry1;
                if (Math.Max(ly2, ry2) < -50 || Math.Min(ly1, ry1) > ActualHeight + 50) continue;

                string fill, line;
                switch (c.Kind)
                {
                    case ChunkKind.Insert: fill = "DiffInsertBg"; line = "DiffInsertLine"; break;
                    case ChunkKind.Delete: fill = "DiffDeleteBg"; line = "DiffDeleteLine"; break;
                    default: fill = "DiffChangeBg"; line = "DiffChangeLine"; break;
                }
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(0, ly1), true, true);
                    g.BezierTo(new Point(w / 2, ly1), new Point(w / 2, ry1), new Point(w, ry1), true, false);
                    g.LineTo(new Point(w, ry2), true, false);
                    g.BezierTo(new Point(w / 2, ry2), new Point(w / 2, ly2), new Point(0, ly2), true, false);
                }
                geometry.Freeze();
                var pen = new Pen(Ui.Brush(line), ReferenceEquals(c, current) ? 1.6 : 0.8);
                dc.DrawGeometry(Ui.Brush(fill), pen, geometry);

                // Copy arrows: on the left edge (copy to the right) and the right edge (copy to the left); never into a
                // Word, Excel, PowerPoint or PDF file, which is only shown as text.
                if (r.File?.IsDocument != true) Arrow(dc, new Point(9, (ly1 + ly2) / 2), true, i);
                if (l.File?.IsDocument != true) Arrow(dc, new Point(w - 9, (ry1 + ry2) / 2), false, i);
            }
        }

        /// <summary>Draws one copy arrow, pointing right or left.</summary>
        private void Arrow(DrawingContext dc, Point centre, bool toRight, int change)
        {
            var area = new Rect(centre.X - 8, centre.Y - 8, 16, 16);
            _arrows.Add((area, change, toRight));
            dc.DrawEllipse(Ui.Brush("SurfaceBg"), new Pen(Ui.Brush("InputBorder"), 1), centre, 7.5, 7.5);
            var d = toRight ? 1 : -1;
            var pen = new Pen(Ui.Brush("Text"), 1.4) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(pen, new Point(centre.X - 3.5 * d, centre.Y), new Point(centre.X + 3.5 * d, centre.Y));
            dc.DrawLine(pen, new Point(centre.X + 0.5 * d, centre.Y - 3), new Point(centre.X + 3.5 * d, centre.Y));
            dc.DrawLine(pen, new Point(centre.X + 0.5 * d, centre.Y + 3), new Point(centre.X + 3.5 * d, centre.Y));
        }

        /// <summary>The arrow under the mouse, if any.</summary>
        private (Rect Area, int Change, bool ToRight)? HitArrow(Point p)
        {
            foreach (var a in _arrows)
            {
                var grown = a.Area;
                grown.Inflate(2, 2);
                if (grown.Contains(p)) return a;
            }
            return null;
        }

        /// <summary>Shows a hand over an arrow.</summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            var hit = HitArrow(e.GetPosition(this));
            Cursor = hit != null ? Cursors.Hand : Cursors.Arrow;
            ToolTip = hit == null ? null : hit.Value.ToRight ? "Copy this change to the right (Alt+Right)" : "Copy this change to the left (Alt+Left)";
        }

        /// <summary>Copies the change whose arrow was clicked.</summary>
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            var hit = HitArrow(e.GetPosition(this));
            if (hit == null) return;
            e.Handled = true;
            _owner.CopyChange(hit.Value.Change, hit.Value.ToRight);
        }

        /// <summary>Scrolls the text when the mouse wheel is turned over the strip.</summary>
        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            // Scrolling over the strip scrolls the files.
            _owner.RightSide.Editor.ScrollToVerticalOffset(_owner.RightSide.Editor.VerticalOffset - e.Delta);
            e.Handled = true;
        }
    }

    /// <summary>A map of the whole comparison beside the scrollbar: every change, and the visible part. Click to jump.</summary>
    public sealed class Overview : FrameworkElement
    {
        /// <summary>The comparison window.</summary>
        private readonly MainWindow _owner;

        /// <summary>Makes the map beside the scrollbar.</summary>
        public Overview(MainWindow owner)
        {
            _owner = owner;
            Width = 16;
            Cursor = Cursors.Hand;
            ToolTip = "All the changes. Click to go there.";
        }

        /// <summary>Draws a mark for every change at its place in the file, and a box for the part on screen.</summary>
        protected override void OnRender(DrawingContext dc)
        {
            dc.DrawRectangle(Ui.Brush("SurfaceAltBg"), new Pen(Ui.Brush("BorderBrush"), 1), new Rect(0, 0, ActualWidth, ActualHeight));
            var cmp = _owner.Comparison;
            if (cmp == null) return;
            var total = Math.Max(1, Math.Max(cmp.Left.Count, cmp.Right.Count));
            var h = ActualHeight - 4;
            var half = (ActualWidth - 4) / 2;
            foreach (var c in cmp.Changes)
            {
                var brush = Ui.Brush(c.Kind == ChunkKind.Insert ? "DiffInsertLine" : c.Kind == ChunkKind.Delete ? "DiffDeleteLine" : "DiffChangeLine");
                double y1 = 2 + c.A * h / total, y2 = 2 + c.AEnd * h / total;
                dc.DrawRectangle(brush, null, new Rect(2, y1, half, Math.Max(2, y2 - y1)));
                y1 = 2 + c.B * h / total;
                y2 = 2 + c.BEnd * h / total;
                dc.DrawRectangle(brush, null, new Rect(2 + half, y1, half, Math.Max(2, y2 - y1)));
            }
            var editor = _owner.RightSide.Editor;
            var extent = editor.ExtentHeight;
            if (extent > 0)
            {
                var top = editor.VerticalOffset / extent * ActualHeight;
                var size = Math.Max(6, editor.ViewportHeight / extent * ActualHeight);
                var accent = Ui.Brush("Accent");
                dc.DrawRectangle(null, new Pen(accent, 1.5), new Rect(1, top, ActualWidth - 2, Math.Min(size, ActualHeight - top)));
            }
        }

        /// <summary>Starts following the mouse and jumps to where it was clicked.</summary>
        protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            CaptureMouse();
            Jump(e.GetPosition(this).Y);
        }

        /// <summary>Jumps while the mouse is dragged.</summary>
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsMouseCaptured) Jump(e.GetPosition(this).Y);
        }

        /// <summary>Stops following the mouse.</summary>
        protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e) => ReleaseMouseCapture();

        /// <summary>Scrolls so the place at this height of the map is on screen.</summary>
        private void Jump(double y)
        {
            var editor = _owner.RightSide.HasFile ? _owner.RightSide.Editor : _owner.LeftSide.Editor;
            var target = y / Math.Max(1, ActualHeight) * editor.ExtentHeight - editor.ViewportHeight / 2;
            editor.ScrollToVerticalOffset(Math.Max(0, target));
        }
    }
}
