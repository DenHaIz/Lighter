using System;
using System.Collections.Generic;
using System.ServiceProcess;
using Microsoft.Win32;

namespace Lighter
{
    /// <summary>Services with automatic start, plus the ones Lighter has disabled (so they can be turned back on).</summary>
    static class ServiceSource
    {
        // Original start type of services disabled by Lighter: value name = service, data = "auto" | "delayed".
        const string RecordKey = @"SOFTWARE\Lighter\DisabledServices";
        const string ServicesKey = @"SYSTEM\CurrentControlSet\Services\";

        public static List<AutorunItem> Load()
        {
            var list = new List<AutorunItem>();
            var ours = ReadRecords();
            using (var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            {
                foreach (var sc in ServiceController.GetServices())
                {
                    using (sc)
                    {
                        ServiceStartMode mode;
                        try { mode = sc.StartType; }
                        catch { continue; }
                        bool disabledByUs = mode == ServiceStartMode.Disabled && ours.ContainsKey(sc.ServiceName);
                        if (mode != ServiceStartMode.Automatic && !disabledByUs) continue;

                        string image = "", dll = null;
                        bool delayed = false;
                        try
                        {
                            using (var k = hklm.OpenSubKey(ServicesKey + sc.ServiceName))
                            {
                                if (k != null)
                                {
                                    image = Convert.ToString(k.GetValue("ImagePath")) ?? "";
                                    delayed = Convert.ToInt32(k.GetValue("DelayedAutostart", 0)) == 1;
                                    using (var p = k.OpenSubKey("Parameters"))
                                        if (p != null) dll = p.GetValue("ServiceDll") as string;
                                }
                            }
                        }
                        catch (System.Security.SecurityException) { } // some service keys are locked down even for admins
                        if (disabledByUs) delayed = ours[sc.ServiceName] == "delayed";

                        bool? running = null;
                        try { running = sc.Status == ServiceControllerStatus.Running; }
                        catch { }

                        var it = new AutorunItem
                        {
                            Kind = SourceKind.Service,
                            Name = sc.DisplayName,
                            ServiceName = sc.ServiceName,
                            Command = image,
                            Enabled = mode == ServiceStartMode.Automatic,
                            Delayed = delayed,
                            Running = running,
                            CanToggle = true,
                            CanDelete = false,
                        };
                        it.ExePath = PathUtil.ExtractExe(image);
                        it.FileExists = image.Length == 0 || PathUtil.SafeExists(it.ExePath);
                        // svchost-hosted services: the DLL tells who actually made the service.
                        it.Publisher = PathUtil.GetPublisher(PathUtil.SafeExists(dll) ? dll : it.ExePath);
                        it.IsSystem = it.Publisher.IndexOf("Microsoft", StringComparison.OrdinalIgnoreCase) >= 0;
                        list.Add(it);
                    }
                }
            }
            return list;
        }

        static Dictionary<string, string> ReadRecords()
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var k = Registry.LocalMachine.OpenSubKey(RecordKey))
                if (k != null)
                    foreach (string name in k.GetValueNames())
                        result[name] = Convert.ToString(k.GetValue(name));
            return result;
        }

        public static void SetEnabled(AutorunItem it, bool enabled)
        {
            if (!enabled)
            {
                using (var k = Registry.LocalMachine.CreateSubKey(RecordKey))
                    k.SetValue(it.ServiceName, it.Delayed ? "delayed" : "auto");
                Native.SetServiceStart(it.ServiceName, Native.SERVICE_DISABLED, null);
            }
            else
            {
                string orig;
                bool delayed = ReadRecords().TryGetValue(it.ServiceName, out orig) ? orig == "delayed" : it.Delayed;
                Native.SetServiceStart(it.ServiceName, Native.SERVICE_AUTO_START, delayed);
                using (var k = Registry.LocalMachine.OpenSubKey(RecordKey, true))
                    if (k != null) k.DeleteValue(it.ServiceName, false);
                it.Delayed = delayed;
            }
            it.Enabled = enabled;
        }
    }
}
