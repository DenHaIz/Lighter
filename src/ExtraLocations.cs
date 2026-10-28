using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace Lighter
{
    /// <summary>
    /// Places where programs start that Task Manager does not show, and where malware likes to hide:
    /// Winlogon Shell/Userinit, Image File Execution Options "Debugger" (hijacks a program's start),
    /// Active Setup, BootExecute and AppInit_DLLs. Windows defaults are marked as system (hidden by the
    /// "Third-party" filter); anything else is always shown, whoever signed it.
    /// </summary>
    static class ExtraLocations
    {
        const string Winlogon = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Winlogon";
        const string Ifeo = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        const string Ifeo32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options";
        const string ActiveSetup = @"SOFTWARE\Microsoft\Active Setup\Installed Components";
        const string ActiveSetup32 = @"SOFTWARE\WOW6432Node\Microsoft\Active Setup\Installed Components";
        const string SessionManager = @"SYSTEM\CurrentControlSet\Control\Session Manager";
        const string Windows = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Windows";
        const string Windows32 = @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Windows";

        const string DefaultShell = "explorer.exe";
        const string DefaultBoot = "autocheck autochk *";
        static readonly string DefaultUserinit = Path.Combine(Environment.SystemDirectory, "userinit.exe") + ",";

        public static void AddMachine(List<AutorunItem> list, RegistryKey root)
        {
            Try(() =>
            {
                using (var k = root.OpenSubKey(Winlogon))
                    if (k != null)
                    {
                        AddWinlogon(list, k, "Shell", null, DefaultShell);
                        AddWinlogon(list, k, "Userinit", null, DefaultUserinit);
                    }
            });
            Try(() => AddIfeo(list, root, Ifeo, false));
            Try(() => AddIfeo(list, root, Ifeo32, true));
            Try(() => AddActiveSetup(list, root, ActiveSetup, false));
            Try(() => AddActiveSetup(list, root, ActiveSetup32, true));
            Try(() => AddBootExecute(list, root));
            Try(() => AddAppInit(list, root, Windows, false));
            Try(() => AddAppInit(list, root, Windows32, true));
        }

        /// <summary>A user's own Shell replaces Explorer for that user only.</summary>
        public static void AddUser(List<AutorunItem> list, RegistryKey root, UserProfile user)
        {
            Try(() =>
            {
                using (var k = root.OpenSubKey(@"Software\Microsoft\Windows NT\CurrentVersion\Winlogon"))
                    if (k != null && k.GetValue("Shell") != null) AddWinlogon(list, k, "Shell", user, null);
            });
        }

        static void Try(Action a)
        {
            try { a(); }
            catch { }
        }

        static AutorunItem Item(Special special, string name, string command, string regPath, string valueName, string label, bool wow32)
        {
            var it = new AutorunItem
            {
                Kind = SourceKind.Registry,
                Special = special,
                Name = name,
                Command = command,
                RegPath = regPath,
                ApprovedName = valueName,
                RegLabel = label,
                Wow32 = wow32,
                CanDelete = true,
            };
            it.ExePath = it.SignPath = PathUtil.ExtractExe(command);
            it.FileExists = it.ExePath.Length == 0 || PathUtil.SafeExists(it.ExePath);
            it.Publisher = PathUtil.GetPublisher(it.ExePath);
            return it;
        }

        static bool SameCommand(string a, string b)
        {
            return string.Equals(Environment.ExpandEnvironmentVariables(a ?? "").Trim().TrimEnd(','),
                Environment.ExpandEnvironmentVariables(b ?? "").Trim().TrimEnd(','), StringComparison.OrdinalIgnoreCase);
        }

        static void AddWinlogon(List<AutorunItem> list, RegistryKey k, string value, UserProfile user, string def)
        {
            string cmd = Convert.ToString(k.GetValue(value, "", RegistryValueOptions.DoNotExpandEnvironmentNames)).Trim();
            if (cmd.Length == 0 && def == null) return;
            var it = Item(Special.Winlogon, "Winlogon " + value, cmd, user == null ? Winlogon : @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon",
                value, (user == null ? @"HKLM\" : @"HKCU\") + @"Winlogon\" + value, false);
            it.User = user;
            bool isDefault = def != null && (SameCommand(cmd, def) || (value == "Userinit" && SameCommand(cmd, "userinit.exe")));
            if (isDefault)
            {
                it.IsSystem = true;
                it.CanDelete = false;
            }
            else
            {
                it.AlwaysShow = true;
                if (def != null)
                {
                    // The machine-wide value cannot go: it is put back to the Windows default instead.
                    it.ResetToDefault = true;
                    it.DefaultValue = value == "Userinit" ? DefaultUserinit : DefaultShell;
                }
                // Userinit is a list: what matters is whatever was added next to userinit.exe.
                string extra = cmd.Split(',').Select(x => x.Trim()).FirstOrDefault(x => x.Length > 0 && !SameCommand(x, DefaultUserinit) && !SameCommand(x, "userinit.exe"));
                if (value == "Userinit" && extra != null)
                {
                    it.ExePath = it.SignPath = PathUtil.ExtractExe(extra);
                    it.FileExists = PathUtil.SafeExists(it.ExePath);
                    it.Publisher = PathUtil.GetPublisher(it.ExePath);
                }
            }
            list.Add(it);
        }

        static void AddIfeo(List<AutorunItem> list, RegistryKey root, string path, bool wow32)
        {
            using (var k = root.OpenSubKey(path))
            {
                if (k == null) return;
                foreach (string exe in k.GetSubKeyNames())
                {
                    using (var sub = k.OpenSubKey(exe))
                    {
                        string dbg = sub == null ? null : sub.GetValue("Debugger") as string;
                        if (string.IsNullOrEmpty(dbg)) continue;
                        var it = Item(Special.Ifeo, exe, dbg, path + "\\" + exe, "Debugger", "IFEO\\" + exe + " (Debugger)", wow32);
                        it.AlwaysShow = true;
                        list.Add(it);
                    }
                }
            }
        }

        static void AddActiveSetup(List<AutorunItem> list, RegistryKey root, string path, bool wow32)
        {
            using (var k = root.OpenSubKey(path))
            {
                if (k == null) return;
                foreach (string id in k.GetSubKeyNames())
                {
                    using (var sub = k.OpenSubKey(id))
                    {
                        if (sub == null) continue;
                        string stub = sub.GetValue("StubPath", null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
                        if (string.IsNullOrEmpty(stub)) continue;
                        string name = sub.GetValue("") as string;
                        if (string.IsNullOrEmpty(name) || name.StartsWith("@")) name = id;
                        var it = Item(Special.ActiveSetup, name, stub, path + "\\" + id, "StubPath", "Active Setup\\" + id, wow32);
                        // ShellComponent: Active Setup calls this DLL with StubPath as its argument.
                        string shell = sub.GetValue("ShellComponent") as string;
                        if (!string.IsNullOrEmpty(shell))
                        {
                            string dll = Environment.ExpandEnvironmentVariables(shell);
                            if (!Path.IsPathRooted(dll)) dll = Path.Combine(wow32 ? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) : Environment.SystemDirectory, dll);
                            it.ExePath = it.SignPath = dll;
                            it.FileExists = PathUtil.SafeExists(dll);
                            it.Publisher = PathUtil.GetPublisher(dll);
                            it.Command = dll + " " + stub;
                        }
                        object installed = sub.GetValue("IsInstalled");
                        it.Enabled = installed == null || Convert.ToInt32(installed) != 0;
                        it.CanToggle = true;
                        list.Add(it);
                    }
                }
            }
        }

        static void AddBootExecute(List<AutorunItem> list, RegistryKey root)
        {
            using (var k = root.OpenSubKey(SessionManager))
            {
                var lines = k == null ? null : k.GetValue("BootExecute") as string[];
                if (lines == null) return;
                foreach (string line in lines.Where(x => !string.IsNullOrWhiteSpace(x)))
                {
                    // "autocheck autochk *": a native program from System32 with its arguments.
                    string[] words = line.Trim().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    string prog = words.Length > 1 && words[0].Equals("autocheck", StringComparison.OrdinalIgnoreCase) ? words[1] : words[0];
                    var it = Item(Special.BootExecute, prog, line.Trim(), SessionManager, "BootExecute", "BootExecute", false);
                    string exe = Path.Combine(Environment.SystemDirectory, Path.HasExtension(prog) ? prog : prog + ".exe");
                    it.ExePath = it.SignPath = exe;
                    it.FileExists = PathUtil.SafeExists(exe);
                    it.Publisher = PathUtil.GetPublisher(exe);
                    // Windows itself writes "autocheck autochk *", sometimes with switches (/q /v ...).
                    string norm = line.Trim();
                    if (norm.StartsWith("autocheck autochk ", StringComparison.OrdinalIgnoreCase) && norm.EndsWith("*")
                        && norm.Substring(18, norm.Length - 19).Split(' ').All(x => x.Length == 0 || x.StartsWith("/")))
                    {
                        it.IsSystem = true;
                        it.CanDelete = false;
                    }
                    else it.AlwaysShow = true;
                    list.Add(it);
                }
            }
        }

        static void AddAppInit(List<AutorunItem> list, RegistryKey root, string path, bool wow32)
        {
            using (var k = root.OpenSubKey(path))
            {
                string dlls = k == null ? null : k.GetValue("AppInit_DLLs") as string;
                if (string.IsNullOrWhiteSpace(dlls)) return;
                var it = Item(Special.AppInit, "AppInit_DLLs", dlls.Trim(), path, "AppInit_DLLs", "AppInit_DLLs", wow32);
                string first = dlls.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                it.ExePath = it.SignPath = PathUtil.ExtractExe(first);
                it.FileExists = PathUtil.SafeExists(it.ExePath);
                it.Publisher = PathUtil.GetPublisher(it.ExePath);
                it.Enabled = Convert.ToInt32(k.GetValue("LoadAppInit_DLLs", 0)) == 1;
                it.CanToggle = true;
                it.AlwaysShow = true;
                list.Add(it);
            }
        }

        // ----- actions -----

        /// <summary>Active Setup: IsInstalled 1/0. AppInit_DLLs: LoadAppInit_DLLs 1/0.</summary>
        public static void SetEnabled(AutorunItem it, bool enabled)
        {
            if (it.Special != Special.ActiveSetup && it.Special != Special.AppInit)
                throw new InvalidOperationException(L.T("Эту запись нельзя отключить, только удалить.", "This entry cannot be disabled, only deleted."));
            using (var root = HiveRoot.Open(it.User, true))
            using (var k = root.Key.OpenSubKey(it.RegPath, true))
            {
                if (k == null) throw new InvalidOperationException(L.T("Раздел реестра не найден.", "Registry key not found."));
                k.SetValue(it.Special == Special.ActiveSetup ? "IsInstalled" : "LoadAppInit_DLLs", enabled ? 1 : 0, RegistryValueKind.DWord);
            }
            it.Enabled = enabled;
        }

        /// <summary>Removes the entry after a .reg backup of the value as it was; returns the backup path.</summary>
        public static string Delete(AutorunItem it)
        {
            using (var root = HiveRoot.Open(it.User, true))
            using (var k = root.Key.OpenSubKey(it.RegPath, true))
            {
                if (k == null) throw new InvalidOperationException(L.T("Раздел реестра не найден.", "Registry key not found."));
                object value = k.GetValue(it.ApprovedName, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                if (value == null) throw new InvalidOperationException(L.T("Запись уже удалена.", "The entry has already been deleted."));
                string backup = Backup.ExportRegValue(root.Name + "\\" + it.RegPath, it.ApprovedName, value, k.GetValueKind(it.ApprovedName), it.Name);
                switch (it.Special)
                {
                    case Special.Winlogon:
                        if (it.ResetToDefault) k.SetValue(it.ApprovedName, it.DefaultValue, RegistryValueKind.String);
                        else k.DeleteValue(it.ApprovedName);
                        break;
                    case Special.BootExecute:
                        var lines = ((string[])value).Where(x => !string.Equals(x.Trim(), it.Command, StringComparison.OrdinalIgnoreCase)).ToArray();
                        k.SetValue(it.ApprovedName, lines, RegistryValueKind.MultiString);
                        break;
                    case Special.AppInit:
                        k.SetValue(it.ApprovedName, "", RegistryValueKind.String);
                        break;
                    default:
                        k.DeleteValue(it.ApprovedName);
                        break;
                }
                return backup;
            }
        }
    }
}
