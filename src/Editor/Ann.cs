using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.IO;
using System.Xml.Linq;

namespace RXCapture
{
    public enum AnnKind { Arrow, Line, Shape, Callout, Text, Step, Stamp, Pen, Blur, Magnify, Spotlight, Image }

    /// <summary>
    /// One vector annotation drawn over the base image (arrow, shape, text, blur region, ...).
    /// A single class with a Kind keeps cloning, undo and (de)serialisation simple.
    /// </summary>
    public class Ann
    {
        public AnnKind Kind;
        public PointF P1, P2, Tail;
        public List<PointF> Pts = new List<PointF>();
        public Color Stroke = Color.FromArgb(229, 57, 53);
        public Color Fill = Color.Transparent;
        public Color TextColor = Color.Black;
        public float Width = 3;
        public DashStyle Dash = DashStyle.Solid;
        public int Variant;               // arrow head / shape type / stamp id / blur mode / lens shape
        public bool Shadow;
        public float Opacity = 1f;
        public string Text = "";
        public string FontName = "Arial";
        public float FontSize = 20;
        public bool Bold, Italic, Underline;
        public int Align;                 // 0 left, 1 centre, 2 right
        public bool AutoSize = true;
        public int Number = 1;
        public float Zoom = 2f;
        public float TailPos = -1;        // callout: where the tail leaves its side, as a fraction 0..1 of that side (-1 = follow the tip)
        public float TailW;               // callout: half width of the tail base (0 = automatic)
        public bool Highlighter;
        public Bitmap Img;
        public bool Editing;              // transient: text is being edited inline, so the renderer skips it

        public const float Pad = 6f;

        // ------------------------------------------------------------ factory / defaults

        public static Ann Create(AnnKind kind)
        {
            var a = new Ann { Kind = kind };
            switch (kind)
            {
                case AnnKind.Arrow: a.Width = 5; a.Stroke = Color.FromArgb(229, 57, 53); break;
                case AnnKind.Line: a.Width = 4; a.Stroke = Color.FromArgb(229, 57, 53); break;
                case AnnKind.Shape: a.Width = 4; a.Stroke = Color.FromArgb(229, 57, 53); break;
                case AnnKind.Callout:
                    a.Width = 3; a.Stroke = Color.FromArgb(229, 57, 53); a.Fill = Color.FromArgb(255, 255, 255); a.TextColor = Color.FromArgb(229, 57, 53); a.FontSize = 20; break;
                case AnnKind.Text: a.TextColor = Color.FromArgb(229, 57, 53); a.FontSize = 20; a.Width = 2; break;
                case AnnKind.Step: a.Fill = Color.FromArgb(229, 57, 53); a.Stroke = Color.White; a.TextColor = Color.White; a.Width = 2; a.FontSize = 18; a.Bold = true; a.Zoom = 40f; break;   // Zoom doubles as the default size of a step
                case AnnKind.Stamp: break;
                case AnnKind.Pen: a.Width = 4; a.Stroke = Color.FromArgb(229, 57, 53); break;
                case AnnKind.Blur: a.Width = 10; a.Variant = 0; break;
                case AnnKind.Magnify: a.Width = 3; a.Stroke = Color.FromArgb(60, 60, 60); a.Zoom = 2f; a.Shadow = true; break;
                case AnnKind.Spotlight: a.Fill = Color.FromArgb(150, 0, 0, 0); a.Width = 0; break;
            }
            return a;
        }

        public Ann Clone()
        {
            var c = (Ann)MemberwiseClone();
            c.Pts = new List<PointF>(Pts);
            return c;
        }

        // ------------------------------------------------------------ geometry

        public RectangleF Rect
        {
            get { return RectangleF.FromLTRB(Math.Min(P1.X, P2.X), Math.Min(P1.Y, P2.Y), Math.Max(P1.X, P2.X), Math.Max(P1.Y, P2.Y)); }
            set { P1 = value.Location; P2 = new PointF(value.Right, value.Bottom); }
        }

        public bool HasText { get { return Kind == AnnKind.Text || Kind == AnnKind.Callout || Kind == AnnKind.Step; } }
        public bool IsLine { get { return Kind == AnnKind.Arrow || Kind == AnnKind.Line; } }

        public RectangleF PenBounds()
        {
            if (Pts.Count == 0) return RectangleF.Empty;
            float x1 = float.MaxValue, y1 = float.MaxValue, x2 = float.MinValue, y2 = float.MinValue;
            foreach (var p in Pts) { x1 = Math.Min(x1, p.X); y1 = Math.Min(y1, p.Y); x2 = Math.Max(x2, p.X); y2 = Math.Max(y2, p.Y); }
            return RectangleF.FromLTRB(x1, y1, x2, y2);
        }

        /// <summary>Bounds used for selection outline and repaint.</summary>
        public RectangleF SelBounds()
        {
            RectangleF r;
            if (Kind == AnnKind.Pen) r = PenBounds();
            else if (IsLine)
            {
                r = Rect;
                float hl = Kind == AnnKind.Arrow ? Math.Max(10, Width * 3.5f) : 0;
                r.Inflate(hl * 0.6f, hl * 0.6f);
            }
            else r = Rect;
            if (Kind == AnnKind.Callout)
            {
                var t = new RectangleF(Tail.X, Tail.Y, 0, 0);
                r = RectangleF.Union(r, t);
            }
            float w = (Kind == AnnKind.Pen || IsLine || Kind == AnnKind.Shape || Kind == AnnKind.Callout || Kind == AnnKind.Magnify) ? Width / 2 : 0;
            r.Inflate(w, w);
            if (Shadow) r.Inflate(6, 6);
            return r;
        }

        /// <summary>Moves only the body of a callout; its tail tip stays anchored on the point it points at (like Snagit).</summary>
        public void MoveBody(float dx, float dy)
        {
            P1 = new PointF(P1.X + dx, P1.Y + dy);
            P2 = new PointF(P2.X + dx, P2.Y + dy);
        }

        public void Move(float dx, float dy)
        {
            P1 = new PointF(P1.X + dx, P1.Y + dy);
            P2 = new PointF(P2.X + dx, P2.Y + dy);
            Tail = new PointF(Tail.X + dx, Tail.Y + dy);
            for (int i = 0; i < Pts.Count; i++) Pts[i] = new PointF(Pts[i].X + dx, Pts[i].Y + dy);
        }

        /// <summary>Scale geometry (and stroke / font sizes) about the origin, used when the image is resized.</summary>
        public void ScaleAll(float sx, float sy)
        {
            P1 = new PointF(P1.X * sx, P1.Y * sy);
            P2 = new PointF(P2.X * sx, P2.Y * sy);
            Tail = new PointF(Tail.X * sx, Tail.Y * sy);
            TailW *= (sx + sy) / 2;
            for (int i = 0; i < Pts.Count; i++) Pts[i] = new PointF(Pts[i].X * sx, Pts[i].Y * sy);
            float s = (sx + sy) / 2;
            Width = Math.Max(0.5f, Width * s);
            FontSize = Math.Max(4, FontSize * s);
        }

        public static float DistToSegment(PointF p, PointF a, PointF b)
        {
            float dx = b.X - a.X, dy = b.Y - a.Y;
            float l2 = dx * dx + dy * dy;
            if (l2 < 1e-6f) return (float)Math.Sqrt((p.X - a.X) * (p.X - a.X) + (p.Y - a.Y) * (p.Y - a.Y));
            float t = Math.Max(0, Math.Min(1, ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / l2));
            float px = a.X + t * dx, py = a.Y + t * dy;
            return (float)Math.Sqrt((p.X - px) * (p.X - px) + (p.Y - py) * (p.Y - py));
        }

        public bool HitTest(PointF p, float tol)
        {
            switch (Kind)
            {
                case AnnKind.Arrow:
                case AnnKind.Line:
                    return DistToSegment(p, P1, P2) <= Width / 2 + tol + (Kind == AnnKind.Arrow ? 3 : 0);
                case AnnKind.Pen:
                    for (int i = 0; i + 1 < Pts.Count; i++) if (DistToSegment(p, Pts[i], Pts[i + 1]) <= Width / 2 + tol) return true;
                    return Pts.Count == 1 && DistToSegment(p, Pts[0], Pts[0]) <= Width / 2 + tol;
                case AnnKind.Shape:
                    {
                        var r = Rect; float e = Width / 2 + tol;
                        var outer = RectangleF.Inflate(r, e, e);
                        bool inOuter = Variant == 2 ? InEllipse(outer, p) : outer.Contains(p);
                        if (!inOuter) return false;
                        if (Fill.A > 0) return true;
                        var inner = RectangleF.Inflate(r, -e, -e);
                        if (inner.Width <= 0 || inner.Height <= 0) return true;
                        bool inInner = Variant == 2 ? InEllipse(inner, p) : inner.Contains(p);
                        return !inInner;
                    }
                case AnnKind.Spotlight:
                    {
                        return RectangleF.Inflate(Rect, 6 + tol, 6 + tol).Contains(p);
                    }
                case AnnKind.Callout:
                    {
                        var r = RectangleF.Inflate(Rect, tol, tol);
                        if (r.Contains(p)) return true;
                        float dt = (float)Math.Sqrt((p.X - Tail.X) * (p.X - Tail.X) + (p.Y - Tail.Y) * (p.Y - Tail.Y));
                        if (dt <= 8 + tol) return true;                       // the tip itself
                        var body = Rect;
                        if (body.Width < 4 || body.Height < 4) return false;
                        try
                        {
                            using (var path = CalloutPath())         // the visible tail wedge, not just a line to the tip
                            {
                                if (path.IsVisible(p)) return true;
                                using (var pen = new Pen(Color.Black, Math.Max(1f, Width) + 2 * tol)) return path.IsOutlineVisible(p, pen);
                            }
                        }
                        catch { return false; }
                    }
                default:
                    return RectangleF.Inflate(Rect, tol, tol).Contains(p);
            }
        }

        static bool InEllipse(RectangleF r, PointF p)
        {
            if (r.Width <= 0 || r.Height <= 0) return false;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            float nx = (p.X - cx) / (r.Width / 2), ny = (p.Y - cy) / (r.Height / 2);
            return nx * nx + ny * ny <= 1;
        }

        // ------------------------------------------------------------ handles

        /// <summary>Handle positions. Rect kinds: 0 TL,1 T,2 TR,3 R,4 BR,5 B,6 BL,7 L, (8 = callout tail). Line kinds: 0 start, 1 end.</summary>
        public PointF[] Handles()
        {
            if (IsLine) return new[] { P1, P2 };
            RectangleF r = Kind == AnnKind.Pen ? PenBounds() : Rect;
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            var list = new List<PointF> {
                new PointF(r.Left, r.Top), new PointF(cx, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, cy),
                new PointF(r.Right, r.Bottom), new PointF(cx, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, cy) };
            if (Kind == AnnKind.Callout)
            {
                list.Add(Tail);                                   // 8: the tip
                var tg = GetTailGeo();
                if (tg.Valid)
                {
                    bool horiz = tg.Side == 0 || tg.Side == 2;
                    float o = (tg.Side == 0 || tg.Side == 3) ? -9 : 9;      // sit just outside the box so it never covers a resize handle
                    var bc = horiz ? new PointF(tg.Center.X, tg.Center.Y + o) : new PointF(tg.Center.X + o, tg.Center.Y);
                    var we = horiz ? new PointF(tg.Center.X + tg.HalfW, tg.Center.Y) : new PointF(tg.Center.X, tg.Center.Y + tg.HalfW);
                    list.Add(bc);                                 // 9: slides the tail along its side
                    list.Add(we);                                 // 10: widens / narrows the tail base
                }
                else { list.Add(new PointF(-1e6f, -1e6f)); list.Add(new PointF(-1e6f, -1e6f)); }
            }
            return list.ToArray();
        }

        public void DragHandle(int idx, PointF p, bool shift)
        {
            if (IsLine)
            {
                if (shift)
                {
                    PointF o = idx == 0 ? P2 : P1;
                    p = SnapAngle(o, p);
                }
                if (idx == 0) P1 = p; else P2 = p;
                return;
            }
            if (Kind == AnnKind.Callout && idx == 8) { Tail = p; return; }
            if (Kind == AnnKind.Callout && (idx == 9 || idx == 10))
            {
                var tg = GetTailGeo();
                if (!tg.Valid) return;
                var rc = Rect; bool horiz = tg.Side == 0 || tg.Side == 2;
                if (idx == 9) TailPos = Math.Max(0f, Math.Min(1f, horiz ? (p.X - rc.X) / Math.Max(1, rc.Width) : (p.Y - rc.Y) / Math.Max(1, rc.Height)));
                else
                {
                    if (TailPos < 0) TailPos = tg.Along / Math.Max(1, tg.Len);      // freeze the position while the width changes
                    TailW = Math.Max(4f, Math.Abs(horiz ? p.X - tg.Center.X : p.Y - tg.Center.Y));
                }
                return;
            }
            RectangleF old = Kind == AnnKind.Pen ? PenBounds() : Rect;
            float l = old.Left, t = old.Top, r = old.Right, b = old.Bottom;
            if (idx == 0 || idx == 6 || idx == 7) l = p.X;
            if (idx == 2 || idx == 3 || idx == 4) r = p.X;
            if (idx == 0 || idx == 1 || idx == 2) t = p.Y;
            if (idx == 4 || idx == 5 || idx == 6) b = p.Y;
            if ((Kind == AnnKind.Step || Kind == AnnKind.Stamp || shift) && (idx == 0 || idx == 2 || idx == 4 || idx == 6))
            {
                // keep the aspect ratio for corner drags
                float ow = Math.Max(1, old.Width), oh = Math.Max(1, old.Height);
                float nw = Math.Abs(r - l), nh = Math.Abs(b - t);
                float k = Math.Max(nw / ow, nh / oh);
                nw = ow * k; nh = oh * k;
                if (idx == 0 || idx == 6) l = r - nw * Math.Sign(r - l == 0 ? 1 : r - l); else r = l + nw * Math.Sign(r - l == 0 ? 1 : r - l);
                if (idx == 0 || idx == 2) t = b - nh * Math.Sign(b - t == 0 ? 1 : b - t); else b = t + nh * Math.Sign(b - t == 0 ? 1 : b - t);
            }
            var nr = RectangleF.FromLTRB(Math.Min(l, r), Math.Min(t, b), Math.Max(l, r), Math.Max(t, b));
            if (Kind == AnnKind.Pen)
            {
                float sx = old.Width < 0.01f ? 1 : nr.Width / old.Width, sy = old.Height < 0.01f ? 1 : nr.Height / old.Height;
                for (int i = 0; i < Pts.Count; i++)
                    Pts[i] = new PointF(nr.X + (Pts[i].X - old.X) * sx, nr.Y + (Pts[i].Y - old.Y) * sy);
                return;
            }
            if (Kind == AnnKind.Text && AutoSize && (idx == 3 || idx == 7)) AutoSize = false;
            if (Kind == AnnKind.Text && (idx == 1 || idx == 5)) AutoSize = false;
            Rect = nr;
            if (Kind == AnnKind.Text || Kind == AnnKind.Callout) { if (!AutoSize) EnforceMinHeight(); }
        }

        void EnforceMinHeight()
        {
            var sz = MeasureText(Rect.Width - Pad * 2);
            if (Rect.Height < sz.Height + Pad * 2 && Kind == AnnKind.Text)
                P2 = new PointF(P2.X, Math.Min(P1.Y, P2.Y) + sz.Height + Pad * 2);
        }

        public static PointF SnapAngle(PointF o, PointF p)
        {
            float dx = p.X - o.X, dy = p.Y - o.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return p;
            double a = Math.Atan2(dy, dx);
            double step = Math.PI / 4;
            a = Math.Round(a / step) * step;
            return new PointF(o.X + (float)(Math.Cos(a) * len), o.Y + (float)(Math.Sin(a) * len));
        }

        // ------------------------------------------------------------ text

        static Bitmap measureBmp;
        static Graphics measureG;
        static Graphics MG
        {
            get
            {
                if (measureG == null) { measureBmp = new Bitmap(1, 1); measureG = Graphics.FromImage(measureBmp); measureG.TextRenderingHint = TextRenderingHint.AntiAliasGridFit; }
                return measureG;
            }
        }

        public FontStyle Style
        {
            get { return (Bold ? FontStyle.Bold : 0) | (Italic ? FontStyle.Italic : 0) | (Underline ? FontStyle.Underline : 0); }
        }

        public Font MakeFont(float scale)
        {
            float px = Math.Max(4, FontSize * scale);
            try { return new Font(FontName, px, Style, GraphicsUnit.Pixel); }
            catch { return new Font("Segoe UI", px, Style, GraphicsUnit.Pixel); }
        }

        StringFormat MakeFormat(bool wrap, bool centerV)
        {
            var sf = new StringFormat(StringFormatFlags.NoClip);
            sf.Alignment = Align == 1 ? StringAlignment.Center : (Align == 2 ? StringAlignment.Far : StringAlignment.Near);
            sf.LineAlignment = centerV ? StringAlignment.Center : StringAlignment.Near;
            sf.Trimming = StringTrimming.None;
            if (!wrap) sf.FormatFlags |= StringFormatFlags.NoWrap;
            return sf;
        }

        /// <summary>Measure the text; maxWidth &lt;= 0 means no wrapping.</summary>
        public SizeF MeasureText(float maxWidth)
        {
            string t = string.IsNullOrEmpty(Text) ? " " : Text;
            using (var f = MakeFont(1))
            using (var sf = MakeFormat(maxWidth > 0, false))
            {
                SizeF sz = maxWidth > 0 ? MG.MeasureString(t, f, (int)Math.Ceiling(maxWidth), sf) : MG.MeasureString(t, f, 100000, sf);
                return new SizeF((float)Math.Ceiling(sz.Width), (float)Math.Ceiling(sz.Height));
            }
        }

        /// <summary>Re-fit the box to the text when AutoSize is on.</summary>
        public void FitText()
        {
            if (Kind == AnnKind.Text && AutoSize)
            {
                var sz = MeasureText(0);
                float w = Math.Max(sz.Width + Pad * 2 + 2, 24), h = sz.Height + Pad * 2;
                P2 = new PointF(P1.X + w, P1.Y + h);
            }
            else if (Kind == AnnKind.Callout && AutoSize)
            {
                var sz = MeasureText(0);
                float w = Math.Max(sz.Width + 24, 60), h = Math.Max(sz.Height + 20, 40);
                var old = Rect;
                Rect = new RectangleF(old.X, old.Y, w, h);
            }
        }

        // ------------------------------------------------------------ drawing

        Color Col(Color c, bool shadow)
        {
            if (shadow) return Color.FromArgb((int)(c.A * 0.38f * Opacity), 0, 0, 0);
            if (Opacity >= 0.999f) return c;
            return Color.FromArgb((int)(c.A * Opacity), c);
        }

        Pen MakePen(Color c, float w, bool shadow)
        {
            var p = new Pen(Col(c, shadow), Math.Max(0.5f, w));
            p.LineJoin = LineJoin.Round;
            if (Dash != DashStyle.Solid) { p.DashStyle = Dash; }
            return p;
        }

        public void Draw(Graphics g, Bitmap target)
        {
            if (Kind == AnnKind.Blur || Kind == AnnKind.Spotlight) { DrawCore(g, target, false); return; }
            if (Shadow)
            {
                var st = g.Save();
                float o = 2f + Math.Min(6f, Width * 0.4f);
                g.TranslateTransform(o, o);
                DrawCore(g, target, true);
                g.Restore(st);
            }
            DrawCore(g, target, false);
        }

        void DrawCore(Graphics g, Bitmap target, bool sh)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            switch (Kind)
            {
                case AnnKind.Arrow: DrawArrow(g, sh); break;
                case AnnKind.Line:
                    using (var p = MakePen(Stroke, Width, sh)) { p.StartCap = LineCap.Round; p.EndCap = LineCap.Round; g.DrawLine(p, P1, P2); }
                    break;
                case AnnKind.Shape: DrawShape(g, sh); break;
                case AnnKind.Callout: DrawCallout(g, sh); break;
                case AnnKind.Text: DrawTextBox(g, sh); break;
                case AnnKind.Step: DrawStep(g, sh); break;
                case AnnKind.Stamp:
                    if (Rect.Width < 1 || Rect.Height < 1) break;
                    if (sh) { using (var b = new SolidBrush(Col(Color.Black, true))) g.FillEllipse(b, Rect); }
                    else Icons.DrawStamp(g, Variant, Rect);
                    break;
                case AnnKind.Pen: DrawPen(g, sh); break;
                case AnnKind.Blur: DrawBlur(g, target); break;
                case AnnKind.Magnify: DrawMagnify(g, target, sh); break;
                case AnnKind.Spotlight: DrawSpotlight(g, target); break;
                case AnnKind.Image:
                    if (Img == null) break;
                    if (sh) { using (var b = new SolidBrush(Col(Color.Black, true))) g.FillRectangle(b, Rect); }
                    else if (Opacity >= 0.999f) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(Img, Rect); }
                    else
                        using (var ia = new ImageAttributes())
                        {
                            ia.SetColorMatrix(new ColorMatrix(new float[][] { new float[] { 1, 0, 0, 0, 0 }, new float[] { 0, 1, 0, 0, 0 }, new float[] { 0, 0, 1, 0, 0 }, new float[] { 0, 0, 0, Opacity, 0 }, new float[] { 0, 0, 0, 0, 1 } }));
                            var rr = Rect;
                            g.DrawImage(Img, Rectangle.Round(rr), 0, 0, Img.Width, Img.Height, GraphicsUnit.Pixel, ia);
                        }
                    break;
            }
        }

        void DrawArrow(Graphics g, bool sh)
        {
            float dx = P2.X - P1.X, dy = P2.Y - P1.Y;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 1) return;
            float ux = dx / len, uy = dy / len;
            float hl = Math.Max(10f, Width * 3.6f), hw = hl * 0.55f;
            if (hl > len * 0.9f) hl = len * 0.9f;
            bool both = Variant == 1, open = Variant == 2;
            PointF a = P1, b = P2;
            // shorten the shaft so it does not poke through filled heads
            PointF sa = a, sb = b;
            if (!open) { sb = new PointF(b.X - ux * hl * 0.75f, b.Y - uy * hl * 0.75f); if (both) sa = new PointF(a.X + ux * hl * 0.75f, a.Y + uy * hl * 0.75f); }
            using (var pen = MakePen(Stroke, Width, sh))
            {
                pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                g.DrawLine(pen, sa, sb);
                if (open)
                {
                    Action<PointF, float, float> vhead = delegate(PointF tip, float sx, float sy)
                    {
                        var l = new PointF(tip.X - sx * hl + (-sy) * hw, tip.Y - sy * hl + sx * hw);
                        var r = new PointF(tip.X - sx * hl - (-sy) * hw, tip.Y - sy * hl - sx * hw);
                        var pp = new Pen(pen.Color, pen.Width) { LineJoin = LineJoin.Round, StartCap = LineCap.Round, EndCap = LineCap.Round };
                        g.DrawLines(pp, new[] { l, tip, r });
                    };
                    vhead(b, ux, uy);
                    if (both) vhead(a, -ux, -uy);
                }
            }
            if (!open)
                using (var br = new SolidBrush(Col(Stroke, sh)))
                {
                    Action<PointF, float, float> head = delegate(PointF tip, float sx, float sy)
                    {
                        var bc = new PointF(tip.X - sx * hl, tip.Y - sy * hl);
                        g.FillPolygon(br, new[] { tip, new PointF(bc.X - sy * hw, bc.Y + sx * hw), new PointF(bc.X + sy * hw, bc.Y - sx * hw) });
                    };
                    head(b, ux, uy);
                    if (both) head(a, -ux, -uy);
                }
        }

        GraphicsPath ShapePath(RectangleF r)
        {
            var p = new GraphicsPath();
            if (Variant == 2) p.AddEllipse(r);
            else if (Variant == 1)
            {
                float rad = Math.Max(2, Math.Min(r.Width, r.Height) * 0.18f);
                AddRound(p, r, rad);
            }
            else p.AddRectangle(r);
            return p;
        }

        static void AddRound(GraphicsPath p, RectangleF r, float rad)
        {
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            if (d < 1) { p.AddRectangle(r); return; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
        }

        void DrawShape(Graphics g, bool sh)
        {
            var r = Rect;
            if (r.Width < 1 || r.Height < 1) return;
            using (var path = ShapePath(r))
            {
                if (Fill.A > 0) using (var b = new SolidBrush(Col(Fill, sh))) g.FillPath(b, path);
                if (Width > 0 && Stroke.A > 0) using (var p = MakePen(Stroke, Width, sh)) g.DrawPath(p, path);
            }
        }

        /// <summary>Where the callout tail leaves the box: which side, its centre along that side and its half width.</summary>
        struct TailGeo
        {
            public bool Valid;       // false while the tip is inside the box (no tail is drawn)
            public int Side;         // 0 top, 1 right, 2 bottom, 3 left
            public float Along;      // distance of the tail base centre from the start of that side
            public float HalfW;      // half width of the tail base
            public float Len;        // length of that side
            public float Lo, Hi;     // allowed range for Along
            public PointF Center;    // base centre on the box edge
        }

        TailGeo GetTailGeo()
        {
            var r = Rect;
            var t = new TailGeo();
            if (Kind != AnnKind.Callout || r.Width < 4 || r.Height < 4 || r.Contains(Tail)) return t;
            float rad = Math.Max(4, Math.Min(r.Width, r.Height) * 0.2f);
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
            float nx = (Tail.X - cx) / Math.Max(1, r.Width / 2), ny = (Tail.Y - cy) / Math.Max(1, r.Height / 2);
            t.Side = Math.Abs(nx) > Math.Abs(ny) ? (nx > 0 ? 1 : 3) : (ny > 0 ? 2 : 0);
            bool horiz = t.Side == 0 || t.Side == 2;
            t.Len = horiz ? r.Width : r.Height;
            float autoW = Math.Min(26, t.Len * 0.28f);
            t.HalfW = TailW > 0 ? Math.Min(TailW, Math.Max(3, t.Len / 2 - d)) : autoW;
            t.Lo = d + t.HalfW; t.Hi = t.Len - d - t.HalfW;
            if (t.Hi < t.Lo) t.Lo = t.Hi = t.Len / 2;
            float along = TailPos >= 0 ? TailPos * t.Len : (horiz ? Tail.X - r.X : Tail.Y - r.Y);
            t.Along = Math.Max(t.Lo, Math.Min(t.Hi, along));
            switch (t.Side)
            {
                case 0: t.Center = new PointF(r.X + t.Along, r.Y); break;
                case 1: t.Center = new PointF(r.Right, r.Y + t.Along); break;
                case 2: t.Center = new PointF(r.X + t.Along, r.Bottom); break;
                default: t.Center = new PointF(r.X, r.Y + t.Along); break;
            }
            t.Valid = true;
            return t;
        }

        GraphicsPath CalloutPath()
        {
            var r = Rect;
            var path = new GraphicsPath();
            float rad = Math.Max(4, Math.Min(r.Width, r.Height) * 0.2f);
            float d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            var tg = GetTailGeo();
            int side = tg.Valid ? tg.Side : -1;
            float baseW = tg.HalfW;
            path.StartFigure();
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            if (side == 0)
            {
                float bx = tg.Center.X;
                path.AddLine(bx - baseW, r.Y, Tail.X, Tail.Y); path.AddLine(Tail.X, Tail.Y, bx + baseW, r.Y);
            }
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            if (side == 1)
            {
                float by = tg.Center.Y;
                path.AddLine(r.Right, by - baseW, Tail.X, Tail.Y); path.AddLine(Tail.X, Tail.Y, r.Right, by + baseW);
            }
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            if (side == 2)
            {
                float bx = tg.Center.X;
                path.AddLine(bx + baseW, r.Bottom, Tail.X, Tail.Y); path.AddLine(Tail.X, Tail.Y, bx - baseW, r.Bottom);
            }
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            if (side == 3)
            {
                float by = tg.Center.Y;
                path.AddLine(r.X, by + baseW, Tail.X, Tail.Y); path.AddLine(Tail.X, Tail.Y, r.X, by - baseW);
            }
            path.CloseFigure();
            return path;
        }

        void DrawCallout(Graphics g, bool sh)
        {
            var r = Rect;
            if (r.Width < 4 || r.Height < 4) return;
            using (var path = CalloutPath())
            {
                if (Fill.A > 0) using (var b = new SolidBrush(Col(Fill, sh))) g.FillPath(b, path);
                if (Width > 0) using (var p = MakePen(Stroke, Width, sh)) g.DrawPath(p, path);
            }
            if (!sh) DrawText(g, r, true);
        }

        void DrawTextBox(Graphics g, bool sh)
        {
            var r = Rect;
            if (r.Width < 2 || r.Height < 2) return;
            if (Fill.A > 0) using (var b = new SolidBrush(Col(Fill, sh))) g.FillRectangle(b, r);
            if (Variant == 1 && Width > 0) using (var p = MakePen(Stroke, Width, sh)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            if (string.IsNullOrEmpty(Text)) return;
            DrawText(g, r, false, sh);
        }

        void DrawText(Graphics g, RectangleF r, bool centerV, bool sh = false)
        {
            if (string.IsNullOrEmpty(Text) || Editing) return;
            var inner = RectangleF.Inflate(r, -Pad, -Pad);
            if (Kind == AnnKind.Callout) inner = RectangleF.Inflate(r, -8, -6);
            using (var f = MakeFont(1))
            using (var sf = MakeFormat(!((Kind == AnnKind.Text || Kind == AnnKind.Callout) && AutoSize), centerV))
            using (var b = new SolidBrush(Col(TextColor, sh)))
            {
                if (Kind == AnnKind.Callout && Align == 0) sf.Alignment = StringAlignment.Center;
                g.DrawString(Text, f, b, inner, sf);
            }
        }

        void DrawStep(Graphics g, bool sh)
        {
            var r = Rect;
            float d = Math.Max(r.Width, r.Height);
            var sq = new RectangleF(r.X, r.Y, d, d);
            using (var b = new SolidBrush(Col(Fill.A > 0 ? Fill : Color.FromArgb(229, 57, 53), sh))) g.FillEllipse(b, sq);
            if (Width > 0 && Stroke.A > 0) using (var p = MakePen(Stroke, Width, sh)) g.DrawEllipse(p, sq);
            if (sh) return;
            using (var f = new Font(FontName, Math.Max(6, d * 0.52f), (Bold ? FontStyle.Bold : FontStyle.Regular), GraphicsUnit.Pixel))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            using (var b = new SolidBrush(Col(TextColor, false)))
                g.DrawString(Number.ToString(CultureInfo.InvariantCulture), f, b, new RectangleF(sq.X, sq.Y + d * 0.03f, sq.Width, sq.Height), sf);
        }

        void DrawPen(Graphics g, bool sh)
        {
            if (Pts.Count == 0) return;
            Color c = Highlighter ? Color.FromArgb((int)(Stroke.A * 0.45f), Stroke) : Stroke;
            using (var p = MakePen(c, Width, sh))
            {
                p.StartCap = Highlighter ? LineCap.Flat : LineCap.Round; p.EndCap = p.StartCap;
                if (Dash != DashStyle.Solid) p.DashStyle = Dash;
                if (Pts.Count == 1)
                {
                    using (var b = new SolidBrush(p.Color)) g.FillEllipse(b, Pts[0].X - Width / 2, Pts[0].Y - Width / 2, Width, Width);
                }
                else if (Pts.Count == 2) g.DrawLine(p, Pts[0], Pts[1]);
                else g.DrawCurve(p, Pts.ToArray(), 0.4f);
            }
        }

        void DrawBlur(Graphics g, Bitmap target)
        {
            if (target == null) return;
            var r = Rectangle.Intersect(Rectangle.Round(Rect), new Rectangle(0, 0, target.Width, target.Height));
            if (r.Width < 2 || r.Height < 2) return;
            using (var region = target.Clone(r, PixelFormat.Format32bppArgb))
            using (var res = Variant == 0 ? Effects.Pixelate(region, (int)Math.Max(3, Width)) : Effects.BoxBlur(region, (int)Math.Max(2, Width / 2)))
            {
                var st = g.Save();
                g.CompositingMode = CompositingMode.SourceCopy;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImageUnscaled(res, r.X, r.Y);
                g.Restore(st);
            }
        }

        void DrawMagnify(Graphics g, Bitmap target, bool sh)
        {
            var r = Rect;
            if (r.Width < 6 || r.Height < 6) return;
            using (var path = new GraphicsPath())
            {
                if (Variant == 1) path.AddEllipse(r); else path.AddRectangle(r);
                if (sh)
                {
                    using (var b = new SolidBrush(Col(Color.Black, true))) g.FillPath(b, path);
                    return;
                }
                if (target != null)
                {
                    float z = Math.Max(1.1f, Zoom);
                    var srcRect = new Rectangle((int)(r.X + r.Width / 2 - r.Width / z / 2), (int)(r.Y + r.Height / 2 - r.Height / z / 2), (int)Math.Max(1, r.Width / z), (int)Math.Max(1, r.Height / z));
                    var inside = Rectangle.Intersect(srcRect, new Rectangle(0, 0, target.Width, target.Height));
                    if (inside.Width > 0 && inside.Height > 0)
                        using (var copy = target.Clone(srcRect.IntersectsWith(new Rectangle(0, 0, target.Width, target.Height)) ? inside : srcRect, PixelFormat.Format32bppArgb))
                        {
                            var st = g.Save();
                            g.SetClip(path, CombineMode.Intersect);
                            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                            g.PixelOffsetMode = PixelOffsetMode.Half;
                            var dst = new RectangleF(r.X + (inside.X - srcRect.X) * z, r.Y + (inside.Y - srcRect.Y) * z, inside.Width * z, inside.Height * z);
                            g.CompositingMode = CompositingMode.SourceCopy;
                            g.DrawImage(copy, dst);
                            g.Restore(st);
                        }
                }
                if (Width > 0) using (var p = MakePen(Stroke, Width, false)) g.DrawPath(p, path);
            }
        }

        void DrawSpotlight(Graphics g, Bitmap target)
        {
            if (target == null) return;
            var r = Rect;
            using (var path = new GraphicsPath(FillMode.Alternate))
            {
                path.AddRectangle(new RectangleF(-1, -1, target.Width + 2, target.Height + 2));
                if (r.Width >= 1 && r.Height >= 1)
                {
                    if (Variant == 1) path.AddEllipse(r); else path.AddRectangle(r);
                }
                using (var b = new SolidBrush(Fill.A > 0 ? Fill : Color.FromArgb(150, 0, 0, 0))) g.FillPath(b, path);
            }
        }

        // ------------------------------------------------------------ serialisation

        static string C(Color c) { return c.ToArgb().ToString("X8", CultureInfo.InvariantCulture); }
        static Color C(string s) { return Color.FromArgb(int.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture)); }
        static string F(float f) { return f.ToString("R", CultureInfo.InvariantCulture); }
        static float F(string s) { return float.Parse(s, CultureInfo.InvariantCulture); }
        static string PT(PointF p) { return F(p.X) + "," + F(p.Y); }
        static PointF PT(string s) { var a = s.Split(','); return new PointF(F(a[0]), F(a[1])); }

        public XElement ToXml()
        {
            var e = new XElement("a",
                new XAttribute("kind", Kind.ToString()), new XAttribute("p1", PT(P1)), new XAttribute("p2", PT(P2)), new XAttribute("tail", PT(Tail)),
                new XAttribute("stroke", C(Stroke)), new XAttribute("fill", C(Fill)), new XAttribute("tc", C(TextColor)),
                new XAttribute("w", F(Width)), new XAttribute("dash", (int)Dash), new XAttribute("var", Variant), new XAttribute("sh", Shadow ? 1 : 0),
                new XAttribute("op", F(Opacity)), new XAttribute("font", FontName), new XAttribute("fs", F(FontSize)),
                new XAttribute("b", Bold ? 1 : 0), new XAttribute("i", Italic ? 1 : 0), new XAttribute("u", Underline ? 1 : 0),
                new XAttribute("al", Align), new XAttribute("auto", AutoSize ? 1 : 0), new XAttribute("n", Number), new XAttribute("z", F(Zoom)), new XAttribute("hl", Highlighter ? 1 : 0),
                new XAttribute("tp", F(TailPos)), new XAttribute("tw", F(TailW)));
            if (!string.IsNullOrEmpty(Text)) e.Add(new XElement("t", Text));
            if (Pts.Count > 0) e.Add(new XElement("pts", string.Join(";", Pts.ConvertAll(p => PT(p)).ToArray())));
            if (Img != null)
                using (var ms = new MemoryStream()) { Img.Save(ms, ImageFormat.Png); e.Add(new XElement("img", Convert.ToBase64String(ms.ToArray()))); }
            return e;
        }

        public static Ann FromXml(XElement e)
        {
            var a = new Ann();
            a.Kind = (AnnKind)Enum.Parse(typeof(AnnKind), (string)e.Attribute("kind"));
            a.P1 = PT((string)e.Attribute("p1")); a.P2 = PT((string)e.Attribute("p2")); a.Tail = PT((string)e.Attribute("tail"));
            a.Stroke = C((string)e.Attribute("stroke")); a.Fill = C((string)e.Attribute("fill")); a.TextColor = C((string)e.Attribute("tc"));
            a.Width = F((string)e.Attribute("w")); a.Dash = (DashStyle)(int)e.Attribute("dash"); a.Variant = (int)e.Attribute("var");
            a.Shadow = (int)e.Attribute("sh") == 1; a.Opacity = F((string)e.Attribute("op"));
            a.FontName = (string)e.Attribute("font"); a.FontSize = F((string)e.Attribute("fs"));
            a.Bold = (int)e.Attribute("b") == 1; a.Italic = (int)e.Attribute("i") == 1; a.Underline = (int)e.Attribute("u") == 1;
            a.Align = (int)e.Attribute("al"); a.AutoSize = (int)e.Attribute("auto") == 1; a.Number = (int)e.Attribute("n"); a.Zoom = F((string)e.Attribute("z"));
            a.Highlighter = (int)e.Attribute("hl") == 1;
            if (e.Attribute("tp") != null) a.TailPos = F((string)e.Attribute("tp"));
            if (e.Attribute("tw") != null) a.TailW = F((string)e.Attribute("tw"));
            var t = e.Element("t"); if (t != null) a.Text = t.Value;
            var pts = e.Element("pts");
            if (pts != null && pts.Value.Length > 0) foreach (var s in pts.Value.Split(';')) a.Pts.Add(PT(s));
            var img = e.Element("img");
            if (img != null)
                using (var ms = new MemoryStream(Convert.FromBase64String(img.Value)))
                using (var im = Image.FromStream(ms)) a.Img = Effects.ToArgb(im);
            return a;
        }
    }
}
