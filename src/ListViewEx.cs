using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Lighter
{
    /// <summary>
    /// Owner-drawn details ListView: themed header and rows, status dots, file icons,
    /// animated hover/selection, row flash after an action and fade-out on removal.
    /// </summary>
    class ThemedListView : ListView
    {
        public Dictionary<string, Bitmap> Icons;
        public int DotColumn = -1;
        public string EmptyText = "";
        public int SortColumn;
        public bool SortDesc;
        public bool[] Numeric = new bool[0];
        public bool QuietSelection;

        readonly Dictionary<ListViewItem, float> hover = new Dictionary<ListViewItem, float>();
        readonly Dictionary<ListViewItem, float> sel = new Dictionary<ListViewItem, float>();
        readonly Dictionary<ListViewItem, float> flash = new Dictionary<ListViewItem, float>();
        readonly Dictionary<ListViewItem, Color> flashColor = new Dictionary<ListViewItem, Color>();
        readonly Dictionary<ListViewItem, float> fade = new Dictionary<ListViewItem, float>();
        ListViewItem hoverItem;
        bool fitting;

        const TextFormatFlags CellFlags =
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

        public ThemedListView()
        {
            OwnerDraw = true;
            View = View.Details;
            FullRowSelect = true;
            HideSelection = false;
            MultiSelect = true;
            BorderStyle = BorderStyle.None;
            HeaderStyle = ColumnHeaderStyle.Clickable;
            DoubleBuffered = true;
            Font = Theme.UiFont;
            // Row height comes from the small image list; icons are drawn by hand.
            SmallImageList = new ImageList { ImageSize = new Size(1, Theme.S(30)) };
            ListViewItemSorter = new Sorter(this);
        }

        static int S(int v) { return Theme.S(v); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTheme();
            FitLastColumn();
        }

        public void ApplyTheme()
        {
            var p = Theme.Current;
            BackColor = p.Surface;
            ForeColor = p.Text;
            if (!IsHandleCreated) return;
            // Dark scrollbars on Windows 10 1809+; the regular Explorer look otherwise.
            Native.SetWindowTheme(Handle, p.IsDark ? "DarkMode_Explorer" : "Explorer", null);
            IntPtr header = SendMessage(Handle, 0x101F /* LVM_GETHEADER */, IntPtr.Zero, IntPtr.Zero);
            if (header != IntPtr.Zero) InvalidateRect(header, IntPtr.Zero, true);
            Invalidate();
        }

        /// <summary>Stretches the last column so the header has no unpainted gap on the right.</summary>
        public void FitLastColumn()
        {
            if (fitting || Columns.Count == 0 || !IsHandleCreated) return;
            fitting = true;
            try
            {
                int sum = 0;
                for (int i = 0; i < Columns.Count - 1; i++) sum += Columns[i].Width;
                int w = Math.Max(S(140), ClientSize.Width - sum - 1);
                var last = Columns[Columns.Count - 1];
                if (last.Width != w) last.Width = w;
            }
            finally { fitting = false; }
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); FitLastColumn(); }
        protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); FitLastColumn(); }

        protected override void OnColumnWidthChanged(ColumnWidthChangedEventArgs e)
        {
            base.OnColumnWidthChanged(e);
            if (e.ColumnIndex != Columns.Count - 1) FitLastColumn();
        }

        protected override void OnColumnClick(ColumnClickEventArgs e)
        {
            base.OnColumnClick(e);
            Transition.CrossFade(this, () =>
            {
                if (e.Column == SortColumn) SortDesc = !SortDesc;
                else { SortColumn = e.Column; SortDesc = Numeric.Length > e.Column && Numeric[e.Column]; }
                Sort();
            }, 180);
        }

        // ---------- animated states ----------

        void InvalidateRow(ListViewItem it)
        {
            if (it.ListView != this) return;
            try
            {
                var b = it.Bounds;
                Invalidate(new Rectangle(0, b.Y, ClientSize.Width, b.Height));
            }
            catch { }
        }

        void Animate(Dictionary<ListViewItem, float> map, ListViewItem it, string tag, float target, int ms)
        {
            float from;
            if (!map.TryGetValue(it, out from)) from = tag == "s" ? (it.Selected ? 0 : 1) : 0;
            Anim.Run(Tuple.Create(this, it, tag), ms, t =>
            {
                float v = from + (target - from) * t;
                if (t >= 1) map.Remove(it); else map[it] = v;
                InvalidateRow(it);
            });
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            SetHover(GetItemAt(e.X, e.Y));
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            SetHover(null);
        }

        void SetHover(ListViewItem it)
        {
            if (it == hoverItem) return;
            var old = hoverItem;
            hoverItem = it;
            if (old != null) AnimateHover(old, 0, 240);
            if (it != null) AnimateHover(it, 1, 110);
        }

        void AnimateHover(ListViewItem it, float target, int ms)
        {
            float from;
            hover.TryGetValue(it, out from);
            Anim.Run(Tuple.Create(this, it, "h"), ms, t =>
            {
                float v = from + (target - from) * t;
                if (v <= 0.001f) hover.Remove(it); else hover[it] = v;
                InvalidateRow(it);
            });
        }

        protected override void OnItemSelectionChanged(ListViewItemSelectionChangedEventArgs e)
        {
            base.OnItemSelectionChanged(e);
            // Range selections (Shift+click) change many rows at once: animating each would only cost frames.
            if (QuietSelection || sel.Count > 24) return;
            Animate(sel, e.Item, "s", e.IsSelected ? 1 : 0, e.IsSelected ? 140 : 220);
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

        /// <summary>Briefly tints rows to confirm an action on them.</summary>
        public void Flash(IEnumerable<ListViewItem> items, Color color)
        {
            foreach (var row in items)
            {
                var it = row;
                flashColor[it] = color;
                Anim.Run(Tuple.Create(this, it, "f"), 1100, t =>
                {
                    if (t >= 1) { flash.Remove(it); flashColor.Remove(it); }
                    else flash[it] = 1 - t;
                    InvalidateRow(it);
                }, null, t => t * t);
            }
        }

        /// <summary>Fades rows out, then removes them with a short cross-fade so the rest settles smoothly.</summary>
        public void FadeOutAndRemove(List<ListViewItem> items, Action done)
        {
            if (items.Count == 0) { if (done != null) done(); return; }
            Anim.Run(new object(), 300, t =>
            {
                foreach (var it in items) { fade[it] = t; InvalidateRow(it); }
            }, () =>
            {
                Transition.CrossFade(this, () =>
                {
                    BeginUpdate();
                    foreach (var it in items)
                    {
                        fade.Remove(it);
                        hover.Remove(it);
                        sel.Remove(it);
                        if (it == hoverItem) hoverItem = null;
                        if (it.ListView == this) Items.Remove(it);
                    }
                    EndUpdate();
                    FitLastColumn();
                }, 200);
                if (done != null) done();
            });
        }

        /// <summary>Clears per-row animation state before the list is repopulated.</summary>
        public void ResetRowStates()
        {
            hover.Clear();
            sel.Clear();
            flash.Clear();
            flashColor.Clear();
            fade.Clear();
            hoverItem = null;
        }

        // ---------- drawing ----------

        Color RowBack(ListViewItem it)
        {
            var p = Theme.Current;
            Color bg = p.Surface;
            float h;
            if (hover.TryGetValue(it, out h)) bg = Anim.Blend(bg, p.Hover, h * 0.85f);
            float s;
            if (!sel.TryGetValue(it, out s)) s = it.Selected ? 1 : 0;
            if (s > 0) bg = Anim.Blend(bg, Focused ? p.Selected : p.SelectedInactive, s);
            float f;
            if (flash.TryGetValue(it, out f)) bg = Anim.Blend(bg, flashColor[it], f * 0.35f);
            float d;
            if (fade.TryGetValue(it, out d)) bg = Anim.Blend(bg, p.Surface, d);
            return bg;
        }

        protected override void OnDrawColumnHeader(DrawListViewColumnHeaderEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            var r = e.Bounds;
            using (var b = new SolidBrush(p.Surface)) g.FillRectangle(b, r);
            bool sorted = e.ColumnIndex == SortColumn;
            TextRenderer.DrawText(g, e.Header.Text, Theme.UiBold,
                new Rectangle(r.X + S(10), r.Y, r.Width - S(34), r.Height), sorted ? p.Text : p.TextDim, p.Surface, CellFlags);
            if (sorted && Theme.HasGlyphs)
                TextRenderer.DrawText(g, SortDesc ? "" : "", Theme.GlyphSmall,
                    new Rectangle(r.Right - S(24), r.Y, S(16), r.Height), p.Accent, p.Surface,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var pen = new Pen(p.Border))
            {
                g.DrawLine(pen, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
                if (e.ColumnIndex < Columns.Count - 1)
                    g.DrawLine(pen, r.Right - 1, r.Top + S(7), r.Right - 1, r.Bottom - S(7));
            }
        }

        protected override void OnDrawItem(DrawListViewItemEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            var it = e.Item;
            Color bg = RowBack(it);
            var r = new Rectangle(0, e.Bounds.Y, Math.Max(ClientSize.Width, e.Bounds.Right), e.Bounds.Height);
            using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);

            float s;
            if (!sel.TryGetValue(it, out s)) s = it.Selected ? 1 : 0;
            if (s > 0.01f)
            {
                // Accent bar on the left grows with the selection animation.
                int barH = (int)((r.Height - S(12)) * s);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                Theme.FillRound(g, Anim.Blend(bg, p.Accent, s), new RectangleF(S(2), r.Y + (r.Height - barH) / 2f, S(3), barH), S(1));
                g.SmoothingMode = SmoothingMode.None;
            }
        }

        protected override void OnDrawSubItem(DrawListViewSubItemEventArgs e)
        {
            var p = Theme.Current;
            var g = e.Graphics;
            var it = e.Item;
            var info = it.Tag as IRowInfo;
            Rectangle r = e.Bounds;
            if (e.ColumnIndex == 0) r.Width = Columns[0].Width;
            Color bg = RowBack(it);

            Color fg = e.ColumnIndex == 0 ? p.Text : Anim.Blend(p.Text, p.TextDim, 0.35f);
            if (info != null && info.RowKind == RowKind.Dim) fg = p.TextDim;
            else if (info != null && info.RowKind == RowKind.Danger) fg = p.Danger;
            float fd;
            fade.TryGetValue(it, out fd);
            if (fd > 0) fg = Anim.Blend(fg, bg, fd);

            int x = r.X + S(12);
            if (e.ColumnIndex == 0)
            {
                DrawIcon(g, info, new Rectangle(x, r.Y + (r.Height - S(16)) / 2, S(16), S(16)), bg, fd);
                x += S(16) + S(10);
            }
            if (e.ColumnIndex == DotColumn && info != null && info.Dot > 0)
            {
                Color dc = info.Dot == 1 ? p.Success : (info.Dot == 2 ? p.TextDim : p.Danger);
                int d = S(8);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var b = new SolidBrush(Anim.Blend(dc, bg, fd)))
                    g.FillEllipse(b, x, r.Y + (r.Height - d) / 2f, d, d);
                g.SmoothingMode = SmoothingMode.None;
                x += d + S(8);
            }
            TextRenderer.DrawText(g, e.SubItem.Text, Font, new Rectangle(x, r.Y, Math.Max(0, r.Right - x - S(8)), r.Height), fg, bg, CellFlags);
        }

        void DrawIcon(Graphics g, IRowInfo info, Rectangle r, Color bg, float fadeOut)
        {
            var p = Theme.Current;
            Bitmap bmp = null;
            string path = info != null ? info.IconPath : null;
            if (!string.IsNullOrEmpty(path) && Icons != null) Icons.TryGetValue(path, out bmp);
            if (bmp != null)
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                if (fadeOut <= 0)
                {
                    g.DrawImage(bmp, r);
                }
                else
                {
                    using (var ia = new ImageAttributes())
                    {
                        ia.SetColorMatrix(new ColorMatrix { Matrix33 = 1 - fadeOut });
                        g.DrawImage(bmp, r, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                    }
                }
                return;
            }
            if (!Theme.HasGlyphs) return;
            bool missing = info != null && info.RowKind == RowKind.Danger;
            Color c = Anim.Blend(missing ? p.Danger : p.TextDim, bg, fadeOut);
            TextRenderer.DrawText(g, missing ? "" : "", Theme.Glyph, r, c, bg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (Items.Count == 0 && !string.IsNullOrEmpty(EmptyText))
            {
                if (m.Msg == 0x000F /* WM_PAINT */)
                {
                    using (var g = CreateGraphics()) DrawEmptyText(g);
                }
                else if (m.Msg == 0x0318 /* WM_PRINTCLIENT: transition snapshots must include the hint too */)
                {
                    using (var g = Graphics.FromHdc(m.WParam)) DrawEmptyText(g);
                }
            }
        }

        void DrawEmptyText(Graphics g)
        {
            TextRenderer.DrawText(g, EmptyText, Font, new Rectangle(0, S(56), ClientSize.Width, S(60)),
                Theme.Current.TextDim, Theme.Current.Surface,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.NoPrefix | TextFormatFlags.WordBreak);
        }

        class Sorter : IComparer
        {
            readonly ThemedListView lv;
            public Sorter(ThemedListView lv) { this.lv = lv; }

            public int Compare(object a, object b)
            {
                var x = (ListViewItem)a;
                var y = (ListViewItem)b;
                int c = lv.SortColumn;
                string sx = c < x.SubItems.Count ? x.SubItems[c].Text : "";
                string sy = c < y.SubItems.Count ? y.SubItems[c].Text : "";
                int r;
                if (c < lv.Numeric.Length && lv.Numeric[c])
                {
                    double dx, dy;
                    double.TryParse(sx, NumberStyles.Any, CultureInfo.CurrentCulture, out dx);
                    double.TryParse(sy, NumberStyles.Any, CultureInfo.CurrentCulture, out dy);
                    r = dx.CompareTo(dy);
                }
                else
                {
                    r = string.Compare(sx, sy, StringComparison.CurrentCultureIgnoreCase);
                }
                return lv.SortDesc ? -r : r;
            }
        }
    }
}
