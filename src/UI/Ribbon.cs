using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RXCapture
{
    public abstract class RItem
    {
        public string Text = "";
        public string Icon;
        public string Tip;
        public Func<bool> IsChecked;
        public Func<bool> IsEnabled;
        public Action Click;
        public Func<ContextMenuStrip> DropDown;
        public Rectangle Bounds;

        public bool Enabled { get { return IsEnabled == null || IsEnabled(); } }
        public bool Checked { get { return IsChecked != null && IsChecked(); } }
        public abstract int PreferredWidth(float s);
        public abstract void Paint(Graphics g, float s, bool hover, bool down);
        public virtual bool HandleClick(Ribbon r, Point p) { return false; }

        protected static readonly Font F = new Font("Segoe UI", 9.5f);

        protected static void Arrow(Graphics g, float cx, float cy, float s, Color c)
        {
            using (var b = new SolidBrush(c))
            {
                float w = 3.2f * s;
                g.FillPolygon(b, new[] { new PointF(cx - w, cy - w * 0.5f), new PointF(cx + w, cy - w * 0.5f), new PointF(cx, cy + w * 0.6f) });
            }
        }
    }

    public enum BtnStyle { Tool, Big, Small, Icon }

    public class RBtn : RItem
    {
        public BtnStyle Style;
        public Func<string> DynamicText;
        public Func<Color> Swatch;          // optional colour bar drawn under the icon (Outline / Fill / Text colour)

        public RBtn(string text, string icon, BtnStyle style, Action click)
        {
            Text = text; Icon = icon; Style = style; Click = click;
        }

        string Label { get { return Loc.T(DynamicText != null ? DynamicText() : Text); } }

        public override int PreferredWidth(float s)
        {
            switch (Style)
            {
                case BtnStyle.Tool: return (int)(38 * s);
                case BtnStyle.Icon: return (int)(28 * s);
                case BtnStyle.Big:
                    {
                        int w = 0;
                        foreach (var line in Label.Split('\n')) w = Math.Max(w, TextRenderer.MeasureText(line, F, new Size(1000, 100), TextFormatFlags.NoPadding).Width);
                        return Math.Max((int)(46 * s), w + (int)(14 * s));
                    }
                default:
                    {
                        int tw = Label.Length == 0 ? 0 : TextRenderer.MeasureText(Label, F, new Size(1000, 100), TextFormatFlags.NoPadding).Width + (int)(4 * s);
                        return (int)(8 * s) + (Icon != null ? (int)(18 * s) : 0) + tw + (DropDown != null ? (int)(12 * s) : 0) + (int)(6 * s);
                    }
            }
        }

        public override void Paint(Graphics g, float s, bool hover, bool down)
        {
            bool en = Enabled, chk = Checked;
            var r = Bounds;
            if (en && (hover || chk || down))
            {
                var bg = down ? Color.FromArgb(96, 96, 104) : (chk ? Theme.Checked : Theme.Hover);
                using (var b = new SolidBrush(bg)) g.FillRectangle(b, r);
                using (var p = new Pen(chk ? Color.FromArgb(120, 120, 128) : Color.FromArgb(84, 84, 90))) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
            Color tc = en ? Theme.Text : Color.FromArgb(105, 105, 110);
            var flags = TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            switch (Style)
            {
                case BtnStyle.Tool:
                case BtnStyle.Icon:
                    {
                        int isz = (int)((Style == BtnStyle.Tool ? 26 : 18) * s);
                        DrawIcon(g, Icon, isz, new Rectangle(r.X + (r.Width - isz) / 2, r.Y + (r.Height - isz) / 2, isz, isz), en);
                        break;
                    }
                case BtnStyle.Big:
                    {
                        int isz = (int)(28 * s);
                        DrawIcon(g, Icon, isz, new Rectangle(r.X + (r.Width - isz) / 2, r.Y + (int)(4 * s), isz, isz), en);
                        var tr = new Rectangle(r.X, r.Y + (int)(4 * s) + isz + (int)(1 * s), r.Width, r.Height - isz - (int)(6 * s));
                        string lbl = Label;
                        TextRenderer.DrawText(g, lbl, F, tr, tc, flags | TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.WordBreak);
                        if (DropDown != null)
                        {
                            int lines = lbl.Split('\n').Length;
                            var sz = TextRenderer.MeasureText(lbl.Split('\n')[lines - 1], F, new Size(1000, 100), TextFormatFlags.NoPadding);
                            Arrow(g, r.X + r.Width / 2f, tr.Y + F.Height * lines + 4 * s, s, tc);
                        }
                        break;
                    }
                default:
                    {
                        int x = r.X + (int)(6 * s);
                        if (Icon != null)
                        {
                            int isz = (int)(16 * s);
                            DrawIcon(g, Icon, isz, new Rectangle(x, r.Y + (r.Height - isz) / 2 - (Swatch != null ? (int)(1 * s) : 0), isz, isz), en);
                            if (Swatch != null)
                            {
                                var sc = Swatch();
                                var bar = new Rectangle(x, r.Y + (r.Height + isz) / 2 - (int)(2 * s), isz, Math.Max(2, (int)(3 * s)));
                                using (var tb = new System.Drawing.Drawing2D.HatchBrush(System.Drawing.Drawing2D.HatchStyle.LargeCheckerBoard, Color.FromArgb(200, 200, 200), Color.White)) g.FillRectangle(tb, bar);
                                using (var b = new SolidBrush(sc)) g.FillRectangle(b, bar);
                            }
                            x += isz + (int)(4 * s);
                        }
                        var tr = new Rectangle(x, r.Y, r.Right - x - (DropDown != null ? (int)(12 * s) : (int)(2 * s)), r.Height);
                        TextRenderer.DrawText(g, Label, F, tr, tc, flags | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
                        if (DropDown != null) Arrow(g, r.Right - 8 * s, r.Y + r.Height / 2f, s, tc);
                        break;
                    }
            }
        }

        static void DrawIcon(Graphics g, string icon, int size, Rectangle r, bool enabled)
        {
            if (icon == null) return;
            var bmp = Icons.Get(icon, size, true);
            if (enabled) g.DrawImage(bmp, r);
            else
                using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix(new float[][] { new float[] { 1, 0, 0, 0, 0 }, new float[] { 0, 1, 0, 0, 0 }, new float[] { 0, 0, 1, 0, 0 }, new float[] { 0, 0, 0, 0.35f, 0 }, new float[] { 0, 0, 0, 0, 1 } }));
                    g.DrawImage(bmp, r, 0, 0, bmp.Width, bmp.Height, GraphicsUnit.Pixel, ia);
                }
        }
    }

    /// <summary>Static caption inside a group (e.g. "Font:").</summary>
    public class RLabel : RItem
    {
        public RLabel(string text) { Text = text; }
        public override int PreferredWidth(float s) { return TextRenderer.MeasureText(Loc.T(Text), F, new Size(1000, 100), TextFormatFlags.NoPadding).Width + (int)(8 * s); }
        public override void Paint(Graphics g, float s, bool hover, bool down)
        {
            TextRenderer.DrawText(g, Loc.T(Text), F, Bounds, Theme.TextDim, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }
    }

    /// <summary>Row of large owner-drawn swatches (the "Styles" gallery).</summary>
    public class RGallery : RItem
    {
        public Func<int> Count;
        public Func<int> Selected;
        public Action<Graphics, Rectangle, int> DrawCell;
        public Action<int> Pick;
        public int CellW = 62, CellH = 50;

        public override int PreferredWidth(float s) { return (int)((CellW + 4) * (Count == null ? 0 : Count()) * s) + (int)(6 * s); }

        Rectangle Cell(int i, float s)
        {
            int cw = (int)(CellW * s), ch = (int)(CellH * s), gap = (int)(4 * s);
            int y = Bounds.Y + (Bounds.Height - ch) / 2;
            return new Rectangle(Bounds.X + (int)(3 * s) + i * (cw + gap), y, cw, ch);
        }

        public override void Paint(Graphics g, float s, bool hover, bool down)
        {
            int n = Count == null ? 0 : Count();
            int sel = Selected == null ? -1 : Selected();
            for (int i = 0; i < n; i++)
            {
                var c = Cell(i, s);
                using (var b = new SolidBrush(Color.FromArgb(58, 58, 62))) g.FillRectangle(b, c);
                var st = g.Save();
                g.SetClip(Rectangle.Inflate(c, -3, -3));
                if (DrawCell != null) DrawCell(g, Rectangle.Inflate(c, -3, -3), i);
                g.Restore(st);
                using (var p = new Pen(i == sel ? Theme.AccentLight : Color.FromArgb(82, 82, 88), i == sel ? 2f : 1f))
                    g.DrawRectangle(p, c.X, c.Y, c.Width - 1, c.Height - 1);
            }
        }

        public override bool HandleClick(Ribbon r, Point p)
        {
            float s = Theme.Scale(r) * Ribbon.UiZoom;
            int n = Count == null ? 0 : Count();
            for (int i = 0; i < n; i++)
                if (Cell(i, s).Contains(p)) { if (Pick != null) Pick(i); return true; }
            return false;
        }
    }

    public class RGroup
    {
        public string Title;
        public Func<bool> Visible;
        public List<List<RItem>> Columns = new List<List<RItem>>();
        public Rectangle Bounds;

        public RGroup(string title) { Title = title; }

        public RGroup Col(params RItem[] items) { Columns.Add(new List<RItem>(items)); return this; }
        public RGroup ColEach(params RItem[] items) { foreach (var i in items) Columns.Add(new List<RItem> { i }); return this; }
        public bool IsVisible { get { return Visible == null || Visible(); } }
    }

    public class RTab
    {
        public string Title;
        public bool IsFile;
        public List<RGroup> Groups = new List<RGroup>();
        public Rectangle Bounds;
        public RTab(string t, bool file = false) { Title = t; IsFile = file; }
        public RGroup Group(string title) { var g = new RGroup(title); Groups.Add(g); return g; }
    }

    /// <summary>Owner-drawn dark ribbon: quick-access buttons, tabs, and groups of buttons/galleries.</summary>
    public class Ribbon : Control
    {
        public readonly List<RTab> Tabs = new List<RTab>();
        public readonly List<RItem> Qat = new List<RItem>();
        public int Active = 1;
        public Func<ContextMenuStrip> FileMenu;
        public Action<Point> FilePopup;                 // screen point below the File tab; replaces the dropdown when set

        readonly ToolTip tip = new ToolTip { InitialDelay = 500, ReshowDelay = 100 };
        RItem hot, pressed;
        bool layoutDirty = true;
        readonly List<RItem> laidOut = new List<RItem>();
        string lastTip;
        static readonly Font TabFont = new Font("Segoe UI", 9.5f);
        static readonly Font GroupFont = new Font("Segoe UI", 8.5f);
        public const float UiZoom = 1.2f;                // the whole toolbar is drawn 20% larger than the DPI scale alone

        public Ribbon()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            Dock = DockStyle.Top;
            BackColor = Theme.Ribbon;
            TabStop = false;
        }

        float S { get { return Theme.Scale(this) * UiZoom; } }
        int TabH { get { return (int)(25 * S); } }
        int ContentH { get { return (int)(88 * S); } }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); UpdateHeight(); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); UpdateHeight(); Relayout(); }

        void UpdateHeight() { Height = TabH + ContentH + 1; }

        public void Relayout() { layoutDirty = true; Invalidate(); }

        protected override void OnResize(EventArgs e) { base.OnResize(e); layoutDirty = true; }

        // ------------------------------------------------------------ layout

        void DoLayout(Graphics g)
        {
            float s = S;
            laidOut.Clear();
            int x = (int)(6 * s), th = TabH;
            // File tab first, then the other tabs, then the quick access icons
            foreach (var t in Tabs)
            {
                int w = TextRenderer.MeasureText(Loc.T(t.Title), TabFont, new Size(1000, 100), TextFormatFlags.NoPadding).Width + (int)((t.IsFile ? 34 : 28) * s);
                t.Bounds = new Rectangle(x, (int)(2 * s), w, th - (int)(2 * s));
                x += w + (int)(2 * s);
            }
            x += (int)(14 * s);
            int q = (int)(26 * s);
            foreach (var it in Qat) { it.Bounds = new Rectangle(x, (th - q) / 2, q, q); x += q; laidOut.Add(it); }
            if (Active < 0 || Active >= Tabs.Count) Active = Math.Min(1, Tabs.Count - 1);
            var tab = Tabs[Active];
            if (tab.IsFile) tab = Tabs[Math.Min(1, Tabs.Count - 1)];
            int gx = (int)(4 * s), gy = th + (int)(3 * s);
            int labelH = (int)(16 * s), innerH = ContentH - (int)(6 * s) - labelH;
            foreach (var grp in tab.Groups)
            {
                if (!grp.IsVisible) { grp.Bounds = Rectangle.Empty; continue; }
                int start = gx;
                gx += (int)(4 * s);
                foreach (var col in grp.Columns)
                {
                    int cw = 0;
                    foreach (var it in col) cw = Math.Max(cw, it.PreferredWidth(s));
                    int n = col.Count;
                    for (int i = 0; i < n; i++)
                    {
                        int y0 = gy + innerH * i / n, y1 = gy + innerH * (i + 1) / n;
                        col[i].Bounds = new Rectangle(gx, y0, cw, y1 - y0);
                        laidOut.Add(col[i]);
                    }
                    gx += cw + (int)(2 * s);
                }
                int titleW = TextRenderer.MeasureText(Loc.T(grp.Title), GroupFont, new Size(1000, 100), TextFormatFlags.NoPadding).Width + (int)(10 * s);
                if (gx - start < titleW) gx = start + titleW;
                gx += (int)(4 * s);
                grp.Bounds = new Rectangle(start, gy, gx - start, innerH + labelH);
                gx += (int)(2 * s);
            }
            layoutDirty = false;
        }

        RItem HitItem(Point p)
        {
            for (int i = laidOut.Count - 1; i >= 0; i--) if (laidOut[i].Bounds.Contains(p)) return laidOut[i];
            return null;
        }

        // ------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            if (Tabs.Count == 0) return;
            if (layoutDirty) DoLayout(g);
            float s = S;
            g.SmoothingMode = SmoothingMode.None;
            g.Clear(Theme.Ribbon);
            int th = TabH;
            using (var b = new SolidBrush(Theme.TabBar)) g.FillRectangle(b, 0, 0, Width, th);
            using (var p = new Pen(Theme.Border)) g.DrawLine(p, 0, th, Width, th);

            for (int i = 0; i < Tabs.Count; i++)
            {
                var t = Tabs[i];
                bool active = i == Active && !t.IsFile;
                if (t.IsFile)
                {
                    using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, t.Bounds);
                }
                else if (active)
                {
                    using (var b = new SolidBrush(Theme.Ribbon)) g.FillRectangle(b, t.Bounds.X, t.Bounds.Y, t.Bounds.Width, t.Bounds.Height + 1);
                    using (var p = new Pen(Theme.Border)) { g.DrawLine(p, t.Bounds.X, t.Bounds.Y, t.Bounds.Right, t.Bounds.Y); g.DrawLine(p, t.Bounds.X, t.Bounds.Y, t.Bounds.X, t.Bounds.Bottom); g.DrawLine(p, t.Bounds.Right, t.Bounds.Y, t.Bounds.Right, t.Bounds.Bottom); }
                }
                TextRenderer.DrawText(g, Loc.T(t.Title), TabFont, t.Bounds, active || t.IsFile ? Color.White : Color.FromArgb(205, 205, 210),
                    TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            foreach (var it in Qat) it.Paint(g, s, it == hot, it == pressed && it == hot);

            var tab = Tabs[Active].IsFile ? Tabs[Math.Min(1, Tabs.Count - 1)] : Tabs[Active];
            foreach (var grp in tab.Groups)
            {
                if (!grp.IsVisible || grp.Bounds.IsEmpty) continue;
                foreach (var col in grp.Columns)
                    foreach (var it in col) it.Paint(g, s, it == hot, it == pressed && it == hot);
                var lr = new Rectangle(grp.Bounds.X, grp.Bounds.Bottom - (int)(18 * s), grp.Bounds.Width, (int)(18 * s));
                TextRenderer.DrawText(g, Loc.T(grp.Title), GroupFont, lr, Theme.TextDim, TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                using (var p = new Pen(Theme.Border)) g.DrawLine(p, grp.Bounds.Right + (int)(1 * s), grp.Bounds.Y + 2, grp.Bounds.Right + (int)(1 * s), grp.Bounds.Bottom - 2);
            }
            using (var p = new Pen(Color.FromArgb(28, 28, 28))) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        // ------------------------------------------------------------ input

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var it = HitItem(e.Location);
            if (it != hot) { hot = it; Invalidate(); }
            string t = it != null && it.Enabled ? (it.Tip ?? (it.Text.Length > 0 ? it.Text.Replace("\n", " ") : null)) : null;
            if (t != null) t = Loc.T(t);
            if (t != lastTip) { lastTip = t; tip.SetToolTip(this, t ?? ""); }
        }

        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot != null) { hot = null; Invalidate(); } }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            for (int i = 0; i < Tabs.Count; i++)
                if (Tabs[i].Bounds.Contains(e.Location))
                {
                    if (Tabs[i].IsFile)
                    {
                        if (FilePopup != null) FilePopup(PointToScreen(new Point(Tabs[i].Bounds.X, Tabs[i].Bounds.Bottom)));
                        else if (FileMenu != null) { var m = FileMenu(); m.Show(this, new Point(Tabs[i].Bounds.X, Tabs[i].Bounds.Bottom)); }
                    }
                    else { Active = i; Relayout(); }
                    return;
                }
            pressed = HitItem(e.Location);
            Invalidate();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            var it = pressed; pressed = null;
            Invalidate();
            if (it == null || e.Button != MouseButtons.Left || !it.Bounds.Contains(e.Location) || !it.Enabled) return;
            if (it.HandleClick(this, e.Location)) { Invalidate(); return; }
            var btn = it as RBtn;
            float s = S;
            bool inArrow = btn != null && it.DropDown != null && it.Click != null &&
                (btn.Style == BtnStyle.Big ? e.Y > it.Bounds.Y + it.Bounds.Height * 0.62 : e.X > it.Bounds.Right - 14 * s);
            if (it.DropDown != null && (it.Click == null || inArrow))
            {
                var m = it.DropDown();
                if (m != null) m.Show(this, new Point(it.Bounds.X, it.Bounds.Bottom));
            }
            else if (it.Click != null) it.Click();
            Invalidate();
        }
    }
}
