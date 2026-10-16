using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Lighter
{
    class Palette
    {
        public bool IsDark;
        public Color Bg, Surface, SurfaceAlt, Hover, Pressed, Border;
        public Color Text, TextDim, Accent, AccentText, Selected, SelectedInactive;
        public Color Danger, Success, Warning;
    }

    static class Theme
    {
        public static readonly Palette Dark = new Palette
        {
            IsDark = true,
            Bg = Hex(0x1B1C1F), Surface = Hex(0x232428), SurfaceAlt = Hex(0x2B2D31),
            Hover = Hex(0x33353B), Pressed = Hex(0x3D4048), Border = Hex(0x393B41),
            Text = Hex(0xE6E7EA), TextDim = Hex(0x9A9DA6), Accent = Hex(0x4C8DFF), AccentText = Color.White,
            Selected = Hex(0x2C4370), SelectedInactive = Hex(0x34373E),
            Danger = Hex(0xFF6B6B), Success = Hex(0x3DD68C), Warning = Hex(0xF5B83D),
        };

        public static readonly Palette Light = new Palette
        {
            IsDark = false,
            Bg = Hex(0xF3F4F6), Surface = Hex(0xFFFFFF), SurfaceAlt = Hex(0xEDEEF1),
            Hover = Hex(0xE4E6EB), Pressed = Hex(0xD7DAE0), Border = Hex(0xD9DCE2),
            Text = Hex(0x1F2328), TextDim = Hex(0x6B7280), Accent = Hex(0x2563EB), AccentText = Color.White,
            Selected = Hex(0xD6E4FD), SelectedInactive = Hex(0xE6E8EC),
            Danger = Hex(0xD93636), Success = Hex(0x16A34A), Warning = Hex(0xD97706),
        };

        public static Palette Current = Dark;

        public static float Scale = 1f;
        public static Font UiFont, UiBold, TitleFont, Glyph, GlyphSmall;
        public static bool HasGlyphs;

        const string SettingsKey = @"Software\Lighter";

        public static void Init(float dpiScale)
        {
            Scale = dpiScale;
            UiFont = new Font("Segoe UI", 9.5f);
            UiBold = new Font("Segoe UI Semibold", 9.5f);
            TitleFont = new Font("Segoe UI Semibold", 13f);
            Glyph = new Font("Segoe MDL2 Assets", 10f);
            GlyphSmall = new Font("Segoe MDL2 Assets", 8f);
            HasGlyphs = Glyph.Name == "Segoe MDL2 Assets";
            Current = LoadDark() ? Dark : Light;
        }

        public static int S(int v)
        {
            return (int)Math.Round(v * Scale);
        }

        /// <summary>Dark is the default; the choice is remembered in HKCU\Software\Lighter.</summary>
        static bool LoadDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(SettingsKey))
                    return k == null || !"light".Equals(k.GetValue("Theme") as string);
            }
            catch { return true; }
        }

        public static void Save()
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(SettingsKey))
                    k.SetValue("Theme", Current.IsDark ? "dark" : "light");
            }
            catch { }
        }

        static Color Hex(int rgb)
        {
            return Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
        }

        public static GraphicsPath RoundRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        public static void FillRound(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var path = RoundRect(r, radius))
            using (var b = new SolidBrush(color))
                g.FillPath(b, path);
        }

        public static void DrawRound(Graphics g, Color color, RectangleF r, float radius)
        {
            using (var path = RoundRect(r, radius))
            using (var pen = new Pen(color))
                g.DrawPath(pen, path);
        }

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("user32.dll")]
        static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        /// <summary>Dark or light window title bar (Windows 10 20H1+ / 11).</summary>
        public static void ApplyTitleBar(IntPtr hwnd)
        {
            int on = Current.IsDark ? 1 : 0;
            try
            {
                if (DwmSetWindowAttribute(hwnd, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, 4);
                // SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED
                SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);
            }
            catch { }
        }
    }
}
