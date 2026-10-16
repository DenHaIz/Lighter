using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lighter
{
    static class StartupFolderSource
    {
        public static List<AutorunItem> Load()
        {
            var list = new List<AutorunItem>();
            object shell = null;
            try { shell = Com.Create("WScript.Shell"); }
            catch { }
            try
            {
                AddFolder(list, shell, Environment.GetFolderPath(Environment.SpecialFolder.Startup),
                    RegistryHive.CurrentUser, false);
                AddFolder(list, shell, Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                    RegistryHive.LocalMachine, true);
            }
            finally
            {
                Com.Release(shell);
            }
            return list;
        }

        static void AddFolder(List<AutorunItem> list, object shell, string dir, RegistryHive hive, bool common)
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
                    Hive = hive,
                    ApprovedSub = "StartupFolder",
                    ApprovedName = fileName,
                    CommonFolder = common,
                    CanToggle = true,
                    CanDelete = true,
                };
                it.FileExists = PathUtil.SafeExists(target) || Directory.Exists(target);
                it.Publisher = PathUtil.GetPublisher(target);
                it.Enabled = StartupApproved.IsEnabled(hive, it.ApprovedSub, fileName);
                list.Add(it);
            }
        }

        /// <summary>Moves the file into the backup folder instead of deleting it.</summary>
        public static string Delete(AutorunItem it)
        {
            string backup = Backup.MoveFile(it.FilePath);
            StartupApproved.Remove(it.Hive, it.ApprovedSub, it.ApprovedName);
            return backup;
        }
    }
}
