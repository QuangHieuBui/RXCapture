using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>Colour swatch grid shown inside a ToolStripDropDown (Outline / Fill / Text colour pickers).</summary>
    public class ColorPalette : Control
    {
        static readonly Color[] Colors = {
            Color.FromArgb(0,0,0), Color.FromArgb(64,64,64), Color.FromArgb(128,128,128), Color.FromArgb(192,192,192), Color.FromArgb(255,255,255),
            Color.FromArgb(229,57,53), Color.FromArgb(244,143,177), Color.FromArgb(255,152,0), Color.FromArgb(255,235,59), Color.FromArgb(255,214,10),
            Color.FromArgb(76,175,80), Color.FromArgb(0,150,136), Color.FromArgb(3,169,244), Color.FromArgb(33,101,235), Color.FromArgb(63,81,181),
            Color.FromArgb(156,39,176), Color.FromArgb(121,85,72), Color.FromArgb(183,28,28), Color.FromArgb(230,81,0), Color.FromArgb(27,94,32),
            Color.FromArgb(13,71,161), Color.FromArgb(74,20,140), Color.FromArgb(255,205,210), Color.FromArgb(255,224,178), Color.FromArgb(255,249,196),
            Color.FromArgb(200,230,201), Color.FromArgb(178,235,242), Color.FromArgb(187,222,251), Color.FromArgb(225,190,231), Color.FromArgb(215,204,200)
        };

        readonly Action<Color> pick;
        readonly Action none;
        readonly Action more;
        readonly Color current;
        int hover = -1;
        const int Cols = 5;

        public ColorPalette(Color current, bool allowNone, Action<Color> pick, Action none, Action more)
        {
            this.pick = pick; this.none = allowNone ? none : null; this.more = more; this.current = current;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            float s = Theme.Scale(this);
            int cell = (int)(26 * s);
            Width = Cols * cell + 12;
            Height = (Colors.Length / Cols) * cell + 12 + (int)(28 * s) * (this.none != null ? 2 : 1);
            BackColor = Color.FromArgb(37, 37, 38);
        }

        int Cell { get { return (int)(26 * Theme.Scale(this)); } }

        Rectangle SwatchRect(int i) { int c = Cell; return new Rectangle(6 + (i % Cols) * c, 6 + (i / Cols) * c, c - 3, c - 3); }
        Rectangle NoneRect { get { int rows = Colors.Length / Cols; return new Rectangle(6, 6 + rows * Cell + 2, Width - 12, (int)(24 * Theme.Scale(this))); } }
        Rectangle MoreRect { get { int rows = Colors.Length / Cols; int off = none != null ? (int)(28 * Theme.Scale(this)) : 0; return new Rectangle(6, 6 + rows * Cell + 2 + off, Width - 12, (int)(24 * Theme.Scale(this))); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            for (int i = 0; i < Colors.Length; i++)
            {
                var r = SwatchRect(i);
                using (var b = new SolidBrush(Colors[i])) g.FillRectangle(b, r);
                bool sel = Colors[i].ToArgb() == Color.FromArgb(255, current).ToArgb() && current.A > 0;
                using (var p = new Pen(i == hover ? Color.White : (sel ? Theme.AccentLight : Color.FromArgb(90, 90, 96)), sel || i == hover ? 2f : 1f)) g.DrawRectangle(p, r);
            }
            if (none != null) DrawButton(g, NoneRect, Loc.T("No colour"), hover == 100);
            DrawButton(g, MoreRect, Loc.T("More colours…"), hover == 101);
        }

        void DrawButton(Graphics g, Rectangle r, string text, bool hot)
        {
            using (var b = new SolidBrush(hot ? Color.FromArgb(70, 70, 76) : Color.FromArgb(52, 52, 56))) g.FillRectangle(b, r);
            TextRenderer.DrawText(g, text, Font, r, Theme.Text, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        int Hit(Point p)
        {
            for (int i = 0; i < Colors.Length; i++) if (SwatchRect(i).Contains(p)) return i;
            if (none != null && NoneRect.Contains(p)) return 100;
            if (MoreRect.Contains(p)) return 101;
            return -1;
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); int h = Hit(e.Location); if (h != hover) { hover = h; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); hover = -1; Invalidate(); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            int h = Hit(e.Location);
            if (h < 0) return;
            var dd = Parent as ToolStripDropDown;
            if (h < Colors.Length) pick(Colors[h]);
            else if (h == 100) none();
            else if (more != null) more();
            if (dd != null) dd.Close();
        }

        /// <summary>Shows the palette below a ribbon item and calls back with the chosen colour.</summary>
        public static ContextMenuStrip Menu(Color current, bool allowNone, Action<Color> pick, Action none)
        {
            var dd = Theme.Menu();
            dd.Padding = Padding.Empty;
            Action more = delegate
            {
                using (var cd = new ColorDialog { Color = current.A == 0 ? Color.Red : current, FullOpen = true, AnyColor = true })
                    if (cd.ShowDialog() == DialogResult.OK) pick(cd.Color);
            };
            var pal = new ColorPalette(current, allowNone, pick, none, more);
            var host = new ToolStripControlHost(pal) { Margin = Padding.Empty, Padding = Padding.Empty, AutoSize = false, Size = pal.Size };
            dd.Items.Add(host);
            return dd;
        }
    }

    /// <summary>Small helper for building simple value menus (line width, font size...).</summary>
    public static class MenuUtil
    {
        public static ContextMenuStrip ValueMenu(IEnumerable<string> labels, Func<int, bool> isChecked, Action<int> pick)
        {
            var m = Theme.Menu();
            int i = 0;
            foreach (var l in labels)
            {
                int idx = i++;
                var it = new ToolStripMenuItem(l);
                it.ForeColor = Theme.Text;
                it.Checked = isChecked != null && isChecked(idx);
                it.Click += delegate { pick(idx); };
                m.Items.Add(it);
            }
            return m;
        }
    }
}
