using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace Lighter
{
    static class PathUtil
    {
        static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        static readonly Dictionary<string, string> publishers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Generic launchers: a process running one of these says nothing about which autorun started it.
        static readonly HashSet<string> hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "svchost.exe", "rundll32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe",
            "cscript.exe", "mshta.exe", "conhost.exe", "explorer.exe", "dllhost.exe", "taskhostw.exe"
        };

        /// <summary>Extracts the executable path from a command line (quotes, spaces, env vars, NT prefixes).</summary>
        public static string ExtractExe(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return "";
            string s = Environment.ExpandEnvironmentVariables(command.Trim());
            if (s.StartsWith(@"\??\")) s = s.Substring(4);
            if (s.StartsWith(@"\SystemRoot\", StringComparison.OrdinalIgnoreCase)) s = WinDir + s.Substring(11);
            else if (s.StartsWith(@"System32\", StringComparison.OrdinalIgnoreCase)) s = Path.Combine(WinDir, s);

            string exe = null;
            if (s.StartsWith("\""))
            {
                int end = s.IndexOf('"', 1);
                exe = end > 0 ? s.Substring(1, end - 1) : s.Substring(1);
            }
            else
            {
                // Unquoted paths may contain spaces: grow the candidate word by word until it names a file.
                int idx = -1;
                while (true)
                {
                    idx = s.IndexOf(' ', idx + 1);
                    string cand = idx < 0 ? s : s.Substring(0, idx);
                    if (SafeExists(cand)) { exe = cand; break; }
                    if (SafeExists(cand + ".exe")) { exe = cand + ".exe"; break; }
                    if (cand.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) { exe = cand; break; }
                    if (idx < 0) break;
                }
                if (exe == null)
                {
                    int sp = s.IndexOf(' ');
                    exe = sp > 0 ? s.Substring(0, sp) : s;
                }
            }
            return Resolve(exe.Trim().TrimEnd(','));
        }

        static string Resolve(string exe)
        {
            if (exe.Length == 0) return exe;
            try
            {
                if (Path.IsPathRooted(exe)) return exe;
                string name = Path.HasExtension(exe) ? exe : exe + ".exe";
                var dirs = new List<string> { Environment.SystemDirectory, WinDir };
                string pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
                dirs.AddRange(pathVar.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries));
                foreach (string d in dirs)
                {
                    string cand = Path.Combine(Environment.ExpandEnvironmentVariables(d.Trim()), name);
                    if (File.Exists(cand)) return cand;
                }
            }
            catch { }
            return exe;
        }

        public static bool SafeExists(string path)
        {
            try { return !string.IsNullOrEmpty(path) && File.Exists(path); }
            catch { return false; }
        }

        public static string GetPublisher(string path)
        {
            if (!SafeExists(path)) return "";
            lock (publishers)
            {
                string cached;
                if (publishers.TryGetValue(path, out cached)) return cached;
            }
            string result = "";
            try { result = (FileVersionInfo.GetVersionInfo(path).CompanyName ?? "").Trim(); }
            catch { }
            lock (publishers) publishers[path] = result;
            return result;
        }

        public static bool IsHost(string path)
        {
            try { return hosts.Contains(Path.GetFileName(path)); }
            catch { return false; }
        }

        /// <summary>Wraps a path in quotes when it contains spaces.</summary>
        public static string Quote(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith("\"") || path.IndexOf(' ') < 0) return path ?? "";
            return "\"" + path + "\"";
        }
    }
}
