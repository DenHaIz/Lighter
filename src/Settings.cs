using Microsoft.Win32;

namespace Lighter
{
    /// <summary>Theme and language, kept in HKCU\Software\Lighter (the account Lighter runs under).</summary>
    static class Settings
    {
        const string Key = @"Software\Lighter";

        public static string Theme = Read("Theme");
        public static string Language = Read("Language");

        static string Read(string name)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(Key))
                    return k != null ? k.GetValue(name) as string : null;
            }
            catch { return null; }
        }

        public static void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(Key))
                {
                    if (Theme != null) k.SetValue("Theme", Theme);
                    if (Language != null) k.SetValue("Language", Language);
                }
            }
            catch { }
        }
    }
}
