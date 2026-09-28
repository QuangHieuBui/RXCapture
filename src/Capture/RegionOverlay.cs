using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ShotCraft
{
    public enum OverlayMode { AllInOne, Region, Window, Freehand, Fixed }
    public enum CaptureAction { Image, Video, Scroll }

    public class OverlayResult
    {
        public Rectangle Rect;            // screen coordinates
        public CaptureAction Action;
        public List<Point> Freeform;      // screen coordinates, null unless freehand
        public WinInfo Window;
    }

    /// <summary>
    /// Full-desktop overlay showing a frozen, dimmed screenshot. The user picks a window/object, drags a region,
    /// draws a freehand shape or places a fixed-size box. Works across multiple monitors and DPI settings.
    /// </summary>
    public class RegionOverlay : Form
    {
        enum St { Idle, Dragging, Adjusting, Moving, Resizing, Freehand }

        class Btn { public Rectangle R; public string Id; public string Label; public string Icon; }

        readonly Bitmap frozen;
        readonly Bitmap dimmed;
        readonly Rectangle vs;
        readonly List<WinInfo> tops;
        readonly OverlayMode mode;
        readonly CaptureAction? forced;
        readonly AppSettings cfg = AppSettings.Current;

        St st = St.Idle;
        Point mouse, downPt;
        bool moved;
        Rectangle sel = Rectangle.Empty;
        Rectangle selAtDown;
        int handle = -1;
        List<Rectangle> chain = new List<Rectangle>();
        int level;
        WinInfo hoverWin;
        readonly List<Point> free = new List<Point>();
        readonly List<Btn> buttons = new List<Btn>();
        Rectangle lastDirty = Rectangle.Empty;
        Rectangle hintRect;
        Rectangle hoverRect { get { return (chain.Count > 0 && mode != OverlayMode.Region && mode != OverlayMode.Freehand && mode != OverlayMode.Fixed) ? chain[Math.Max(0, Math.Min(level, chain.Count - 1))] : Rectangle.Empty; } }
        public OverlayResult Result;

        static readonly Color Hi = Color.FromArgb(0, 168, 255);
        readonly Font small = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Pixel);
        readonly Font smallB = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);

        public RegionOverlay(Bitmap frozen, Rectangle vs, List<WinInfo> tops, OverlayMode mode, CaptureAction? forced)
        {
            this.frozen = frozen; this.vs = vs; this.tops = tops; this.mode = mode; this.forced = forced;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            KeyPreview = true;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Bounds = vs;
            Cursor = Cursors.Cross;
            BackColor = Color.Black;

            dimmed = new Bitmap(frozen.Width, frozen.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dimmed))
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix(new float[][] {
                    new float[] {0.5f,0,0,0,0}, new float[] {0,0.5f,0,0,0}, new float[] {0,0,0.5f,0,0}, new float[] {0,0,0,1,0}, new float[] {0,0,0,0,1} }));
                g.DrawImage(frozen, new Rectangle(0, 0, frozen.Width, frozen.Height), 0, 0, frozen.Width, frozen.Height, GraphicsUnit.Pixel, ia);
            }

            var mon = Screen.FromPoint(Cursor.Position).Bounds;
            int hw = 640, hh = 30;
            hintRect = new Rectangle(mon.X - vs.X + (mon.Width - hw) / 2, mon.Y - vs.Y + 14, hw, hh);
        }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_TOOLWINDOW; return cp; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED) return;   // we always want exact physical bounds
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, vs.X, vs.Y, vs.Width, vs.Height, Native.SWP_SHOWWINDOW);
            Activate();
            Native.SetForegroundWindow(Handle);
            Focus();
            var p = PointToClient(Cursor.Position);
            mouse = p;
            UpdateHover();
            Redraw();
        }

        public static OverlayResult Pick(Bitmap frozen, Rectangle vs, List<WinInfo> tops, OverlayMode mode, CaptureAction? forced)
        {
            using (var f = new RegionOverlay(frozen, vs, tops, mode, forced))
            {
                f.ShowDialog();
                return f.Result;
            }
        }

        // ---------------------------------------------------------------- state helpers

        Point ToScreen(Point p) { return new Point(p.X + vs.X, p.Y + vs.Y); }

        Rectangle Clamp(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, ClientSize.Width, ClientSize.Height));
            return r;
        }

        static Rectangle Norm(Point a, Point b)
        {
            return Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));
        }

        void UpdateHover()
        {
            if (mode == OverlayMode.Fixed)
            {
                int w = Math.Min(cfg.FixedWidth, ClientSize.Width), h = Math.Min(cfg.FixedHeight, ClientSize.Height);
                var r = new Rectangle(mouse.X - w / 2, mouse.Y - h / 2, w, h);
                r.X = Math.Max(0, Math.Min(r.X, ClientSize.Width - w));
                r.Y = Math.Max(0, Math.Min(r.Y, ClientSize.Height - h));
                sel = r;
                return;
            }
            if (mode == OverlayMode.Region || mode == OverlayMode.Freehand) { chain.Clear(); return; }
            WinInfo top;
            var ch = WindowFinder.Chain(ToScreen(mouse), tops, out top);
            if (mode == OverlayMode.Window && ch.Count > 1) ch = new List<Rectangle> { ch[0] };
            for (int i = 0; i < ch.Count; i++)   // to client coordinates
                ch[i] = new Rectangle(ch[i].X - vs.X, ch[i].Y - vs.Y, ch[i].Width, ch[i].Height);
            bool same = ch.Count == chain.Count;
            if (same) for (int i = 0; i < ch.Count; i++) if (ch[i] != chain[i]) { same = false; break; }
            if (!same) { chain = ch; level = mode == OverlayMode.Window ? 0 : ch.Count - 1; }
            hoverWin = top;
        }

        void Finish(CaptureAction action)
        {
            Rectangle r = st == St.Freehand || free.Count > 2 && mode == OverlayMode.Freehand ? BoundsOf(free) : sel;
            r = Clamp(r);
            if (r.Width < 2 || r.Height < 2) return;
            Result = new OverlayResult
            {
                Rect = new Rectangle(r.X + vs.X, r.Y + vs.Y, r.Width, r.Height),
                Action = forced.HasValue ? forced.Value : action,
                Window = hoverWin
            };
            if (mode == OverlayMode.Freehand && free.Count > 2)
            {
                Result.Freeform = new List<Point>();
                foreach (var p in free) Result.Freeform.Add(ToScreen(p));
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        static Rectangle BoundsOf(List<Point> pts)
        {
            if (pts.Count == 0) return Rectangle.Empty;
            int x1 = int.MaxValue, y1 = int.MaxValue, x2 = int.MinValue, y2 = int.MinValue;
            foreach (var p in pts) { x1 = Math.Min(x1, p.X); y1 = Math.Min(y1, p.Y); x2 = Math.Max(x2, p.X); y2 = Math.Max(y2, p.Y); }
            return Rectangle.FromLTRB(x1, y1, x2 + 1, y2 + 1);
        }

        void EndSelection()
        {
            if (sel.Width < 3 || sel.Height < 3) { sel = Rectangle.Empty; st = St.Idle; return; }
            if (mode == OverlayMode.Window || mode == OverlayMode.Fixed || cfg.CaptureImmediately || forced.HasValue)
            {
                Finish(CaptureAction.Image);
                return;
            }
            st = St.Adjusting;
        }

        // ---------------------------------------------------------------- input

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            mouse = e.Location;
            switch (st)
            {
                case St.Idle:
                    UpdateHover();
                    break;
                case St.Dragging:
                    if (!moved && (Math.Abs(mouse.X - downPt.X) > 3 || Math.Abs(mouse.Y - downPt.Y) > 3)) moved = true;
                    if (moved)
                    {
                        var b = mouse;
                        if ((ModifierKeys & Keys.Shift) != 0)
                        {
                            int d = Math.Max(Math.Abs(b.X - downPt.X), Math.Abs(b.Y - downPt.Y));
                            b = new Point(downPt.X + Math.Sign(b.X - downPt.X) * d, downPt.Y + Math.Sign(b.Y - downPt.Y) * d);
                        }
                        sel = Clamp(Norm(downPt, b));
                    }
                    break;
                case St.Freehand:
                    if (free.Count == 0 || free[free.Count - 1] != mouse) free.Add(mouse);
                    break;
                case St.Moving:
                    {
                        int dx = mouse.X - downPt.X, dy = mouse.Y - downPt.Y;
                        var r = selAtDown; r.Offset(dx, dy);
                        r.X = Math.Max(0, Math.Min(r.X, ClientSize.Width - r.Width));
                        r.Y = Math.Max(0, Math.Min(r.Y, ClientSize.Height - r.Height));
                        sel = r;
                    }
                    break;
                case St.Resizing:
                    sel = ResizeRect(selAtDown, handle, mouse);
                    break;
                case St.Adjusting:
                    Cursor = CursorFor(HitHandle(mouse));
                    break;
            }
            Redraw();
        }

        Rectangle ResizeRect(Rectangle r, int h, Point p)
        {
            int l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
            // handles: 0 TL,1 T,2 TR,3 R,4 BR,5 B,6 BL,7 L
            if (h == 0 || h == 6 || h == 7) l = p.X;
            if (h == 2 || h == 3 || h == 4) rt = p.X;
            if (h == 0 || h == 1 || h == 2) t = p.Y;
            if (h == 4 || h == 5 || h == 6) b = p.Y;
            return Clamp(Rectangle.FromLTRB(Math.Min(l, rt), Math.Min(t, b), Math.Max(l, rt), Math.Max(t, b)));
        }

        Point[] HandlePoints()
        {
            var r = sel;
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            return new[] {
                new Point(r.Left, r.Top), new Point(cx, r.Top), new Point(r.Right, r.Top), new Point(r.Right, cy),
                new Point(r.Right, r.Bottom), new Point(cx, r.Bottom), new Point(r.Left, r.Bottom), new Point(r.Left, cy) };
        }

        int HitHandle(Point p)
        {
            if (st != St.Adjusting) return -1;
            var pts = HandlePoints();
            for (int i = 0; i < pts.Length; i++)
                if (Math.Abs(p.X - pts[i].X) <= 7 && Math.Abs(p.Y - pts[i].Y) <= 7) return i;
            if (sel.Contains(p)) return 8;
            return -1;
        }

        static Cursor CursorFor(int h)
        {
            switch (h)
            {
                case 0: case 4: return Cursors.SizeNWSE;
                case 2: case 6: return Cursors.SizeNESW;
                case 1: case 5: return Cursors.SizeNS;
                case 3: case 7: return Cursors.SizeWE;
                case 8: return Cursors.SizeAll;
                default: return Cursors.Cross;
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            mouse = e.Location;
            if (e.Button == MouseButtons.Right || e.Button == MouseButtons.Middle)
            {
                if (st == St.Adjusting || sel != Rectangle.Empty && mode != OverlayMode.Fixed) { sel = Rectangle.Empty; st = St.Idle; free.Clear(); Cursor = Cursors.Cross; UpdateHover(); Redraw(); }
                else { Close(); }
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            if (st == St.Adjusting)
            {
                foreach (var b in buttons)
                    if (b.R.Contains(mouse))
                    {
                        if (b.Id == "cancel") Close();
                        else if (b.Id == "image") Finish(CaptureAction.Image);
                        else if (b.Id == "video") Finish(CaptureAction.Video);
                        else if (b.Id == "scroll") Finish(CaptureAction.Scroll);
                        return;
                    }
                int h = HitHandle(mouse);
                if (h >= 0)
                {
                    downPt = mouse; selAtDown = sel; handle = h;
                    st = h == 8 ? St.Moving : St.Resizing;
                    return;
                }
                sel = Rectangle.Empty; st = St.Idle;
            }

            downPt = mouse; moved = false;
            if (mode == OverlayMode.Fixed) { UpdateHover(); EndSelection(); return; }
            if (mode == OverlayMode.Freehand) { free.Clear(); free.Add(mouse); st = St.Freehand; return; }
            st = St.Dragging;
            sel = Rectangle.Empty;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            switch (st)
            {
                case St.Dragging:
                    if (moved) { EndSelection(); }
                    else
                    {
                        var hr = hoverRect;
                        if (!hr.IsEmpty) { sel = Clamp(hr); EndSelection(); }
                        else st = St.Idle;
                    }
                    break;
                case St.Freehand:
                    if (free.Count > 4) { st = St.Idle; Finish(CaptureAction.Image); }
                    else { free.Clear(); st = St.Idle; }
                    break;
                case St.Moving:
                case St.Resizing:
                    st = St.Adjusting;
                    break;
            }
            Redraw();
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (st == St.Adjusting && sel.Contains(e.Location)) Finish(CaptureAction.Image);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (st == St.Idle && chain.Count > 1)
            {
                level = Math.Max(0, Math.Min(chain.Count - 1, level + (e.Delta > 0 ? -1 : 1)));
                Redraw();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    if (st == St.Adjusting) { sel = Rectangle.Empty; st = St.Idle; UpdateHover(); Redraw(); }
                    else Close();
                    break;
                case Keys.Enter:
                case Keys.Space:
                    if (st == St.Adjusting) Finish(CaptureAction.Image);
                    else if (st == St.Idle && !hoverRect.IsEmpty) { sel = Clamp(hoverRect); EndSelection(); if (st == St.Adjusting) Finish(CaptureAction.Image); }
                    else if (mode == OverlayMode.Fixed) Finish(CaptureAction.Image);
                    break;
                case Keys.C:
                    try
                    {
                        var c = frozen.GetPixel(Math.Max(0, Math.Min(frozen.Width - 1, mouse.X)), Math.Max(0, Math.Min(frozen.Height - 1, mouse.Y)));
                        Clipboard.SetText(string.Format("#{0:X2}{1:X2}{2:X2}", c.R, c.G, c.B));
                    }
                    catch { }
                    break;
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                    {
                        int step = e.Shift ? 10 : 1;
                        int dx = e.KeyCode == Keys.Left ? -step : e.KeyCode == Keys.Right ? step : 0;
                        int dy = e.KeyCode == Keys.Up ? -step : e.KeyCode == Keys.Down ? step : 0;
                        if (st == St.Adjusting)
                        {
                            var r = sel;
                            if (e.Control) { r.Width = Math.Max(2, r.Width + dx); r.Height = Math.Max(2, r.Height + dy); }
                            else r.Offset(dx, dy);
                            sel = Clamp(r);
                        }
                        else Cursor.Position = new Point(Cursor.Position.X + dx, Cursor.Position.Y + dy);
                        e.Handled = true;
                        Redraw();
                    }
                    break;
            }
        }

        // ---------------------------------------------------------------- painting

        Rectangle BrightRect()
        {
            if (st == St.Idle) return mode == OverlayMode.Fixed ? sel : hoverRect;
            return sel;
        }

        Rectangle LoupeRect()
        {
            if (!cfg.ShowMagnifier || st == St.Adjusting) return Rectangle.Empty;
            const int w = 150, h = 184;
            int x = mouse.X + 22, y = mouse.Y + 22;
            if (x + w > ClientSize.Width) x = mouse.X - 22 - w;
            if (y + h > ClientSize.Height) y = mouse.Y - 22 - h;
            return new Rectangle(x, y, w, h);
        }

        Rectangle ToolbarRect()
        {
            if (st != St.Adjusting && st != St.Moving && st != St.Resizing) return Rectangle.Empty;
            int w = 4 * 70 + 10, h = 66;
            int x = sel.X + sel.Width / 2 - w / 2, y = sel.Bottom + 14;
            if (y + h > ClientSize.Height) y = sel.Y - h - 14;
            if (y < 0) y = Math.Max(0, sel.Bottom - h - 14);
            x = Math.Max(4, Math.Min(x, ClientSize.Width - w - 4));
            return new Rectangle(x, y, w, h);
        }

        Rectangle Dirty()
        {
            Rectangle d = hintRect;
            var b = BrightRect();
            if (!b.IsEmpty) d = Rectangle.Union(d, Rectangle.Inflate(b, 70, 40));
            var t = ToolbarRect(); if (!t.IsEmpty) d = Rectangle.Union(d, Rectangle.Inflate(t, 4, 4));
            var l = LoupeRect(); if (!l.IsEmpty) d = Rectangle.Union(d, Rectangle.Inflate(l, 6, 6));
            if (free.Count > 0) d = Rectangle.Union(d, Rectangle.Inflate(BoundsOf(free), 6, 6));
            return d;
        }

        void Redraw()
        {
            var cur = Dirty();
            Invalidate(lastDirty.IsEmpty ? cur : Rectangle.Union(lastDirty, cur));
            lastDirty = cur;
        }

        void Chip(Graphics g, string text, int x, int y, Font f, Color back)
        {
            var sz = g.MeasureString(text, f);
            var r = new Rectangle(x, y, (int)sz.Width + 10, (int)sz.Height + 4);
            r.X = Math.Max(2, Math.Min(r.X, ClientSize.Width - r.Width - 2));
            r.Y = Math.Max(2, Math.Min(r.Y, ClientSize.Height - r.Height - 2));
            using (var p = RoundRect(r, 4))
            using (var b = new SolidBrush(back))
                g.FillPath(b, p);
            g.DrawString(text, f, Brushes.White, r.X + 5, r.Y + 2);
        }

        static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var clip = e.ClipRectangle;
            if (clip.IsEmpty) return;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.DrawImage(dimmed, clip, clip, GraphicsUnit.Pixel);

            var bright = BrightRect();
            if (mode == OverlayMode.Freehand && free.Count > 2)
            {
                using (var path = new GraphicsPath())
                {
                    path.AddPolygon(free.ToArray());
                    g.SetClip(path, CombineMode.Intersect);
                    g.SetClip(clip, CombineMode.Intersect);
                    g.DrawImage(frozen, clip, clip, GraphicsUnit.Pixel);
                    g.ResetClip();
                }
            }
            else if (!bright.IsEmpty)
            {
                var r = Rectangle.Intersect(bright, clip);
                if (!r.IsEmpty) g.DrawImage(frozen, r, r, GraphicsUnit.Pixel);
            }

            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

            if (mode == OverlayMode.Freehand && free.Count > 1)
                using (var p = new Pen(Hi, 2f) { LineJoin = LineJoin.Round })
                    g.DrawLines(p, free.ToArray());

            if (!bright.IsEmpty)
            {
                using (var p = new Pen(Hi, 2f)) g.DrawRectangle(p, bright.X - 1, bright.Y - 1, bright.Width + 1, bright.Height + 1);
                string label;
                if (st == St.Idle && hoverWin != null && mode != OverlayMode.Fixed)
                    label = Trunc(hoverWin.Title, 40) + (hoverWin.Title.Length > 0 ? "   " : "") + bright.Width + " × " + bright.Height;
                else label = bright.Width + " × " + bright.Height;
                int ly = bright.Y - 26 >= 2 ? bright.Y - 26 : bright.Y + 4;
                Chip(g, label, bright.X, ly, smallB, Color.FromArgb(220, 20, 24, 30));
            }

            if (st == St.Adjusting || st == St.Moving || st == St.Resizing)
            {
                foreach (var hp in HandlePoints())
                {
                    var hr = new Rectangle(hp.X - 4, hp.Y - 4, 8, 8);
                    g.FillRectangle(Brushes.White, hr);
                    using (var p = new Pen(Hi, 1.5f)) g.DrawRectangle(p, hr);
                }
                DrawToolbar(g);
            }

            var lr = LoupeRect();
            if (!lr.IsEmpty && lr.IntersectsWith(clip)) DrawLoupe(g, lr);

            if (hintRect.IntersectsWith(clip)) DrawHint(g);
        }

        static string Trunc(string s, int n) { return s.Length <= n ? s : s.Substring(0, n - 1) + "…"; }

        void DrawHint(Graphics g)
        {
            string t;
            switch (mode)
            {
                case OverlayMode.Window: t = Loc.T("Click a window to capture  •  Esc = cancel"); break;
                case OverlayMode.Freehand: t = Loc.T("Draw a freehand shape  •  Esc = cancel"); break;
                case OverlayMode.Fixed: t = Loc.T("Move the box and click to capture  •  Esc = cancel"); break;
                case OverlayMode.Region: t = Loc.T("Drag to select a region  •  Enter = capture  •  Esc = cancel"); break;
                default: t = Loc.T("Click a window/object or drag a region  •  Wheel = parent/child  •  C = copy colour  •  Esc = cancel"); break;
            }
            var sz = g.MeasureString(t, small);
            var r = new Rectangle(hintRect.X + (hintRect.Width - (int)sz.Width - 20) / 2, hintRect.Y, (int)sz.Width + 20, hintRect.Height);
            using (var p = RoundRect(r, 8)) using (var b = new SolidBrush(Color.FromArgb(200, 17, 24, 39))) g.FillPath(b, p);
            g.DrawString(t, small, Brushes.White, r.X + 10, r.Y + (r.Height - sz.Height) / 2);
        }

        void DrawToolbar(Graphics g)
        {
            buttons.Clear();
            var tr = ToolbarRect();
            if (tr.IsEmpty) return;
            using (var p = RoundRect(tr, 10))
            {
                using (var b = new SolidBrush(Color.FromArgb(235, 24, 30, 40))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(90, 255, 255, 255))) g.DrawPath(pen, p);
            }
            string[] ids = { "image", "video", "scroll", "cancel" };
            string[] icons = { "camera", "video", "scroll", "close" };
            string[] labels = { Loc.T("Image"), Loc.T("Video"), Loc.T("Scroll"), Loc.T("Cancel") };
            for (int i = 0; i < 4; i++)
            {
                var r = new Rectangle(tr.X + 5 + i * 70, tr.Y + 4, 70, 58);
                buttons.Add(new Btn { R = r, Id = ids[i] });
                if (r.Contains(mouse))
                    using (var p = RoundRect(r, 7)) using (var b = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillPath(b, p);
                g.DrawImage(Icons.Get(icons[i], 30, true), r.X + 20, r.Y + 4, 30, 30);
                using (var sf = new StringFormat { Alignment = StringAlignment.Center })
                    g.DrawString(labels[i], small, Brushes.White, new RectangleF(r.X, r.Y + 37, r.Width, 18), sf);
            }
        }

        void DrawLoupe(Graphics g, Rectangle lr)
        {
            const int cells = 15, cell = 9;
            int size = cells * cell;   // 135
            using (var p = RoundRect(lr, 8))
            {
                using (var b = new SolidBrush(Color.FromArgb(235, 20, 24, 30))) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(120, 255, 255, 255))) g.DrawPath(pen, p);
            }
            var img = new Rectangle(lr.X + (lr.Width - size) / 2, lr.Y + 7, size, size);
            var src = new Rectangle(mouse.X - cells / 2, mouse.Y - cells / 2, cells, cells);
            var st0 = g.Save();
            g.SetClip(img);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.FillRectangle(Brushes.Black, img);
            g.DrawImage(frozen, img, src, GraphicsUnit.Pixel);
            g.Restore(st0);
            // grid + centre pixel
            using (var pen = new Pen(Color.FromArgb(40, 255, 255, 255)))
                for (int i = 0; i <= cells; i++) { g.DrawLine(pen, img.X + i * cell, img.Y, img.X + i * cell, img.Bottom); g.DrawLine(pen, img.X, img.Y + i * cell, img.Right, img.Y + i * cell); }
            var c = new Rectangle(img.X + (cells / 2) * cell, img.Y + (cells / 2) * cell, cell, cell);
            using (var pen = new Pen(Color.Red, 2f)) g.DrawRectangle(pen, c);
            Color col = Color.Black;
            try { col = frozen.GetPixel(Math.Max(0, Math.Min(frozen.Width - 1, mouse.X)), Math.Max(0, Math.Min(frozen.Height - 1, mouse.Y))); } catch { }
            var sp = ToScreen(mouse);
            g.DrawString(string.Format("X {0}   Y {1}", sp.X, sp.Y), small, Brushes.White, lr.X + 8, img.Bottom + 4);
            using (var b = new SolidBrush(col)) g.FillRectangle(b, lr.X + 8, img.Bottom + 22, 12, 12);
            g.DrawRectangle(Pens.White, lr.X + 8, img.Bottom + 22, 12, 12);
            g.DrawString(string.Format("#{0:X2}{1:X2}{2:X2}", col.R, col.G, col.B), small, Brushes.White, lr.X + 26, img.Bottom + 21);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { dimmed.Dispose(); small.Dispose(); smallB.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
