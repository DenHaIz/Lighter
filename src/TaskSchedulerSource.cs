using System;
using System.Collections.Generic;
using System.Security.Principal;
using System.Text;

namespace Lighter
{
    /// <summary>
    /// Task Scheduler (COM "Schedule.Service"): tasks that start at logon or at boot, plus every third-party task
    /// with any trigger (updaters on a schedule etc.). Microsoft tasks are listed only for logon/boot.
    /// </summary>
    static class TaskSchedulerSource
    {
        const int EnumHidden = 1;

        static object Connect()
        {
            object svc = Com.Create("Schedule.Service");
            Com.Call(svc, "Connect");
            return svc;
        }

        public static List<AutorunItem> Load()
        {
            var list = new List<AutorunItem>();
            object svc = Connect();
            try { Walk(Com.Call(svc, "GetFolder", "\\"), list); }
            finally { Com.Release(svc); }
            return list;
        }

        static void Walk(object folder, List<AutorunItem> list)
        {
            try
            {
                string folderPath = (string)Com.Get(folder, "Path");
                object tasks = null;
                try { tasks = Com.Call(folder, "GetTasks", EnumHidden); }
                catch { }
                if (tasks != null)
                {
                    foreach (object task in Com.Items(tasks))
                    {
                        try
                        {
                            AutorunItem it = FromTask(task, folderPath);
                            if (it != null) list.Add(it);
                        }
                        catch
                        {
                            // The task is listed but its definition cannot be read: still show it
                            // (unless it is a Microsoft task) so nothing is hidden silently.
                            AutorunItem it = Unreadable(task, folderPath);
                            if (it != null) list.Add(it);
                        }
                        finally { Com.Release(task); }
                    }
                    Com.Release(tasks);
                }

                object subs = null;
                try { subs = Com.Call(folder, "GetFolders", 0); }
                catch { }
                if (subs != null)
                {
                    foreach (object sub in Com.Items(subs)) Walk(sub, list);
                    Com.Release(subs);
                }
            }
            finally { Com.Release(folder); }
        }

        static readonly string[] TriggerTags =
        {
            "<LogonTrigger", "<BootTrigger", "<TimeTrigger", "<CalendarTrigger", "<IdleTrigger",
            "<EventTrigger", "<SessionStateChangeTrigger", "<RegistrationTrigger",
        };

        static readonly TaskTriggers[] TriggerFlags =
        {
            TaskTriggers.Logon, TaskTriggers.Boot, TaskTriggers.Schedule, TaskTriggers.Schedule, TaskTriggers.Idle,
            TaskTriggers.Event, TaskTriggers.Session, TaskTriggers.Registration,
        };

        static bool IsSystemFolder(string folderPath)
        {
            return folderPath.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// One COM call per task: the task XML. A plain text check of the triggers rejects most tasks
        /// before anything else is read.
        /// </summary>
        static AutorunItem FromTask(object task, string folderPath)
        {
            string xml = (string)Com.Get(task, "Xml");
            bool system = IsSystemFolder(folderPath);
            var triggers = TaskTriggers.Unknown;
            for (int i = 0; i < TriggerTags.Length; i++)
                if (xml.IndexOf(TriggerTags[i], StringComparison.Ordinal) >= 0) triggers |= TriggerFlags[i];
            // Microsoft tasks: only logon/boot (the rest is hundreds of maintenance tasks).
            // Third-party tasks: anything that starts by itself.
            if (system ? (triggers & (TaskTriggers.Logon | TaskTriggers.Boot)) == 0 : triggers == TaskTriggers.Unknown)
                return null;

            string cmd = "", exe = "";
            int exec = xml.IndexOf("<Exec>", StringComparison.Ordinal);
            if (exec >= 0)
            {
                int end = xml.IndexOf("</Exec>", exec, StringComparison.Ordinal);
                string path = Tag(xml, "Command", exec, end);
                string args = Tag(xml, "Arguments", exec, end);
                cmd = PathUtil.Quote(path) + (args.Length > 0 ? " " + args : "");
                exe = PathUtil.ExtractExe(cmd);
            }
            else
            {
                int com = xml.IndexOf("<ComHandler>", StringComparison.Ordinal);
                if (com >= 0) cmd = "COM " + Tag(xml, "ClassId", com, -1);
            }

            bool enabled = (bool)Com.Get(task, "Enabled");
            var it = new AutorunItem
            {
                Kind = SourceKind.Task,
                Name = (string)Com.Get(task, "Name"),
                TaskPath = (string)Com.Get(task, "Path"),
                Command = cmd,
                ExePath = exe,
                Enabled = enabled,
                TaskFolder = folderPath,
                Triggers = triggers,
                RunAs = RunAsName(xml),
                IsSystem = system,
                CanToggle = true,
                CanDelete = true,
            };
            it.FileExists = exe.Length == 0 || PathUtil.SafeExists(exe);
            it.Publisher = PathUtil.GetPublisher(exe);
            if (it.Publisher.Length == 0)
            {
                string author = Tag(xml, "Author", 0, -1);
                if (author.Length > 0 && !author.StartsWith("$(")) it.Publisher = author;
            }
            return it;
        }

        static AutorunItem Unreadable(object task, string folderPath)
        {
            if (IsSystemFolder(folderPath)) return null;
            try
            {
                var it = new AutorunItem
                {
                    Kind = SourceKind.Task,
                    Name = (string)Com.Get(task, "Name"),
                    TaskPath = (string)Com.Get(task, "Path"),
                    TaskFolder = folderPath,
                    CanToggle = true,
                    CanDelete = true,
                };
                try { it.Enabled = (bool)Com.Get(task, "Enabled"); }
                catch { }
                return it;
            }
            catch { return null; }
        }

        static readonly Dictionary<string, string> AccountNames = new Dictionary<string, string>();

        /// <summary>
        /// The account from &lt;Principal&gt;: a user name (without domain), SYSTEM, or a group such as
        /// "Users" (the task then runs for everyone who logs on).
        /// </summary>
        static string RunAsName(string xml)
        {
            int p = xml.IndexOf("<Principals>", StringComparison.Ordinal);
            if (p < 0) return "";
            string id = Tag(xml, "UserId", p, -1);
            if (id.Length == 0) id = Tag(xml, "GroupId", p, -1);
            if (id.Length == 0) return "";
            string name;
            lock (AccountNames)
                if (AccountNames.TryGetValue(id, out name)) return name;
            name = id;
            if (id.StartsWith("S-1-", StringComparison.Ordinal))
            {
                try { name = new SecurityIdentifier(id).Translate(typeof(NTAccount)).Value; }
                catch { }
            }
            int slash = name.LastIndexOf('\\');
            if (slash >= 0) name = name.Substring(slash + 1);
            lock (AccountNames) AccountNames[id] = name;
            return name;
        }

        /// <summary>Text of the first &lt;name&gt; element between from and limit (-1 = end of text).</summary>
        static string Tag(string xml, string name, int from, int limit)
        {
            int s = xml.IndexOf("<" + name + ">", from, StringComparison.Ordinal);
            if (s < 0 || (limit >= 0 && s > limit)) return "";
            s += name.Length + 2;
            int e = xml.IndexOf("</" + name + ">", s, StringComparison.Ordinal);
            return e < 0 ? "" : XmlDecode(xml.Substring(s, e - s).Trim());
        }

        static string XmlDecode(string s)
        {
            if (s.IndexOf('&') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++)
            {
                int semi;
                if (s[i] != '&' || (semi = s.IndexOf(';', i)) < 0) { sb.Append(s[i]); continue; }
                string ent = s.Substring(i + 1, semi - i - 1);
                string rep = null;
                switch (ent)
                {
                    case "amp": rep = "&"; break;
                    case "lt": rep = "<"; break;
                    case "gt": rep = ">"; break;
                    case "quot": rep = "\""; break;
                    case "apos": rep = "'"; break;
                    default:
                        try
                        {
                            if (ent.StartsWith("#x")) rep = char.ConvertFromUtf32(Convert.ToInt32(ent.Substring(2), 16));
                            else if (ent.StartsWith("#")) rep = char.ConvertFromUtf32(int.Parse(ent.Substring(1)));
                        }
                        catch { }
                        break;
                }
                if (rep == null) { sb.Append(s[i]); continue; }
                sb.Append(rep);
                i = semi;
            }
            return sb.ToString();
        }

        static object GetTask(object svc, string taskPath)
        {
            object root = Com.Call(svc, "GetFolder", "\\");
            try { return Com.Call(root, "GetTask", taskPath); }
            finally { Com.Release(root); }
        }

        public static void SetEnabled(string taskPath, bool enabled)
        {
            object svc = Connect();
            try
            {
                object task = GetTask(svc, taskPath);
                try { Com.Set(task, "Enabled", enabled); }
                finally { Com.Release(task); }
            }
            finally { Com.Release(svc); }
        }

        public static string Delete(AutorunItem it)
        {
            object svc = Connect();
            try
            {
                object task = GetTask(svc, it.TaskPath);
                string xml;
                try { xml = (string)Com.Get(task, "Xml"); }
                finally { Com.Release(task); }
                string backup = Backup.SaveText(it.Name, ".xml", xml);
                int slash = it.TaskPath.LastIndexOf('\\');
                string folderPath = slash <= 0 ? "\\" : it.TaskPath.Substring(0, slash);
                object folder = Com.Call(svc, "GetFolder", folderPath);
                try { Com.Call(folder, "DeleteTask", it.TaskPath.Substring(slash + 1), 0); }
                finally { Com.Release(folder); }
                return backup;
            }
            finally { Com.Release(svc); }
        }
    }
}
