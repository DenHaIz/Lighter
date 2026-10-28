using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Lighter
{
    /// <summary>A user profile on this PC (from ProfileList).</summary>
    class UserProfile
    {
        public string Sid;
        public string Name;   // profile folder name, e.g. "ivanov"
        public string Dir;    // C:\Users\ivanov
        public bool IsMe;     // the account Lighter runs under

        const string ProfileList = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

        /// <summary>
        /// Every real user profile: local and domain accounts (S-1-5-21-…) and Microsoft Entra accounts (S-1-12-1-…).
        /// System accounts, backup copies (.bak) and profiles whose folder is gone are skipped.
        /// </summary>
        public static List<UserProfile> All()
        {
            var list = new List<UserProfile>();
            string me = null;
            try { me = WindowsIdentity.GetCurrent().User.Value; }
            catch { }
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var pl = hklm.OpenSubKey(ProfileList))
            {
                if (pl == null) return list;
                foreach (string sid in pl.GetSubKeyNames())
                {
                    if (sid.IndexOf('.') >= 0) continue;
                    if (!sid.StartsWith("S-1-5-21-", StringComparison.Ordinal) && !sid.StartsWith("S-1-12-1-", StringComparison.Ordinal)) continue;
                    string dir = null;
                    try
                    {
                        using (var k = pl.OpenSubKey(sid))
                            if (k != null) dir = Environment.ExpandEnvironmentVariables(Convert.ToString(k.GetValue("ProfileImagePath")) ?? "");
                    }
                    catch { }
                    if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                    list.Add(new UserProfile
                    {
                        Sid = sid,
                        Name = Path.GetFileName(dir.TrimEnd('\\')),
                        Dir = dir,
                        IsMe = sid == me,
                    });
                }
            }
            // The current account first, the rest by name.
            list.Sort((a, b) => a.IsMe != b.IsMe ? (a.IsMe ? -1 : 1) : string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        /// <summary>
        /// Expands a "User Shell Folders" path (e.g. %USERPROFILE%\AppData\…) for this user rather than for
        /// the account Lighter runs under.
        /// </summary>
        public string ExpandPath(string path)
        {
            path = Replace(path, "%USERPROFILE%", Dir);
            path = Replace(path, "%APPDATA%", Path.Combine(Dir, @"AppData\Roaming"));
            path = Replace(path, "%LOCALAPPDATA%", Path.Combine(Dir, @"AppData\Local"));
            path = Replace(path, "%USERNAME%", Name);
            return Environment.ExpandEnvironmentVariables(path);
        }

        static string Replace(string s, string token, string value)
        {
            int i;
            while ((i = s.IndexOf(token, StringComparison.OrdinalIgnoreCase)) >= 0)
                s = s.Substring(0, i) + value + s.Substring(i + token.Length);
            return s;
        }
    }

    /// <summary>
    /// The registry root of HKLM (user == null) or of a user profile. A logged-on user's hive is in HKEY_USERS.
    /// The hive of a user who is not logged on is loaded from NTUSER.DAT only while this object lives
    /// (RegLoadAppKey: private to Lighter and unloaded when the handle closes, so the user can still log on).
    /// Keep it short-lived and always dispose it.
    /// </summary>
    sealed class HiveRoot : IDisposable
    {
        public readonly RegistryKey Key;
        /// <summary>Root name for .reg files: HKEY_LOCAL_MACHINE or HKEY_USERS\S-1-…</summary>
        public readonly string Name;

        readonly RegistryKey parent;
        readonly object gate;

        static readonly Dictionary<string, object> Gates = new Dictionary<string, object>();

        const int KEY_READ = 0x20019;
        const int KEY_ALL_ACCESS = 0xF003F;

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        static extern int RegLoadAppKey(string file, out IntPtr key, int samDesired, int options, int reserved);

        HiveRoot(RegistryKey key, RegistryKey parent, string name, object gate)
        {
            Key = key; this.parent = parent; Name = name; this.gate = gate;
        }

        public static HiveRoot Open(UserProfile user, bool writable)
        {
            if (user == null)
                return new HiveRoot(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), null,
                    "HKEY_LOCAL_MACHINE", null);

            // One open hive per user at a time: the loaders run in parallel and a private hive
            // cannot be loaded twice.
            object gate;
            lock (Gates)
                if (!Gates.TryGetValue(user.Sid, out gate)) Gates[user.Sid] = gate = new object();
            Monitor.Enter(gate);
            RegistryKey users = null;
            try
            {
                string name = @"HKEY_USERS\" + user.Sid;
                users = RegistryKey.OpenBaseKey(RegistryHive.Users, RegistryView.Registry64);
                RegistryKey k = users.OpenSubKey(user.Sid, writable);
                if (k != null) return new HiveRoot(k, users, name, gate);
                users.Dispose();
                users = null;

                IntPtr h;
                int rc = RegLoadAppKey(Path.Combine(user.Dir, "NTUSER.DAT"), out h, writable ? KEY_ALL_ACCESS : KEY_READ, 0, 0);
                if (rc != 0) throw new Win32Exception(rc);
                return new HiveRoot(RegistryKey.FromHandle(new SafeRegistryHandle(h, true)), null, name, gate);
            }
            catch
            {
                if (users != null) users.Dispose();
                Monitor.Exit(gate);
                throw;
            }
        }

        public void Dispose()
        {
            Key.Dispose();
            if (parent != null) parent.Dispose();
            if (gate != null) Monitor.Exit(gate);
        }
    }
}
