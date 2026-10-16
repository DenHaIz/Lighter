using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Lighter
{
    /// <summary>
    /// The Lighter mark: a ring that is solid from 12 o'clock clockwise to about 8 o'clock and
    /// broken into short dashes on the way back up — violet on a dark gray rounded tile.
    /// Used for the header, the window/taskbar icon and (via tools\MakeIcon.cs) the .exe icon.
    /// </summary>
    static class Logo
    {
        public static readonly Color Violet = Color.FromArgb(0x9D, 0x6B, 0xFF);
        public static readonly Color Tile = Color.FromArgb(0x2B, 0x2D, 0x31);

        const float SolidStart = 270f;   // 12 o'clock (GDI angles grow clockwise from 3 o'clock)
        const float SolidSweep = 236f;   // ends at ~8 o'clock
        const float DashedSweep = 360f - SolidSweep;

        static Icon appIcon;

        public static Icon AppIcon
        {
            get
            {
                if (appIcon == null)
                    appIcon = new Icon(new MemoryStream(BuildIco(new[] { 16, 20, 24, 32, 40, 48, 64, 256 })));
                return appIcon;
            }
        }

        public static void Draw(Graphics g, RectangleF r, bool tile)
        {
            var oldSmoothing = g.SmoothingMode;
            var oldOffset = g.PixelOffsetMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float size = Math.Min(r.Width, r.Height);
            var center = new PointF(r.X + r.Width / 2, r.Y + r.Height / 2);
            float ring = size;
            if (tile)
            {
                using (var path = Theme.RoundRect(r, size * 0.22f))
                using (var b = new SolidBrush(Tile))
                    g.FillPath(b, path);
                ring = size * (size < 24 ? 0.76f : 0.66f);
            }

            // Reference proportions: outer diameter 1100, stroke 100. Small sizes get a heavier
            // stroke and fewer dashes so the mark stays readable.
            float stroke = Math.Max(ring * 100f / 1100f, size < 24 ? size * 0.12f : (size < 40 ? size * 0.085f : 0f));
            int dashes = size < 24 ? 3 : (size < 32 ? 4 : (size < 48 ? 5 : 7));
            float radius = ring / 2 - stroke / 2;
            var arc = new RectangleF(center.X - radius, center.Y - radius, radius * 2, radius * 2);

            using (var pen = new Pen(Violet, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
            {
                g.DrawArc(pen, arc, SolidStart, SolidSweep);
                // gaps + dashes fill the open part; a dash is 1.6x as long as a gap
                float gap = DashedSweep / ((dashes + 1) + 1.6f * dashes);
                float dash = gap * 1.6f;
                float a = SolidStart + SolidSweep + gap;
                for (int i = 0; i < dashes; i++)
                {
                    g.DrawArc(pen, arc, a, dash);
                    a += dash + gap;
                }
            }

            g.SmoothingMode = oldSmoothing;
            g.PixelOffsetMode = oldOffset;
        }

        static Bitmap Render(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.Transparent);
                Draw(g, new RectangleF(0, 0, size, size), true);
            }
            return bmp;
        }

        /// <summary>Multi-size .ico: 32-bit DIB frames for small sizes, PNG for 256.</summary>
        public static byte[] BuildIco(int[] sizes)
        {
            var frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = Render(sizes[i]))
                    frames[i] = sizes[i] >= 256 ? Png(bmp) : Dib(bmp);

            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0);
                w.Write((short)1);
                w.Write((short)sizes.Length);
                int offset = 6 + 16 * sizes.Length;
                for (int i = 0; i < sizes.Length; i++)
                {
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                    w.Write((byte)0);
                    w.Write((byte)0);
                    w.Write((short)1);
                    w.Write((short)32);
                    w.Write(frames[i].Length);
                    w.Write(offset);
                    offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
                return ms.ToArray();
            }
        }

        static byte[] Png(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        static byte[] Dib(Bitmap bmp)
        {
            int n = bmp.Width;
            int maskStride = ((n + 31) / 32) * 4;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40);
                w.Write(n);
                w.Write(n * 2);     // XOR + AND masks
                w.Write((short)1);
                w.Write((short)32);
                w.Write(0);
                w.Write(n * n * 4 + maskStride * n);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = n - 1; y >= 0; y--)
                    for (int x = 0; x < n; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A);
                    }
                w.Write(new byte[maskStride * n]); // alpha channel does the masking
                return ms.ToArray();
            }
        }
    }
}
