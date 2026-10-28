using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;

namespace Lighter
{
    /// <summary>
    /// The Lighter mark: a ring that is solid from 12 o'clock clockwise to about 8 o'clock and broken
    /// into short dashes on the way back up — violet on the dark gray tile. Used for the header, the
    /// window/taskbar icon and (via tools\MakeIcon.cs) the .exe icon. Raster versions are supersampled
    /// (<see cref="Render"/>), as in DriveUP.
    /// </summary>
    static class Logo
    {
        public static readonly Color Mark = Color.FromArgb(0x9D, 0x6B, 0xFF);

        const float SolidStart = 270f;   // 12 o'clock (GDI angles grow clockwise from 3 o'clock)
        const float SolidSweep = 236f;   // ends at about 8 o'clock
        const float DashedSweep = 360f - SolidSweep;

        static Icon appIcon;
        /// <summary>Every size Windows asks for at 100-200 % scaling (title bar, taskbar, Explorer), so it never scales one up.</summary>
        public static readonly int[] IconSizes = { 16, 20, 24, 28, 30, 32, 36, 40, 48, 56, 60, 64, 72, 80, 96, 128, 256 };
        static Bitmap header;

        public static Icon AppIcon
        {
            get
            {
                // The window and taskbar never need more than 64 px (200 %): plain frames, nothing to decode.
                if (appIcon == null)
                    appIcon = new Icon(new MemoryStream(BuildIco(IconSizes.Where(x => x <= 64).ToArray(), 256)));
                return appIcon;
            }
        }

        static readonly Color TileTop = Color.FromArgb(0x33, 0x35, 0x3B);
        static readonly Color TileBottom = Color.FromArgb(0x27, 0x28, 0x2C);

        /// <summary>The tiled mark at a whole-pixel spot, from a supersampled picture: smooth even at 32 px.</summary>
        public static void DrawSharp(Graphics g, int x, int y, int size)
        {
            if (header == null || header.Width != size)
            {
                if (header != null) header.Dispose();
                header = Render(size);
            }
            var state = g.Save();
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(header, new Rectangle(x, y, size, size), 0, 0, size, size, GraphicsUnit.Pixel);
            g.Restore(state);
        }

        /// <summary>shown: the size it ends up on screen (drawn larger to be scaled down), for the line weights.</summary>
        public static void Draw(Graphics g, RectangleF r, bool tile, float shown)
        {
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            float size = Math.Min(r.Width, r.Height);
            var box = new RectangleF(r.X + (r.Width - size) / 2, r.Y + (r.Height - size) / 2, size, size);
            if (tile)
            {
                using (var path = Theme.RoundRect(box, size * 0.22f))
                using (var b = new LinearGradientBrush(new PointF(0, box.Top - 1), new PointF(0, box.Bottom + 1), TileTop, TileBottom))
                    g.FillPath(b, path);
            }
            // Everything below is drawn in a 256 x 256 box.
            g.TranslateTransform(box.X, box.Y);
            g.ScaleTransform(size / 256f, size / 256f);

            // Small icons get a heavier ring and fewer dashes so the mark stays readable.
            float look = shown > 0 ? shown : size;
            float ring = look < 24 ? 194 : 170;
            float stroke = look < 24 ? 31 : (look < 40 ? 22 : 16);
            int dashes = look < 24 ? 3 : (look < 32 ? 4 : (look < 48 ? 5 : 7));
            float radius = ring / 2 - stroke / 2;
            var arc = new RectangleF(128 - radius, 128 - radius, radius * 2, radius * 2);
            using (var pen = new Pen(Mark, stroke) { StartCap = LineCap.Flat, EndCap = LineCap.Flat })
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
            g.Restore(state);
        }

        /// <summary>
        /// The tiled mark, size x size: drawn 4 times larger and scaled down with a high-quality filter,
        /// so every edge pixel is a true coverage average (GDI+'s own anti-aliasing steps on thin curves).
        /// </summary>
        public static Bitmap Render(int size)
        {
            int k = size >= 128 ? 2 : 4, big = size * k;
            using (var hi = new Bitmap(big, big, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(hi))
                {
                    g.Clear(Color.Transparent);
                    Draw(g, new RectangleF(0, 0, big, big), true, size);
                }
                var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                using (var wrap = new ImageAttributes())
                {
                    g.Clear(Color.Transparent);
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.CompositingQuality = CompositingQuality.HighQuality;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    wrap.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(hi, new Rectangle(0, 0, size, size), 0, 0, big, big, GraphicsUnit.Pixel, wrap);
                }
                return bmp;
            }
        }

        /// <summary>
        /// Multi-size .ico: 32-bit DIB frames below pngFrom, PNG (much smaller; Windows reads it since Vista) from it up.
        /// </summary>
        public static byte[] BuildIco(int[] sizes, int pngFrom)
        {
            var frames = new byte[sizes.Length][];
            for (int i = 0; i < sizes.Length; i++)
                using (var bmp = Render(sizes[i]))
                    frames[i] = sizes[i] >= pngFrom ? Png(bmp) : Dib(bmp);

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
