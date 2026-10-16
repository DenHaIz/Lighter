using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Lighter
{
    static class RegistryRunSource
    {
        class RunKey
        {
            public RegistryHive Hive;
            public string Path;
            public string Approved;
            public string Label;
            public bool Wow32;
            public bool Once;

            public RunKey(RegistryHive hive, string path, string approved, string label, bool wow32, bool once)
            {
                Hive = hive; Path = path; Approved = approved; Label = label; Wow32 = wow32; Once = once;
            }
        }

        const string Cv = @"Software\Microsoft\Windows\CurrentVersion\";
        const string Cv32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\";

        static readonly RunKey[] Keys =
        {
            new RunKey(RegistryHive.CurrentUser, Cv + "Run", "Run", @"HKCU\Run", false, false),
            new RunKey(RegistryHive.CurrentUser, Cv + "RunOnce", null, @"HKCU\RunOnce", false, true),
            new RunKey(RegistryHive.LocalMachine, Cv + "Run", "Run", @"HKLM\Run", false, false),
            new RunKey(RegistryHive.LocalMachine, Cv + "RunOnce", null, @"HKLM\RunOnce", false, true),
            new RunKey(RegistryHive.LocalMachine, Cv32 + "Run", "Run32", @"HKLM\Run", true, false),
            new RunKey(RegistryHive.LocalMachine, Cv32 + "RunOnce", null, @"HKLM\RunOnce", true, true),
        };

        public static List<AutorunItem> Load()
        {
            var list = new List<AutorunItem>();
            foreach (var rk in Keys)
            {
                using (var root = RegistryKey.OpenBaseKey(rk.Hive, RegistryView.Registry64))
                using (var k = root.OpenSubKey(rk.Path))
                {
                    if (k == null) continue;
                    foreach (string name in k.GetValueNames())
                    {
                        if (name.Length == 0) continue;
                        var it = new AutorunItem
                        {
                            Kind = SourceKind.Registry,
                            Name = name,
                            Command = Convert.ToString(k.GetValue(name)) ?? "",
                            Hive = rk.Hive,
                            RegPath = rk.Path,
                            ApprovedSub = rk.Approved,
                            ApprovedName = name,
                            RegLabel = rk.Label,
                            Wow32 = rk.Wow32,
                            Once = rk.Once,
                            CanToggle = rk.Approved != null,
                            CanDelete = true,
                        };
                        it.ExePath = PathUtil.ExtractExe(it.Command);
                        it.FileExists = PathUtil.SafeExists(it.ExePath);
                        it.Publisher = PathUtil.GetPublisher(it.ExePath);
                        it.Enabled = rk.Approved == null || StartupApproved.IsEnabled(rk.Hive, rk.Approved, name);
                        list.Add(it);
                    }
                }
            }
            return list;
        }

        public static string Delete(AutorunItem it)
        {
            string backup;
            using (var root = RegistryKey.OpenBaseKey(it.Hive, RegistryView.Registry64))
            using (var k = root.OpenSubKey(it.RegPath, true))
            {
                if (k == null) throw new InvalidOperationException(L.T("Раздел реестра не найден.", "Registry key not found."));
                object value = k.GetValue(it.ApprovedName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) throw new InvalidOperationException(L.T("Запись уже удалена.", "The entry has already been deleted."));
                backup = Backup.ExportRegValue(k.Name, it.ApprovedName, value, k.GetValueKind(it.ApprovedName));
                k.DeleteValue(it.ApprovedName);
            }
            if (it.ApprovedSub != null) StartupApproved.Remove(it.Hive, it.ApprovedSub, it.ApprovedName);
            return backup;
        }
    }
}
