using System;
using System.Collections.Generic;
using System.Text;

namespace Lighter
{
    /// <summary>Task Scheduler tasks that start at logon or at boot (COM "Schedule.Service").</summary>
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
                        catch { }
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

        /// <summary>
        /// One COM call per task: the task XML. A plain text check rejects the vast majority of tasks
        /// (no logon/boot trigger) before anything else is read.
        /// </summary>
        static AutorunItem FromTask(object task, string folderPath)
        {
            string xml = (string)Com.Get(task, "Xml");
            bool logon = xml.IndexOf("<LogonTrigger", StringComparison.Ordinal) >= 0;
            bool boot = xml.IndexOf("<BootTrigger", StringComparison.Ordinal) >= 0;
            if (!logon && !boot) return null;

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
                OnLogon = logon,
                OnBoot = boot,
                IsSystem = folderPath.StartsWith("\\Microsoft", StringComparison.OrdinalIgnoreCase),
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
