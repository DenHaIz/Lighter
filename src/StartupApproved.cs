using System;
using Microsoft.Win32;

namespace Lighter
{
    /// <summary>
    /// Enabled/disabled flags kept by Explorer and Task Manager under ...\Explorer\StartupApproved.
    /// Value is REG_BINARY(12): even first byte = enabled, odd = disabled; bytes 4..11 = FILETIME of disabling.
    /// </summary>
    static class StartupApproved
    {
        const string BaseKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";

        public static bool IsEnabled(RegistryHive hive, string sub, string name)
        {
            using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (var k = root.OpenSubKey(BaseKey + sub))
            {
                if (k == null) return true;
                var data = k.GetValue(name) as byte[];
                if (data == null || data.Length == 0) return true;
                return (data[0] & 1) == 0;
            }
        }

        public static void Set(RegistryHive hive, string sub, string name, bool enabled)
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
            using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (var k = root.CreateSubKey(BaseKey + sub))
                k.SetValue(name, data, RegistryValueKind.Binary);
        }

        public static void Remove(RegistryHive hive, string sub, string name)
        {
            using (var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64))
            using (var k = root.OpenSubKey(BaseKey + sub, true))
                if (k != null) k.DeleteValue(name, false);
        }
    }
}
