using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Win32;

namespace Kududiff.Core
{
    /// <summary>How the app looks: like Windows, always light, or always dark.</summary>
    public enum ThemeMode { System, Light, Dark }

    /// <summary>Settings are stored under HKEY_CURRENT_USER, so no administrator rights are needed.</summary>
    public static class Settings
    {
        /// <summary>Where the settings are kept in the registry, under HKEY_CURRENT_USER.</summary>
        private const string KeyPath = @"Software\KUDUDIFF";

        /// <summary>A setting as text, or fallback when it isn't there or can't be read.</summary>
        private static string Get(string name, string fallback)
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                {
                    return key?.GetValue(name) as string ?? fallback;
                }
            }
            catch
            {
                return fallback;
            }
        }

        /// <summary>Keeps a setting (quietly does nothing when the registry can't be written).</summary>
        private static void Set(string name, string value)
        {
            try
            {
                using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
                {
                    key?.SetValue(name, value ?? string.Empty);
                }
            }
            catch
            {
                // Settings are best-effort.
            }
        }

        /// <summary>A yes/no setting (kept as "1" or "0").</summary>
        private static bool GetBool(string name, bool fallback) => Get(name, fallback ? "1" : "0") == "1";
        /// <summary>Keeps a yes/no setting.</summary>
        private static void SetBool(string name, bool value) => Set(name, value ? "1" : "0");

        /// <summary>A whole-number setting.</summary>
        private static int GetInt(string name, int fallback) => int.TryParse(Get(name, fallback.ToString()), out var v) ? v : fallback;

        /// <summary>A setting that is one of a list of choices (an enum).</summary>
        private static T GetEnum<T>(string name, T fallback) where T : struct =>
            Enum.TryParse(Get(name, fallback.ToString()), out T v) ? v : fallback;

        public static ThemeMode Theme
        {
            get => GetEnum("Theme", ThemeMode.System);
            set => Set("Theme", value.ToString());
        }

        public static bool CheckForUpdates
        {
            get => GetBool("CheckForUpdates", true);
            set => SetBool("CheckForUpdates", value);
        }

        public static string SkippedVersion
        {
            get => Get("SkippedVersion", string.Empty);
            set => Set("SkippedVersion", value);
        }

        /// <summary>"Compare with KUDUDIFF" on files' right-click menu. Turned on by the installer when chosen.</summary>
        public static bool ShellMenu
        {
            get => GetBool("ShellMenu", false);
            set => SetBool("ShellMenu", value);
        }

        /// <summary>Keep the previous version as "name.bak" when saving.</summary>
        public static bool KeepBackup
        {
            get => GetBool("KeepBackup", true);
            set => SetBool("KeepBackup", value);
        }

        public static int FontSize
        {
            get => Math.Max(9, Math.Min(24, GetInt("FontSize", 13)));
            set => Set("FontSize", value.ToString());
        }

        public static bool WordWrap
        {
            get => GetBool("WordWrap", false);
            set => SetBool("WordWrap", value);
        }

        /// <summary>Show spaces, tabs and line ends as marks.</summary>
        public static bool ShowInvisibles
        {
            get => GetBool("ShowInvisibles", false);
            set => SetBool("ShowInvisibles", value);
        }

        public static bool SyntaxColours
        {
            get => GetBool("SyntaxColours", true);
            set => SetBool("SyntaxColours", value);
        }

        /// <summary>Excel and spreadsheet files: compare each cell's formula too, not only what it shows.</summary>
        public static bool CompareFormulas
        {
            get => GetBool("CompareFormulas", false);
            set => SetBool("CompareFormulas", value);
        }

        public static string LastFolder
        {
            get => Get("LastFolder", string.Empty);
            set => Set("LastFolder", value);
        }

        /// <summary>The comparison options last used, so the next comparison starts the same way.</summary>
        public static CompareOptions Options
        {
            get => new CompareOptions
            {
                Algorithm = GetEnum("Algorithm", DiffAlgorithm.Standard),
                Whitespace = GetEnum("Whitespace", WhitespaceMode.Compare),
                IgnoreCase = GetBool("IgnoreCase", false),
                IgnoreBlankLines = GetBool("IgnoreBlankLines", false),
                CompareLineEndings = GetBool("CompareLineEndings", false),
                IgnorePatterns = Get("IgnorePatterns", string.Empty).Split('\n').Where(p => p.Length > 0).ToList(),
                Inline = GetEnum("Inline", InlineMode.Words),
                DetectMoves = GetBool("DetectMoves", true)
            };
            set
            {
                Set("Algorithm", value.Algorithm.ToString());
                Set("Whitespace", value.Whitespace.ToString());
                SetBool("IgnoreCase", value.IgnoreCase);
                SetBool("IgnoreBlankLines", value.IgnoreBlankLines);
                SetBool("CompareLineEndings", value.CompareLineEndings);
                Set("IgnorePatterns", string.Join("\n", value.IgnorePatterns.Where(p => !string.IsNullOrWhiteSpace(p))));
                Set("Inline", value.Inline.ToString());
                SetBool("DetectMoves", value.DetectMoves);
            }
        }

        /// <summary>Names of files and folders a folder comparison leaves out (the usual list until it's changed).</summary>
        public static List<string> FolderLeaveOut
        {
            get
            {
                var saved = Get("FolderLeaveOut", null);
                return saved == null ? FolderOptions.UsualLeaveOut.ToList() : FolderOptions.Split(saved);
            }
            set => Set("FolderLeaveOut", FolderOptions.Join(value ?? new List<string>()));
        }

        /// <summary>A folder comparison compares the left-out names too.</summary>
        public static bool FolderShowLeftOut
        {
            get => GetBool("FolderShowLeftOut", false);
            set => SetBool("FolderShowLeftOut", value);
        }

        /// <summary>A folder comparison hides the files and folders that are the same on both sides.</summary>
        public static bool FolderHideSame
        {
            get => GetBool("FolderHideSame", false);
            set => SetBool("FolderHideSame", value);
        }

        /// <summary>Recently compared pairs of files (or folders), newest first.</summary>
        public static List<(string Left, string Right)> Recent
        {
            get => Get("Recent", string.Empty).Split('\n')
                .Select(l => l.Split('|'))
                .Where(p => p.Length == 2 && p[0].Length > 0 && p[1].Length > 0)
                .Select(p => (p[0], p[1])).ToList();
        }

        /// <summary>Puts a pair of files at the top of the recent list (12 kept, no repeats).</summary>
        public static void AddRecent(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right)) return;
            var list = Recent.Where(r => !(string.Equals(r.Left, left, StringComparison.OrdinalIgnoreCase) && string.Equals(r.Right, right, StringComparison.OrdinalIgnoreCase))).ToList();
            list.Insert(0, (left, right));
            Set("Recent", string.Join("\n", list.Take(12).Select(r => r.Left + "|" + r.Right)));
        }

        /// <summary>Empties the recent list.</summary>
        public static void ClearRecent() => Set("Recent", string.Empty);
    }
}
