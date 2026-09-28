using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>Wrapping grid of library thumbnails (the tray at the bottom of the editor and the Library window).</summary>
    public class ThumbGrid : ScrollableControl
    {
        List<LibItem> items = new List<LibItem>();
        readonly Dictionary<string, Bitmap> thumbs = new Dictionary<string, Bitmap>();
        public LibItem Selected;
        public int CellW = 132, CellH = 82;
        int hover = -1;
        static readonly Font LabelFont = new Font("Segoe UI", 8.5f);

        public event Action<LibItem> ItemOpen;

        public ThumbGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Back;
            AutoScroll = true;
            LibraryStore.Changed += OnStoreChanged;
        }

        void OnStoreChanged(object s, EventArgs e)
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Reload);
        }

        protected override void Dispose(bool disposing)
        {
            LibraryStore.Changed -= OnStoreChanged;
            base.Dispose(disposing);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkScrollbars(this);
            Reload();
        }

        float S { get { return Theme.Scale(this); } }

        public void Reload()
        {
            items = LibraryStore.List();
            var keep = new HashSet<string>();
            foreach (var it in items) keep.Add(it.ThumbFile);
            foreach (var k in new List<string>(thumbs.Keys))
                if (!keep.Contains(k)) { thumbs[k].Dispose(); thumbs.Remove(k); }
            if (Selected != null)
            {
                LibItem match = null;
                foreach (var it in items) if (it.Id == Selected.Id) match = it;
                Selected = match;
            }
            UpdateScroll();
            Invalidate();
        }

        public void Select(string id)
        {
            Selected = null;
            foreach (var it in items) if (it.Id == id) Selected = it;
            Invalidate();
            if (Selected != null) EnsureVisible(Selected);
        }

        void EnsureVisible(LibItem it)
        {
            int i = items.IndexOf(it);
            if (i < 0) return;
            var r = CellRect(i);
            if (r.Top < 0) AutoScrollPosition = new Point(0, -(r.Top - AutoScrollPosition.Y));
            else if (r.Bottom > ClientSize.Height) AutoScrollPosition = new Point(0, -(r.Bottom - ClientSize.Height - AutoScrollPosition.Y));
        }

        int Cols { get { int cw = (int)(CellW * S); return Math.Max(1, ClientSize.Width / cw); } }

        void UpdateScroll()
        {
            int cw = (int)(CellW * S), ch = (int)(CellH * S);
            int cols = Math.Max(1, (ClientSize.Width + (VScroll ? SystemInformation.VerticalScrollBarWidth : 0)) / cw);
            int rows = (items.Count + cols - 1) / cols;
            AutoScrollMinSize = new Size(0, rows * ch + 4);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); UpdateScroll(); }

        Rectangle CellRect(int i)
        {
            int cw = (int)(CellW * S), ch = (int)(CellH * S), cols = Cols;
            return new Rectangle((i % cols) * cw, (i / cols) * ch + AutoScrollPosition.Y, cw, ch);
        }

        int HitIndex(Point p)
        {
            for (int i = 0; i < items.Count; i++) if (CellRect(i).Contains(p)) return i;
            return -1;
        }

        Bitmap Thumb(LibItem it)
        {
            Bitmap b;
            if (thumbs.TryGetValue(it.ThumbFile, out b)) return b;
            try
            {
                using (var fs = File.OpenRead(it.ThumbFile))
                using (var im = Image.FromStream(fs)) b = new Bitmap(im);
            }
            catch { b = null; }
            thumbs[it.ThumbFile] = b;
            return b;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            float s = S;
            for (int i = 0; i < items.Count; i++)
            {
                var c = CellRect(i);
                if (!c.IntersectsWith(e.ClipRectangle)) continue;
                var it = items[i];
                bool sel = Selected != null && Selected.Id == it.Id;
                if (sel) using (var b = new SolidBrush(Color.FromArgb(88, 88, 88))) g.FillRectangle(b, c.X + 1, c.Y + 1, c.Width - 2, c.Height - 2);
                else if (i == hover) using (var b = new SolidBrush(Color.FromArgb(52, 52, 52))) g.FillRectangle(b, c.X + 1, c.Y + 1, c.Width - 2, c.Height - 2);
                var box = new Rectangle(c.X + (int)(10 * s), c.Y + (int)(6 * s), c.Width - (int)(20 * s), c.Height - (int)(24 * s));
                var th = Thumb(it);
                Rectangle ir = box;
                if (th != null)
                {
                    float k = Math.Min((float)box.Width / th.Width, (float)box.Height / th.Height);
                    int w = Math.Max(1, (int)(th.Width * k)), h = Math.Max(1, (int)(th.Height * k));
                    ir = new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
                    using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, ir);
                    g.DrawImage(th, ir);
                }
                if (it.IsVideo)
                {
                    // film-strip edges
                    using (var b = new SolidBrush(Color.FromArgb(30, 30, 30)))
                    {
                        g.FillRectangle(b, ir.X - (int)(5 * s), ir.Y, (int)(6 * s), ir.Height);
                        g.FillRectangle(b, ir.Right - (int)(1 * s), ir.Y, (int)(6 * s), ir.Height);
                    }
                    for (int y = ir.Y + 2; y < ir.Bottom - 4; y += (int)(7 * s))
                    {
                        g.FillRectangle(Brushes.White, ir.X - (int)(3 * s), y, (int)(3 * s), (int)(3 * s));
                        g.FillRectangle(Brushes.White, ir.Right + (int)(1 * s), y, (int)(3 * s), (int)(3 * s));
                    }
                }
                string label = it.Ext;
                var lr = new Rectangle(c.X + (int)(6 * s), c.Bottom - (int)(18 * s), c.Width - (int)(12 * s), (int)(16 * s));
                TextRenderer.DrawText(g, label, LabelFont, lr, Color.White, TextFormatFlags.NoPadding | TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
                if (it.IsVideo)
                    TextRenderer.DrawText(g, TimeSpan.FromSeconds(it.DurationSec).ToString(it.DurationSec >= 3600 ? @"hh\:mm\:ss" : @"mm\:ss"), LabelFont, lr, Color.White, TextFormatFlags.NoPadding | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            }
            if (items.Count == 0)
                TextRenderer.DrawText(g, Loc.T("No captures yet — press Print Screen to take one"), LabelFont, ClientRectangle, Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = HitIndex(e.Location);
            if (h != hover) { hover = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int i = HitIndex(e.Location);
            if (i < 0) return;
            if (e.Button == MouseButtons.Left)
            {
                Selected = items[i]; Invalidate();
                if (ItemOpen != null) ItemOpen(items[i]);
            }
            else if (e.Button == MouseButtons.Right)
            {
                Selected = items[i]; Invalidate();
                ShowMenu(items[i], e.Location);
            }
        }

        void ShowMenu(LibItem it, Point p)
        {
            var m = Theme.Menu();
            m.Items.Add(Theme.Item("Open", "open", delegate { if (ItemOpen != null) ItemOpen(it); }));
            if (!it.IsVideo)
                m.Items.Add(Theme.Item("Copy image", "copy", delegate
                {
                    try { var d = Document.LoadProject(it.File); using (var b = d.Render()) Exporter.CopyToClipboard(b); } catch { }
                }));
            else
                m.Items.Add(Theme.Item("Play video", "video", delegate { try { System.Diagnostics.Process.Start(it.File); } catch { } }));
            m.Items.Add(Theme.Item("Show in folder", "folder", delegate { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + it.File + "\""); } catch { } }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Theme.Item("Delete", "trash", delegate { LibraryStore.Delete(it); }));
            m.Show(this, p);
        }
    }
}
