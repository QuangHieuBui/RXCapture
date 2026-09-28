using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;
using Timer = System.Windows.Forms.Timer;

namespace RXCapture
{
    /// <summary>
    /// Snagit-style capture widget: a small always-on-top tab docked to the top edge of the screen.
    /// Hover or click it to expand (big red Capture button, Editor / menu buttons, profile list);
    /// move the mouse away and it folds back into the tab.
    /// </summary>
    public class CaptureWidget : Form
    {
        class Item { public string Name; public CaptureMode Mode; public CapturePreset Preset; public string Icon; }
        enum Hit { None, Grip, Editor, Capture, Gear, Bar, Item, Prev, Next, Manage }

        static readonly string[] PageTitles = { "Image Profiles", "Scrolling Profiles", "Video Profiles", "My Presets" };
        static readonly Color Bg = Color.FromArgb(38, 38, 40);
        static readonly Color BarBlue = Color.FromArgb(0, 94, 148);
        const int Rows = 5;               // fixed for every page, so the widget keeps the same height when you switch pages

        readonly AppSettings cfg = AppSettings.Current;
        readonly Timer poll = new Timer { Interval = 30 };
        readonly ToolTip tip = new ToolTip();
        float s = 1f;
        int centerX;                 // horizontal centre of the widget (screen px)
        Rectangle anchor;            // monitor the widget hangs from
        readonly Timer anim = new Timer { Interval = 15 };
        const int ExpandMs = 260, FoldMs = 200;   // slower unfold, quicker fold
        int AnimMs { get { return collapsed ? FoldMs : ExpandMs; } }
        int expW, expH, animFrom, animTo;
        DateTime animT0;
        bool tabMode;                // painting the small folded tab (only once the fold animation has finished)
        bool collapsed, menuOpen, dragging, lastRec, down;
        int viewPage, scroll, rows;
        int dragDx;
        Hit hover = Hit.None, downHit = Hit.None;
        int hoverItem = -1, downItem = -1;
        string tipText = "";
        DateTime outsideSince = DateTime.MinValue;
        Rectangle rEditor, rCapture, rGear, rBar, rList, rPrev, rNext, rManage, rTitle;
        int ySep1, ySep2, yDots;

        public CaptureWidget()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            BackColor = Bg; ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            viewPage = Math.Max(0, Math.Min(PageTitles.Length - 1, cfg.WidgetPage));
            anchor = Screen.PrimaryScreen.Bounds;
            centerX = cfg.WidgetCenterX == int.MinValue ? anchor.Left + anchor.Width / 2 : cfg.WidgetCenterX;
            var scr = Screen.FromPoint(new Point(centerX, anchor.Top + 2));
            anchor = scr.Bounds;
            collapsed = cfg.WidgetAutoHide;
            poll.Tick += (a, b) => Poll();
            poll.Start();
            anim.Tick += (a, b) => AnimTick();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= 0x08000000 | 0x80 | 0x8; return cp; }   // NOACTIVATE | TOOLWINDOW | TOPMOST
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x21) { m.Result = (IntPtr)3; return; }   // WM_MOUSEACTIVATE -> MA_NOACTIVATE: clicking never steals focus
            base.WndProc(ref m);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            RecorderForm.ExcludeFromCapture(Handle);   // the widget never shows up in captures / recordings
            Apply();
        }

        protected override void OnShown(EventArgs e) { base.OnShown(e); Apply(); }

        protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Apply(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { poll.Stop(); poll.Dispose(); anim.Stop(); anim.Dispose(); tip.Dispose(); }
            base.Dispose(disposing);
        }

        // ------------------------------------------------------------------ items

        List<Item> Build(int page)
        {
            var l = new List<Item>();
            switch (page)
            {
                case 0:
                    l.Add(B("All-in-One", CaptureMode.AllInOne, "camera")); l.Add(B("Region", CaptureMode.Region, "region"));
                    l.Add(B("Window", CaptureMode.Window, "window")); l.Add(B("Full Screen", CaptureMode.FullScreen, "fullscreen"));
                    l.Add(B("Freehand", CaptureMode.Freehand, "freehand"));
                    break;
                case 1:
                    l.Add(B("Scrolling window", CaptureMode.Scrolling, "scroll"));
                    foreach (var p in cfg.Presets) if (p.Mode == CaptureMode.Scrolling) l.Add(P(p));
                    break;
                case 2:
                    l.Add(B("Video (region)", CaptureMode.Video, "video")); l.Add(B("Video (window)", CaptureMode.VideoWindow, "window"));
                    l.Add(B("Video (screen)", CaptureMode.VideoScreen, "fullscreen"));
                    foreach (var p in cfg.Presets) if (p.Mode == CaptureMode.Video || p.Mode == CaptureMode.VideoWindow || p.Mode == CaptureMode.VideoScreen) l.Add(P(p));
                    break;
                default:
                    foreach (var p in cfg.Presets) l.Add(P(p));
                    break;
            }
            return l;
        }

        static Item B(string name, CaptureMode m, string icon) { return new Item { Name = name, Mode = m, Icon = icon }; }

        static Item P(CapturePreset p)
        {
            string ic = p.Mode == CaptureMode.Window || p.Mode == CaptureMode.VideoWindow ? "window" :
                p.Mode == CaptureMode.FullScreen || p.Mode == CaptureMode.VideoScreen ? "fullscreen" :
                p.Mode == CaptureMode.Scrolling ? "scroll" : p.Mode == CaptureMode.Freehand ? "freehand" :
                p.Mode == CaptureMode.Video ? "video" : p.Mode == CaptureMode.Region || p.Mode == CaptureMode.Fixed || p.Mode == CaptureMode.Repeat ? "region" : "camera";
            return new Item { Name = p.Name, Mode = p.Mode, Preset = p, Icon = ic };
        }

        /// <summary>The profile the red button captures with.</summary>
        Item Selected()
        {
            var l = Build(cfg.WidgetPage);
            if (l.Count == 0) l = Build(0);
            return l[Math.Max(0, Math.Min(l.Count - 1, cfg.WidgetIndex))];
        }

        void DoCapture()
        {
            if (VideoRecorder.IsActive) { VideoRecorder.RequestStop(); return; }
            var it = Selected();
            if (it.Preset != null) it.Preset.ApplyTo(cfg);
            cfg.Save();
            App.Capture(it.Mode);
        }

        // ------------------------------------------------------------------ layout

        int S(int v) { return (int)Math.Round(v * s); }

        void Apply() { Apply(false); }

        /// <summary>Re-lays the widget out; with animate the height eases to the new size (expand / fold / list change).</summary>
        void Apply(bool animate)
        {
            if (!IsHandleCreated) return;
            s = Math.Max(1f, DeviceDpi / 96f);
            int w, h;
            int cx;
            {
                w = S(196); cx = w / 2;
                rEditor = new Rectangle(S(16), S(20), S(36), S(36));
                rCapture = new Rectangle(cx - S(30), S(10), S(60), S(60));
                rGear = new Rectangle(w - S(16) - S(36), S(20), S(36), S(36));
                rBar = new Rectangle(0, S(78), w, S(22));
                int y = rBar.Bottom;
                if (cfg.WidgetListOpen)
                {
                    rTitle = new Rectangle(0, y + S(6), w, S(18));
                    ySep1 = rTitle.Bottom + S(4);
                    y = ySep1 + S(6);
                    var l = Build(viewPage);
                    rows = Rows;
                    scroll = Math.Max(0, Math.Min(scroll, Math.Max(0, l.Count - rows)));
                    rList = new Rectangle(S(12), y, w - S(24), rows * S(22));
                    ySep2 = rList.Bottom + S(6);
                    y = ySep2 + S(8);
                    rPrev = new Rectangle(S(12), y, S(24), S(18)); rNext = new Rectangle(w - S(12) - S(24), y, S(24), S(18));
                    yDots = y + S(9);
                    y += S(26);
                    rManage = new Rectangle(S(28), y, w - S(56), S(24));
                    y = rManage.Bottom + S(10);
                }
                else y += S(8);
                h = y;
            }
            expW = w; expH = h;
            int targetH = collapsed ? S(8) : h;
            if (!animate || Width <= 0)
            {
                anim.Stop();
                animTo = targetH;
                tabMode = collapsed;
                Place(w, targetH);
                Invalidate();
                return;
            }
            if (!collapsed) tabMode = false;
            animFrom = Height; animTo = targetH; animT0 = DateTime.Now;
            if (animFrom == animTo && Width == w) { tabMode = collapsed; return; }
            anim.Start();
        }

        void AnimTick()
        {
            double t = (DateTime.Now - animT0).TotalMilliseconds / AnimMs;
            if (t >= 1)
            {
                anim.Stop();
                tabMode = collapsed;
                Place(expW, animTo);
                Invalidate();
                return;
            }
            double e = 1 - Math.Pow(1 - t, 3);          // ease-out
            int h = animFrom + (int)Math.Round((animTo - animFrom) * e);
            Place(expW, Math.Max(S(8), h));
            Invalidate();
        }

        void Place(int w, int h)
        {
            int left = Math.Max(anchor.Left, Math.Min(anchor.Right - w, centerX - w / 2));
            SetBounds(left, anchor.Top, w, h);
            ApplyShape();
        }

        void ApplyShape()
        {
            int r = S(tabMode ? 4 : 10);
            using (var p = new GraphicsPath())
            {
                p.AddLine(0, 0, Width, 0);
                p.AddLine(Width, 0, Width, Height - r);
                p.AddArc(Width - 2 * r, Height - 2 * r, 2 * r, 2 * r, 0, 90);
                p.AddArc(0, Height - 2 * r, 2 * r, 2 * r, 90, 90);
                p.CloseFigure();
                var old = Region;
                Region = new Region(p);
                if (old != null) old.Dispose();
            }
        }

        void SetCollapsed(bool c)
        {
            if (collapsed == c) return;
            collapsed = c; hover = Hit.None; hoverItem = -1;
            Apply(true);
        }

        // ------------------------------------------------------------------ auto fold

        void Poll()
        {
            if (!Visible || IsDisposed) return;
            bool rec = VideoRecorder.IsActive;
            if (rec != lastRec) { lastRec = rec; Invalidate(); }
            var pt = Cursor.Position;
            var b = Bounds; b.Inflate(S(2), S(2));
            bool inside = b.Contains(pt) || menuOpen || dragging;
            if (inside)
            {
                outsideSince = DateTime.MinValue;
                if (collapsed) SetCollapsed(false);
            }
            else if (!collapsed && cfg.WidgetAutoHide)
            {
                if (outsideSince == DateTime.MinValue) outsideSince = DateTime.Now;
                else if ((DateTime.Now - outsideSince).TotalMilliseconds > 120) SetCollapsed(true);
            }
            else if (collapsed && !cfg.WidgetAutoHide) SetCollapsed(false);
        }

        // ------------------------------------------------------------------ painting

        static GraphicsPath BottomRound(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            p.AddLine(r.Left, r.Top, r.Right, r.Top);
            p.AddLine(r.Right, r.Top, r.Right, r.Bottom - rad);
            p.AddArc(r.Right - 2 * rad, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 0, 90);
            p.AddArc(r.Left, r.Bottom - 2 * rad, 2 * rad, 2 * rad, 90, 90);
            p.CloseFigure();
            return p;
        }

        void Dots(Graphics g, int x0, int x1, int y, Color c)
        {
            using (var b = new SolidBrush(c))
                for (int x = x0; x <= x1; x += S(4)) g.FillRectangle(b, x, y, Math.Max(1, S(2)), Math.Max(1, S(2)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Bg);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            int cx = Width / 2;
            Color dot = Color.FromArgb(95, 95, 100);

            if (tabMode)
            {
                // flat bar, as wide as the expanded widget: a red marker in the middle between two rows of grip dots
                int pw = S(44), ph = Math.Max(2, S(3)), py = (Height - ph) / 2;
                Dots(g, S(14), cx - pw / 2 - S(10), Height / 2 - Math.Max(1, S(1)), dot);
                Dots(g, cx + pw / 2 + S(10), Width - S(14), Height / 2 - Math.Max(1, S(1)), dot);
                using (var p = Theme.RoundRect(new Rectangle(cx - pw / 2, py, pw, ph), ph / 2))
                using (var b = new SolidBrush(Color.FromArgb(226, 40, 40))) g.FillPath(b, p);
                DrawBorder(g);
                return;
            }

            Dots(g, S(28), Width - S(28), S(3), dot);

            DrawRound(g, rEditor, hover == Hit.Editor, "pen");
            DrawRound(g, rGear, hover == Hit.Gear, "settings");
            DrawCaptureButton(g);

            using (var b = new SolidBrush(hover == Hit.Bar ? Color.FromArgb(0, 110, 170) : BarBlue)) g.FillRectangle(b, rBar);
            DrawListGlyph(g);

            if (cfg.WidgetListOpen)
            {
                var l = Build(viewPage);
                TextRenderer.DrawText(g, Loc.T(PageTitles[viewPage]), Font, rTitle, Color.FromArgb(215, 215, 215), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                using (var p = new Pen(Color.FromArgb(90, 90, 95))) { g.DrawLine(p, S(12), ySep1, Width - S(12), ySep1); g.DrawLine(p, S(12), ySep2, Width - S(12), ySep2); }

                if (l.Count == 0)
                    TextRenderer.DrawText(g, Loc.T("No presets yet. Use Manage Profiles to save one."), Font, rList, Theme.TextDim, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                int selPage = cfg.WidgetPage, selIdx = cfg.WidgetIndex;
                for (int r = 0; r < rows; r++)
                {
                    int i = scroll + r;
                    if (i >= l.Count) break;
                    var rr = new Rectangle(rList.X, rList.Y + r * S(22), rList.Width - (l.Count > rows ? S(6) : 0), S(22));
                    bool sel = selPage == viewPage && selIdx == i;
                    if (sel) using (var b = new SolidBrush(BarBlue)) g.FillRectangle(b, rr);
                    else if (hover == Hit.Item && hoverItem == i) using (var b = new SolidBrush(Color.FromArgb(58, 58, 62))) g.FillRectangle(b, rr);
                    int isz = S(16);
                    g.DrawImage(Icons.Get(l[i].Icon, isz, true), rr.X + S(6), rr.Y + (rr.Height - isz) / 2, isz, isz);
                    TextRenderer.DrawText(g, Loc.T(l[i].Name), Font, new Rectangle(rr.X + S(28), rr.Y, rr.Width - S(32), rr.Height), sel ? Color.White : Color.FromArgb(205, 205, 205),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
                }
                if (l.Count > rows)
                {
                    int th = Math.Max(S(12), rList.Height * rows / l.Count);
                    int ty = rList.Y + (rList.Height - th) * scroll / Math.Max(1, l.Count - rows);
                    using (var b = new SolidBrush(Color.FromArgb(110, 110, 116))) g.FillRectangle(b, rList.Right - S(4), ty, S(3), th);
                }

                DrawArrow(g, rPrev, true, hover == Hit.Prev);
                DrawArrow(g, rNext, false, hover == Hit.Next);
                int n = PageTitles.Length, gap = S(12), x0 = cx - (n - 1) * gap / 2;
                for (int i = 0; i < n; i++)
                    using (var b = new SolidBrush(i == viewPage ? Color.White : Color.FromArgb(105, 105, 110)))
                    { int d = S(5); g.FillEllipse(b, x0 + i * gap - d / 2, yDots - d / 2, d, d); }

                bool mh = hover == Hit.Manage;
                using (var b = new SolidBrush(mh ? Color.FromArgb(78, 78, 84) : Color.FromArgb(64, 64, 68))) g.FillRectangle(b, rManage);
                using (var p = new Pen(Color.FromArgb(96, 96, 102))) g.DrawRectangle(p, rManage.X, rManage.Y, rManage.Width - 1, rManage.Height - 1);
                TextRenderer.DrawText(g, Loc.T("Manage Profiles"), Font, rManage, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            }
            DrawBorder(g);
        }

        void DrawBorder(Graphics g)
        {
            using (var p = BottomRound(new Rectangle(0, 0, Width - 1, Height - 1), S(tabMode ? 4 : 10)))
            using (var pen = new Pen(Color.FromArgb(78, 78, 84))) g.DrawPath(pen, p);
        }

        void DrawRound(Graphics g, Rectangle r, bool hot, string icon)
        {
            var rr = new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1);
            using (var b = new SolidBrush(hot ? Color.FromArgb(70, 70, 76) : Color.FromArgb(52, 52, 56))) g.FillEllipse(b, rr);
            using (var p = new Pen(hot ? Color.FromArgb(200, 200, 205) : Color.FromArgb(140, 140, 146), Math.Max(1f, S(1)) * 1.5f)) g.DrawEllipse(p, rr);
            int sz = S(20);
            g.DrawImage(Icons.Get(icon, sz, true), r.X + (r.Width - sz) / 2, r.Y + (r.Height - sz) / 2, sz, sz);
        }

        void DrawCaptureButton(Graphics g)
        {
            bool isDown = down && downHit == Hit.Capture;
            bool hot = hover == Hit.Capture;
            var r = new Rectangle(rCapture.X, rCapture.Y, rCapture.Width - 1, rCapture.Height - 1);
            using (var p = new Pen(Color.FromArgb(hot ? 255 : 225, 235, 235, 235), Math.Max(2f, S(3))))
                g.DrawEllipse(p, r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2);
            var inner = Rectangle.Inflate(r, -S(5), -S(5));
            Color top = isDown ? Color.FromArgb(170, 20, 20) : (hot ? Color.FromArgb(255, 84, 76) : Color.FromArgb(240, 62, 54));
            Color bot = isDown ? Color.FromArgb(130, 12, 12) : (hot ? Color.FromArgb(214, 40, 34) : Color.FromArgb(200, 32, 28));
            using (var b = new LinearGradientBrush(inner, top, bot, 90f)) g.FillEllipse(b, inner);
            if (lastRec)
                using (var b = new SolidBrush(Color.White)) { int q = S(16); g.FillRectangle(b, rCapture.X + (rCapture.Width - q) / 2, rCapture.Y + (rCapture.Height - q) / 2, q, q); }
        }

        void DrawListGlyph(Graphics g)
        {
            int cx = rBar.X + rBar.Width / 2, cy = rBar.Y + rBar.Height / 2;
            using (var b = new SolidBrush(Color.White))
                for (int i = -1; i <= 1; i++)
                {
                    int y = cy + i * S(5) - S(1);
                    g.FillRectangle(b, cx - S(7), y, S(2), S(2));
                    g.FillRectangle(b, cx - S(3), y, S(9), S(2));
                }
        }

        void DrawArrow(Graphics g, Rectangle r, bool left, bool hot)
        {
            int cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2, a = S(4);
            var pts = left ? new[] { new Point(cx + a, cy - a - 1), new Point(cx + a, cy + a + 1), new Point(cx - a, cy) }
                           : new[] { new Point(cx - a, cy - a - 1), new Point(cx - a, cy + a + 1), new Point(cx + a, cy) };
            using (var b = new SolidBrush(hot ? Color.White : Color.FromArgb(150, 150, 156))) g.FillPolygon(b, pts);
        }

        // ------------------------------------------------------------------ mouse

        static bool InCircle(Rectangle r, Point p)
        {
            double rx = r.Width / 2.0, ry = r.Height / 2.0, dx = p.X - (r.X + rx), dy = p.Y - (r.Y + ry);
            return (dx * dx) / (rx * rx) + (dy * dy) / (ry * ry) <= 1.0;
        }

        Hit HitTest(Point p, out int item)
        {
            item = -1;
            if (collapsed) return Hit.Grip;
            if (InCircle(rCapture, p)) return Hit.Capture;
            if (InCircle(rEditor, p)) return Hit.Editor;
            if (InCircle(rGear, p)) return Hit.Gear;
            if (rBar.Contains(p)) return Hit.Bar;
            if (cfg.WidgetListOpen)
            {
                if (rList.Contains(p))
                {
                    int i = scroll + (p.Y - rList.Y) / S(22);
                    if (i < Build(viewPage).Count) { item = i; return Hit.Item; }
                    return Hit.None;
                }
                if (rPrev.Contains(p)) return Hit.Prev;
                if (rNext.Contains(p)) return Hit.Next;
                if (rManage.Contains(p)) return Hit.Manage;
            }
            if (p.Y < S(9)) return Hit.Grip;
            return Hit.None;
        }

        string TipFor(Hit h, int item)
        {
            switch (h)
            {
                case Hit.Capture: return VideoRecorder.IsActive ? Loc.T("Stop recording") : Loc.T("Capture") + ": " + Loc.T(Selected().Name);
                case Hit.Editor: return Loc.T("Open the editor");
                case Hit.Gear: return Loc.T("Menu");
                case Hit.Bar: return Loc.T("Show / hide the profile list");
                case Hit.Grip: return Loc.T("Drag to move along the top edge");
                default: return "";
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragging)
            {
                var c = Cursor.Position;
                anchor = Screen.FromPoint(c).Bounds;
                centerX = c.X - dragDx;
                Apply();
                return;
            }
            int item;
            var h = HitTest(e.Location, out item);
            if (h != hover || item != hoverItem)
            {
                hover = h; hoverItem = item;
                Cursor = h == Hit.None || h == Hit.Grip ? (h == Hit.Grip ? Cursors.SizeWE : Cursors.Default) : Cursors.Hand;
                string t = TipFor(h, item);
                if (t != tipText) { tipText = t; try { tip.SetToolTip(this, t); } catch { } }
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!dragging) { hover = Hit.None; hoverItem = -1; Invalidate(); }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { if (!collapsed) ShowMenu(); return; }
            if (e.Button != MouseButtons.Left) return;
            int item;
            var h = HitTest(e.Location, out item);
            if (collapsed) { SetCollapsed(false); return; }         // click on the tab expands it
            if (h == Hit.Grip || h == Hit.None) { dragging = true; dragDx = Cursor.Position.X - centerX; Capture = true; return; }
            down = true; downHit = h; downItem = item; Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragging)
            {
                dragging = false; Capture = false;
                cfg.WidgetCenterX = centerX; cfg.Save();
                return;
            }
            if (!down) return;
            down = false;
            int item;
            var h = HitTest(e.Location, out item);
            var dh = downHit; int di = downItem;
            downHit = Hit.None; downItem = -1;
            Invalidate();
            if (h != dh || item != di) return;
            switch (h)
            {
                case Hit.Capture: DoCapture(); break;
                case Hit.Editor: App.ShowEditor(); break;
                case Hit.Gear: ShowMenu(); break;
                case Hit.Bar: cfg.WidgetListOpen = !cfg.WidgetListOpen; cfg.Save(); Apply(true); break;
                case Hit.Item: cfg.WidgetPage = viewPage; cfg.WidgetIndex = item; cfg.Save(); Invalidate(); break;
                case Hit.Prev: SetPage(viewPage - 1); break;
                case Hit.Next: SetPage(viewPage + 1); break;
                case Hit.Manage: App.ShowMain(3); break;
            }
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            int item;
            if (e.Button == MouseButtons.Left && HitTest(e.Location, out item) == Hit.Item)
            {
                cfg.WidgetPage = viewPage; cfg.WidgetIndex = item; cfg.Save();
                DoCapture();
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (collapsed || !cfg.WidgetListOpen) return;
            int more = Build(viewPage).Count - rows;
            if (rList.Contains(e.Location) && more > 0) { scroll = Math.Max(0, Math.Min(more, scroll - Math.Sign(e.Delta))); Invalidate(); }
            else SetPage(viewPage + (e.Delta < 0 ? 1 : -1));
        }

        void SetPage(int p)
        {
            int n = PageTitles.Length;
            viewPage = (p % n + n) % n;
            scroll = 0;
            Apply(true);
        }

        DateTime menuClosedAt = DateTime.MinValue;
        bool menuClosedByClick;      // the menu was dismissed by a click on the widget itself

        void ShowMenu()
        {
            // clicking the gear again while the menu is open only closes it: the menu already closed on that mouse-down, so do not reopen it
            if (menuClosedByClick && (DateTime.Now - menuClosedAt).TotalMilliseconds < 700) { menuClosedByClick = false; return; }
            menuClosedByClick = false;
            var m = Theme.Menu();
            m.Items.Add(Theme.Item("Capture window", "camera", (a, b) => App.ShowMain()));
            m.Items.Add(Theme.Item("Editor", "pen", (a, b) => App.ShowEditor()));
            m.Items.Add(Theme.Item("Library", "library", (a, b) => App.ShowLibrary()));
            m.Items.Add(Theme.Item("Settings…", "settings", (a, b) => App.ShowSettings()));
            m.Items.Add(new ToolStripSeparator());
            var ah = Theme.Item("Auto-hide", null, (a, b) => { cfg.WidgetAutoHide = !cfg.WidgetAutoHide; cfg.Save(); });
            ah.Checked = cfg.WidgetAutoHide;
            m.Items.Add(ah);
            m.Items.Add(Theme.Item("Hide capture widget", "close", (a, b) => App.SetWidgetVisible(false)));
            menuOpen = true;
            m.Closed += (a, b) =>
            {
                menuOpen = false; outsideSince = DateTime.Now;
                menuClosedAt = DateTime.Now;
                menuClosedByClick = b.CloseReason == ToolStripDropDownCloseReason.AppClicked && Bounds.Contains(Cursor.Position);
            };
            m.Show(this, new Point(rGear.Left, rGear.Bottom));
        }
    }
}
