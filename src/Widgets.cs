using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Lighter
{
    /// <summary>A line of text that fades in whenever it changes (footer status, hints).</summary>
    class StatusText : SkinControl
    {
        readonly Tweened alpha;
        public Color? ForeOverride;
        public TextFormatFlags Flags = TextLeft;
        string previous = "";

        public StatusText()
        {
            alpha = new Tweened(Invalidate, 1f);
        }

        /// <summary>Changes the text: the old one fades out as the new one fades in.</summary>
        public void SetText(string text)
        {
            text = text ?? "";
            if (text == Text) return;
            // Mid-fade the line still mostly shows the old text: keep fading that one out.
            if (alpha.Value >= 0.5f) previous = Text;
            Text = text;
            alpha.Set(0);
            alpha.To(1, 320);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Color pb = ParentBg;
            e.Graphics.Clear(pb);
            Color fg = ForeOverride ?? Theme.Current.TextDim;
            float a = alpha.Value;
            // Out, then in: the two texts never overlap.
            if (a < 0.5f && previous.Length > 0)
                TextDraw.DrawText(e.Graphics, previous, Font, ClientRectangle, Anim.Blend(pb, fg, 1 - a * 2), Flags);
            if (a > 0.5f)
                TextDraw.DrawText(e.Graphics, Text, Font, ClientRectangle, Anim.Blend(pb, fg, (a - 0.5f) * 2), Flags);
        }
    }

    /// <summary>Animated check box drawing shared by the category list and the lists.</summary>
    static class CheckMark
    {
        /// <summary>value 0..1 = how checked (animates the tick being drawn); partial = some children checked.</summary>
        public static void Draw(Graphics g, RectangleF r, float value, bool partial, float hover, bool enabled, Color bg)
        {
            var p = Theme.Current;
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float on = partial ? 1f : value;
            Color border = Anim.Blend(Anim.Blend(Anim.Blend(p.Border, p.TextDim, 0.45f), p.TextDim, hover), p.Accent, on);
            Color fill = Anim.Blend(Anim.Blend(p.Surface, p.Hover, hover * 0.5f), p.Accent, on);
            if (!enabled)
            {
                border = Anim.Blend(border, bg, 0.55f);
                fill = Anim.Blend(fill, bg, 0.55f);
            }
            Theme.FillRound(g, fill, r, Theme.S(4));
            Theme.DrawRound(g, border, r, Theme.S(4));
            using (var pen = new Pen(enabled ? Color.White : Anim.Blend(Color.White, bg, 0.4f), Math.Max(1.6f, r.Width * 0.13f))
                   { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            {
                if (partial)
                {
                    float y = r.Y + r.Height / 2;
                    g.DrawLine(pen, r.X + r.Width * 0.28f, y, r.Right - r.Width * 0.28f, y);
                }
                else if (value > 0.01f)
                {
                    // The tick is drawn on as the box fills: first stroke, then the long one.
                    var a = new PointF(r.X + r.Width * 0.24f, r.Y + r.Height * 0.53f);
                    var b = new PointF(r.X + r.Width * 0.43f, r.Y + r.Height * 0.71f);
                    var c = new PointF(r.X + r.Width * 0.77f, r.Y + r.Height * 0.32f);
                    float t1 = Math.Min(1, value / 0.4f), t2 = Math.Max(0, (value - 0.4f) / 0.6f);
                    var b1 = new PointF(Anim.Lerp(a.X, b.X, t1), Anim.Lerp(a.Y, b.Y, t1));
                    if (t2 <= 0) g.DrawLine(pen, a, b1);
                    else g.DrawLines(pen, new[] { a, b, new PointF(Anim.Lerp(b.X, c.X, t2), Anim.Lerp(b.Y, c.Y, t2)) });
                }
            }
            g.SmoothingMode = old;
        }
    }

    /// <summary>
    /// Icon glyphs drawn as shapes: centred on their actual ink (a glyph's text box is not centred
    /// on its drawing), scaled to a given size and made heavier by an outline of the same colour.
    /// Segoe MDL2 has a single thin weight, which looked faint inside the category tiles.
    /// </summary>
    static class GlyphShape
    {
        // Shapes centred on (0,0), keyed by glyph and size.
        static readonly Dictionary<string, GraphicsPath> cache = new Dictionary<string, GraphicsPath>();

        static GraphicsPath Get(string glyph, float ink)
        {
            string key = glyph + "|" + Math.Round(ink * 4);
            GraphicsPath path;
            if (cache.TryGetValue(key, out path)) return path;
            path = new GraphicsPath(FillMode.Winding);
            using (var family = new FontFamily("Segoe MDL2 Assets"))
                path.AddString(glyph, family, 0, 100f, PointF.Empty, StringFormat.GenericTypographic);
            var b = path.GetBounds();
            if (b.Width > 0 && b.Height > 0)
                using (var m = new Matrix())
                {
                    float k = ink / Math.Max(b.Width, b.Height);
                    m.Scale(k, k);
                    m.Translate(-(b.X + b.Width / 2), -(b.Y + b.Height / 2));
                    path.Transform(m);
                }
            cache[key] = path;
            return path;
        }

        /// <summary>Draws the glyph so its drawing fits a square of size ink centred on c; weight thickens the lines.</summary>
        public static void Draw(Graphics g, string glyph, PointF c, float ink, Color color, float weight)
        {
            if (!Theme.HasGlyphs || string.IsNullOrEmpty(glyph)) return;
            var path = Get(glyph, ink);
            var state = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TranslateTransform(c.X, c.Y);
            using (var b = new SolidBrush(color)) g.FillPath(b, path);
            if (weight > 0)
                using (var pen = new Pen(color, weight) { LineJoin = LineJoin.Round })
                    g.DrawPath(pen, path);
            g.Restore(state);
        }
    }

    /// <summary>
    /// Placeholder bars with a soft light band sweeping across them, shown where a list is still
    /// loading. All placeholders share one clock, so bands on the same screen move in step.
    /// </summary>
    static class Shimmer
    {
        static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        const int Period = 1500;

        public static Brush Make(int width, Color bg)
        {
            var p = Theme.Current;
            Color bar = Anim.Blend(bg, p.Pressed, p.IsDark ? 0.5f : 0.55f);
            Color hi = Anim.Blend(bar, p.IsDark ? p.TextDim : Color.White, p.IsDark ? 0.16f : 0.75f);
            float w = Math.Max(1, width);
            float band = Math.Max(Theme.S(140), w * 0.3f);
            float left = -2 * band, span = w + 4 * band;
            float phase = Anim.EaseInOut((clock.ElapsedMilliseconds % Period) / (float)Period);
            float c = -band + phase * (w + 2 * band);
            var brush = new LinearGradientBrush(new RectangleF(left, 0, span, 1), bar, bar, 0f);
            brush.InterpolationColors = new ColorBlend
            {
                Colors = new[] { bar, bar, hi, bar, bar },
                Positions = new[] { 0f, (c - band / 2 - left) / span, (c - left) / span, (c + band / 2 - left) / span, 1f },
            };
            return brush;
        }

        public static void Bar(Graphics g, Brush b, RectangleF r)
        {
            using (var path = Theme.RoundRect(r, Math.Min(r.Height, r.Width) / 2))
                g.FillPath(b, path);
        }

        public static void Tile(Graphics g, Brush b, RectangleF r, float radius)
        {
            using (var path = Theme.RoundRect(r, radius))
                g.FillPath(b, path);
        }

        /// <summary>A stable pseudo-random 0..1 per (row, column), so placeholder bars vary in length.</summary>
        public static float Vary(int row, int col)
        {
            unchecked
            {
                uint h = (uint)(row * 73856093) ^ (uint)(col * 19349663 + 7);
                h ^= h >> 13;
                h *= 0x5bd1e995;
                h ^= h >> 15;
                return (h % 1000) / 1000f;
            }
        }

        /// <summary>Repaints the control every frame while loading() holds.</summary>
        public static void Run(Control c, object key, Func<bool> loading)
        {
            Anim.Frame(key, () =>
            {
                if (c.IsDisposed || !loading()) return false;
                if (c.Visible) c.Invalidate();
                return true;
            });
        }
    }

}
