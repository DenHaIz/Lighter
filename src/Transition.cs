using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Lighter
{
    /// <summary>
    /// Flicker-free content changes. The "before" picture is put on an overlay first, the change
    /// happens underneath it, then the overlay blends to the "after" picture (optionally sliding).
    /// Pictures are GDI bitmaps and blending uses AlphaBlend, so a frame costs a couple of blits.
    /// A new transition on the same target continues from the frame currently on screen.
    /// </summary>
    static class Transition
    {
        static readonly List<Overlay> active = new List<Overlay>();

        public static void CrossFade(Control target, Action change, int ms = 200, int slideX = 0)
        {
            if (target == null || target.Parent == null || !target.IsHandleCreated || !target.Visible ||
                target.Width < 2 || target.Height < 2)
            {
                change();
                return;
            }

            Overlay ov = null;
            foreach (var o in active.ToArray())
            {
                if (o.Target == target) ov = o;
                else if (target.Contains(o)) o.Finish(); // nested overlay would end up inside the snapshot
            }

            GdiBitmap before = ov != null ? ov.CaptureCurrent() : GdiBitmap.Snap(target);
            if (before == null)
            {
                if (ov != null) ov.Finish();
                change();
                return;
            }
            if (ov == null)
            {
                ov = new Overlay(target);
                active.Add(ov);
                target.Parent.Controls.Add(ov);
                ov.BringToFront();
            }
            ov.Hold(before);   // the screen now shows "before", whatever happens below
            ov.Update();

            change();

            GdiBitmap after = GdiBitmap.Snap(target);
            if (after == null) { ov.Finish(); return; }
            ov.Start(after, slideX, ms);
        }

        class Overlay : Control
        {
            public readonly Control Target;
            GdiBitmap before, after;
            int slide;
            float t;
            bool finished;
            readonly object key = new object();

            public Overlay(Control target)
            {
                Target = target;
                Bounds = target.Bounds;
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
                SetStyle(ControlStyles.Selectable, false);
            }

            public void Hold(GdiBitmap picture)
            {
                Anim.Cancel(key);
                Swap(ref before, picture);
                Swap(ref after, null);
                t = 0;
                Invalidate();
            }

            public void Start(GdiBitmap picture, int slideX, int ms)
            {
                Swap(ref after, picture);
                slide = slideX;
                Anim.Run(key, ms, v => { t = v; Invalidate(); Update(); }, Finish);
            }

            /// <summary>The frame currently on screen, used as the starting point of an interrupting transition.</summary>
            public GdiBitmap CaptureCurrent()
            {
                if (before == null) return null;
                var bmp = GdiBitmap.Create(Width, Height);
                if (bmp == null) return null;
                Compose(bmp.Dc);
                return bmp;
            }

            static void Swap(ref GdiBitmap field, GdiBitmap value)
            {
                if (field != null && field != value) field.Dispose();
                field = value;
            }

            void Compose(IntPtr hdc)
            {
                if (before == null) return;
                GdiBitmap.BitBlt(hdc, 0, 0, Width, Height, before.Dc, 0, 0, GdiBitmap.SRCCOPY);
                if (after == null || t <= 0) return;
                int dx = (int)Math.Round(slide * (1 - t));
                int w = Width - Math.Abs(dx);
                if (w <= 0) return;
                GdiBitmap.Blend(hdc, Math.Max(0, dx), after.Dc, Math.Max(0, -dx), w, Height, (byte)Math.Round(255 * t));
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                IntPtr hdc = e.Graphics.GetHdc();
                try { Compose(hdc); }
                finally { e.Graphics.ReleaseHdc(hdc); }
            }

            public void Finish()
            {
                if (finished) return;
                finished = true;
                Anim.Cancel(key);
                active.Remove(this);
                if (Parent != null) Parent.Controls.Remove(this);
                Dispose();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    Swap(ref before, null);
                    Swap(ref after, null);
                }
                base.Dispose(disposing);
            }
        }
    }

    /// <summary>A screen-compatible GDI bitmap selected into its own memory DC.</summary>
    sealed class GdiBitmap : IDisposable
    {
        public IntPtr Dc { get; private set; }
        IntPtr bitmap, old;

        public const int SRCCOPY = 0x00CC0020;
        const int WM_PRINT = 0x0317;
        const int PRF_CHECKVISIBLE = 0x1, PRF_NONCLIENT = 0x2, PRF_CLIENT = 0x4, PRF_ERASEBKGND = 0x8, PRF_CHILDREN = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        struct BLENDFUNCTION
        {
            public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
        }

        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr hdc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int w, int h);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr obj);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr hdc);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int rop);
        [DllImport("msimg32.dll")] static extern bool AlphaBlend(IntPtr dst, int x, int y, int w, int h, IntPtr src, int sx, int sy, int sw, int sh, BLENDFUNCTION bf);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        GdiBitmap() { }

        public static GdiBitmap Create(int w, int h)
        {
            IntPtr screen = GetDC(IntPtr.Zero);
            try
            {
                var b = new GdiBitmap();
                b.Dc = CreateCompatibleDC(screen);
                b.bitmap = CreateCompatibleBitmap(screen, w, h);
                if (b.Dc == IntPtr.Zero || b.bitmap == IntPtr.Zero)
                {
                    b.Dispose();
                    return null;
                }
                b.old = SelectObject(b.Dc, b.bitmap);
                return b;
            }
            finally { ReleaseDC(IntPtr.Zero, screen); }
        }

        /// <summary>Renders a control and its visible children off-screen (WM_PRINT), without touching the screen.</summary>
        public static GdiBitmap Snap(Control c)
        {
            var b = Create(c.Width, c.Height);
            if (b == null) return null;
            SendMessage(c.Handle, WM_PRINT, b.Dc, (IntPtr)(PRF_CHECKVISIBLE | PRF_NONCLIENT | PRF_CLIENT | PRF_ERASEBKGND | PRF_CHILDREN));
            return b;
        }

        public static void Blend(IntPtr dst, int x, IntPtr src, int sx, int w, int h, byte alpha)
        {
            var bf = new BLENDFUNCTION { BlendOp = 0, BlendFlags = 0, SourceConstantAlpha = alpha, AlphaFormat = 0 };
            AlphaBlend(dst, x, 0, w, h, src, sx, 0, w, h, bf);
        }

        public void Dispose()
        {
            if (Dc != IntPtr.Zero)
            {
                if (old != IntPtr.Zero) SelectObject(Dc, old);
                DeleteDC(Dc);
                Dc = IntPtr.Zero;
            }
            if (bitmap != IntPtr.Zero)
            {
                DeleteObject(bitmap);
                bitmap = IntPtr.Zero;
            }
        }
    }
}
