using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Windows.Forms;

namespace ShotCraft
{
    public class FileEntry
    {
        public string Text, Icon;
        public Action Click;
        public List<FileEntry> Sub;          // shown as a fly-out menu (entry gets an arrow)
        public bool Enabled = true;
        public bool SepAbove;
        public FileEntry(string text, string icon, Action click) { Text = text; Icon = icon; Click = click; }
    }

    public class FileRecent
    {
        public string Text;
        public Action Click;
        public FileRecent(string text, Action click) { Text = text; Click = click; }
    }

    /// <summary>
    /// The editor's File panel, laid out like Snagit's: big-icon commands on the left, recent files on the right,
    /// "Editor Options" and "Exit" in a bar at the bottom. Opens under the File tab and closes when it loses focus.
    /// </summary>
    public class FileMenuForm : Form
    {
        static readonly Color LeftBg = Color.FromArgb(38, 38, 40), RightBg = Color.FromArgb(52, 52, 55), BottomBg = Color.FromArgb(42, 42, 44);
        static readonly Color Line = Color.FromArgb(78, 78, 84), HoverBg = Color.FromArgb(66, 66, 72);

        readonly List<FileEntry> entries;
        readonly List<FileRecent> recents;
        readonly FileEntry optionsEntry, exitEntry;
        readonly float s;
        readonly int leftW, rowH, sepH, bottomH;
        readonly List<Rectangle> rowRects = new List<Rectangle>();
        readonly List<Rectangle> recentRects = new List<Rectangle>();
        Rectangle rOptions, rExit;
        int hotEntry = -1, hotRecent = -1, hotBottom = -1;     // hotBottom: 0 options, 1 exit
        bool subOpen;

        int S(int v) { return (int)Math.Round(v * s); }

        public FileMenuForm(Form owner, List<FileEntry> entries, List<FileRecent> recents, FileEntry options, FileEntry exit)
        {
            this.entries = entries; this.recents = recents; optionsEntry = options; exitEntry = exit;
            s = Math.Max(1f, owner.DeviceDpi / 96f);
            leftW = S(196); rowH = S(44); sepH = S(9); bottomH = S(38);
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            AutoScaleMode = AutoScaleMode.None;
            Owner = owner;
            BackColor = RightBg; ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);

            int y = 0;
            foreach (var e in entries)
            {
                if (e.SepAbove) y += sepH;
                rowRects.Add(new Rectangle(0, y, leftW, rowH));
                y += rowH;
            }
            int listH = y + S(6);
            int recH = S(56) + Math.Max(1, recents.Count) * S(26) + S(8);
            int h = Math.Max(listH, recH) + bottomH;
            int w = S(520);
            ClientSize = new Size(w, h);
            int ry = S(48);
            foreach (var r in recents) { recentRects.Add(new Rectangle(leftW + S(8), ry, w - leftW - S(16), S(26))); ry += S(26); }
            rExit = new Rectangle(w - S(140), h - bottomH, S(136), bottomH);
            rOptions = new Rectangle(rExit.X - S(160), h - bottomH, S(156), bottomH);
        }

        protected override bool ShowWithoutActivation { get { return false; } }

        protected override void OnDeactivate(EventArgs e)
        {
            base.OnDeactivate(e);
            if (!subOpen && !IsDisposed) Close();
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.Escape: Close(); return true;
                case Keys.Down: Step(1); return true;
                case Keys.Up: Step(-1); return true;
                case Keys.Enter: if (hotEntry >= 0) Activate(hotEntry); else if (hotRecent >= 0) Run(recents[hotRecent].Click); return true;
                case Keys.Right: if (hotEntry >= 0 && entries[hotEntry].Sub != null) Activate(hotEntry); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        void Step(int d)
        {
            int n = entries.Count;
            int i = hotEntry < 0 ? (d > 0 ? -1 : n) : hotEntry;
            for (int k = 0; k < n; k++) { i = (i + d + n) % n; if (entries[i].Enabled) break; }
            hotEntry = i; hotRecent = -1; hotBottom = -1; Invalidate();
        }

        void Run(Action a)
        {
            Close();
            if (a != null) a();
        }

        void Activate(int i)
        {
            var e = entries[i];
            if (!e.Enabled) return;
            if (e.Sub != null) { ShowSub(i); return; }
            Run(e.Click);
        }

        void ShowSub(int i)
        {
            var e = entries[i];
            var m = Theme.Menu();
            foreach (var sub in e.Sub)
            {
                var act = sub.Click;
                var it = Theme.Item(sub.Text, sub.Icon, (a, b) => Run(act));
                it.Enabled = sub.Enabled;
                m.Items.Add(it);
            }
            subOpen = true;
            m.Closed += (a, b) => { subOpen = false; BeginInvoke((Action)(delegate { if (!IsDisposed && ActiveForm != this) Close(); })); };
            var r = rowRects[i];
            m.Show(this, new Point(r.Right - S(4), r.Top));
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            int w = ClientSize.Width, h = ClientSize.Height;
            using (var b = new SolidBrush(LeftBg)) g.FillRectangle(b, 0, 0, leftW, h - bottomH);
            using (var b = new SolidBrush(RightBg)) g.FillRectangle(b, leftW, 0, w - leftW, h - bottomH);
            using (var b = new SolidBrush(BottomBg)) g.FillRectangle(b, 0, h - bottomH, w, bottomH);
            using (var p = new Pen(Line)) { g.DrawLine(p, 0, h - bottomH, w, h - bottomH); g.DrawLine(p, leftW, 0, leftW, h - bottomH); }

            for (int i = 0; i < entries.Count; i++)
            {
                var en = entries[i]; var r = rowRects[i];
                if (en.SepAbove) using (var p = new Pen(Line)) g.DrawLine(p, S(8), r.Top - sepH / 2, leftW - S(8), r.Top - sepH / 2);
                if (i == hotEntry && en.Enabled) using (var b = new SolidBrush(HoverBg)) g.FillRectangle(b, r);
                int isz = S(28);
                DrawIcon(g, en.Icon, new Rectangle(r.X + S(10), r.Y + (r.Height - isz) / 2, isz, isz), en.Enabled);
                TextRenderer.DrawText(g, Loc.T(en.Text), Font, new Rectangle(r.X + S(52), r.Y, r.Width - S(74), r.Height),
                    en.Enabled ? Color.FromArgb(232, 232, 232) : Color.FromArgb(110, 110, 116), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                if (en.Sub != null)
                {
                    int cx = r.Right - S(14), cy = r.Y + r.Height / 2, a = S(4);
                    using (var b = new SolidBrush(en.Enabled ? Color.FromArgb(190, 190, 195) : Color.FromArgb(90, 90, 96)))
                        g.FillPolygon(b, new[] { new Point(cx - a / 2, cy - a), new Point(cx - a / 2, cy + a), new Point(cx + a, cy) });
                }
            }

            TextRenderer.DrawText(g, Loc.T("Recent Files"), Font, new Rectangle(leftW + S(12), S(14), w - leftW - S(24), S(22)), Color.FromArgb(235, 235, 235), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var p = new Pen(Line)) g.DrawLine(p, leftW + S(8), S(38), w - S(8), S(38));
            if (recents.Count == 0)
                TextRenderer.DrawText(g, Loc.T("No recent files"), Font, new Rectangle(leftW + S(12), S(46), w - leftW - S(24), S(24)), Theme.TextDim, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            using (var ul = new Font(Font, FontStyle.Underline))
                for (int i = 0; i < recents.Count; i++)
                {
                    var r = recentRects[i];
                    if (i == hotRecent) using (var b = new SolidBrush(HoverBg)) g.FillRectangle(b, r);
                    string num = (i + 1).ToString();
                    var nsz = TextRenderer.MeasureText(g, num, ul, new Size(100, 40), TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, num, ul, new Rectangle(r.X + S(6), r.Y, nsz.Width + 2, r.Height), Color.FromArgb(225, 225, 225), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
                    TextRenderer.DrawText(g, recents[i].Text, Font, new Rectangle(r.X + S(6) + nsz.Width + S(5), r.Y, r.Width - nsz.Width - S(14), r.Height), Color.FromArgb(225, 225, 225),
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                }

            DrawBottom(g, rOptions, optionsEntry, hotBottom == 0);
            DrawBottom(g, rExit, exitEntry, hotBottom == 1);
            using (var p = new Pen(Color.FromArgb(90, 90, 96))) g.DrawRectangle(p, 0, 0, w - 1, h - 1);
        }

        void DrawBottom(Graphics g, Rectangle r, FileEntry en, bool hot)
        {
            if (hot) using (var b = new SolidBrush(HoverBg)) g.FillRectangle(b, r);
            int isz = S(16);
            g.DrawImage(Icons.Get(en.Icon, isz, true), r.X + S(8), r.Y + (r.Height - isz) / 2, isz, isz);
            TextRenderer.DrawText(g, Loc.T(en.Text), Font, new Rectangle(r.X + S(30), r.Y, r.Width - S(32), r.Height), Color.FromArgb(225, 225, 225), TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        }

        static void DrawIcon(Graphics g, string name, Rectangle r, bool enabled)
        {
            var ic = Icons.Get(name, r.Width, true);
            if (enabled) { g.DrawImage(ic, r); return; }
            var cm = new ColorMatrix { Matrix33 = 0.32f };
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(cm);
                g.DrawImage(ic, r, 0, 0, ic.Width, ic.Height, GraphicsUnit.Pixel, ia);
            }
        }

        // ------------------------------------------------------------------ mouse

        void HitTest(Point p, out int entry, out int recent, out int bottom)
        {
            entry = recent = bottom = -1;
            for (int i = 0; i < rowRects.Count; i++) if (rowRects[i].Contains(p)) { entry = i; return; }
            for (int i = 0; i < recentRects.Count; i++) if (recentRects[i].Contains(p)) { recent = i; return; }
            if (rOptions.Contains(p)) bottom = 0; else if (rExit.Contains(p)) bottom = 1;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int en, re, bo;
            HitTest(e.Location, out en, out re, out bo);
            if (en != hotEntry || re != hotRecent || bo != hotBottom)
            {
                hotEntry = en; hotRecent = re; hotBottom = bo;
                bool live = (en >= 0 && entries[en].Enabled) || re >= 0 || bo >= 0;
                Cursor = live ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!subOpen) { hotEntry = hotRecent = hotBottom = -1; Invalidate(); }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            int en, re, bo;
            HitTest(e.Location, out en, out re, out bo);
            if (en >= 0) Activate(en);
            else if (re >= 0) Run(recents[re].Click);
            else if (bo == 0) Run(optionsEntry.Click);
            else if (bo == 1) Run(exitEntry.Click);
        }
    }
}
