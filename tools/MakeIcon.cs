using System;
using System.IO;

namespace Lighter
{
    /// <summary>Writes app.ico (the .exe icon) from the same drawing code the app uses at runtime.</summary>
    static class MakeIcon
    {
        static int Main(string[] args)
        {
            string path = args.Length > 0 ? args[0] : "app.ico";
            File.WriteAllBytes(path, Logo.BuildIco(new[] { 16, 20, 24, 32, 40, 48, 64, 256 }));
            Console.WriteLine("Icon written: " + Path.GetFullPath(path));
            return 0;
        }
    }
}
