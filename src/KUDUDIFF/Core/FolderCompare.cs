// Compares two folders: every file and folder matched by name on both sides, files read byte by byte when their sizes
// are the same; and FolderCopy, which copies the differences from one side to the other.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Kududiff.Core
{
    /// <summary>What a file or folder is like on the two sides.</summary>
    public enum FolderState
    {
        /// <summary>On both sides with the same contents (a folder: everything inside is the same).</summary>
        Same,
        /// <summary>On both sides, but the contents differ (a folder: something inside differs).</summary>
        Different,
        OnlyLeft,
        OnlyRight,
        /// <summary>On both sides, but one of them couldn't be read, so it isn't known whether they're the same.</summary>
        Unreadable
    }

    /// <summary>One file or folder in a folder comparison, with what it is like on each side.</summary>
    public sealed class FolderItem
    {
        /// <summary>The file or folder's name.</summary>
        public string Name { get; set; }
        /// <summary>The path below the two folders compared, such as "src\App.cs" (empty for the folders themselves).</summary>
        public string RelativePath { get; set; }
        /// <summary>True for a folder.</summary>
        public bool IsFolder { get; set; }
        /// <summary>Same, different, only on one side, or couldn't be read.</summary>
        public FolderState State { get; set; }
        /// <summary>It's in the left folder.</summary>
        public bool OnLeft { get; set; }
        /// <summary>It's in the right folder.</summary>
        public bool OnRight { get; set; }
        /// <summary>Its size on the left, in bytes.</summary>
        public long LeftSize { get; set; }
        /// <summary>Its size on the right, in bytes.</summary>
        public long RightSize { get; set; }
        /// <summary>When it was last changed on the left.</summary>
        public DateTime LeftTime { get; set; }
        /// <summary>When it was last changed on the right.</summary>
        public DateTime RightTime { get; set; }
        /// <summary>Why a side couldn't be read, when it couldn't.</summary>
        public string Problem { get; set; }
        /// <summary>The folder it's in (null for the two folders compared).</summary>
        public FolderItem Parent { get; set; }
        /// <summary>The files and folders inside it.</summary>
        public List<FolderItem> Children { get; } = new List<FolderItem>();
        /// <summary>How deep it is below the folders compared (0 for what's directly inside them).</summary>
        public int Depth => Parent == null ? -1 : Parent.Depth + 1;

        /// <summary>It's on both sides.</summary>
        public bool OnBoth => OnLeft && OnRight;
        /// <summary>It isn't the same on both sides.</summary>
        public bool IsChange => State != FolderState.Same;

        /// <summary>Everything below it, folder by folder.</summary>
        public IEnumerable<FolderItem> Below()
        {
            foreach (var c in Children)
            {
                yield return c;
                foreach (var d in c.Below()) yield return d;
            }
        }
    }

    /// <summary>What to leave out of a folder comparison.</summary>
    public sealed class FolderOptions
    {
        /// <summary>Usually left out: version control, packages, build output and the files Windows makes by itself.</summary>
        public static readonly IReadOnlyList<string> UsualLeaveOut = new[] { ".git", ".svn", "node_modules", "bin", "obj", "Thumbs.db", "desktop.ini" };

        /// <summary>Names of files and folders to leave out; * and ? stand for any letters and any one letter.</summary>
        public List<string> LeaveOut { get; set; } = UsualLeaveOut.ToList();
        /// <summary>Compare everything, the left-out names too.</summary>
        public bool ShowLeftOut { get; set; }

        /// <summary>A copy of these options with its own list of names.</summary>
        public FolderOptions Clone() => new FolderOptions { LeaveOut = LeaveOut.ToList(), ShowLeftOut = ShowLeftOut };

        /// <summary>Whether a file or folder with this name is left out.</summary>
        public bool LeavesOut(string name)
        {
            if (ShowLeftOut) return false;
            foreach (var raw in LeaveOut)
            {
                var pattern = (raw ?? string.Empty).Trim();
                if (pattern.Length == 0) continue;
                if (pattern.IndexOfAny(new[] { '*', '?' }) < 0)
                {
                    if (string.Equals(name, pattern, StringComparison.OrdinalIgnoreCase)) return true;
                }
                else if (Regex.IsMatch(name, "^" + Regex.Escape(pattern).Replace(@"\*", ".*").Replace(@"\?", ".") + "$", RegexOptions.IgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>The list as it's shown and typed in Settings: names separated by commas.</summary>
        public static string Join(IEnumerable<string> names) => string.Join(", ", names.Where(n => !string.IsNullOrWhiteSpace(n)));

        /// <summary>Names typed in a box (separated by commas, semicolons or lines) as a list, each once.</summary>
        public static List<string> Split(string text) =>
            (text ?? string.Empty).Split(new[] { ',', ';', '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(n => n.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Two folders compared: every file and folder below them, matched by name (upper and lower case don't matter, as in
    /// Windows), and each marked the same, different or on one side only. Files of the same name are compared by their
    /// contents: different sizes are different straight away, files of the same size are read and compared byte by byte.
    /// Links to other folders aren't followed (they could lead back into the folder and never end); files are only read.
    /// </summary>
    public sealed class FolderComparison
    {
        /// <summary>How much of each file is read at a time when comparing them byte by byte (64 KB).</summary>
        private const int Buffer = 1 << 16;

        /// <summary>The left folder.</summary>
        public string Left { get; private set; }
        /// <summary>The right folder.</summary>
        public string Right { get; private set; }
        /// <summary>The options it was compared with.</summary>
        public FolderOptions Options { get; private set; }
        /// <summary>The top of the tree: the two folders themselves.</summary>
        public FolderItem Root { get; private set; }
        /// <summary>How many files are the same on both sides.</summary>
        public int Same { get; private set; }
        /// <summary>How many files differ.</summary>
        public int Different { get; private set; }
        /// <summary>How many files are only on the left.</summary>
        public int OnlyLeft { get; private set; }
        /// <summary>How many files are only on the right.</summary>
        public int OnlyRight { get; private set; }
        /// <summary>How many files couldn't be read on one side or both.</summary>
        public int Unreadable { get; private set; }
        /// <summary>Files and folders left out by name, and links that weren't followed.</summary>
        public int LeftOut { get; private set; }
        /// <summary>How many links to other folders were found (and not followed).</summary>
        public int Links { get; private set; }

        /// <summary>The files compared (folders aren't counted).</summary>
        public int Files => Same + Different + OnlyLeft + OnlyRight + Unreadable;
        /// <summary>True when every file is the same on both sides.</summary>
        public bool Identical => Different + OnlyLeft + OnlyRight + Unreadable == 0;

        /// <summary>
        /// Compares two folders. <paramref name="progress"/> is told the path being looked at now and then.
        /// Throws <see cref="ArgumentException"/> when they can't be compared (not a folder, the same folder, one inside the other).
        /// </summary>
        public static FolderComparison Run(string left, string right, FolderOptions options, CancellationToken cancel = default, Action<string> progress = null)
        {
            left = Full(left);
            right = Full(right);
            if (!Directory.Exists(left)) throw new ArgumentException(Lang.T("The folder \"{0}\" isn't there.", left));
            if (!Directory.Exists(right)) throw new ArgumentException(Lang.T("The folder \"{0}\" isn't there.", right));
            if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException(Lang.T("Both sides are the same folder."));
            if (Inside(left, right) || Inside(right, left)) throw new ArgumentException(Lang.T("One folder is inside the other, so they can't be compared."));

            var cmp = new FolderComparison { Left = left, Right = right, Options = (options ?? new FolderOptions()).Clone() };
            cmp.Root = new FolderItem
            {
                Name = string.Empty, RelativePath = string.Empty, IsFolder = true, OnLeft = true, OnRight = true,
                LeftTime = Directory.GetLastWriteTime(left), RightTime = Directory.GetLastWriteTime(right)
            };
            var lastNote = DateTime.MinValue;
            cmp.Fill(cmp.Root, cancel, path =>
            {
                if (progress == null || (DateTime.UtcNow - lastNote).TotalMilliseconds < 100) return;
                lastNote = DateTime.UtcNow;
                progress(path);
            });
            cmp.Settle(cmp.Root);
            return cmp;
        }

        /// <summary>A folder's full path without a separator at the end (except for a drive itself, such as "D:\").</summary>
        public static string Full(string path)
        {
            var full = Path.GetFullPath(path);
            var root = Path.GetPathRoot(full) ?? string.Empty;
            return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
        }

        /// <summary>Whether <paramref name="inner"/> is somewhere below <paramref name="outer"/> (both full paths).</summary>
        public static bool Inside(string inner, string outer)
        {
            var o = outer.EndsWith(Path.DirectorySeparatorChar.ToString()) ? outer : outer + Path.DirectorySeparatorChar;
            return inner.StartsWith(o, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>The full path of an item on the left.</summary>
        public string LeftPath(FolderItem item) => item.RelativePath.Length == 0 ? Left : Path.Combine(Left, item.RelativePath);
        /// <summary>The full path of an item on the right.</summary>
        public string RightPath(FolderItem item) => item.RelativePath.Length == 0 ? Right : Path.Combine(Right, item.RelativePath);

        private sealed class Entry
        {
            /// <summary>Its name.</summary>
            public string Name;
            /// <summary>True for a folder.</summary>
            public bool IsFolder;
            /// <summary>True for a link to somewhere else (not followed).</summary>
            public bool IsLink;
            /// <summary>Its size in bytes.</summary>
            public long Size;
            /// <summary>When it was last changed.</summary>
            public DateTime Time;
        }

        /// <summary>The files and folders directly inside one side's folder, or null with the reason when it can't be listed.</summary>
        private List<Entry> List(string folder, out string problem)
        {
            problem = null;
            var list = new List<Entry>();
            try
            {
                foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
                {
                    if (Options.LeavesOut(info.Name))
                    {
                        LeftOut++;
                        continue;
                    }
                    var folderEntry = (info.Attributes & FileAttributes.Directory) != 0;
                    var link = folderEntry && (info.Attributes & FileAttributes.ReparsePoint) != 0;
                    list.Add(new Entry
                    {
                        Name = info.Name,
                        IsFolder = folderEntry,
                        IsLink = link,
                        Size = !folderEntry && info is FileInfo f ? f.Length : 0,
                        Time = info.LastWriteTime
                    });
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                problem = ex.Message;
                return null;
            }
            return list;
        }

        /// <summary>
        /// Lists one folder on both sides, matches what's inside by name (upper and lower case the same) and goes on
        /// into the folders below.
        /// </summary>
        private void Fill(FolderItem folder, CancellationToken cancel, Action<string> progress)
        {
            cancel.ThrowIfCancellationRequested();
            progress(folder.RelativePath);
            string leftProblem = null, rightProblem = null;
            var left = folder.OnLeft ? List(LeftPath(folder), out leftProblem) : new List<Entry>();
            var right = folder.OnRight ? List(RightPath(folder), out rightProblem) : new List<Entry>();
            if (left == null || right == null)
            {
                // A folder that can't be opened on one side: what's inside it isn't known.
                folder.Problem = leftProblem ?? rightProblem;
                folder.State = FolderState.Unreadable;
                left = left ?? new List<Entry>();
                right = right ?? new List<Entry>();
                if (folder.OnBoth) return;
            }

            // Matched by name and kind; a file on one side and a folder of the same name on the other are two items.
            var names = left.Select(e => (e.Name, e.IsFolder)).Concat(right.Select(e => (e.Name, e.IsFolder)))
                .GroupBy(k => (k.Name.ToUpperInvariant(), k.IsFolder)).Select(g => g.First())
                .OrderBy(k => k.IsFolder ? 0 : 1).ThenBy(k => k.Name, StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var (name, isFolder) in names)
            {
                var l = left.FirstOrDefault(e => e.IsFolder == isFolder && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                var r = right.FirstOrDefault(e => e.IsFolder == isFolder && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
                if ((l?.IsLink ?? false) || (r?.IsLink ?? false))
                {
                    Links++;
                    LeftOut++;
                    continue;
                }
                var item = new FolderItem
                {
                    Name = r != null && l == null ? r.Name : l.Name,
                    RelativePath = folder.RelativePath.Length == 0 ? (l ?? r).Name : Path.Combine(folder.RelativePath, (l ?? r).Name),
                    IsFolder = isFolder,
                    Parent = folder,
                    OnLeft = l != null,
                    OnRight = r != null,
                    LeftSize = l?.Size ?? 0,
                    RightSize = r?.Size ?? 0,
                    LeftTime = l?.Time ?? default,
                    RightTime = r?.Time ?? default
                };
                folder.Children.Add(item);
                if (isFolder) Fill(item, cancel, progress);
                else CompareFile(item, cancel);
            }
        }

        /// <summary>
        /// Decides whether a file is the same on both sides: different sizes differ, the same size is read to the end.
        /// </summary>
        private void CompareFile(FolderItem item, CancellationToken cancel)
        {
            if (!item.OnBoth)
            {
                item.State = item.OnLeft ? FolderState.OnlyLeft : FolderState.OnlyRight;
                return;
            }
            if (item.LeftSize != item.RightSize)
            {
                item.State = FolderState.Different;
                return;
            }
            try
            {
                item.State = SameBytes(LeftPath(item), RightPath(item), cancel) ? FolderState.Same : FolderState.Different;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
            {
                item.State = FolderState.Unreadable;
                item.Problem = ex.Message;
            }
        }

        /// <summary>Whether two files have exactly the same bytes (read side by side, stopping at the first difference).</summary>
        public static bool SameBytes(string a, string b, CancellationToken cancel = default)
        {
            using (var fa = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Buffer, FileOptions.SequentialScan))
            using (var fb = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, Buffer, FileOptions.SequentialScan))
            {
                if (fa.Length != fb.Length) return false;
                var ba = new byte[Buffer];
                var bb = new byte[Buffer];
                while (true)
                {
                    cancel.ThrowIfCancellationRequested();
                    var na = Fill(fa, ba);
                    var nb = Fill(fb, bb);
                    if (na != nb) return false;
                    if (na == 0) return true;
                    for (var i = 0; i < na; i++)
                        if (ba[i] != bb[i]) return false;
                }
            }
        }

        /// <summary>Reads into the buffer until it's full or the file ends; returns how much was read.</summary>
        private static int Fill(Stream s, byte[] buffer)
        {
            var total = 0;
            while (total < buffer.Length)
            {
                var n = s.Read(buffer, total, buffer.Length - total);
                if (n == 0) break;
                total += n;
            }
            return total;
        }

        /// <summary>Works out each folder's state from what's inside it, and counts the files.</summary>
        private void Settle(FolderItem folder)
        {
            foreach (var c in folder.Children)
            {
                if (c.IsFolder) Settle(c);
                else
                {
                    switch (c.State)
                    {
                        case FolderState.Same: Same++; break;
                        case FolderState.Different: Different++; break;
                        case FolderState.OnlyLeft: OnlyLeft++; break;
                        case FolderState.OnlyRight: OnlyRight++; break;
                        default: Unreadable++; break;
                    }
                }
            }
            if (folder.State == FolderState.Unreadable && folder.OnBoth) { Unreadable++; return; }
            if (!folder.OnBoth) folder.State = folder.OnLeft ? FolderState.OnlyLeft : FolderState.OnlyRight;
            else if (folder.Children.Any(c => c.State == FolderState.Unreadable) && folder.Children.All(c => c.State == FolderState.Same || c.State == FolderState.Unreadable))
                folder.State = FolderState.Unreadable;
            else folder.State = folder.Children.All(c => c.State == FolderState.Same) ? FolderState.Same : FolderState.Different;
        }

        /// <summary>Every file and folder in a state.</summary>
        public IEnumerable<FolderItem> Rows(FolderState state) => Root.Below().Where(i => i.State == state);

        /// <summary>One line saying how the folders compare.</summary>
        public string Summary()
        {
            if (Files == 0 && Root.Children.Count == 0) return Lang.T("Both folders are empty");
            if (Identical) return Lang.T("The folders are the same: {0}", Format.Count(Same, "file"));
            var parts = new List<string>();
            if (Different > 0) parts.Add(Lang.T("{0} different", Different));
            if (OnlyLeft > 0) parts.Add(Lang.T("{0} only on the left", OnlyLeft));
            if (OnlyRight > 0) parts.Add(Lang.T("{0} only on the right", OnlyRight));
            if (Unreadable > 0) parts.Add(Lang.T("{0} couldn't be read", Unreadable));
            if (Same > 0) parts.Add(Lang.T("{0} the same", Same));
            return Lang.T("Files: {0}", string.Join(", ", parts));
        }

        /// <summary>The words shown for a state ("Same", "Different"...), in the language in use.</summary>
        public static string StateText(FolderState state)
        {
            switch (state)
            {
                case FolderState.Same: return Lang.T("Same");
                case FolderState.Different: return Lang.T("Different");
                case FolderState.OnlyLeft: return Lang.T("Only on the left");
                case FolderState.OnlyRight: return Lang.T("Only on the right");
                default: return Lang.T("Couldn't be read");
            }
        }
    }

    /// <summary>A file to be copied from one side to the other.</summary>
    public sealed class CopyStep
    {
        /// <summary>The file copied from (null when only a folder is made).</summary>
        public string From { get; set; }
        /// <summary>Where it's copied to.</summary>
        public string To { get; set; }
        /// <summary>A folder to make (empty on the side copied from); From is null.</summary>
        public bool MakeFolder { get; set; }
        /// <summary>A file with this name is already there and will be replaced.</summary>
        public bool Replaces { get; set; }
    }

    /// <summary>
    /// Copying files and folders from one side of a folder comparison to the other. Nothing is ever deleted: what is only
    /// on the side copied to stays. A file that is replaced is first kept as "name.bak" (when asked), and the new copy is
    /// written under a temporary name and only then put in place, so a failed copy never leaves half a file.
    /// </summary>
    public static class FolderCopy
    {
        /// <summary>What copying <paramref name="item"/> across would do: the files that differ or are missing on the other side.</summary>
        public static List<CopyStep> Plan(FolderComparison cmp, FolderItem item, bool toRight)
        {
            var steps = new List<CopyStep>();
            void Add(FolderItem i)
            {
                var onSource = toRight ? i.OnLeft : i.OnRight;
                if (!onSource) return;
                var from = toRight ? cmp.LeftPath(i) : cmp.RightPath(i);
                var to = toRight ? cmp.RightPath(i) : cmp.LeftPath(i);
                var onTarget = toRight ? i.OnRight : i.OnLeft;
                if (i.IsFolder)
                {
                    if (!onTarget) steps.Add(new CopyStep { To = to, MakeFolder = true });
                    foreach (var c in i.Children) Add(c);
                    return;
                }
                if (i.State == FolderState.Same) return;
                steps.Add(new CopyStep { From = from, To = to, Replaces = onTarget });
            }
            Add(item);
            // Only the folders that end up with nothing in them need making by hand; the others are made as files go in.
            return steps.Where(s => !s.MakeFolder || !steps.Any(o => !o.MakeFolder && o.To.StartsWith(s.To + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))).ToList();
        }

        /// <summary>Carries out the steps. Returns the problems, one line each (empty when everything was copied).</summary>
        public static List<string> Run(IEnumerable<CopyStep> steps, bool keepBackup, CancellationToken cancel = default)
        {
            var problems = new List<string>();
            foreach (var step in steps)
            {
                cancel.ThrowIfCancellationRequested();
                try
                {
                    if (step.MakeFolder)
                    {
                        Directory.CreateDirectory(step.To);
                        continue;
                    }
                    Directory.CreateDirectory(Path.GetDirectoryName(step.To));
                    var tag = ".kududiff-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                    var temp = step.To + tag + ".tmp";
                    var old = step.To + tag + ".old";
                    try
                    {
                        File.Copy(step.From, temp, true);
                        if (File.Exists(step.To))
                        {
                            if (keepBackup) File.Copy(step.To, step.To + ".bak", true);
                            var attributes = File.GetAttributes(step.To);
                            if ((attributes & FileAttributes.ReadOnly) != 0) File.SetAttributes(step.To, attributes & ~FileAttributes.ReadOnly);
                            // The old file is moved aside, not deleted, until the new one is in its place.
                            File.Move(step.To, old);
                            try { File.Move(temp, step.To); }
                            catch
                            {
                                File.Move(old, step.To);
                                throw;
                            }
                            File.Delete(old);
                        }
                        else File.Move(temp, step.To);
                    }
                    finally
                    {
                        if (File.Exists(temp)) File.Delete(temp);
                    }
                }
                catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is System.Security.SecurityException)
                {
                    problems.Add((step.From ?? step.To) + ": " + ex.Message);
                }
            }
            return problems;
        }
    }
}
