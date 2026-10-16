using System;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace Lighter
{
    static class Native
    {
        const uint SC_MANAGER_CONNECT = 0x0001;
        const uint SERVICE_QUERY_CONFIG = 0x0001;
        const uint SERVICE_CHANGE_CONFIG = 0x0002;
        const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
        const uint SERVICE_CONFIG_DELAYED_AUTO_START_INFO = 3;

        public const uint SERVICE_AUTO_START = 2;
        public const uint SERVICE_DISABLED = 4;

        const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

        const uint SHGFI_ICON = 0x100;
        const uint SHGFI_SMALLICON = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        struct SERVICE_DELAYED_AUTO_START_INFO
        {
            public int fDelayedAutostart;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenSCManager(string machineName, string databaseName, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern IntPtr OpenService(IntPtr scManager, string serviceName, uint access);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig(IntPtr service, uint serviceType, uint startType, uint errorControl,
            string binaryPath, string loadOrderGroup, IntPtr tagId, string dependencies,
            string serviceStartName, string password, string displayName);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool ChangeServiceConfig2(IntPtr service, uint infoLevel, ref SERVICE_DELAYED_AUTO_START_INFO info);

        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool CloseServiceHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder exeName, ref int size);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool CloseHandle(IntPtr handle);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        static extern IntPtr SHGetFileInfo(string path, uint fileAttributes, ref SHFILEINFO info, uint size, uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool DestroyIcon(IntPtr icon);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string subIdList);

        /// <summary>Sets a service start type; delayed is applied only when it has a value.</summary>
        public static void SetServiceStart(string serviceName, uint startType, bool? delayed)
        {
            IntPtr scm = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) throw new Win32Exception();
            try
            {
                IntPtr svc = OpenService(scm, serviceName, SERVICE_CHANGE_CONFIG | SERVICE_QUERY_CONFIG);
                if (svc == IntPtr.Zero) throw new Win32Exception();
                try
                {
                    if (!ChangeServiceConfig(svc, SERVICE_NO_CHANGE, startType, SERVICE_NO_CHANGE,
                            null, null, IntPtr.Zero, null, null, null, null))
                        throw new Win32Exception();
                    if (delayed.HasValue)
                    {
                        var info = new SERVICE_DELAYED_AUTO_START_INFO { fDelayedAutostart = delayed.Value ? 1 : 0 };
                        ChangeServiceConfig2(svc, SERVICE_CONFIG_DELAYED_AUTO_START_INFO, ref info);
                    }
                }
                finally { CloseServiceHandle(svc); }
            }
            finally { CloseServiceHandle(scm); }
        }

        public static string GetProcessPath(int pid)
        {
            IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (h == IntPtr.Zero) return "";
            try
            {
                var sb = new StringBuilder(1024);
                int size = sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString(0, size) : "";
            }
            finally { CloseHandle(h); }
        }

        /// <summary>Returns the shell's small icon for a file, or null.</summary>
        public static Bitmap GetSmallIcon(string path)
        {
            var info = new SHFILEINFO();
            IntPtr r = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf(info), SHGFI_ICON | SHGFI_SMALLICON);
            if (r == IntPtr.Zero || info.hIcon == IntPtr.Zero) return null;
            try
            {
                using (var icon = Icon.FromHandle(info.hIcon))
                    return icon.ToBitmap();
            }
            finally { DestroyIcon(info.hIcon); }
        }
    }
}
