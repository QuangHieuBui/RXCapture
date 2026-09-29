using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>Clickable colour swatch (not restyled by the theme).</summary>
    public class ColorSwatch : Control
    {
        Color color;
        public bool AllowNone;
        public event EventHandler ValueChanged;
        public ColorSwatch(Color c) { color = c; Size = new Size(64, 24); Cursor = Cursors.Hand; SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true); }
        public Color Value { get { return color; } set { color = value; Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Ribbon);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var tb = new HatchBrush(HatchStyle.LargeCheckerBoard, Color.FromArgb(200, 200, 200), Color.White)) g.FillRectangle(tb, r);
            using (var b = new SolidBrush(color)) g.FillRectangle(b, r);
            using (var p = new Pen(Color.FromArgb(120, 120, 128))) g.DrawRectangle(p, r);
        }
        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            var m = ColorPalette.Menu(color, AllowNone, delegate(Color c) { Value = c; Raise(); }, delegate { Value = Color.Transparent; Raise(); });
            m.Show(this, new Point(0, Height));
        }
        void Raise() { if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
    }

    /// <summary>Simple vertical form builder with an optional live preview panel.</summary>
    public class ParamDialog : DarkForm
    {
        int y = 14;
        readonly int labelW = 130;
        readonly List<Control> inputs = new List<Control>();
        readonly Panel previewPanel = new Panel();
        Bitmap previewSrc, previewOut;
        public Func<Bitmap, float, Bitmap> PreviewFn;
        readonly Timer debounce = new Timer { Interval = 120 };

        public ParamDialog(string title, Bitmap source)
        {
            Text = Loc.T(title);
            ClientSize = new Size(420, 100);
            if (source != null)
            {
                float k = Math.Min(280f / source.Width, 200f / source.Height);
                if (k > 1) k = 1;
                previewSrc = LibraryStore.MakeThumb(source, 280, 200);
                PreviewScale = k;
            }
            debounce.Tick += delegate { debounce.Stop(); UpdatePreview(); };
        }

        public float PreviewScale = 1f;

        void Row(string label, Control c, int h)
        {
            var l = new Label { Text = Loc.T(label), Left = 14, Top = y + 3, Width = labelW, Height = 20, AutoEllipsis = true };
            c.Left = 14 + labelW; c.Top = y;
            Controls.Add(l); Controls.Add(c);
            inputs.Add(c);
            y += Math.Max(h, 24) + 8;
        }

        void Changed() { debounce.Stop(); debounce.Start(); }

        public NumericUpDown AddNumber(string label, int min, int max, int val)
        {
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)), Width = 90 };
            n.ValueChanged += delegate { Changed(); };
            Row(label, n, 24);
            return n;
        }

        public TrackBar AddSlider(string label, int min, int max, int val)
        {
            var t = new TrackBar { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, val)), TickStyle = TickStyle.None, AutoSize = false, Width = 180, Height = 28 };    // AutoSize would make it ~45px tall and cover the top of the next row
            var v = new Label { Left = 14 + labelW + 186, Top = y + 3, Width = 40, Text = t.Value.ToString() };
            t.ValueChanged += delegate { v.Text = t.Value.ToString(); Changed(); };
            Controls.Add(v);
            Row(label, t, 28);
            return t;
        }

        public ColorSwatch AddColor(string label, Color c, bool allowNone = false)
        {
            var s = new ColorSwatch(c) { AllowNone = allowNone };
            s.ValueChanged += delegate { Changed(); };
            Row(label, s, 24);
            return s;
        }

        public CheckBox AddCheck(string label, bool v)
        {
            var c = new CheckBox { Text = Loc.T(label), Checked = v, Width = 220, Left = 14 };
            c.CheckedChanged += delegate { Changed(); };
            c.Top = y; Controls.Add(c); inputs.Add(c);
            y += 30;
            return c;
        }

        public ComboBox AddCombo(string label, string[] items, int sel)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 180 };
            foreach (var it in items) c.Items.Add(Loc.T(it));
            c.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, sel));
            c.SelectedIndexChanged += delegate { Changed(); };
            Row(label, c, 24);
            return c;
        }

        public TextBox AddText(string label, string v)
        {
            var t = new TextBox { Text = v, Width = 220 };
            t.TextChanged += delegate { Changed(); };
            Row(label, t, 24);
            return t;
        }

        public void Finish(int width = 440)
        {
            int contentBottom = y;
            int px = 14 + labelW + 250;
            if (previewSrc != null && PreviewFn != null)
            {
                width = Math.Max(width, 14 + labelW + 270 + 300);
                previewPanel.SetBounds(14 + labelW + 270, 14, 290, 210);
                previewPanel.Paint += PreviewPaint;
                previewPanel.BackColor = Color.FromArgb(60, 60, 64);
                Controls.Add(previewPanel);
                contentBottom = Math.Max(contentBottom, 230);
            }
            var ok = new Button { Text = Loc.T("OK"), DialogResult = DialogResult.OK, Width = 88, Height = 28, Top = contentBottom + 6, Left = width - 2 * 96 - 8 };
            var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, Width = 88, Height = 28, Top = contentBottom + 6, Left = width - 96 - 8 };
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
            ClientSize = new Size(width, contentBottom + 48);
            if (previewSrc != null && PreviewFn != null) UpdatePreview();
        }

        void UpdatePreview()
        {
            if (previewSrc == null || PreviewFn == null) return;
            try
            {
                var old = previewOut;
                previewOut = PreviewFn(previewSrc, PreviewScale);
                if (old != null) old.Dispose();
            }
            catch { }
            previewPanel.Invalidate();
        }

        void PreviewPaint(object s, PaintEventArgs e)
        {
            var g = e.Graphics;
            var r = previewPanel.ClientRectangle;
            using (var tb = new HatchBrush(HatchStyle.LargeCheckerBoard, Color.FromArgb(200, 200, 200), Color.White)) g.FillRectangle(tb, r);
            var bmp = previewOut ?? previewSrc;
            if (bmp == null) return;
            float k = Math.Min((float)(r.Width - 8) / bmp.Width, (float)(r.Height - 8) / bmp.Height);
            if (k > 1) k = 1;
            int w = (int)(bmp.Width * k), h = (int)(bmp.Height * k);
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(bmp, (r.Width - w) / 2, (r.Height - h) / 2, w, h);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { debounce.Dispose(); if (previewSrc != null) previewSrc.Dispose(); if (previewOut != null) previewOut.Dispose(); }
            base.Dispose(disposing);
        }
    }

    public static class Dlg
    {
        public static bool Resize(IWin32Window owner, Document doc, out int w, out int h)
        {
            w = doc.Width; h = doc.Height;
            using (var d = new ParamDialog("Resize Image", null))
            {
                var mode = d.AddCombo("Unit", new[] { "Percent", "Pixels" }, 0);
                var nw = d.AddNumber("Width", 1, 30000, 100);
                var nh = d.AddNumber("Height", 1, 30000, 100);
                var keep = d.AddCheck("Keep aspect ratio", true);
                bool busy = false;
                double ratio = (double)doc.Width / doc.Height;
                Action setDefaults = delegate
                {
                    busy = true;
                    if (mode.SelectedIndex == 0) { nw.Value = 100; nh.Value = 100; }
                    else { nw.Value = doc.Width; nh.Value = doc.Height; }
                    busy = false;
                };
                mode.SelectedIndexChanged += delegate { setDefaults(); };
                nw.ValueChanged += delegate
                {
                    if (busy || !keep.Checked) return; busy = true;
                    if (mode.SelectedIndex == 0) nh.Value = nw.Value; else nh.Value = Math.Max(1, Math.Min(30000, (decimal)Math.Round((double)nw.Value / ratio)));
                    busy = false;
                };
                nh.ValueChanged += delegate
                {
                    if (busy || !keep.Checked) return; busy = true;
                    if (mode.SelectedIndex == 0) nw.Value = nh.Value; else nw.Value = Math.Max(1, Math.Min(30000, (decimal)Math.Round((double)nh.Value * ratio)));
                    busy = false;
                };
                d.Finish(420);
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                if (mode.SelectedIndex == 0) { w = (int)Math.Round(doc.Width * (double)nw.Value / 100.0); h = (int)Math.Round(doc.Height * (double)nh.Value / 100.0); }
                else { w = (int)nw.Value; h = (int)nh.Value; }
                return w > 0 && h > 0;
            }
        }

        public static bool CanvasSize(IWin32Window owner, Document doc, out int w, out int h, out int anchor, out Color fill)
        {
            w = doc.Width; h = doc.Height; anchor = 4; fill = Color.Transparent;
            using (var d = new ParamDialog("Canvas Size", null))
            {
                var nw = d.AddNumber("Width", 1, 30000, doc.Width);
                var nh = d.AddNumber("Height", 1, 30000, doc.Height);
                var col = d.AddColor("Fill colour", Color.Transparent, true);
                // 3x3 anchor grid
                var grid = new RadioButton[9];
                var host = new Panel { Width = 96, Height = 78 };
                for (int i = 0; i < 9; i++)
                {
                    grid[i] = new RadioButton { Appearance = Appearance.Button, Width = 28, Height = 22, Left = (i % 3) * 30, Top = (i / 3) * 24, Checked = i == 4, FlatStyle = FlatStyle.Flat, Tag = i };
                    host.Controls.Add(grid[i]);
                }
                var lbl = new Label { Text = Loc.T("Anchor"), Left = 14, Top = 118, Width = 130, Height = 20 };
                host.Left = 144; host.Top = 116;
                d.Controls.Add(lbl); d.Controls.Add(host);
                d.Finish(400);
                d.Height += 90;
                foreach (Control c in d.Controls) if (c is Button) c.Top += 90;
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                w = (int)nw.Value; h = (int)nh.Value; fill = col.Value;
                for (int i = 0; i < 9; i++) if (grid[i].Checked) anchor = i;
                return true;
            }
        }

        public static bool Adjust(IWin32Window owner, Bitmap src, out int br, out int co, out int sa, out int hu, out float gamma)
        {
            br = co = sa = hu = 0; gamma = 1f;
            using (var d = new ParamDialog("Adjust Colours", src))
            {
                var b = d.AddSlider("Brightness", -100, 100, 0);
                var c = d.AddSlider("Contrast", -100, 100, 0);
                var s = d.AddSlider("Saturation", -100, 100, 0);
                var h = d.AddSlider("Hue", -180, 180, 0);
                var g = d.AddSlider("Gamma (x10)", 3, 30, 10);
                d.PreviewFn = delegate(Bitmap p, float k) { return Effects.Adjust(p, b.Value, c.Value, s.Value, h.Value, g.Value / 10f); };
                d.Finish(460);
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                br = b.Value; co = c.Value; sa = s.Value; hu = h.Value; gamma = g.Value / 10f;
                return true;
            }
        }

        public static bool Border(IWin32Window owner, Bitmap src, out int width, out Color color)
        {
            width = 4; color = Color.Black;
            using (var d = new ParamDialog("Border", src))
            {
                var w = d.AddNumber("Width (px)", 1, 200, 4);
                var c = d.AddColor("Colour", Color.FromArgb(40, 40, 40));
                d.PreviewFn = delegate(Bitmap p, float k) { return Effects.Border(p, Math.Max(1, (int)(w.Value * (decimal)k)), c.Value); };
                d.Finish(420);
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                width = (int)w.Value; color = c.Value; return true;
            }
        }

        public static bool Shadow(IWin32Window owner, Bitmap src, out int dx, out int dy, out int blur, out Color color, out int opacity)
        {
            dx = dy = 6; blur = 8; color = Color.Black; opacity = 60;
            using (var d = new ParamDialog("Drop Shadow", src))
            {
                var x = d.AddNumber("Offset X", -100, 100, 6);
                var y = d.AddNumber("Offset Y", -100, 100, 6);
                var b = d.AddNumber("Blur", 0, 60, 8);
                var o = d.AddSlider("Opacity %", 0, 100, 60);
                var c = d.AddColor("Colour", Color.Black);
                d.PreviewFn = delegate(Bitmap p, float k)
                {
                    Point off;
                    return Effects.DropShadow(p, (int)(x.Value * (decimal)k), (int)(y.Value * (decimal)k), (int)(b.Value * (decimal)k), c.Value, o.Value, out off);
                };
                d.Finish(420);
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                dx = (int)x.Value; dy = (int)y.Value; blur = (int)b.Value; color = c.Value; opacity = o.Value; return true;
            }
        }

        public static bool Edges(IWin32Window owner, Bitmap src, out bool top, out bool right, out bool bottom, out bool left, out int depth, out int tooth, out int style)
        {
            top = right = bottom = left = true; depth = 8; tooth = 16; style = 2;
            using (var d = new ParamDialog("Torn Edge", src))
            {
                var st = d.AddCombo("Style", new[] { "Zigzag", "Wave", "Torn" }, 2);
                var dp = d.AddNumber("Depth (px)", 2, 100, 8);
                var th = d.AddNumber("Tooth size (px)", 4, 100, 16);
                var t = d.AddCheck("Top", true); var r = d.AddCheck("Right", true); var b = d.AddCheck("Bottom", true); var l = d.AddCheck("Left", true);
                d.PreviewFn = delegate(Bitmap p, float k) { return Effects.EdgeEffect(p, t.Checked, r.Checked, b.Checked, l.Checked, Math.Max(2, (int)(dp.Value * (decimal)k)), Math.Max(4, (int)(th.Value * (decimal)k)), st.SelectedIndex); };
                d.Finish(420);
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                top = t.Checked; right = r.Checked; bottom = b.Checked; left = l.Checked; depth = (int)dp.Value; tooth = (int)th.Value; style = st.SelectedIndex;
                return true;
            }
        }

        public static bool Watermark(IWin32Window owner, Bitmap src, out string text, out int size, out Color color, out int opacity, out int anchor, out int margin, out bool bold)
        {
            text = "RXCapture"; size = 32; color = Color.White; opacity = 60; anchor = 8; margin = 16; bold = true;
            using (var d = new ParamDialog("Watermark", src))
            {
                var t = d.AddText("Text", "RXCapture");
                var s = d.AddNumber("Font size (px)", 6, 400, 32);
                var b = d.AddCheck("Bold", true);
                var c = d.AddColor("Colour", Color.White);
                var o = d.AddSlider("Opacity %", 5, 100, 60);
                var a = d.AddCombo("Position", new[] { "Top left", "Top centre", "Top right", "Middle left", "Centre", "Middle right", "Bottom left", "Bottom centre", "Bottom right" }, 8);
                var m = d.AddNumber("Margin (px)", 0, 500, 16);
                d.PreviewFn = delegate(Bitmap p, float k) { return Effects.WatermarkText(p, t.Text, "Segoe UI", (float)Math.Max(4.0, (double)s.Value * k), c.Value, o.Value, a.SelectedIndex, (int)((double)m.Value * k), b.Checked); };
                d.Finish(430);
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                text = t.Text; size = (int)s.Value; color = c.Value; opacity = o.Value; anchor = a.SelectedIndex; margin = (int)m.Value; bold = b.Checked;
                return true;
            }
        }

        public static string Prompt(IWin32Window owner, string title, string label, string def)
        {
            using (var d = new ParamDialog(title, null))
            {
                var t = d.AddText(label, def);
                d.Finish(430);
                return d.ShowDialog(owner) == DialogResult.OK ? t.Text.Trim() : null;
            }
        }

        /// <summary>Returns the new hotkey text ("" = none) or null if cancelled.</summary>
        public static string HotkeyPrompt(IWin32Window owner, string title, string current)
        {
            using (var d = new ParamDialog(title, null))
            {
                var hk = new HotkeyBox { Text = current, Width = 200, Left = 14 + 130, Top = 14 };
                d.Controls.Add(new Label { Text = Loc.T("Hotkey"), Left = 14, Top = 17, Width = 120, Height = 20 });
                d.Controls.Add(hk);
                d.Controls.Add(new Label { Text = Loc.T("Click the box and press the new combination. Backspace clears it."), Left = 14, Top = 50, Width = 360, Height = 36, ForeColor = Theme.TextDim });
                var ok = new Button { Text = Loc.T("OK"), DialogResult = DialogResult.OK, Width = 88, Height = 28, Left = 190, Top = 100 };
                var cancel = new Button { Text = Loc.T("Cancel"), DialogResult = DialogResult.Cancel, Width = 88, Height = 28, Left = 286, Top = 100 };
                d.Controls.Add(ok); d.Controls.Add(cancel);
                d.AcceptButton = ok; d.CancelButton = cancel;
                d.ClientSize = new Size(390, 144);
                return d.ShowDialog(owner) == DialogResult.OK ? hk.Text : null;
            }
        }

        public static bool Number(IWin32Window owner, string title, string label, int min, int max, ref int value)
        {
            using (var d = new ParamDialog(title, null))
            {
                var n = d.AddNumber(label, min, max, value);
                d.Finish(360);
                if (d.ShowDialog(owner) != DialogResult.OK) return false;
                value = (int)n.Value; return true;
            }
        }
    }
}
