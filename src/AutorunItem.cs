using System;
using System.Collections.Generic;

namespace Lighter
{
    enum SourceKind { Registry, StartupFolder, Task, Service }

    /// <summary>Registry places beyond Run/RunOnce (see <see cref="ExtraLocations"/>).</summary>
    enum Special { None, Winlogon, Ifeo, ActiveSetup, BootExecute, AppInit }

    /// <summary>Task Scheduler trigger types of a task; Unknown = the task definition could not be read.</summary>
    [Flags]
    enum TaskTriggers
    {
        Unknown = 0, Logon = 1, Boot = 2, Schedule = 4, Idle = 8, Event = 16, Session = 32, Registration = 64,
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
        /// <summary>Part of Windows: signed by Microsoft, or a task in the \Microsoft folder.</summary>
        public bool IsSystem;
        /// <summary>The file whose signature counts (the service DLL for svchost services).</summary>
        public string SignPath = "";
        public SigInfo Sig = SigInfo.NoFile;
        public Special Special;
        /// <summary>The entry can only be put back to the Windows default value, not removed (Winlogon in HKLM).</summary>
        public bool ResetToDefault;
        /// <summary>Not a Windows default: never hidden as "system", even when the file is signed by Microsoft.</summary>
        public bool AlwaysShow;
        public string DefaultValue;

        // Registry / startup folder
        public UserProfile User;     // whose HKCU / Startup folder; null = all users (HKLM, common folder)
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
        public TaskTriggers Triggers;
        public string RunAs = "";    // account the task runs as
        public string ServiceName;
        public bool Delayed;
        public bool? Running;
        public string Account = "";  // account a service runs as

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
                        return CommonFolder ? L.T("Папка автозагрузки (общая)", "Startup folder (common)")
                            : L.T("Папка автозагрузки", "Startup folder");
                    case SourceKind.Task:
                        return L.T("Планировщик: ", "Scheduler: ") + TaskFolder + " · " + TriggerText(Triggers);
                    default:
                        string state = !Running.HasValue ? ""
                            : " · " + (Running.Value ? L.T("работает", "running") : L.T("остановлена", "stopped"));
                        return L.T("Служба: ", "Service: ") + ServiceName + state;
                }
            }
        }

        static string TriggerText(TaskTriggers t)
        {
            if (t == TaskTriggers.Unknown) return L.T("нет доступа к заданию", "no access to the task");
            var parts = new List<string>();
            if ((t & TaskTriggers.Logon) != 0) parts.Add(L.T("при входе", "at logon"));
            if ((t & TaskTriggers.Boot) != 0) parts.Add(L.T("при загрузке", "at boot"));
            if ((t & TaskTriggers.Schedule) != 0) parts.Add(L.T("по расписанию", "on schedule"));
            if ((t & TaskTriggers.Idle) != 0) parts.Add(L.T("при простое", "on idle"));
            if ((t & TaskTriggers.Event) != 0) parts.Add(L.T("по событию", "on event"));
            if ((t & TaskTriggers.Session) != 0) parts.Add(L.T("при подключении/блокировке", "on connect/lock"));
            if ((t & TaskTriggers.Registration) != 0) parts.Add(L.T("при создании", "on creation"));
            return string.Join(", ", parts);
        }

        /// <summary>Whose entry it is: a user profile, everyone, or the account a task/service runs as.</summary>
        public string UserName
        {
            get
            {
                if (Kind == SourceKind.Task) return RunAs;
                if (Kind == SourceKind.Service) return Account;
                return User != null ? User.Name : L.T("Для всех", "Everyone");
            }
        }

        /// <summary>
        /// The Publisher column: the verified signer, or why there is none ("Not signed · company from the file",
        /// which anyone can write into a file, so it is shown only as a hint).
        /// </summary>
        public string PublisherText
        {
            get
            {
                string company = Publisher.Length > 0 ? " · " + Publisher : "";
                switch (Sig.State)
                {
                    case SigState.Signed: return Sig.Signer;
                    case SigState.Invalid: return L.T("Подпись недействительна", "Invalid signature") + company;
                    case SigState.Unsigned: return L.T("Нет подписи", "Not signed") + company;
                    default: return Publisher;
                }
            }
        }

        /// <summary>Signed by nobody (or not validly): worth a look.</summary>
        public bool Unsigned
        {
            get { return Sig.State == SigState.Unsigned || Sig.State == SigState.Invalid; }
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
            // gray: off, red: file missing, amber: no valid signature, green: fine
            get { return !Enabled ? 2 : (!FileExists ? 3 : (Unsigned ? 5 : 1)); }
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
                    if (it.Special != Special.None)
                    {
                        ExtraLocations.SetEnabled(it, enabled);
                        break;
                    }
                    if (it.ApprovedSub == null)
                        throw new InvalidOperationException(L.T("Эту запись нельзя отключить, только удалить.", "This entry cannot be disabled, only deleted."));
                    using (var root = HiveRoot.Open(it.User, true))
                        StartupApproved.Set(root.Key, it.ApprovedSub, it.ApprovedName, enabled);
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
                case SourceKind.Registry: return it.Special != Special.None ? ExtraLocations.Delete(it) : RegistryRunSource.Delete(it);
                case SourceKind.StartupFolder: return StartupFolderSource.Delete(it);
                case SourceKind.Task: return TaskSchedulerSource.Delete(it);
                default: throw new InvalidOperationException(L.T("Службы не удаляются — их можно только отключить.", "Services are not deleted, only disabled."));
            }
        }
    }
}
