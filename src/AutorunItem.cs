using System;
using Microsoft.Win32;

namespace Lighter
{
    enum SourceKind { Registry, StartupFolder, Task, Service }

    enum RowKind { Normal, Dim, Danger }

    /// <summary>What the list needs to draw a row: text color, icon and status dot.</summary>
    interface IRowInfo
    {
        RowKind RowKind { get; }
        string IconPath { get; }
        /// <summary>0 = none, 1 = on (green), 2 = off (gray), 3 = problem (red).</summary>
        int Dot { get; }
    }

    class AutorunItem : IRowInfo
    {
        public SourceKind Kind;
        public string Name = "";
        public string Command = "";
        public string ExePath = "";
        public string Publisher = "";
        public bool Enabled = true;
        public bool CanToggle;
        public bool CanDelete;
        public bool FileExists = true;
        public bool IsSystem;

        // Registry / startup folder
        public RegistryHive Hive;
        public string RegPath;
        public string ApprovedSub;   // StartupApproved subkey, null when Windows cannot disable the entry
        public string ApprovedName;
        public string FilePath;      // file in a startup folder
        public string RegLabel;      // e.g. HKCU\Run
        public bool Wow32;           // WOW6432Node key
        public bool Once;            // RunOnce: runs one time, cannot be disabled
        public bool CommonFolder;    // all-users startup folder

        // Scheduler / services
        public string TaskPath;
        public string TaskFolder;
        public bool OnLogon, OnBoot;
        public string ServiceName;
        public bool Delayed;
        public bool? Running;

        // State and location are built on demand so they follow the current UI language.
        public string StateText
        {
            get
            {
                if (Kind == SourceKind.Service)
                    return !Enabled ? L.T("Отключена", "Disabled")
                        : Delayed ? L.T("Автоматически (отложенный)", "Automatic (delayed)") : L.T("Автоматически", "Automatic");
                if (Once) return L.T("Однократно", "Run once");
                return Enabled ? L.T("Включено", "Enabled") : L.T("Отключено", "Disabled");
            }
        }

        public string Location
        {
            get
            {
                switch (Kind)
                {
                    case SourceKind.Registry:
                        return L.T("Реестр: ", "Registry: ") + RegLabel + (Wow32 ? L.T(" (32-бит)", " (32-bit)") : "");
                    case SourceKind.StartupFolder:
                        return CommonFolder ? L.T("Папка автозагрузки (все пользователи)", "Startup folder (all users)")
                            : L.T("Папка автозагрузки (пользователь)", "Startup folder (current user)");
                    case SourceKind.Task:
                        string trigger = OnLogon && OnBoot ? L.T("при входе и загрузке", "at logon and boot")
                            : OnLogon ? L.T("при входе", "at logon") : L.T("при загрузке", "at boot");
                        return L.T("Планировщик: ", "Scheduler: ") + TaskFolder + " · " + trigger;
                    default:
                        string state = !Running.HasValue ? ""
                            : " · " + (Running.Value ? L.T("работает", "running") : L.T("остановлена", "stopped"));
                        return L.T("Служба: ", "Service: ") + ServiceName + state;
                }
            }
        }

        public RowKind RowKind
        {
            get { return !FileExists ? RowKind.Danger : (!Enabled ? RowKind.Dim : RowKind.Normal); }
        }

        public string IconPath
        {
            get { return FileExists ? ExePath : null; }
        }

        public int Dot
        {
            get { return !Enabled ? 2 : (FileExists ? 1 : 3); }
        }

        /// <summary>The file to show in Explorer: the shortcut itself for startup-folder entries.</summary>
        public string OpenPath
        {
            get { return Kind == SourceKind.StartupFolder ? FilePath : ExePath; }
        }
    }

    /// <summary>Dispatches enable/disable/delete to the source that owns the item.</summary>
    static class Autoruns
    {
        public static void SetEnabled(AutorunItem it, bool enabled)
        {
            switch (it.Kind)
            {
                case SourceKind.Registry:
                case SourceKind.StartupFolder:
                    if (it.ApprovedSub == null)
                        throw new InvalidOperationException(L.T("Эту запись нельзя отключить, только удалить.", "This entry cannot be disabled, only deleted."));
                    StartupApproved.Set(it.Hive, it.ApprovedSub, it.ApprovedName, enabled);
                    it.Enabled = enabled;
                    break;
                case SourceKind.Task:
                    TaskSchedulerSource.SetEnabled(it.TaskPath, enabled);
                    it.Enabled = enabled;
                    break;
                case SourceKind.Service:
                    ServiceSource.SetEnabled(it, enabled);
                    break;
            }
        }

        /// <summary>Deletes the entry after saving a backup; returns the backup path.</summary>
        public static string Delete(AutorunItem it)
        {
            switch (it.Kind)
            {
                case SourceKind.Registry: return RegistryRunSource.Delete(it);
                case SourceKind.StartupFolder: return StartupFolderSource.Delete(it);
                case SourceKind.Task: return TaskSchedulerSource.Delete(it);
                default: throw new InvalidOperationException(L.T("Службы не удаляются — их можно только отключить.", "Services are not deleted, only disabled."));
            }
        }
    }
}
