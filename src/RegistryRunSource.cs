using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Lighter
{
    static class RegistryRunSource
    {
        class RunKey
        {
            public string Path;
            public string Approved;
            public string Label;
            public bool Wow32;
            public bool Once;

            public RunKey(string path, string approved, string label, bool wow32, bool once)
            {
                Path = path; Approved = approved; Label = label; Wow32 = wow32; Once = once;
            }
        }

        const string Cv = @"Software\Microsoft\Windows\CurrentVersion\";
        const string Cv32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\";

        static readonly RunKey[] MachineKeys =
        {
            new RunKey(Cv + "Run", "Run", @"HKLM\Run", false, false),
            new RunKey(Cv + "RunOnce", null, @"HKLM\RunOnce", false, true),
            new RunKey(Cv32 + "Run", "Run32", @"HKLM\Run", true, false),
            new RunKey(Cv32 + "RunOnce", null, @"HKLM\RunOnce", true, true),
        };

        static readonly RunKey[] UserKeys =
        {
            new RunKey(Cv + "Run", "Run", @"HKCU\Run", false, false),
            new RunKey(Cv + "RunOnce", null, @"HKCU\RunOnce", false, true),
        };

        /// <summary>HKLM plus the HKCU of every user profile on the PC (users that cannot be read are skipped).</summary>
        public static List<AutorunItem> Load()
        {
            var list = new List<AutorunItem>();
            using (var root = HiveRoot.Open(null, false))
                AddKeys(list, root.Key, null, MachineKeys);
            foreach (var user in UserProfile.All())
            {
                try
                {
                    using (var root = HiveRoot.Open(user, false))
                        AddKeys(list, root.Key, user, UserKeys);
                }
                catch { }
            }
            return list;
        }

        static void AddKeys(List<AutorunItem> list, RegistryKey root, UserProfile user, RunKey[] keys)
        {
            var disabled = new Dictionary<string, HashSet<string>>();
            foreach (var rk in keys)
            {
                using (var k = root.OpenSubKey(rk.Path))
                {
                    if (k == null) continue;
                    HashSet<string> off = null;
                    if (rk.Approved != null && !disabled.TryGetValue(rk.Approved, out off))
                        disabled[rk.Approved] = off = StartupApproved.ReadDisabled(root, rk.Approved);
                    foreach (string name in k.GetValueNames())
                    {
                        if (name.Length == 0) continue;
                        var it = new AutorunItem
                        {
                            Kind = SourceKind.Registry,
                            Name = name,
                            Command = Convert.ToString(k.GetValue(name)) ?? "",
                            User = user,
                            RegPath = rk.Path,
                            ApprovedSub = rk.Approved,
                            ApprovedName = name,
                            RegLabel = rk.Label,
                            Wow32 = rk.Wow32,
                            Once = rk.Once,
                            CanToggle = rk.Approved != null,
                            CanDelete = true,
                            Enabled = off == null || !off.Contains(name),
                        };
                        it.ExePath = PathUtil.ExtractExe(it.Command);
                        it.FileExists = PathUtil.SafeExists(it.ExePath);
                        it.Publisher = PathUtil.GetPublisher(it.ExePath);
                        list.Add(it);
                    }
                }
            }
        }

        public static string Delete(AutorunItem it)
        {
            using (var root = HiveRoot.Open(it.User, true))
            {
                string backup;
                using (var k = root.Key.OpenSubKey(it.RegPath, true))
                {
                    if (k == null) throw new InvalidOperationException(L.T("Раздел реестра не найден.", "Registry key not found."));
                    object value = k.GetValue(it.ApprovedName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if (value == null) throw new InvalidOperationException(L.T("Запись уже удалена.", "The entry has already been deleted."));
                    backup = Backup.ExportRegValue(root.Name + "\\" + it.RegPath, it.ApprovedName, value, k.GetValueKind(it.ApprovedName));
                    k.DeleteValue(it.ApprovedName);
                }
                if (it.ApprovedSub != null) StartupApproved.Remove(root.Key, it.ApprovedSub, it.ApprovedName);
                return backup;
            }
        }
    }
}
