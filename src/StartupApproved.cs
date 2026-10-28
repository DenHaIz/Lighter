using System;
using System.Collections.Generic;
using Microsoft.Win32;

namespace Lighter
{
    /// <summary>
    /// Enabled/disabled flags kept by Explorer and Task Manager under ...\Explorer\StartupApproved.
    /// Value is REG_BINARY(12): even first byte = enabled, odd = disabled; bytes 4..11 = FILETIME of disabling.
    /// Every method takes the hive root (HKLM or a user's hive, see <see cref="HiveRoot"/>).
    /// </summary>
    static class StartupApproved
    {
        const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        /// <summary>Names of the disabled entries in one subkey (Run, Run32, StartupFolder).</summary>
        public static HashSet<string> ReadDisabled(RegistryKey root, string sub)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var k = root.OpenSubKey(BaseKey + sub))
            {
                if (k == null) return set;
                foreach (string name in k.GetValueNames())
                {
                    var data = k.GetValue(name) as byte[];
                    if (data != null && data.Length > 0 && (data[0] & 1) != 0) set.Add(name);
                }
            }
            return set;
        }

        public static void Set(RegistryKey root, string sub, string name, bool enabled)
        {
            var data = new byte[12];
            if (enabled)
            {
                data[0] = 2;
            }
            else
            {
                data[0] = 3;
                BitConverter.GetBytes(DateTime.Now.ToFileTime()).CopyTo(data, 4);
            }
            using (var k = root.CreateSubKey(BaseKey + sub))
                k.SetValue(name, data, RegistryValueKind.Binary);
        }

        public static void Remove(RegistryKey root, string sub, string name)
        {
            using (var k = root.OpenSubKey(BaseKey + sub, true))
                if (k != null) k.DeleteValue(name, false);
        }
    }
}
