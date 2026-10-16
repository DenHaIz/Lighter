using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Lighter
{
    class ProcInfo : IRowInfo
    {
        public int Pid;
        public string Name = "";
        public long Memory;
        public string Path = "";
        public string Publisher = "";
        public bool Autorun;

        public RowKind RowKind { get { return RowKind.Normal; } }
        public string IconPath { get { return Path; } }
        public int Dot { get { return Autorun ? 1 : 0; } }
    }

    static class ProcessList
    {
        /// <summary>Flags processes whose executable is started by an enabled autorun entry.</summary>
        public static void MarkAutorun(List<ProcInfo> procs, HashSet<string> autorunPaths)
        {
            foreach (var pi in procs) pi.Autorun = pi.Path.Length > 0 && autorunPaths.Contains(pi.Path);
        }

        public static List<ProcInfo> Load()
        {
            var list = new List<ProcInfo>();
            foreach (var p in Process.GetProcesses())
            {
                using (p)
                {
                    if (p.Id == 0) continue;
                    var pi = new ProcInfo { Pid = p.Id, Name = p.ProcessName };
                    try { pi.Memory = p.WorkingSet64; }
                    catch { }
                    pi.Path = Native.GetProcessPath(p.Id);
                    if (pi.Path.Length > 0)
                    {
                        pi.Name = System.IO.Path.GetFileName(pi.Path);
                        pi.Publisher = PathUtil.GetPublisher(pi.Path);
                    }
                    list.Add(pi);
                }
            }
            return list;
        }

        public static void Kill(int pid)
        {
            using (var p = Process.GetProcessById(pid))
            {
                p.Kill();
                p.WaitForExit(3000);
            }
        }
    }
}
