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
        bool hoverX;                                   // the pointer is over the delete button of the hovered cell
        readonly ToolTip tip = new ToolTip();
        static readonly Font LabelFont = new Font("Segoe UI", 8.5f);

        public event Action<LibItem> ItemOpen;
        /// <summary>Delete was requested for an item (X button or menu). Without a handler the grid asks and deletes it itself.</summary>
        public event Action<LibItem> ItemRemove;
        /// <summary>Delete was requested for several ticked items at once (right-click > Delete selected).</summary>
        public event Action<List<LibItem>> ItemsRemove;

        bool selecting;                                // multi-select mode: every cell shows a tick box
        readonly HashSet<string> ticked = new HashSet<string>();
        int anchor = -1;                               // last ticked cell, the start of a Shift+click range

        public ThumbGrid()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Back;
            AutoScroll = true;
            LibraryStore.Changed += OnStoreChanged;
            scrollTimer.Tick += (s, e) => ScrollTick();
        }

        void OnStoreChanged(object s, EventArgs e)
        {
            if (IsHandleCreated && !IsDisposed) BeginInvoke((Action)Reload);
        }

        protected override void Dispose(bool disposing)
        {
            LibraryStore.Changed -= OnStoreChanged;
            scrollTimer.Dispose();
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
            hover = -1; hoverX = false;
            var ids = new HashSet<string>(); foreach (var it in items) ids.Add(it.Id);
            ticked.RemoveWhere(id => !ids.Contains(id));
            if (selecting && ticked.Count == 0 && items.Count == 0) selecting = false;
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

        // ------------------------------------------------------------ multi-select

        public bool Selecting { get { return selecting; } }
        public int TickedCount { get { return ticked.Count; } }

        List<LibItem> TickedItems() { return items.FindAll(i => ticked.Contains(i.Id)); }

        void SetSelecting(bool on)
        {
            selecting = on;
            if (!on) { ticked.Clear(); anchor = -1; }
            Invalidate();
        }

        void Tick(int i)
        {
            string id = items[i].Id;
            if (!ticked.Remove(id)) ticked.Add(id);
            anchor = i; Invalidate();
        }

        void TickRange(int i)
        {
            if (anchor < 0 || anchor >= items.Count) { Tick(i); return; }
            for (int k = Math.Min(anchor, i); k <= Math.Max(anchor, i); k++) ticked.Add(items[k].Id);
            Invalidate();
        }

        Rectangle BoxRect(Rectangle c)
        {
            int d = (int)(20 * S);
            return new Rectangle(c.X + (int)(6 * S), c.Y + (int)(6 * S), d, d);
        }

        public static bool ConfirmDeleteMany(IWin32Window owner, int count)
        {
            return MessageBox.Show(owner, Loc.T("Delete the selected items from the library?") + " (" + count + ")", "RXCapture", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        void RemoveTicked()
        {
            var list = TickedItems();
            if (list.Count == 0) return;
            if (!ConfirmDeleteMany(FindForm(), list.Count)) return;      // asked here, so cancelling keeps the selection
            if (ItemsRemove != null) ItemsRemove(list);
            else LibraryStore.DeleteMany(list);
            SetSelecting(false);
        }

        /// <summary>The small X button in the top-right corner of a cell.</summary>
        Rectangle XRect(Rectangle c)
        {
            int d = (int)(20 * S);
            return new Rectangle(c.Right - d - (int)(4 * S), c.Y + (int)(4 * S), d, d);
        }

        public static bool ConfirmDelete(IWin32Window owner)
        {
            return MessageBox.Show(owner, Loc.T("Delete this capture from the library?"), "RXCapture", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes;
        }

        void Remove(LibItem it)
        {
            if (ItemRemove != null) ItemRemove(it);
            else if (ConfirmDelete(FindForm())) LibraryStore.Delete(it);
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
                bool tk = selecting && ticked.Contains(it.Id);
                if (tk) using (var b = new SolidBrush(Color.FromArgb(90, Theme.Accent))) g.FillRectangle(b, c.X + 1, c.Y + 1, c.Width - 2, c.Height - 2);
                else if (sel && !selecting) using (var b = new SolidBrush(Color.FromArgb(88, 88, 88))) g.FillRectangle(b, c.X + 1, c.Y + 1, c.Width - 2, c.Height - 2);
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
                if (selecting)
                {
                    var br = BoxRect(c);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(tk ? Theme.Accent : Color.FromArgb(200, 24, 24, 24))) g.FillRectangle(b, br);
                    using (var pn = new Pen(tk ? Theme.AccentLight : Color.FromArgb(220, 255, 255, 255), 1.6f)) g.DrawRectangle(pn, br);
                    if (tk) using (var pn = new Pen(Color.White, Math.Max(2f, 2f * s)))
                    {
                        g.DrawLines(pn, new[] { new Point(br.X + (int)(4 * s), br.Y + br.Height / 2), new Point(br.X + br.Width * 2 / 5, br.Bottom - (int)(5 * s)), new Point(br.Right - (int)(4 * s), br.Y + (int)(5 * s)) });
                    }
                    g.SmoothingMode = SmoothingMode.Default;
                }
                else if (sel || i == hover)
                {
                    var xr = XRect(c); bool over = i == hover && hoverX;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using (var b = new SolidBrush(over ? Theme.Red : Color.FromArgb(200, 24, 24, 24))) g.FillEllipse(b, xr);
                    using (var pn = new Pen(Color.White, Math.Max(1.5f, 1.6f * s)))
                    {
                        int m = (int)(6 * s);
                        g.DrawLine(pn, xr.Left + m, xr.Top + m, xr.Right - m, xr.Bottom - m);
                        g.DrawLine(pn, xr.Right - m, xr.Top + m, xr.Left + m, xr.Bottom - m);
                    }
                    g.SmoothingMode = SmoothingMode.Default;
                }
            }
            if (marquee)
            {
                var mr = MarqueeRect();
                using (var b = new SolidBrush(Color.FromArgb(50, Theme.AccentLight))) g.FillRectangle(b, mr);
                using (var pn = new Pen(Theme.AccentLight)) g.DrawRectangle(pn, mr.X, mr.Y, Math.Max(0, mr.Width - 1), Math.Max(0, mr.Height - 1));
            }
            if (items.Count == 0)
                TextRenderer.DrawText(g, Loc.T("No captures yet — press Print Screen to take one"), LabelFont, ClientRectangle, Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        // ------------------------------------------------------------ mouse: click, Ctrl / Shift, rubber band

        bool pressing, marquee;
        Point downPt, curPt;                     // client coordinates
        int downScroll;                          // AutoScrollPosition.Y when the button went down
        HashSet<string> baseTicked = new HashSet<string>();
        readonly Timer scrollTimer = new Timer { Interval = 40 };

        Rectangle MarqueeRect()
        {
            var a = new Point(downPt.X, downPt.Y + (AutoScrollPosition.Y - downScroll));   // the start point scrolls with the content
            return Rectangle.FromLTRB(Math.Min(a.X, curPt.X), Math.Min(a.Y, curPt.Y), Math.Max(a.X, curPt.X), Math.Max(a.Y, curPt.Y));
        }

        /// <summary>Ticks every cell the rubber band touches (plus what was already ticked when Ctrl was held).</summary>
        void ApplyMarquee()
        {
            var r = MarqueeRect();
            ticked.Clear();
            foreach (var id in baseTicked) ticked.Add(id);
            for (int i = 0; i < items.Count; i++)
                if (CellRect(i).IntersectsWith(r)) { ticked.Add(items[i].Id); anchor = i; }
            Invalidate();
        }

        /// <summary>Shift+click: select from the anchor to <paramref name="i"/>; with Ctrl as well the range is added to the selection.</summary>
        void SelectRange(int i, bool additive)
        {
            int from = anchor;
            if (from < 0 || from >= items.Count) from = Selected != null ? items.FindIndex(x => x.Id == Selected.Id) : 0;
            if (from < 0) from = 0;
            if (!additive) ticked.Clear();
            for (int k = Math.Min(from, i); k <= Math.Max(from, i); k++) ticked.Add(items[k].Id);
            anchor = from; Invalidate();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            Focus();
            pressing = true; marquee = false; downPt = curPt = e.Location; downScroll = AutoScrollPosition.Y;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            curPt = e.Location;
            if (pressing && !marquee && (Math.Abs(e.X - downPt.X) > 4 || Math.Abs(e.Y - downPt.Y) > 4))
            {
                // dragged far enough: this is a rubber-band selection, not a click
                marquee = true;
                bool ctrl = (ModifierKeys & Keys.Control) != 0;
                baseTicked = ctrl && selecting ? new HashSet<string>(ticked) : new HashSet<string>();
                selecting = true;
                scrollTimer.Start();
            }
            if (marquee) { ApplyMarquee(); return; }
            int h = HitIndex(e.Location);
            bool x = !selecting && h >= 0 && XRect(CellRect(h)).Contains(e.Location);
            if (h != hover || x != hoverX)
            {
                hover = h; hoverX = x; Invalidate();
                tip.SetToolTip(this, x ? Loc.T("Delete") : "");
            }
        }

        // keep scrolling while the rubber band is dragged above or below the tray
        void ScrollTick()
        {
            if (!marquee) { scrollTimer.Stop(); return; }
            var p = PointToClient(Cursor.Position);
            int dy = p.Y < 0 ? -Math.Max(8, -p.Y / 2) : p.Y > ClientSize.Height ? Math.Max(8, (p.Y - ClientSize.Height) / 2) : 0;
            if (dy == 0) return;
            AutoScrollPosition = new Point(0, -AutoScrollPosition.Y + dy);
            curPt = new Point(p.X, Math.Max(-1, Math.Min(ClientSize.Height + 1, p.Y)));
            ApplyMarquee();
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; hoverX = false; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left && marquee)
            {
                pressing = marquee = false; scrollTimer.Stop(); Invalidate();
                return;
            }
            pressing = false;
            int i = HitIndex(e.Location);
            if (e.Button == MouseButtons.Left)
            {
                if (i < 0) { if (selecting) SetSelecting(false); return; }      // click on empty space clears the selection
                bool ctrl = (ModifierKeys & Keys.Control) != 0, shift = (ModifierKeys & Keys.Shift) != 0;
                if (shift) { selecting = true; SelectRange(i, ctrl); return; }
                if (ctrl) { selecting = true; Tick(i); return; }
                if (selecting)
                {
                    // Explorer rules: the tick box toggles, anywhere else on the cell selects just this item (double-click opens it)
                    if (BoxRect(CellRect(i)).Contains(e.Location)) { Tick(i); return; }
                    ticked.Clear(); ticked.Add(items[i].Id); anchor = i; Invalidate();
                    return;
                }
                if (XRect(CellRect(i)).Contains(e.Location)) { Remove(items[i]); return; }
                Selected = items[i]; anchor = i; Invalidate();
                if (ItemOpen != null) ItemOpen(items[i]);
            }
            else if (e.Button == MouseButtons.Right)
            {
                if (i < 0) return;
                Selected = items[i]; Invalidate();
                ShowMenu(items[i], e.Location);
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int i = HitIndex(e.Location);
            if (e.Button == MouseButtons.Left && selecting && i >= 0 && ItemOpen != null) { Selected = items[i]; ItemOpen(items[i]); }
        }

        // keyboard while items are ticked (handled here first so the editor does not treat Delete / Ctrl+A as canvas commands)
        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (selecting && Focused)
            {
                if (keyData == Keys.Escape) { SetSelecting(false); return true; }
                if (keyData == Keys.Delete && ticked.Count > 0) { RemoveTicked(); return true; }
                if (keyData == (Keys.Control | Keys.A)) { foreach (var x in items) ticked.Add(x.Id); Invalidate(); return true; }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void ShowMenu(LibItem it, Point p)
        {
            var m = Theme.Menu();
            if (selecting)
            {
                // multi-select mode: actions for the ticked items
                int n = ticked.Count;
                m.Items.Add(Theme.Item("Select all", "check", delegate { foreach (var x in items) ticked.Add(x.Id); Invalidate(); }));
                m.Items.Add(Theme.Item("Deselect all", "close", delegate { ticked.Clear(); anchor = -1; Invalidate(); }));
                m.Items.Add(new ToolStripSeparator());
                var del = Theme.Item("Delete selected", "trash", delegate { RemoveTicked(); });
                del.Text += " (" + n + ")"; del.Enabled = n > 0;
                m.Items.Add(del);
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Theme.Item("Exit selection mode", "close", delegate { SetSelecting(false); }));
                m.Show(this, p);
                return;
            }
            m.Items.Add(Theme.Item("Open", "open", delegate { if (ItemOpen != null) ItemOpen(it); }));
            if (!it.IsVideo)
                m.Items.Add(Theme.Item("Copy image", "copy", delegate
                {
                    try { var d = Document.LoadProject(it.File); using (var b = d.Render()) Exporter.CopyToClipboard(b); } catch { }
                }));
            else
                m.Items.Add(Theme.Item("Play in default player", "video", delegate { try { System.Diagnostics.Process.Start(it.File); } catch { } }));
            m.Items.Add(Theme.Item("Show in folder", "folder", delegate { try { System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + it.File + "\""); } catch { } }));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Theme.Item("Select multiple", "check", delegate { selecting = true; ticked.Clear(); ticked.Add(it.Id); anchor = items.IndexOf(it); Invalidate(); }));
            m.Items.Add(Theme.Item("Delete", "trash", delegate { Remove(it); }));
            m.Show(this, p);
        }
    }
}
