using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Win32;

namespace Lighter
{
    static class StartupFolderSource
    {
        const string ShellFolders = @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders";

        /// <summary>The all-users folder plus the Startup folder of every user profile on the PC.</summary>
        public static List<AutorunItem> Load()
        {
            var list = new List<AutorunItem>();
            object shell = null;
            try { shell = Com.Create("WScript.Shell"); }
            catch { }
            try
            {
                HashSet<string> off;
                using (var root = HiveRoot.Open(null, false))
                    off = StartupApproved.ReadDisabled(root.Key, "StartupFolder");
                AddFolder(list, shell, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), null, off);

                foreach (var user in UserProfile.All())
                {
                    try
                    {
                        string dir;
                        // Read what is needed and release the hive before touching files.
                        using (var root = HiveRoot.Open(user, false))
                        {
                            off = StartupApproved.ReadDisabled(root.Key, "StartupFolder");
                            dir = user.IsMe ? Environment.GetFolderPath(Environment.SpecialFolder.Startup) : StartupDir(root.Key, user);
                        }
                        AddFolder(list, shell, dir, user, off);
                    }
                    catch { }
                }
            }
            finally
            {
                Com.Release(shell);
            }
            return list;
        }

        static string StartupDir(RegistryKey root, UserProfile user)
        {
            string path = null;
            using (var k = root.OpenSubKey(ShellFolders))
                if (k != null) path = k.GetValue("Startup", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            if (string.IsNullOrEmpty(path)) path = @"%USERPROFILE%\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Startup";
            return user.ExpandPath(path);
        }

        static void AddFolder(List<AutorunItem> list, object shell, string dir, UserProfile user, HashSet<string> off)
        {
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return;
            foreach (string file in Directory.GetFiles(dir))
            {
                string fileName = Path.GetFileName(file);
                if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)) continue;

                string target = file, args = "";
                if (shell != null && file.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        object lnk = Com.Call(shell, "CreateShortcut", file);
                        string t = Com.Get(lnk, "TargetPath") as string;
                        args = Com.Get(lnk, "Arguments") as string ?? "";
                        if (!string.IsNullOrEmpty(t)) target = t;
                        Com.Release(lnk);
                    }
                    catch { }
                }

                var it = new AutorunItem
                {
                    Kind = SourceKind.StartupFolder,
                    Name = Path.GetFileNameWithoutExtension(file),
                    FilePath = file,
                    ExePath = target,
                    Command = PathUtil.Quote(target) + (args.Length > 0 ? " " + args : ""),
                    User = user,
                    ApprovedSub = "StartupFolder",
                    ApprovedName = fileName,
                    CommonFolder = user == null,
                    CanToggle = true,
                    CanDelete = true,
                    Enabled = !off.Contains(fileName),
                };
                it.FileExists = PathUtil.SafeExists(target) || Directory.Exists(target);
                it.Publisher = PathUtil.GetPublisher(target);
                list.Add(it);
            }
        }

        /// <summary>Moves the file into the backup folder instead of deleting it.</summary>
        public static string Delete(AutorunItem it)
        {
            string backup = Backup.MoveFile(it.FilePath);
            using (var root = HiveRoot.Open(it.User, true))
                StartupApproved.Remove(root.Key, it.ApprovedSub, it.ApprovedName);
            return backup;
        }
    }
}
