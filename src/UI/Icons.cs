using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace RXCapture
{
    /// <summary>All toolbar / ribbon icons are drawn with GDI+ on a 24x24 grid, so they stay sharp at any DPI.</summary>
    public static class Icons
    {
        public static readonly Color Ink = Color.FromArgb(55, 65, 81);
        public static readonly Color Accent = Color.FromArgb(37, 99, 235);
        public static readonly Color Red = Color.FromArgb(220, 38, 38);
        public static readonly Color Green = Color.FromArgb(22, 163, 74);
        public static readonly Color Amber = Color.FromArgb(245, 158, 11);

        static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();

        public static Bitmap Get(string name, int size) { return Get(name, size, false); }

        public static Bitmap Get(string name, int size, bool light)
        {
            string key = name + "|" + size + "|" + light;
            Bitmap b;
            if (cache.TryGetValue(key, out b)) return b;
            b = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.ScaleTransform(size / 24f, size / 24f);
                Paint(g, name, light);
            }
            cache[key] = b;
            return b;
        }

        static Pen P(Color c, float w)
        {
            var p = new Pen(c, w);
            p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; p.LineJoin = LineJoin.Round;
            return p;
        }

        static GraphicsPath RR(float x, float y, float w, float h, float r)
        {
            var p = new GraphicsPath();
            float d = r * 2;
            p.AddArc(x, y, d, d, 180, 90); p.AddArc(x + w - d, y, d, d, 270, 90);
            p.AddArc(x + w - d, y + h - d, d, d, 0, 90); p.AddArc(x, y + h - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static void Star(Graphics g, Brush fill, Pen pen, float cx, float cy, float R, float r)
        {
            var pts = new PointF[10];
            for (int i = 0; i < 10; i++)
            {
                double a = -Math.PI / 2 + i * Math.PI / 5;
                float rad = (i % 2 == 0) ? R : r;
                pts[i] = new PointF(cx + (float)Math.Cos(a) * rad, cy + (float)Math.Sin(a) * rad);
            }
            if (fill != null) g.FillPolygon(fill, pts);
            if (pen != null) g.DrawPolygon(pen, pts);
        }

        static void Paint(Graphics g, string n, bool light)
        {
            Color ink = light ? Color.White : Ink;
            Color acc = light ? Color.FromArgb(96, 176, 255) : Accent;
            using (var pen = P(ink, 2.3f))
            using (var tp = P(ink, 1.5f))
            using (var accPen = P(acc, 2.5f))
            using (var inkB = new SolidBrush(ink))
            using (var accB = new SolidBrush(acc))
            {
                Color dark = Color.FromArgb(28, 28, 32), blue = Color.FromArgb(44, 132, 240), coral = Color.FromArgb(255, 96, 78), gold = Color.FromArgb(255, 196, 40);
                switch (n)
                {
                    // ---- drawing tools: thin single-colour outlines (no fills), 1.5 wide on the 24 grid
                    case "select":
                        g.DrawPolygon(tp, new[] { new PointF(5.5f, 3), new PointF(5.5f, 20), new PointF(9.6f, 16.2f), new PointF(12.4f, 22), new PointF(15, 20.8f), new PointF(12.2f, 15.2f), new PointF(18, 15) });
                        break;
                    case "arrow":
                        g.DrawLine(tp, 5, 19, 19, 5);
                        g.DrawLines(tp, new[] { new PointF(9.5f, 5), new PointF(19, 5), new PointF(19, 14.5f) });
                        break;
                    case "line":
                        g.DrawLine(tp, 6.3f, 17.7f, 17.7f, 6.3f);
                        g.DrawEllipse(tp, 2.7f, 17.7f, 3.6f, 3.6f); g.DrawEllipse(tp, 17.7f, 2.7f, 3.6f, 3.6f);
                        break;
                    case "shape":
                        using (var rp = RR(2.5f, 5, 12.5f, 10, 2)) g.DrawPath(tp, rp);
                        g.DrawEllipse(tp, 10.5f, 9.5f, 11, 11);
                        break;
                    case "callout":
                        using (var rp = RR(2.5f, 3.5f, 19, 13, 3.5f)) g.DrawPath(tp, rp);
                        g.DrawLines(tp, new[] { new PointF(7, 16.5f), new PointF(6, 21), new PointF(11.5f, 16.5f) });
                        g.DrawLine(tp, 7, 8.2f, 17, 8.2f); g.DrawLine(tp, 7, 11.8f, 13, 11.8f);
                        break;
                    case "text":
                        g.DrawLine(tp, 5, 5.5f, 19, 5.5f); g.DrawLine(tp, 5, 5.5f, 5, 8.5f); g.DrawLine(tp, 19, 5.5f, 19, 8.5f);
                        g.DrawLine(tp, 12, 5.5f, 12, 19); g.DrawLine(tp, 8.5f, 19, 15.5f, 19);
                        break;
                    case "step":
                        g.DrawEllipse(tp, 3, 3, 18, 18);
                        g.DrawLines(tp, new[] { new PointF(10, 9.8f), new PointF(12.6f, 7.6f), new PointF(12.6f, 16.6f) });
                        g.DrawLine(tp, 10.2f, 16.6f, 15, 16.6f);
                        break;
                    case "stamp":
                        Star(g, null, tp, 12, 12.6f, 9.8f, 4.3f);
                        break;
                    case "pen":
                        g.DrawPolygon(tp, new[] { new PointF(15.5f, 4.5f), new PointF(19.5f, 8.5f), new PointF(8.5f, 19.5f), new PointF(3.8f, 20.2f), new PointF(4.5f, 15.5f) });
                        g.DrawLine(tp, 12.8f, 7.2f, 16.8f, 11.2f);
                        break;
                    case "highlighter":
                        g.DrawPolygon(tp, new[] { new PointF(6.5f, 12.5f), new PointF(13.5f, 5.5f), new PointF(19, 11), new PointF(12, 18) });
                        g.DrawPolygon(tp, new[] { new PointF(6.5f, 12.5f), new PointF(12, 18), new PointF(4, 19.5f) });
                        g.DrawLine(tp, 8.5f, 22.2f, 21, 22.2f);
                        break;
                    case "fill":
                        g.DrawPolygon(tp, new[] { new PointF(10, 3), new PointF(18, 11), new PointF(10, 19), new PointF(2, 11) });
                        g.DrawLine(tp, 3.7f, 12.7f, 16.3f, 12.7f);
                        g.DrawEllipse(tp, 17.8f, 14.8f, 4, 5.4f);
                        break;
                    case "blur":
                        using (var rp = RR(3, 3, 18, 18, 1.5f)) g.DrawPath(tp, rp);
                        g.DrawLine(tp, 9, 3, 9, 21); g.DrawLine(tp, 15, 3, 15, 21); g.DrawLine(tp, 3, 9, 21, 9); g.DrawLine(tp, 3, 15, 21, 15);
                        using (var fb = new SolidBrush(Color.FromArgb(95, ink)))
                        {
                            g.FillRectangle(fb, 3.75f, 3.75f, 4.7f, 4.7f); g.FillRectangle(fb, 15.75f, 9.75f, 4.7f, 4.7f); g.FillRectangle(fb, 9.75f, 15.75f, 4.7f, 4.7f);
                        }
                        break;
                    case "magnify":
                        g.DrawEllipse(tp, 3.5f, 3.5f, 12, 12);
                        g.DrawLine(tp, 14, 14, 20.5f, 20.5f);
                        g.DrawLine(tp, 6.5f, 9.5f, 12.5f, 9.5f); g.DrawLine(tp, 9.5f, 6.5f, 9.5f, 12.5f);
                        break;
                    case "spotlight":
                        using (var rp = RR(2.5f, 4.5f, 19, 15, 2)) g.DrawPath(tp, rp);
                        g.DrawEllipse(tp, 7.5f, 7.5f, 9, 9);
                        break;
                    case "eraser":
                        g.DrawPolygon(tp, new[] { new PointF(3.5f, 14), new PointF(11, 6), new PointF(20, 15), new PointF(14.5f, 20.5f), new PointF(9, 20.5f) });
                        g.DrawLine(tp, 7.8f, 17.7f, 15.2f, 10.3f);
                        g.DrawLine(tp, 12.5f, 20.5f, 22, 20.5f);
                        break;
                    case "crop":
                        g.DrawLines(tp, new[] { new PointF(7, 2.5f), new PointF(7, 17), new PointF(21.5f, 17) });
                        g.DrawLines(tp, new[] { new PointF(2.5f, 7), new PointF(17, 7), new PointF(17, 21.5f) });
                        break;
                    case "cutout":
                        g.DrawLine(pen, 7, 4, 17, 15); g.DrawLine(pen, 17, 4, 7, 15);
                        g.DrawEllipse(accPen, 3.5f, 15, 5, 5); g.DrawEllipse(accPen, 15.5f, 15, 5, 5);
                        break;
                    case "camera":
                        using (var rp = RR(2.5f, 7, 19, 13, 2.5f)) g.DrawPath(pen, rp);
                        g.DrawLines(pen, new[] { new PointF(8, 7), new PointF(9.5f, 4), new PointF(14.5f, 4), new PointF(16, 7) });
                        g.DrawEllipse(accPen, 8, 9.5f, 8, 8);
                        break;
                    case "video":
                        using (var rp = RR(2.5f, 6, 13.5f, 12, 2.5f)) g.DrawPath(pen, rp);
                        g.FillPolygon(accB, new[] { new PointF(17, 10.5f), new PointF(21.5f, 7.5f), new PointF(21.5f, 16.5f), new PointF(17, 13.5f) });
                        break;
                    case "region":
                        using (var dp = P(ink, 2.3f)) { dp.DashStyle = DashStyle.Dash; g.DrawRectangle(dp, 4, 5, 16, 14); }
                        g.FillRectangle(accB, 2.5f, 3.5f, 3, 3); g.FillRectangle(accB, 18.5f, 3.5f, 3, 3); g.FillRectangle(accB, 2.5f, 17.5f, 3, 3); g.FillRectangle(accB, 18.5f, 17.5f, 3, 3);
                        break;
                    case "window":
                        using (var rp = RR(2.5f, 4, 19, 16, 2)) g.DrawPath(pen, rp);
                        g.DrawLine(pen, 2.5f, 8.5f, 21.5f, 8.5f);
                        g.FillEllipse(accB, 5, 5.3f, 2, 2); g.FillEllipse(accB, 8.5f, 5.3f, 2, 2);
                        break;
                    case "fullscreen":
                        using (var rp = RR(2.5f, 4, 19, 12.5f, 2)) g.DrawPath(pen, rp);
                        g.DrawLine(pen, 12, 16.5f, 12, 20); g.DrawLine(pen, 8, 20, 16, 20);
                        break;
                    case "scroll":
                        using (var rp = RR(4, 3, 11, 18, 2)) g.DrawPath(pen, rp);
                        g.DrawLines(accPen, new[] { new PointF(18, 9), new PointF(20, 6.5f), new PointF(22, 9) });
                        g.DrawLines(accPen, new[] { new PointF(18, 15), new PointF(20, 17.5f), new PointF(22, 15) });
                        g.DrawLine(accPen, 20, 7, 20, 17);
                        break;
                    case "freehand":
                        g.DrawCurve(P(ink, 2.3f), new[] { new PointF(4, 16), new PointF(6, 8), new PointF(11, 6), new PointF(16, 10), new PointF(19, 17), new PointF(12, 19) }, 0.5f);
                        g.FillEllipse(accB, 2.5f, 14.5f, 3.5f, 3.5f);
                        break;
                    case "fixed":
                        using (var dp = P(ink, 2.3f)) g.DrawRectangle(dp, 4, 6, 16, 12);
                        g.DrawLine(accPen, 4, 21, 20, 21); g.DrawLine(accPen, 4, 19.5f, 4, 22.5f); g.DrawLine(accPen, 20, 19.5f, 20, 22.5f);
                        break;
                    case "repeat":
                        g.DrawArc(accPen, 4.5f, 4.5f, 15, 15, -50, 290);
                        g.FillPolygon(accB, new[] { new PointF(15.5f, 2.5f), new PointF(20.5f, 4.5f), new PointF(16, 8) });
                        break;
                    case "new":
                        g.DrawLine(accPen, 12, 5, 12, 19); g.DrawLine(accPen, 5, 12, 19, 12);
                        break;
                    case "open":
                        {
                            var back = new[] { new PointF(2.5f, 20), new PointF(2.5f, 4.5f), new PointF(9, 4.5f), new PointF(11.2f, 7.2f), new PointF(19.5f, 7.2f), new PointF(19.5f, 11) };
                            g.DrawLines(pen, back);
                            var front = new[] { new PointF(2.5f, 20), new PointF(6, 10.5f), new PointF(22.5f, 10.5f), new PointF(19, 20) };
                            using (var fb = new SolidBrush(Color.FromArgb(150, acc))) g.FillPolygon(fb, front);
                            g.DrawPolygon(pen, front);
                            break;
                        }
                    case "save":
                        using (var rp = RR(2.5f, 2.5f, 19, 19, 3)) g.DrawPath(pen, rp);
                        g.FillRectangle(inkB, 7, 2.5f, 9, 6.5f);
                        using (var lb = new SolidBrush(Color.FromArgb(44, 132, 240))) g.FillRectangle(lb, 6.5f, 13, 11, 8);
                        break;
                    case "copy":
                        using (var rp = RR(2.5f, 2.5f, 12.5f, 14, 2.5f)) g.DrawPath(accPen, rp);
                        using (var rp = RR(9, 8.5f, 12.5f, 13, 2.5f))
                        {
                            using (var fb = new SolidBrush(Color.FromArgb(70, 70, 76))) g.FillPath(fb, rp);
                            g.DrawPath(pen, rp);
                        }
                        break;
                    case "paste":
                        using (var rp = RR(3.5f, 4.5f, 17, 17, 2.5f)) g.DrawPath(pen, rp);
                        using (var cb = new SolidBrush(Color.FromArgb(44, 132, 240))) using (var cp = RR(8, 2, 8, 5.5f, 1.8f)) g.FillPath(cb, cp);
                        g.DrawLine(pen, 7.5f, 12.5f, 16.5f, 12.5f); g.DrawLine(pen, 7.5f, 16.5f, 13.5f, 16.5f);
                        break;
                    case "undo":
                        g.DrawArc(pen, 5, 6, 15, 12, -90, 180);
                        g.DrawLine(pen, 12.5f, 6, 12.5f, 6);
                        g.FillPolygon(inkB, new[] { new PointF(3.5f, 6), new PointF(9.5f, 1.8f), new PointF(9.5f, 10.2f) });
                        break;
                    case "redo":
                        g.DrawArc(pen, 4, 6, 15, 12, -90, -180);
                        g.FillPolygon(inkB, new[] { new PointF(20.5f, 6), new PointF(14.5f, 1.8f), new PointF(14.5f, 10.2f) });
                        break;
                    case "zoomin":
                        g.DrawEllipse(pen, 3.5f, 3.5f, 12, 12); g.DrawLine(P(ink, 3f), 14, 14, 20, 20);
                        g.DrawLine(accPen, 6.5f, 9.5f, 12.5f, 9.5f); g.DrawLine(accPen, 9.5f, 6.5f, 9.5f, 12.5f);
                        break;
                    case "zoomout":
                        g.DrawEllipse(pen, 3.5f, 3.5f, 12, 12); g.DrawLine(P(ink, 3f), 14, 14, 20, 20);
                        g.DrawLine(accPen, 6.5f, 9.5f, 12.5f, 9.5f);
                        break;
                    case "fit":
                        g.DrawLines(pen, new[] { new PointF(3, 8), new PointF(3, 3), new PointF(8, 3) });
                        g.DrawLines(pen, new[] { new PointF(16, 3), new PointF(21, 3), new PointF(21, 8) });
                        g.DrawLines(pen, new[] { new PointF(21, 16), new PointF(21, 21), new PointF(16, 21) });
                        g.DrawLines(pen, new[] { new PointF(8, 21), new PointF(3, 21), new PointF(3, 16) });
                        g.DrawRectangle(accPen, 8, 8, 8, 8);
                        break;
                    case "actual":
                        using (var f = new Font("Segoe UI", 10, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString("1:1", f, inkB, new RectangleF(0, 0, 24, 24), sf);
                        break;
                    case "settings":
                        for (int i = 0; i < 8; i++)
                        {
                            double a = i * Math.PI / 4;
                            g.DrawLine(P(ink, 3f), 12 + (float)Math.Cos(a) * 7, 12 + (float)Math.Sin(a) * 7, 12 + (float)Math.Cos(a) * 9.3f, 12 + (float)Math.Sin(a) * 9.3f);
                        }
                        g.DrawEllipse(pen, 5.5f, 5.5f, 13, 13); g.DrawEllipse(accPen, 9.5f, 9.5f, 5, 5);
                        break;
                    case "library":
                        for (int i = 0; i < 2; i++) for (int j = 0; j < 2; j++)
                            using (var rp = RR(3 + i * 10, 3 + j * 10, 8, 8, 1.5f)) { g.DrawPath(pen, rp); if (i == j) g.FillPath(new SolidBrush(Color.FromArgb(90, acc)), rp); }
                        break;
                    case "print":
                        g.DrawRectangle(pen, 7, 3, 10, 5); g.DrawRectangle(pen, 7, 14, 10, 7);
                        using (var rp = RR(3, 8, 18, 8, 2)) g.DrawPath(pen, rp);
                        break;
                    case "email":
                        g.DrawRectangle(pen, 3, 5.5f, 18, 13);
                        g.DrawLines(accPen, new[] { new PointF(3, 6), new PointF(12, 13), new PointF(21, 6) });
                        break;
                    case "folder":
                        g.DrawLines(pen, new[] { new PointF(3, 19), new PointF(3, 5), new PointF(9.5f, 5), new PointF(11.5f, 8), new PointF(21, 8), new PointF(21, 19), new PointF(3, 19) });
                        break;
                    case "rotate":
                        g.DrawArc(pen, 4, 4, 16, 16, -30, 250);
                        g.FillPolygon(accB, new[] { new PointF(15, 1.5f), new PointF(21.5f, 4), new PointF(16.5f, 8.5f) });
                        break;
                    case "flip":
                        g.DrawLine(accPen, 12, 2.5f, 12, 21.5f);
                        g.DrawPolygon(pen, new[] { new PointF(9.5f, 6), new PointF(9.5f, 18), new PointF(2.5f, 18) });
                        g.FillPolygon(inkB, new[] { new PointF(14.5f, 6), new PointF(14.5f, 18), new PointF(21.5f, 18) });
                        break;
                    case "resize":
                        g.DrawRectangle(pen, 3, 9, 12, 12);
                        g.DrawRectangle(accPen, 8, 3, 13, 13);
                        break;
                    case "canvas":
                        using (var dp = P(ink, 2.1f)) { dp.DashStyle = DashStyle.Dash; g.DrawRectangle(dp, 2.5f, 2.5f, 19, 19); }
                        g.FillRectangle(new SolidBrush(Color.FromArgb(120, acc)), 7, 7, 10, 10);
                        break;
                    case "border":
                        g.DrawRectangle(P(acc, 3f), 4, 4, 16, 16);
                        g.DrawRectangle(pen, 9, 9, 6, 6);
                        break;
                    case "shadow":
                        g.FillRectangle(new SolidBrush(Color.FromArgb(70, ink)), 8, 8, 13, 13);
                        g.FillRectangle(Brushes.White, 3, 3, 13, 13); g.DrawRectangle(pen, 3, 3, 13, 13);
                        break;
                    case "torn":
                        g.DrawLines(pen, new[] { new PointF(3, 3), new PointF(21, 3), new PointF(21, 21), new PointF(3, 21) });
                        g.DrawLines(accPen, new[] { new PointF(3, 3), new PointF(5, 6), new PointF(3, 9), new PointF(5, 12), new PointF(3, 15), new PointF(5, 18), new PointF(3, 21) });
                        break;
                    case "adjust":
                        g.DrawEllipse(pen, 3.5f, 3.5f, 17, 17);
                        g.FillPie(inkB, 3.5f, 3.5f, 17, 17, 90, 180);
                        break;
                    case "gray":
                        using (var lb = new LinearGradientBrush(new RectangleF(3, 3, 18, 18), Color.White, Color.FromArgb(40, 40, 40), 0f))
                            g.FillRectangle(lb, 3, 3, 18, 18);
                        g.DrawRectangle(pen, 3, 3, 18, 18);
                        break;
                    case "watermark":
                        using (var f = new Font("Segoe UI", 12, FontStyle.Bold | FontStyle.Italic, GraphicsUnit.Pixel))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                        {
                            g.DrawRectangle(pen, 3, 4, 18, 16);
                            g.DrawString("W", f, accB, new RectangleF(3, 4, 18, 16), sf);
                        }
                        break;
                    case "trash":
                        g.DrawLine(pen, 4, 6.5f, 20, 6.5f); g.DrawLine(pen, 9, 6, 9, 3.5f); g.DrawLine(pen, 9, 3.5f, 15, 3.5f); g.DrawLine(pen, 15, 3.5f, 15, 6);
                        g.DrawLines(pen, new[] { new PointF(5.5f, 6.5f), new PointF(6.5f, 20.5f), new PointF(17.5f, 20.5f), new PointF(18.5f, 6.5f) });
                        g.DrawLine(accPen, 10, 10, 10, 17); g.DrawLine(accPen, 14, 10, 14, 17);
                        break;
                    case "share":
                        g.DrawLines(pen, new[] { new PointF(8, 9), new PointF(4, 9), new PointF(4, 21), new PointF(20, 21), new PointF(20, 9), new PointF(16, 9) });
                        g.DrawLine(accPen, 12, 15, 12, 3);
                        g.DrawLines(accPen, new[] { new PointF(8, 7), new PointF(12, 3), new PointF(16, 7) });
                        break;
                    case "close":
                        g.DrawLine(P(light ? Color.White : Red, 2.4f), 6, 6, 18, 18); g.DrawLine(P(light ? Color.White : Red, 2.4f), 18, 6, 6, 18);
                        break;
                    case "help":
                        g.DrawEllipse(pen, 3, 3, 18, 18);
                        using (var f = new Font("Segoe UI", 14, FontStyle.Bold, GraphicsUnit.Pixel))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString("?", f, accB, new RectangleF(3, 3.5f, 18, 18), sf);
                        break;
                    case "cursor":
                        g.FillPolygon(inkB, new[] { new PointF(6, 3), new PointF(6, 19.5f), new PointF(10.3f, 15.6f), new PointF(13, 21.5f), new PointF(15.8f, 20.3f), new PointF(13.1f, 14.5f), new PointF(18.6f, 14.3f) });
                        break;
                    case "check":
                        g.DrawLines(P(light ? Color.White : Green, 2.6f), new[] { new PointF(5, 12.5f), new PointF(10, 17.5f), new PointF(19.5f, 6.5f) });
                        break;
                    case "delay":
                        g.DrawEllipse(pen, 3.5f, 4.5f, 17, 17); g.DrawLine(accPen, 12, 8.5f, 12, 13.5f); g.DrawLine(accPen, 12, 13.5f, 15.5f, 15.5f); g.DrawLine(pen, 10, 2.5f, 14, 2.5f);
                        break;
                    case "mic":
                    case "mic_off":
                        using (var rp = RR(8.5f, 2.5f, 7, 12, 3.5f)) g.DrawPath(pen, rp);
                        g.DrawArc(accPen, 5, 7, 14, 11, 0, 180);
                        g.DrawLine(accPen, 12, 18, 12, 21.5f); g.DrawLine(accPen, 8.5f, 21.5f, 15.5f, 21.5f);
                        if (n == "mic_off") g.DrawLine(P(Color.FromArgb(255, 84, 84), 2.6f), 3.5f, 3.5f, 20.5f, 20.5f);      // muted / off: red slash
                        break;
                    case "speaker":
                    case "speaker_off":
                        g.DrawPolygon(pen, new[] { new PointF(3.5f, 9.5f), new PointF(8, 9.5f), new PointF(13, 5), new PointF(13, 19), new PointF(8, 14.5f), new PointF(3.5f, 14.5f) });
                        g.DrawArc(accPen, 11.5f, 8.5f, 6, 7, -65, 130);
                        g.DrawArc(accPen, 10f, 5.5f, 11, 13, -60, 120);
                        if (n == "speaker_off") g.DrawLine(P(Color.FromArgb(255, 84, 84), 2.6f), 3.5f, 3.5f, 20.5f, 20.5f);
                        break;
                    case "pause":
                        g.FillRectangle(inkB, 6, 5, 4.5f, 14); g.FillRectangle(inkB, 13.5f, 5, 4.5f, 14);
                        break;
                    case "stop":
                        g.FillRectangle(new SolidBrush(Red), 5.5f, 5.5f, 13, 13);
                        break;
                    case "record_w":
                        g.FillEllipse(Brushes.White, 5f, 5f, 14, 14);
                        break;
                    case "record":
                        g.FillEllipse(new SolidBrush(Red), 4.5f, 4.5f, 15, 15);
                        break;
                    case "layers":
                        g.DrawPolygon(pen, new[] { new PointF(12, 3), new PointF(21.5f, 8), new PointF(12, 13), new PointF(2.5f, 8) });
                        g.DrawLines(accPen, new[] { new PointF(2.5f, 12.5f), new PointF(12, 17.5f), new PointF(21.5f, 12.5f) });
                        break;
                    case "up":
                        g.DrawLine(pen, 12, 20, 12, 5); g.DrawLines(pen, new[] { new PointF(6, 11), new PointF(12, 5), new PointF(18, 11) });
                        break;
                    case "down":
                        g.DrawLine(pen, 12, 4, 12, 19); g.DrawLines(pen, new[] { new PointF(6, 13), new PointF(12, 19), new PointF(18, 13) });
                        break;
                    case "bold":
                    case "italic":
                    case "underline":
                    case "textcolor":
                    case "font":
                        {
                            var st = n == "bold" ? FontStyle.Bold : (n == "italic" ? FontStyle.Italic : (n == "underline" ? FontStyle.Underline : FontStyle.Bold));
                            string ch = n == "bold" ? "B" : (n == "italic" ? "I" : (n == "underline" ? "U" : (n == "font" ? "Aa" : "A")));
                            using (var f = new Font("Segoe UI", n == "font" ? 14 : 17, st, GraphicsUnit.Pixel))
                            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                                g.DrawString(ch, f, n == "textcolor" ? accB : inkB, new RectangleF(0, 0, 24, 22), sf);
                            break;
                        }
                    case "alignl":
                    case "alignc":
                    case "alignr":
                        {
                            float[] w = { 16, 11, 14, 9 };
                            for (int i = 0; i < 4; i++)
                            {
                                float len = w[i] + (i % 2 == 0 ? 0 : -2);
                                float x = n == "alignl" ? 4 : (n == "alignc" ? 12 - len / 2 : 20 - len);
                                g.DrawLine(pen, x, 6 + i * 4, x + len, 6 + i * 4);
                            }
                            break;
                        }
                    case "tray":
                        g.DrawRectangle(pen, 3, 4, 18, 12); g.DrawLine(accPen, 3, 20, 21, 20); g.FillRectangle(accB, 5, 6, 5, 8); g.FillRectangle(inkB, 12, 6, 7, 3);
                        break;
                    default:
                        g.DrawRectangle(pen, 4, 4, 16, 16);
                        break;
                }
            }
        }

        /// <summary>Vector stamps used by the Stamp tool (drawn at arbitrary size in a bounding box).</summary>
        public static readonly string[] StampNames = { "Check", "Cross", "Warning", "Question", "Info", "Star", "Heart", "Cursor", "Flag", "Bulb" };

        public static void DrawStamp(Graphics g, int variant, RectangleF r)
        {
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TranslateTransform(r.X, r.Y);
            g.ScaleTransform(r.Width / 24f, r.Height / 24f);
            using (var f = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                switch (variant)
                {
                    case 0:
                        g.FillEllipse(new SolidBrush(Green), 1, 1, 22, 22);
                        g.DrawLines(P(Color.White, 3.2f), new[] { new PointF(6.5f, 12.5f), new PointF(10.5f, 16.5f), new PointF(17.5f, 8) });
                        break;
                    case 1:
                        g.FillEllipse(new SolidBrush(Red), 1, 1, 22, 22);
                        g.DrawLine(P(Color.White, 3.2f), 7.5f, 7.5f, 16.5f, 16.5f); g.DrawLine(P(Color.White, 3.2f), 16.5f, 7.5f, 7.5f, 16.5f);
                        break;
                    case 2:
                        g.FillPolygon(new SolidBrush(Amber), new[] { new PointF(12, 1.5f), new PointF(23, 21.5f), new PointF(1, 21.5f) });
                        g.DrawString("!", f, Brushes.White, new RectangleF(1, 4, 22, 19), sf);
                        break;
                    case 3:
                        g.FillEllipse(new SolidBrush(Accent), 1, 1, 22, 22);
                        g.DrawString("?", f, Brushes.White, new RectangleF(1, 1.5f, 22, 22), sf);
                        break;
                    case 4:
                        g.FillEllipse(new SolidBrush(Color.FromArgb(14, 165, 233)), 1, 1, 22, 22);
                        g.DrawString("i", f, Brushes.White, new RectangleF(1, 1.5f, 22, 22), sf);
                        break;
                    case 5:
                        Star(g, new SolidBrush(Amber), P(Color.FromArgb(180, 100, 0), 1.2f), 12, 12.8f, 11, 4.6f);
                        break;
                    case 6:
                        using (var hp = new GraphicsPath())
                        {
                            hp.AddBezier(12, 21, 1, 12, 3, 3, 12, 8.5f);
                            hp.AddBezier(12, 8.5f, 21, 3, 23, 12, 12, 21);
                            g.FillPath(new SolidBrush(Color.FromArgb(236, 72, 153)), hp);
                        }
                        break;
                    case 7:
                        g.FillPolygon(Brushes.White, new[] { new PointF(4, 2), new PointF(4, 20), new PointF(9, 15.5f), new PointF(12, 22), new PointF(15, 20.6f), new PointF(12, 14.4f), new PointF(19, 14) });
                        g.DrawPolygon(P(Color.Black, 1.6f), new[] { new PointF(4, 2), new PointF(4, 20), new PointF(9, 15.5f), new PointF(12, 22), new PointF(15, 20.6f), new PointF(12, 14.4f), new PointF(19, 14) });
                        break;
                    case 8:
                        g.DrawLine(P(Color.FromArgb(75, 85, 99), 2.4f), 5, 2, 5, 22);
                        g.FillPolygon(new SolidBrush(Red), new[] { new PointF(5.5f, 3), new PointF(20, 7.5f), new PointF(5.5f, 12) });
                        break;
                    default:
                        g.FillEllipse(new SolidBrush(Color.FromArgb(253, 224, 71)), 4, 1.5f, 16, 16);
                        g.FillRectangle(new SolidBrush(Color.FromArgb(156, 163, 175)), 8.5f, 17.5f, 7, 4);
                        g.DrawEllipse(P(Color.FromArgb(180, 130, 0), 1.2f), 4, 1.5f, 16, 16);
                        break;
                }
            }
            g.Restore(st);
        }
    }
}
