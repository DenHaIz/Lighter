using System.Globalization;
using Microsoft.Win32;

namespace Lighter
{
    /// <summary>
    /// UI language (Russian / English). Strings are written as pairs right where they are used:
    /// L.T("Включить", "Enable"). The choice is remembered in HKCU\Software\Lighter; the first
    /// run follows the Windows display language.
    /// </summary>
    static class L
    {
        public static bool En;

        const string SettingsKey = @"Software\Lighter";

        public static string T(string ru, string en)
        {
            return En ? en : ru;
        }

        public static void Init()
        {
            string saved = null;
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                    if (k != null) saved = k.GetValue("Language") as string;
            }
            catch { }
            En = saved == "en" || (saved != "ru" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName != "ru");
        }

        public static void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    k.SetValue("Language", En ? "en" : "ru");
            }
            catch { }
        }
    }
}
