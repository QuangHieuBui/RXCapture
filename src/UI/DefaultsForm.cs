using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>
    /// "Default tool properties": pick a drawing tool, edit the colours / width / font / effects it starts with
    /// (with a live preview) or put it back to the built-in values. Saved values survive restarts.
    /// </summary>
    public class DefaultsForm : DarkForm
    {
        readonly ListBox lb = new ListBox { DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 34, IntegralHeight = false, BorderStyle = BorderStyle.None };
        readonly Button bEdit = new Button { Text = Loc.T("Edit…") }, bReset = new Button { Text = Loc.T("Reset") },
            bResetAll = new Button { Text = Loc.T("Reset all") }, bClose = new Button { Text = Loc.T("Close"), DialogResult = DialogResult.Cancel };

        static readonly string[] Icon = { "arrow", "line", "shape", "callout", "text", "step", "pen", "highlighter", "magnify" };
        static readonly string[] Name = { "Arrow", "Line", "Shape", "Callout", "Text", "Step", "Pen", "Highlighter", "Magnify" };

        public DefaultsForm()
        {
            Text = Loc.T("Default tool properties");
            ClientSize = new Size(500, 430);
            CancelButton = bClose;
            Controls.Add(new Label { Text = Loc.T("Choose a tool and set the properties new objects start with. Objects already drawn are not changed."), Left = 14, Top = 12, Width = 470, Height = 40, ForeColor = Theme.TextDim });
            lb.SetBounds(14, 58, 356, 310);
            lb.BackColor = Theme.Field;
            foreach (var t in ToolDefaultsStore.Configurable) lb.Items.Add(t);
            lb.DrawItem += DrawTool;
            lb.SelectedIndex = 0;
            lb.DoubleClick += (s, e) => EditSelected();
            Controls.Add(lb);
            bEdit.SetBounds(384, 58, 102, 30); bReset.SetBounds(384, 94, 102, 30); bResetAll.SetBounds(384, 130, 102, 30);
            bClose.SetBounds(384, 384, 102, 30);
            Controls.Add(bEdit); Controls.Add(bReset); Controls.Add(bResetAll); Controls.Add(bClose);
            bEdit.Click += (s, e) => EditSelected();
            bReset.Click += (s, e) => { var t = Current(); if (t != null) { ToolDefaultsStore.Reset(t.Value); lb.Invalidate(); } };
            bResetAll.Click += (s, e) =>
            {
                if (MessageBox.Show(this, Loc.T("Put every tool back to its built-in default?"), "RXCapture", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                { ToolDefaultsStore.ResetAll(); lb.Invalidate(); }
            };
        }

        Tool? Current() { return lb.SelectedItem is Tool ? (Tool?)(Tool)lb.SelectedItem : null; }

        void EditSelected()
        {
            var t = Current();
            if (t == null) return;
            EditTool(this, t.Value);
            lb.Invalidate();
        }

        static string Summary(Tool t)
        {
            var a = ToolDefaultsStore.Load(t);
            var parts = new List<string>();
            if (a.HasText) parts.Add(a.FontName + " " + (int)a.FontSize + " px");
            if (a.Kind != AnnKind.Stamp && a.Width > 0) parts.Add((int)a.Width + " px");
            if (a.Opacity < 0.999f) parts.Add((int)Math.Round(a.Opacity * 100) + "%");
            if (a.Shadow) parts.Add(Loc.T("shadow"));
            return string.Join("  •  ", parts.ToArray());
        }

        void DrawTool(object s, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            var t = (Tool)lb.Items[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            var g = e.Graphics;
            using (var b = new SolidBrush(sel ? Theme.Accent : (e.Index % 2 == 0 ? Theme.Field : Color.FromArgb(46, 46, 50)))) g.FillRectangle(b, e.Bounds);
            int i = Array.IndexOf(ToolDefaultsStore.Configurable, t);
            g.DrawImage(Icons.Get(Icon[i], 22, true), e.Bounds.X + 8, e.Bounds.Y + (e.Bounds.Height - 22) / 2, 22, 22);
            var a = ToolDefaultsStore.Load(t);
            // colour chip: outline colour (or fill when there is no outline)
            Color chip = a.Kind == AnnKind.Text ? a.TextColor : a.Stroke;
            using (var b = new SolidBrush(chip)) g.FillRectangle(b, e.Bounds.Right - 30, e.Bounds.Y + 9, 16, 16);
            g.DrawRectangle(Pens.Gray, e.Bounds.Right - 30, e.Bounds.Y + 9, 16, 16);
            bool custom = ToolDefaultsStore.IsCustom(t);
            TextRenderer.DrawText(g, Loc.T(Name[i]) + (custom ? "  ★" : ""), Font, new Rectangle(e.Bounds.X + 40, e.Bounds.Y + 2, e.Bounds.Width - 80, 16), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, Summary(t), Font, new Rectangle(e.Bounds.X + 40, e.Bounds.Y + 18, e.Bounds.Width - 80, 14), sel ? Color.FromArgb(220, 235, 250) : Theme.TextDim, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        // ------------------------------------------------------------------ the per-tool editor

        static readonly DashStyle[] Dashes = { DashStyle.Solid, DashStyle.Dash, DashStyle.Dot, DashStyle.DashDot };

        /// <summary>Edits the stored default of one tool. Returns true when it was saved.</summary>
        public static bool EditTool(IWin32Window owner, Tool tool)
        {
            var a = ToolDefaultsStore.Load(tool);
            var k = a.Kind;
            bool isText = k == AnnKind.Text || k == AnnKind.Callout;
            bool hasFont = isText || k == AnnKind.Step;
            bool hasFill = k == AnnKind.Shape || k == AnnKind.Callout || k == AnnKind.Text || k == AnnKind.Step;
            bool hasDash = k == AnnKind.Arrow || k == AnnKind.Line || k == AnnKind.Shape || k == AnnKind.Pen || k == AnnKind.Callout;
            bool canNoWidth = k == AnnKind.Shape || k == AnnKind.Callout || k == AnnKind.Text || k == AnnKind.Step || k == AnnKind.Magnify;
            string title = Loc.T("Default properties") + ": " + Loc.T(Name[Array.IndexOf(ToolDefaultsStore.Configurable, tool)]);

            using (var src = new Bitmap(280, 200))
            {
                using (var g = Graphics.FromImage(src)) g.Clear(Color.FromArgb(205, 210, 218));
                using (var d = new ParamDialog(title, src))
                {
                    ColorSwatch cStroke = null, cFill = null, cText = null;
                    NumericUpDown nWidth = null, nSize = null;
                    ComboBox cbDash = null, cbVariant = null, cbFont = null, cbAlign = null;
                    CheckBox chBold = null, chItalic = null, chUnder = null;

                    cStroke = d.AddColor("Outline colour", a.Stroke);
                    if (hasFill) cFill = d.AddColor("Fill colour", a.Fill, true);
                    nWidth = d.AddNumber("Outline width (px)", canNoWidth ? 0 : 1, 100, (int)a.Width);
                    if (hasDash) cbDash = d.AddCombo("Line type", new[] { "Solid", "Dash", "Dot", "Dash-dot" }, Math.Max(0, Array.IndexOf(Dashes, a.Dash)));
                    if (k == AnnKind.Arrow) cbVariant = d.AddCombo("Head", new[] { "Filled arrow", "Arrow on both ends", "Open arrow" }, a.Variant);
                    else if (k == AnnKind.Shape) cbVariant = d.AddCombo("Shape", new[] { "Rectangle", "Rounded rectangle", "Ellipse" }, a.Variant);
                    else if (k == AnnKind.Magnify) cbVariant = d.AddCombo("Lens", new[] { "Rectangle", "Circle" }, a.Variant);
                    NumericUpDown nStepSize = null;
                    if (k == AnnKind.Step) nStepSize = d.AddNumber("Default size (px)", 12, 300, (int)Math.Round(Math.Max(12f, a.Zoom)));
                    var sOpacity = d.AddSlider("Opacity (%)", 10, 100, (int)Math.Round(a.Opacity * 100));
                    var chShadow = d.AddCheck("Drop shadow", a.Shadow);
                    if (hasFont)
                    {
                        var names = new List<string>();
                        foreach (var f in FontFamily.Families) names.Add(f.Name);
                        if (!names.Contains(a.FontName)) names.Add(a.FontName);
                        cbFont = d.AddCombo("Font", names.ToArray(), Math.Max(0, names.IndexOf(a.FontName)));
                        nSize = d.AddNumber("Font size (px)", 6, 300, (int)Math.Round(a.FontSize));
                        cText = d.AddColor("Text colour", a.TextColor);
                        chBold = d.AddCheck("Bold", a.Bold);
                        if (isText) { chItalic = d.AddCheck("Italic", a.Italic); chUnder = d.AddCheck("Underline", a.Underline); }
                        if (isText) cbAlign = d.AddCombo("Align", new[] { "Left", "Centre", "Right" }, a.Align);
                    }

                    Func<Ann> compose = delegate
                    {
                        var r = a.Clone();
                        r.Stroke = cStroke.Value;
                        if (cFill != null) r.Fill = cFill.Value;
                        r.Width = (float)nWidth.Value;
                        if (cbDash != null) r.Dash = Dashes[Math.Max(0, cbDash.SelectedIndex)];
                        if (cbVariant != null) r.Variant = cbVariant.SelectedIndex;
                        r.Opacity = sOpacity.Value / 100f;
                        if (nStepSize != null) r.Zoom = (float)nStepSize.Value;
                        r.Shadow = chShadow.Checked;
                        if (hasFont)
                        {
                            r.FontName = cbFont.SelectedItem != null ? cbFont.SelectedItem.ToString() : a.FontName;
                            r.FontSize = (float)nSize.Value;
                            r.TextColor = cText.Value;
                            r.Bold = chBold.Checked;
                            if (isText) { r.Italic = chItalic.Checked; r.Underline = chUnder.Checked; r.Align = cbAlign.SelectedIndex; }
                        }
                        if (k == AnnKind.Text) r.Variant = r.Width > 0 ? 1 : 0;      // a text box only draws its outline when the width is above 0
                        return r;
                    };

                    d.PreviewFn = delegate(Bitmap p, float scale)
                    {
                        var bmp = new Bitmap(p);
                        var s = compose();
                        Sample(s, bmp.Width, bmp.Height);
                        using (var g = Graphics.FromImage(bmp))
                        {
                            g.SmoothingMode = SmoothingMode.AntiAlias;
                            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                            try { s.Draw(g, bmp); } catch { }
                        }
                        return bmp;
                    };
                    d.Finish(440);
                    if (d.ShowDialog(owner) != DialogResult.OK) return false;
                    ToolDefaultsStore.Save(tool, compose());
                    return true;
                }
            }
        }

        /// <summary>Lays a template out as a small sample inside a w x h preview.</summary>
        static void Sample(Ann s, int w, int h)
        {
            switch (s.Kind)
            {
                case AnnKind.Arrow: case AnnKind.Line: s.P1 = new PointF(w * 0.12f, h * 0.8f); s.P2 = new PointF(w * 0.88f, h * 0.25f); break;
                case AnnKind.Shape: s.P1 = new PointF(w * 0.2f, h * 0.22f); s.P2 = new PointF(w * 0.8f, h * 0.78f); break;
                case AnnKind.Callout:
                    s.AutoSize = true; s.Text = "Callout text";
                    s.Rect = new RectangleF(w * 0.12f, h * 0.14f, w * 0.6f, h * 0.36f);
                    s.FitText();
                    s.Tail = new PointF(w * 0.3f, h * 0.86f); break;
                case AnnKind.Text: s.AutoSize = true; s.Text = "Sample text"; s.P1 = new PointF(w * 0.14f, h * 0.38f); s.P2 = new PointF(w * 0.14f + 10, h * 0.38f + 10); s.FitText(); break;
                case AnnKind.Step: { float ss = Math.Min(120f, Math.Max(12f, s.Zoom)); s.Number = 1; s.P1 = new PointF(w * 0.5f - ss / 2, h * 0.5f - ss / 2); s.P2 = new PointF(w * 0.5f + ss / 2, h * 0.5f + ss / 2); break; }
                case AnnKind.Pen:
                    s.Pts = new List<PointF>();
                    for (int i = 0; i <= 24; i++) { float x = w * (0.1f + 0.8f * i / 24f); s.Pts.Add(new PointF(x, h * 0.5f + (float)Math.Sin(i / 24.0 * Math.PI * 2) * h * 0.22f)); }
                    break;
                case AnnKind.Magnify: s.P1 = new PointF(w * 0.3f, h * 0.25f); s.P2 = new PointF(w * 0.7f, h * 0.75f); break;
            }
        }
    }
}
