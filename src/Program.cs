using System;
using System.Drawing;
using System.Windows.Forms;

namespace Lighter
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ThemedDialog.Info(null, L.T("Ошибка", "Error"), e.Exception.Message, true);

            float scale;
            using (var g = Graphics.FromHwnd(IntPtr.Zero)) scale = g.DpiX / 96f;
            L.Init();
            Theme.Init(scale);

            Application.Run(new MainForm());
        }
    }
}
