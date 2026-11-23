using System;
using Microsoft.Win32;

namespace Kududiff.Core
{
    /// <summary>
    /// "Compare with KUDUDIFF" on the right-click menu of files and folders in File Explorer. For the current user only
    /// (HKEY_CURRENT_USER), so no administrator rights are needed. On Windows 11 it's under "Show more options".
    /// Windows starts KUDUDIFF once for each selected file; the copies hand their file to the first one (see Hub), so
    /// two selected files open in one comparison, and a single file waits on the left for the second.
    /// </summary>
    public static class ShellIntegration
    {
        /// <summary>Where the right-click menu entry for files is kept.</summary>
        private const string KeyPath = @"Software\Classes\*\shell\KUDUDIFF";
        /// <summary>Where the right-click menu entry for folders is kept.</summary>
        private const string FolderKeyPath = @"Software\Classes\Directory\shell\KUDUDIFF";

        /// <summary>
        /// Adds "Compare with KUDUDIFF" to the right-click menu of files and folders, for this user only.
        /// </summary>
        public static void Register()
        {
            foreach (var path in new[] { KeyPath, FolderKeyPath })
            {
                try
                {
                    using (var key = Registry.CurrentUser.CreateSubKey(path))
                    {
                        key.SetValue(string.Empty, "Compare with KUDUDIFF");
                        key.SetValue("Icon", "\"" + AppInfo.ExePath + "\",0");
                        key.SetValue("MultiSelectModel", "Player");
                        using (var command = key.CreateSubKey("command"))
                            command.SetValue(string.Empty, $"\"{AppInfo.ExePath}\" --pick \"%1\"");
                    }
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
            }
        }

        /// <summary>Takes the right-click menu entries away again.</summary>
        public static void Remove()
        {
            foreach (var path in new[] { KeyPath, FolderKeyPath })
            {
                try { Registry.CurrentUser.DeleteSubKeyTree(path, false); }
                catch (Exception ex) when (ex is UnauthorizedAccessException || ex is System.Security.SecurityException) { }
            }
        }

        public static bool FolderMenuRegistered
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey(FolderKeyPath + @"\command")) return key != null; }
                catch { return false; }
            }
        }

        public static bool IsRegistered
        {
            get
            {
                try { using (var key = Registry.CurrentUser.OpenSubKey(KeyPath + @"\command")) return key != null; }
                catch { return false; }
            }
        }

        /// <summary>The command the menu entry runs, or null when there's none.</summary>
        public static string Command()
        {
            try { using (var key = Registry.CurrentUser.OpenSubKey(KeyPath + @"\command")) return key?.GetValue(string.Empty) as string; }
            catch { return null; }
        }

        /// <summary>Keeps the menu in step with the setting, and pointing at this copy of KUDUDIFF.</summary>
        public static void Refresh()
        {
            if (Settings.ShellMenu) Register(); // also adds the folders' menu for those who had only the files' one
            else if (IsRegistered) Remove();
        }
    }
}
