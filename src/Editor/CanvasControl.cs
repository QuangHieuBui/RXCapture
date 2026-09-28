using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Linq;
using System.Windows.Forms;

namespace RXCapture
{
    public enum Tool { Select, Arrow, Line, Shape, Callout, Text, Step, Stamp, Pen, Highlighter, Blur, Magnify, Spotlight, Fill, Eraser, Crop, CutOut }

    /// <summary>Displays the document (zoom / scroll / checkerboard) and implements all mouse interaction of the editor tools.</summary>
    public class CanvasControl : ScrollableControl
    {
        enum Drag { None, Create, Move, Handle, Marquee, Pan, Edge, CropNew, CropMove, CropResize, Cut, Erase }

        public Document Doc { get; private set; }
        Tool tool = Tool.Select;
        public readonly Dictionary<Tool, Ann> Defaults = new Dictionary<Tool, Ann>();
        public event Action<Ann> StepEditRequested;      // double-click / Enter on a step: the editor asks for its new value
        public readonly List<Ann> Selection = new List<Ann>();
        public float Zoom = 1f;
        public int FillTolerance = 20;
        public Color FillColor = Color.FromArgb(229, 57, 53);
        public int EraserSize = 24;
        public int CutOrientationAuto = 0;

        public event EventHandler SelectionChanged;
        public event EventHandler ZoomChanged;
        public event EventHandler ToolChanged;
        public event EventHandler ViewChanged;              // document content changed (for status bar etc.)
        public event Action<Point> ContextRequested;

        // rendering cache
        Bitmap view, below;
        bool viewDirty = true;
        int liveFrom = -1;
        bool hasAlpha;
        Bitmap alphaCheckedFor;
        static Bitmap checkTile;

        // interaction state
        Drag drag = Drag.None;
        PointF downImg, lastImg;
        Point downScreen, panStart, panScroll;
        bool pushed;
        int dragHandle;
        Ann dragAnn;
        RectangleF marquee;
        Rectangle cropRect;
        bool hasCrop;
        Rectangle cropAtDown;
        PointF cutTo;
        bool cutVertical;
        Point edgeCur;
        bool spaceDown;
        Point mouseScreen;

        // text editing
        TextBox editBox;
        Ann editAnn;
        string editText0;
        bool editIsNew;

        static List<Ann> clip = new List<Ann>();

        public CanvasControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            TabStop = true;
            BackColor = Theme.Back;
            AutoScroll = true;
            InitDefaults();
        }

        void InitDefaults()
        {
            LoadDefaults();
            ToolDefaultsStore.Changed += OnDefaultsChanged;
        }

        /// <summary>(Re)loads every tool's template from the saved defaults, falling back to the built-in ones.</summary>
        void LoadDefaults()
        {
            foreach (var t in ToolDefaultsStore.All) Defaults[t] = ToolDefaultsStore.Load(t);
        }

        void OnDefaultsChanged(object s, EventArgs e)
        {
            if (IsDisposed) return;
            LoadDefaults();
            Invalidate();
        }

        /// <summary>Tool whose defaults describe annotation a (Highlighter and Pen share a kind).</summary>
        Tool? ToolFor(Ann a)
        {
            foreach (var t in ToolDefaultsStore.All)
                if (KindOf(t) == a.Kind && (t == Tool.Highlighter) == a.Highlighter) return t;
            return null;
        }

        /// <summary>The tool "Set as default" would store for: the selected object's tool, else the active drawing tool.</summary>
        public Tool? DefaultTarget
        {
            get
            {
                if (Selection.Count == 1) return ToolFor(Selection[0]);
                Ann d;
                if (Defaults.TryGetValue(tool, out d)) return tool;
                return null;
            }
        }

        /// <summary>Stores the style of the selected object (or of the active tool) as the permanent default. Returns the tool it was stored for.</summary>
        public Tool? SaveAsDefault()
        {
            var t = DefaultTarget;
            if (t == null) return null;
            var src = Selection.Count == 1 ? Selection[0] : Defaults[t.Value];
            ToolDefaultsStore.Save(t.Value, src);
            return t;
        }

        /// <summary>Puts the tool's permanent default back to the factory values.</summary>
        public Tool? ResetDefault()
        {
            var t = DefaultTarget;
            if (t == null) return null;
            ToolDefaultsStore.Reset(t.Value);
            return t;
        }

        public static AnnKind? KindOf(Tool t)
        {
            switch (t)
            {
                case Tool.Arrow: return AnnKind.Arrow;
                case Tool.Line: return AnnKind.Line;
                case Tool.Shape: return AnnKind.Shape;
                case Tool.Callout: return AnnKind.Callout;
                case Tool.Text: return AnnKind.Text;
                case Tool.Step: return AnnKind.Step;
                case Tool.Stamp: return AnnKind.Stamp;
                case Tool.Pen: case Tool.Highlighter: return AnnKind.Pen;
                case Tool.Blur: return AnnKind.Blur;
                case Tool.Magnify: return AnnKind.Magnify;
                case Tool.Spotlight: return AnnKind.Spotlight;
            }
            return null;
        }

        float S { get { return Theme.Scale(this); } }
        public int CanvasMargin { get { return (int)(36 * S); } }

        public Tool CurrentTool
        {
            get { return tool; }
            set
            {
                if (tool == value) return;
                CommitEdit();
                if (tool == Tool.Crop && value != Tool.Crop) hasCrop = false;
                tool = value;
                if (value != Tool.Select) SetSelection(new List<Ann>());
                UpdateCursor(mouseScreen);
                Invalidate();
                if (ToolChanged != null) ToolChanged(this, EventArgs.Empty);
            }
        }

        /// <summary>The annotation whose style the ribbon should show: the selection, else the current tool's defaults.</summary>
        public Ann PropAnn
        {
            get
            {
                if (Selection.Count > 0) return Selection[0];
                Ann d; return Defaults.TryGetValue(tool, out d) ? d : null;
            }
        }

        // ------------------------------------------------------------------ document plumbing

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkScrollbars(this); }

        public void SetDocument(Document d)
        {
            CommitEdit();
            if (Doc != null) Doc.Changed -= OnDocChanged;
            Doc = d;
            Selection.Clear();
            hasCrop = false; drag = Drag.None; liveFrom = -1; below = null; viewDirty = true;
            if (Doc != null) Doc.Changed += OnDocChanged;
            ZoomFit(false);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        void OnDocChanged(object s, EventArgs e)
        {
            below = null; viewDirty = true;
            // drop selected objects that no longer exist (undo/redo replaces the list)
            Selection.RemoveAll(a => !Doc.Items.Contains(a));
            UpdateScroll();
            Invalidate();
            if (ViewChanged != null) ViewChanged(this, EventArgs.Empty);
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        void BeginLive(int from) { liveFrom = from; below = null; viewDirty = true; }
        void EndLive() { liveFrom = -1; below = null; viewDirty = true; }
        void Live() { viewDirty = true; Invalidate(); }

        void EnsureView()
        {
            if (Doc == null) return;
            if (!viewDirty && view != null) return;
            if (liveFrom >= 0 && liveFrom <= Doc.Items.Count)
            {
                if (below == null) below = Doc.Render(liveFrom);
                if (view == null || view.Width != below.Width || view.Height != below.Height) view = new Bitmap(below.Width, below.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(view))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImageUnscaled(below, 0, 0);
                    g.CompositingMode = CompositingMode.SourceOver;
                }
                Doc.DrawItems(view, liveFrom, -1);
            }
            else
            {
                var nv = Doc.Render();
                view = nv;
            }
            if (!ReferenceEquals(alphaCheckedFor, Doc.Base)) { hasAlpha = Effects.HasTransparency(Doc.Base); alphaCheckedFor = Doc.Base; }
            viewDirty = false;
        }

        // ------------------------------------------------------------------ geometry

        int IW { get { return Doc == null ? 0 : (int)Math.Round(Doc.Width * Zoom); } }
        int IH { get { return Doc == null ? 0 : (int)Math.Round(Doc.Height * Zoom); } }

        public Point Origin()
        {
            int m = CanvasMargin;
            int ox = Math.Max(m, (ClientSize.Width - IW) / 2), oy = Math.Max(m, (ClientSize.Height - IH) / 2);
            return new Point(ox + AutoScrollPosition.X, oy + AutoScrollPosition.Y);
        }

        public PointF ToImage(Point s) { var o = Origin(); return new PointF((s.X - o.X) / Zoom, (s.Y - o.Y) / Zoom); }
        public PointF ToScreenF(PointF p) { var o = Origin(); return new PointF(o.X + p.X * Zoom, o.Y + p.Y * Zoom); }
        Point ToScreen(PointF p) { var f = ToScreenF(p); return new Point((int)Math.Round(f.X), (int)Math.Round(f.Y)); }
        RectangleF RectToScreen(RectangleF r) { var a = ToScreenF(r.Location); return new RectangleF(a.X, a.Y, r.Width * Zoom, r.Height * Zoom); }

        void UpdateScroll()
        {
            if (Doc == null) return;
            int m = CanvasMargin;
            AutoScrollMinSize = new Size(IW + 2 * m, IH + 2 * m);
        }

        bool autoFit, autoFitUp;      // keep the whole image fitted while the view is resized, until the user zooms by hand

        public void SetZoom(float z, Point? anchor)
        {
            if (Doc == null) return;
            z = Math.Max(0.05f, Math.Min(16f, z));
            autoFit = false;
            Point a = anchor ?? new Point(ClientSize.Width / 2, ClientSize.Height / 2);
            var imgPt = ToImage(a);
            Zoom = z;
            UpdateScroll();
            var o = Origin();
            // centring offset without scroll:
            int baseX = o.X - AutoScrollPosition.X, baseY = o.Y - AutoScrollPosition.Y;
            int wantX = (int)(a.X - baseX - imgPt.X * Zoom), wantY = (int)(a.Y - baseY - imgPt.Y * Zoom);
            AutoScrollPosition = new Point(-wantX, -wantY);
            Invalidate();
            if (ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
        }

        public void ZoomFit(bool allowUpscale)
        {
            if (Doc == null) return;
            int m = CanvasMargin;
            float aw = Math.Max(50, ClientSize.Width - 2 * m), ah = Math.Max(50, ClientSize.Height - 2 * m);
            float z = Math.Min(aw / Doc.Width, ah / Doc.Height);
            autoFit = true; autoFitUp = allowUpscale;
            if (!allowUpscale) z = Math.Min(1f, z);
            Zoom = Math.Max(0.05f, z);
            UpdateScroll();
            AutoScrollPosition = new Point(0, 0);
            Invalidate();
            if (ZoomChanged != null) ZoomChanged(this, EventArgs.Empty);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            if ((ModifierKeys & Keys.Control) != 0)
            {
                float f = e.Delta > 0 ? 1.15f : 1f / 1.15f;
                SetZoom(Zoom * f, e.Location);
                var he = e as HandledMouseEventArgs; if (he != null) he.Handled = true;
                return;
            }
            base.OnMouseWheel(e);
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (editBox != null) LayoutEditBox();
            if (autoFit && Doc != null && ClientSize.Width > 60 && ClientSize.Height > 60) ZoomFit(autoFitUp);
        }

        // ------------------------------------------------------------------ painting

        static Bitmap CheckTile()
        {
            if (checkTile != null) return checkTile;
            checkTile = new Bitmap(16, 16);
            using (var g = Graphics.FromImage(checkTile))
            {
                g.Clear(Color.FromArgb(255, 255, 255));
                using (var b = new SolidBrush(Color.FromArgb(230, 230, 230)))
                { g.FillRectangle(b, 0, 0, 8, 8); g.FillRectangle(b, 8, 8, 8, 8); }
            }
            return checkTile;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            try { PaintCanvas(e); }
            catch (Exception ex)
            {
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(AppSettings.DataDir, "error.log"), DateTime.Now + " canvas paint\r\n" + ex + "\r\n\r\n"); } catch { }
                viewDirty = true;
            }
        }

        void PaintCanvas(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.Back);
            if (Doc == null) return;
            EnsureView();
            var o = Origin();
            var dest = new Rectangle(o.X, o.Y, IW, IH);
            var vis = Rectangle.Intersect(dest, ClientRectangle);
            vis.Intersect(e.ClipRectangle);
            if (!vis.IsEmpty)
            {
                if (hasAlpha)
                    using (var tb = new TextureBrush(CheckTile()))
                    {
                        tb.TranslateTransform(dest.X, dest.Y);
                        g.FillRectangle(tb, vis);
                    }
                else using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, vis);

                g.InterpolationMode = Zoom >= 2f ? InterpolationMode.NearestNeighbor : (Zoom < 1f ? InterpolationMode.HighQualityBilinear : InterpolationMode.NearestNeighbor);
                g.PixelOffsetMode = PixelOffsetMode.Half;
                float sx = (vis.X - dest.X) / Zoom, sy = (vis.Y - dest.Y) / Zoom;
                float sw = vis.Width / Zoom, sh = vis.Height / Zoom;
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(view, vis, sx, sy, sw, sh, GraphicsUnit.Pixel, ia);
                }
            }
            g.PixelOffsetMode = PixelOffsetMode.Default;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            DrawOverlays(g, dest);
        }

        void HandleSquare(Graphics g, PointF p, bool tail = false)
        {
            float hs = 7 * S;
            var r = new RectangleF(p.X - hs / 2, p.Y - hs / 2, hs, hs);
            if (tail)
            {
                using (var b = new SolidBrush(Color.FromArgb(255, 214, 10))) g.FillEllipse(b, r);
                g.DrawEllipse(Pens.Black, r);
            }
            else
            {
                g.FillRectangle(Brushes.White, r);
                using (var pn = new Pen(Color.FromArgb(40, 40, 40))) g.DrawRectangle(pn, r.X, r.Y, r.Width, r.Height);
            }
        }

        /// <summary>Handle for the shape of the callout tail (slide / widen the base): an orange diamond.</summary>
        void HandleDiamond(Graphics g, PointF p)
        {
            float h = 5.5f * S;
            var pts = new[] { new PointF(p.X, p.Y - h), new PointF(p.X + h, p.Y), new PointF(p.X, p.Y + h), new PointF(p.X - h, p.Y) };
            using (var b = new SolidBrush(Color.FromArgb(255, 150, 30))) g.FillPolygon(b, pts);
            g.DrawPolygon(Pens.Black, pts);
        }

        PointF[] EdgeHandlePoints(Rectangle dest)
        {
            float cx = dest.X + dest.Width / 2f, cy = dest.Y + dest.Height / 2f;
            return new[] {
                new PointF(dest.Left, dest.Top), new PointF(cx, dest.Top), new PointF(dest.Right, dest.Top), new PointF(dest.Right, cy),
                new PointF(dest.Right, dest.Bottom), new PointF(cx, dest.Bottom), new PointF(dest.Left, dest.Bottom), new PointF(dest.Left, cy) };
        }

        void DrawOverlays(Graphics g, Rectangle dest)
        {
            // frame
            using (var p = new Pen(Color.FromArgb(70, 70, 74))) g.DrawRectangle(p, dest.X - 1, dest.Y - 1, dest.Width + 1, dest.Height + 1);

            // selection
            foreach (var a in Selection)
            {
                if (a.IsLine)
                {
                    foreach (var h in a.Handles()) HandleSquare(g, ToScreenF(h));
                    continue;
                }
                var r = RectToScreen(a.Kind == AnnKind.Pen ? a.PenBounds() : a.Rect);
                using (var p = new Pen(Theme.AccentLight, 1f) { DashStyle = DashStyle.Dash })
                    g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
                var hs = a.Handles();
                for (int i = 0; i < hs.Length; i++) { if (i >= 9) HandleDiamond(g, ToScreenF(hs[i])); else HandleSquare(g, ToScreenF(hs[i]), i == 8); }
            }

            // creation preview for text boxes (they draw nothing while empty)
            if (drag == Drag.Create && dragAnn != null && dragAnn.Kind == AnnKind.Callout)
                HandleSquare(g, ToScreenF(dragAnn.Tail), true);      // the anchor the tail is fixed to

            if (drag == Drag.Create && dragAnn != null && dragAnn.Kind == AnnKind.Text)
            {
                var r = RectToScreen(dragAnn.Rect);
                using (var p = new Pen(Theme.AccentLight) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            }

            if (drag == Drag.Marquee)
            {
                var r = RectToScreen(marquee);
                using (var b = new SolidBrush(Color.FromArgb(40, 28, 151, 234))) g.FillRectangle(b, r);
                using (var p = new Pen(Theme.AccentLight)) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            }

            if (tool == Tool.Crop && (hasCrop || drag == Drag.CropNew)) DrawCrop(g, dest);

            if (drag == Drag.Cut)
            {
                var band = CutBandRect();
                var r = RectToScreen(band);
                using (var b = new HatchBrush(HatchStyle.WideUpwardDiagonal, Color.FromArgb(160, 255, 80, 80), Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(b, r);
                using (var p = new Pen(Color.FromArgb(255, 90, 90)) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            }

            // canvas edge handles
            if (drag == Drag.Edge)
            {
                var d = EdgeDelta();
                var nr = new Rectangle(dest.X - (int)(d.Left * Zoom), dest.Y - (int)(d.Top * Zoom), dest.Width + (int)((d.Left + d.Right) * Zoom), dest.Height + (int)((d.Top + d.Bottom) * Zoom));
                using (var p = new Pen(Color.White) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, nr.X, nr.Y, nr.Width, nr.Height);
                string txt = (Doc.Width + d.Left + d.Right) + " × " + (Doc.Height + d.Top + d.Bottom);
                TextRenderer.DrawText(g, txt, Font, new Point(nr.X + 4, nr.Y + 4), Color.White);
            }
            else if (tool != Tool.Crop)
                foreach (var pt in EdgeHandlePoints(dest)) HandleSquare(g, pt);
        }

        Rectangle CropBtnRect(int i)
        {
            var r = RectToScreen(cropRect);
            int w = (int)(70 * S), h = (int)(26 * S), gap = (int)(6 * S);
            int total = 2 * w + gap;
            int x = (int)(r.X + r.Width / 2 - total / 2), y = (int)(r.Bottom + 10 * S);
            if (y + h > ClientSize.Height) y = (int)(r.Y - h - 10 * S);
            x = Math.Max(4, Math.Min(x, ClientSize.Width - total - 4));
            return new Rectangle(x + i * (w + gap), y, w, h);
        }

        void DrawCrop(Graphics g, Rectangle dest)
        {
            var r = RectToScreen(cropRect);
            using (var path = new GraphicsPath(FillMode.Alternate))
            {
                path.AddRectangle(dest);
                path.AddRectangle(r);
                using (var b = new SolidBrush(Color.FromArgb(140, 0, 0, 0))) g.FillPath(b, path);
            }
            using (var p = new Pen(Color.White) { DashStyle = DashStyle.Dash }) g.DrawRectangle(p, r.X, r.Y, r.Width, r.Height);
            // thirds
            using (var p = new Pen(Color.FromArgb(70, 255, 255, 255)))
            {
                g.DrawLine(p, r.X + r.Width / 3, r.Y, r.X + r.Width / 3, r.Bottom); g.DrawLine(p, r.X + 2 * r.Width / 3, r.Y, r.X + 2 * r.Width / 3, r.Bottom);
                g.DrawLine(p, r.X, r.Y + r.Height / 3, r.Right, r.Y + r.Height / 3); g.DrawLine(p, r.X, r.Y + 2 * r.Height / 3, r.Right, r.Y + 2 * r.Height / 3);
            }
            var pts = new[] { new PointF(r.Left, r.Top), new PointF(r.X + r.Width / 2, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Y + r.Height / 2), new PointF(r.Right, r.Bottom), new PointF(r.X + r.Width / 2, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, r.Y + r.Height / 2) };
            foreach (var p in pts) HandleSquare(g, p);
            TextRenderer.DrawText(g, cropRect.Width + " × " + cropRect.Height, Font, new Point((int)r.X + 4, (int)r.Y + 4), Color.White, Color.FromArgb(150, 0, 0, 0));
            if (hasCrop && drag == Drag.None || drag == Drag.CropMove || drag == Drag.CropResize)
            {
                string[] lbl = { Loc.T("Crop"), Loc.T("Cancel") };
                for (int i = 0; i < 2; i++)
                {
                    var br = CropBtnRect(i);
                    using (var pth = Theme.RoundRect(br, 4))
                    {
                        using (var b = new SolidBrush(i == 0 ? Theme.Accent : Color.FromArgb(70, 70, 76))) g.FillPath(b, pth);
                    }
                    TextRenderer.DrawText(g, lbl[i], Font, br, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                }
            }
        }

        // ------------------------------------------------------------------ hit testing helpers

        float Tol { get { return 5f * S / Zoom; } }

        Ann HitAnn(PointF ip)
        {
            for (int i = Doc.Items.Count - 1; i >= 0; i--)
                if (Doc.Items[i].HitTest(ip, Tol)) return Doc.Items[i];
            return null;
        }

        int HitHandle(Ann a, Point screen)
        {
            var hs = a.Handles();
            float tol = 6 * S;
            for (int i = hs.Length - 1; i >= 0; i--)
            {
                var p = ToScreenF(hs[i]);
                if (Math.Abs(p.X - screen.X) <= tol && Math.Abs(p.Y - screen.Y) <= tol) return i;
            }
            return -1;
        }

        int HitEdge(Point screen)
        {
            var o = Origin();
            var pts = EdgeHandlePoints(new Rectangle(o.X, o.Y, IW, IH));
            float tol = 6 * S;
            for (int i = 0; i < pts.Length; i++)
                if (Math.Abs(pts[i].X - screen.X) <= tol && Math.Abs(pts[i].Y - screen.Y) <= tol) return i;
            return -1;
        }

        struct Edges { public int Left, Top, Right, Bottom; }

        Edges EdgeDelta()
        {
            int dx = (int)Math.Round(edgeCur.X / Zoom), dy = (int)Math.Round(edgeCur.Y / Zoom);
            var e = new Edges();
            int h = dragHandle;
            if (h == 0 || h == 6 || h == 7) e.Left = -dx;
            if (h == 2 || h == 3 || h == 4) e.Right = dx;
            if (h == 0 || h == 1 || h == 2) e.Top = -dy;
            if (h == 4 || h == 5 || h == 6) e.Bottom = dy;
            if (Doc.Width + e.Left + e.Right < 1) { e.Left = 0; e.Right = 0; }
            if (Doc.Height + e.Top + e.Bottom < 1) { e.Top = 0; e.Bottom = 0; }
            return e;
        }

        // ------------------------------------------------------------------ selection

        public void SetSelection(List<Ann> sel)
        {
            Selection.Clear();
            Selection.AddRange(sel);
            Invalidate();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
        }

        public void SelectAll() { if (Doc != null) { if (tool != Tool.Select) CurrentTool = Tool.Select; SetSelection(new List<Ann>(Doc.Items)); } }

        public string SelectionInfo()
        {
            if (Doc == null) return "";
            if (Selection.Count > 0)
            {
                var r = Selection[0].SelBounds();
                for (int i = 1; i < Selection.Count; i++) r = RectangleF.Union(r, Selection[i].SelBounds());
                return (int)r.X + "," + (int)r.Y + "  " + (int)r.Width + "x" + (int)r.Height;
            }
            if (hasCrop) return cropRect.X + "," + cropRect.Y + "  " + cropRect.Width + "x" + cropRect.Height;
            return "0,0  " + Doc.Width + "x" + Doc.Height;
        }

        // ------------------------------------------------------------------ mouse

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (Doc == null) return;
            CommitEdit();
            Focus();
            downScreen = e.Location; mouseScreen = e.Location;
            var ip = ToImage(e.Location);
            downImg = ip; lastImg = ip; pushed = false;

            if (e.Button == MouseButtons.Middle || (e.Button == MouseButtons.Left && spaceDown))
            {
                drag = Drag.Pan; panStart = e.Location; panScroll = new Point(-AutoScrollPosition.X, -AutoScrollPosition.Y);
                Cursor = Cursors.Hand;
                return;
            }
            if (e.Button == MouseButtons.Right)
            {
                var hit = HitAnn(ip);
                if (hit != null && !Selection.Contains(hit)) SetSelection(new List<Ann> { hit });
                if (ContextRequested != null) ContextRequested(e.Location);
                return;
            }
            if (e.Button != MouseButtons.Left) return;

            // crop tool has its own rules
            if (tool == Tool.Crop)
            {
                var over = hasCrop ? null : HitAnn(ip);
                if (over != null)
                {
                    if (!Selection.Contains(over)) SetSelection(new List<Ann> { over });
                    StartMove();
                    return;
                }
                MouseDownCrop(e.Location, ip); return;
            }

            // handle of the selected annotation
            if (Selection.Count == 1)
            {
                int h = HitHandle(Selection[0], e.Location);
                if (h >= 0)
                {
                    drag = Drag.Handle; dragHandle = h; dragAnn = Selection[0];
                    BeginLive(Doc.Items.IndexOf(dragAnn));
                    return;
                }
            }
            int eh = HitEdge(e.Location);
            if (eh >= 0) { drag = Drag.Edge; dragHandle = eh; edgeCur = Point.Empty; return; }

            if (tool == Tool.Select) { MouseDownSelect(ip); return; }

            // drawing tools: pressing on an existing object selects / moves it (no need to switch to Select first)
            {
                var over = HitAnn(ip);
                if (over != null && !(tool == Tool.Text && (over.Kind == AnnKind.Text || over.Kind == AnnKind.Callout)))
                {
                    if ((ModifierKeys & Keys.Shift) != 0) { var l = new List<Ann>(Selection); if (l.Contains(over)) l.Remove(over); else l.Add(over); SetSelection(l); }
                    else if (!Selection.Contains(over)) SetSelection(new List<Ann> { over });
                    if (Selection.Contains(over)) StartMove();
                    return;
                }
                // Callout tool: while a callout (the one just added, or any object) is selected, a click on empty canvas
                // only deselects it; the next click on empty canvas then adds a new callout.
                if (over == null && tool == Tool.Callout && Selection.Count > 0)
                {
                    SetSelection(new List<Ann>());
                    Invalidate();
                    return;
                }
            }

            switch (tool)
            {
                case Tool.Fill: DoFill(ip); return;
                case Tool.Eraser: StartErase(); return;
                case Tool.CutOut: drag = Drag.Cut; cutTo = ip; return;
                case Tool.Text:
                    {
                        var t = HitAnn(ip);
                        if (t != null && (t.Kind == AnnKind.Text || t.Kind == AnnKind.Callout)) { SetSelection(new List<Ann> { t }); BeginEdit(t, false); return; }
                        break;
                    }
            }
            StartCreate(ip);
        }

        void MouseDownSelect(PointF ip)
        {
            var hit = HitAnn(ip);
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            if (hit != null)
            {
                if (shift)
                {
                    var l = new List<Ann>(Selection);
                    if (l.Contains(hit)) l.Remove(hit); else l.Add(hit);
                    SetSelection(l);
                }
                else if (!Selection.Contains(hit)) SetSelection(new List<Ann> { hit });
                if (Selection.Contains(hit)) StartMove();
                return;
            }
            if (!shift) SetSelection(new List<Ann>());
            drag = Drag.Marquee; marquee = new RectangleF(ip, SizeF.Empty);
        }

        void StartMove()
        {
            drag = Drag.Move;
            int min = int.MaxValue;
            foreach (var a in Selection) min = Math.Min(min, Doc.Items.IndexOf(a));
            BeginLive(min);
        }

        void StartCreate(PointF ip)
        {
            Ann tpl;
            if (!Defaults.TryGetValue(tool, out tpl)) return;
            var a = tpl.Clone();
            a.Text = a.HasText && a.Kind != AnnKind.Step ? "" : a.Text;
            a.P1 = ip; a.P2 = ip; a.Tail = ip;
            a.Pts = new List<PointF>();
            if (a.Kind == AnnKind.Pen) a.Pts.Add(ip);
            if (a.Kind == AnnKind.Step) a.Number = Doc.NextStepNumber();
            if (a.Kind == AnnKind.Text) a.AutoSize = true;
            if (a.Kind == AnnKind.Callout) { a.AutoSize = false; a.Rect = new RectangleF(ip.X, ip.Y, 0, 0); }   // the press point is the tail tip; the box appears once the mouse moves
            Doc.Push();
            Doc.Items.Add(a);
            dragAnn = a; drag = Drag.Create;
            SetSelection(new List<Ann> { a });
            BeginLive(Doc.Items.Count - 1);
            Live();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (Doc == null) return;
            mouseScreen = e.Location;
            var ip = ToImage(e.Location);
            bool shift = (ModifierKeys & Keys.Shift) != 0;
            switch (drag)
            {
                case Drag.Pan:
                    AutoScrollPosition = new Point(panScroll.X - (e.X - panStart.X), panScroll.Y - (e.Y - panStart.Y));
                    Invalidate();
                    return;
                case Drag.Create:
                    UpdateCreate(ip, shift); return;
                case Drag.Move:
                    {
                        if (!pushed) { if (Math.Abs(e.X - downScreen.X) < 2 && Math.Abs(e.Y - downScreen.Y) < 2) return; Doc.Push(); pushed = true; }
                        float dx = ip.X - lastImg.X, dy = ip.Y - lastImg.Y;
                        // a callout moves as a whole (box and tail); hold Alt to move only the box and keep the tail tip where it is
                        bool bodyOnly = Selection.Count == 1 && (ModifierKeys & Keys.Alt) != 0;
                        foreach (var a in Selection) { if (bodyOnly && a.Kind == AnnKind.Callout) a.MoveBody(dx, dy); else a.Move(dx, dy); }
                        lastImg = ip; Live();
                        return;
                    }
                case Drag.Handle:
                    {
                        if (!pushed) { Doc.Push(); pushed = true; }
                        dragAnn.DragHandle(dragHandle, ip, shift);
                        if (dragAnn.Kind == AnnKind.Callout && dragHandle < 8) dragAnn.AutoSize = false;
                        Live();
                        return;
                    }
                case Drag.Marquee:
                    {
                        marquee = RectangleF.FromLTRB(Math.Min(downImg.X, ip.X), Math.Min(downImg.Y, ip.Y), Math.Max(downImg.X, ip.X), Math.Max(downImg.Y, ip.Y));
                        var sel = new List<Ann>();
                        foreach (var a in Doc.Items) if (a.SelBounds().IntersectsWith(marquee)) sel.Add(a);
                        if (!SameSel(sel)) SetSelection(sel);
                        Invalidate();
                        return;
                    }
                case Drag.Edge:
                    edgeCur = new Point(e.X - downScreen.X, e.Y - downScreen.Y); Invalidate(); return;
                case Drag.CropNew:
                    cropRect = ClampRect(Rectangle.FromLTRB((int)Math.Round(Math.Min(downImg.X, ip.X)), (int)Math.Round(Math.Min(downImg.Y, ip.Y)), (int)Math.Round(Math.Max(downImg.X, ip.X)), (int)Math.Round(Math.Max(downImg.Y, ip.Y))));
                    Invalidate(); RaiseView(); return;
                case Drag.CropMove:
                    {
                        var r = cropAtDown; r.Offset((int)Math.Round(ip.X - downImg.X), (int)Math.Round(ip.Y - downImg.Y));
                        r.X = Math.Max(0, Math.Min(r.X, Doc.Width - r.Width)); r.Y = Math.Max(0, Math.Min(r.Y, Doc.Height - r.Height));
                        cropRect = r; Invalidate(); RaiseView(); return;
                    }
                case Drag.CropResize:
                    cropRect = ResizeCrop(cropAtDown, dragHandle, ip); Invalidate(); RaiseView(); return;
                case Drag.Cut:
                    cutTo = ip; Invalidate(); return;
                case Drag.Erase:
                    EraseTo(ip); return;
            }
            UpdateCursor(e.Location);
        }

        void RaiseView() { if (ViewChanged != null) ViewChanged(this, EventArgs.Empty); }

        bool SameSel(List<Ann> l)
        {
            if (l.Count != Selection.Count) return false;
            for (int i = 0; i < l.Count; i++) if (!ReferenceEquals(l[i], Selection[i])) return false;
            return true;
        }

        /// <summary>
        /// Callout creation is tail-first: the press point is the fixed tail tip. Nothing but the tip shows until the mouse
        /// starts to move; then the box grows between a corner just off the tip and the cursor, following the drag.
        /// (A plain click without a drag gives a default box, see FinishCreate.)
        /// </summary>
        void PlaceCallout(Ann a, PointF ip, bool final)
        {
            const float gapX = 13, gapY = 26, minW = 60, minH = 40;
            var t = a.Tail;
            float dist = (float)Math.Sqrt((ip.X - t.X) * (ip.X - t.X) + (ip.Y - t.Y) * (ip.Y - t.Y)) * Zoom;
            if (dist < 4) { a.Rect = new RectangleF(t.X, t.Y, 0, 0); return; }          // just pressed: only the tip
            float k = Math.Min(1f, dist / 60f);
            float sx = ip.X >= t.X ? 1 : -1, sy = ip.Y >= t.Y ? 1 : -1;
            var near = new PointF(t.X + sx * gapX * k, t.Y + sy * gapY * k);              // near corner, diagonally off the tip
            var f = ip;
            float mw = final ? minW : 8, mh = final ? minH : 8;
            if (Math.Abs(f.X - near.X) < mw) f.X = near.X + sx * mw;
            if (Math.Abs(f.Y - near.Y) < mh) f.Y = near.Y + sy * mh;
            a.Rect = RectangleF.FromLTRB(Math.Min(near.X, f.X), Math.Min(near.Y, f.Y), Math.Max(near.X, f.X), Math.Max(near.Y, f.Y));
        }

        /// <summary>Default box for a callout that was only clicked: up and to the right of the tail tip.</summary>
        static void DefaultCalloutBox(Ann a)
        {
            var t = a.Tail;
            a.Rect = RectangleF.FromLTRB(t.X + 13, t.Y - 26 - 64, t.X + 190, t.Y - 26);
        }

        void UpdateCreate(PointF ip, bool shift)
        {
            var a = dragAnn;
            switch (a.Kind)
            {
                case AnnKind.Pen:
                    {
                        var last = a.Pts[a.Pts.Count - 1];
                        float d = (float)Math.Sqrt((ip.X - last.X) * (ip.X - last.X) + (ip.Y - last.Y) * (ip.Y - last.Y));
                        if (d * Zoom >= 2f) a.Pts.Add(ip);
                        break;
                    }
                case AnnKind.Callout:
                    PlaceCallout(a, ip, false);
                    break;
                case AnnKind.Arrow:
                case AnnKind.Line:
                    a.P2 = shift ? Ann.SnapAngle(a.P1, ip) : ip;
                    break;
                default:
                    {
                        var p = ip;
                        if (shift)
                        {
                            float dx = ip.X - a.P1.X, dy = ip.Y - a.P1.Y, m = Math.Max(Math.Abs(dx), Math.Abs(dy));
                            p = new PointF(a.P1.X + Math.Sign(dx) * m, a.P1.Y + Math.Sign(dy) * m);
                        }
                        a.P2 = p;
                        if (a.Kind == AnnKind.Text) a.AutoSize = false;
                        break;
                    }
            }
            Live();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (Doc == null) return;
            var ip = ToImage(e.Location);
            var d = drag; drag = Drag.None;
            switch (d)
            {
                case Drag.Create: FinishCreate(ip); break;
                case Drag.Move:
                case Drag.Handle:
                    EndLive();
                    if (pushed) Doc.Raise(); else Invalidate();
                    break;
                case Drag.Marquee: Invalidate(); break;
                case Drag.Edge:
                    {
                        var ed = EdgeDelta();
                        if (ed.Left != 0 || ed.Right != 0 || ed.Top != 0 || ed.Bottom != 0) Doc.ResizeEdges(ed.Left, ed.Top, ed.Right, ed.Bottom, Color.Transparent);
                        else Invalidate();
                        break;
                    }
                case Drag.CropNew:
                    if (cropRect.Width < 4 || cropRect.Height < 4) hasCrop = false; else hasCrop = true;
                    Invalidate(); RaiseView(); break;
                case Drag.CropMove:
                case Drag.CropResize: Invalidate(); break;
                case Drag.Cut: FinishCut(); break;
                case Drag.Erase: EndLive(); Doc.Raise(); break;
                case Drag.Pan: UpdateCursor(e.Location); break;
            }
        }

        void FinishCreate(PointF ip)
        {
            var a = dragAnn;
            bool tiny = Math.Abs(a.P2.X - a.P1.X) * Zoom < 4 && Math.Abs(a.P2.Y - a.P1.Y) * Zoom < 4;
            bool cancel = false;
            switch (a.Kind)
            {
                case AnnKind.Arrow: case AnnKind.Line: case AnnKind.Shape: case AnnKind.Blur: case AnnKind.Magnify: case AnnKind.Spotlight:
                    if (tiny) cancel = true;
                    break;
                case AnnKind.Callout:
                    // a plain click gives a default box that fits its text; a drag keeps the size you stretched it to
                    if (Math.Sqrt((ip.X - a.Tail.X) * (ip.X - a.Tail.X) + (ip.Y - a.Tail.Y) * (ip.Y - a.Tail.Y)) * Zoom < 8) { DefaultCalloutBox(a); a.AutoSize = true; }
                    else { PlaceCallout(a, ip, true); a.AutoSize = false; }
                    break;
                case AnnKind.Text:
                    if (tiny) { a.AutoSize = true; a.FitText(); }
                    break;
                case AnnKind.Step:
                    if (tiny) { float ss = Math.Max(12f, a.Zoom); a.Rect = new RectangleF(a.P1.X - ss / 2, a.P1.Y - ss / 2, ss, ss); }      // default step size (40 px)
                    break;
                case AnnKind.Stamp:
                    if (tiny) a.Rect = new RectangleF(a.P1.X - 26, a.P1.Y - 26, 52, 52);
                    break;
            }
            EndLive();
            if (cancel)
            {
                Doc.Items.Remove(a); Doc.DropLastUndo();
                SetSelection(new List<Ann>());
                Invalidate();
                return;
            }
            dragAnn = null;
            Doc.Raise();
            SetSelection(new List<Ann> { a });
            if (a.Kind == AnnKind.Text || a.Kind == AnnKind.Callout) BeginEdit(a, true);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (Doc == null || e.Button != MouseButtons.Left) return;
            if (tool == Tool.Crop && hasCrop && RectToScreen(cropRect).Contains(e.Location)) { CommitCrop(); return; }
            var hit = HitAnn(ToImage(e.Location));
            if (hit != null && (hit.Kind == AnnKind.Text || hit.Kind == AnnKind.Callout))
            {
                SetSelection(new List<Ann> { hit });
                BeginEdit(hit, false);
            }
            else if (hit != null && hit.Kind == AnnKind.Step && tool == Tool.Select && StepEditRequested != null)
            {
                SetSelection(new List<Ann> { hit });
                StepEditRequested(hit);         // double-click a step to change its number
            }
        }

        // ------------------------------------------------------------------ cursor

        void UpdateCursor(Point p)
        {
            if (Doc == null) return;
            if (spaceDown) { Cursor = Cursors.Hand; return; }
            if (tool == Tool.Crop)
            {
                if (hasCrop)
                {
                    int h = HitCropHandle(p);
                    if (h >= 0) { Cursor = SizeCursor(h); return; }
                    if (RectToScreen(cropRect).Contains(p)) { Cursor = Cursors.SizeAll; return; }
                }
                if (HitAnn(ToImage(p)) != null) { Cursor = Cursors.SizeAll; return; }
                Cursor = Cursors.Cross; return;
            }
            if (Selection.Count == 1)
            {
                int h = HitHandle(Selection[0], p);
                if (h >= 0) { Cursor = Selection[0].IsLine || h == 8 ? Cursors.Cross : (h >= 9 ? Cursors.Hand : SizeCursor(h)); return; }
            }
            int eh = HitEdge(p);
            if (eh >= 0) { Cursor = SizeCursor(eh); return; }
            if (tool == Tool.Select)
            {
                var ip = ToImage(p);
                Cursor = HitAnn(ip) != null ? Cursors.SizeAll : Cursors.Default;
                return;
            }
            {
                var over = HitAnn(ToImage(p));
                if (over != null) { Cursor = (tool == Tool.Text && (over.Kind == AnnKind.Text || over.Kind == AnnKind.Callout)) ? Cursors.IBeam : Cursors.SizeAll; return; }
            }
            Cursor = tool == Tool.Text ? Cursors.IBeam : Cursors.Cross;
        }

        static Cursor SizeCursor(int h)
        {
            switch (h)
            {
                case 0: case 4: return Cursors.SizeNWSE;
                case 2: case 6: return Cursors.SizeNESW;
                case 1: case 5: return Cursors.SizeNS;
                default: return Cursors.SizeWE;
            }
        }

        // ------------------------------------------------------------------ crop tool

        Rectangle ClampRect(Rectangle r) { r.Intersect(new Rectangle(0, 0, Doc.Width, Doc.Height)); return r; }

        int HitCropHandle(Point p)
        {
            var r = RectToScreen(cropRect);
            var pts = new[] { new PointF(r.Left, r.Top), new PointF(r.X + r.Width / 2, r.Top), new PointF(r.Right, r.Top), new PointF(r.Right, r.Y + r.Height / 2), new PointF(r.Right, r.Bottom), new PointF(r.X + r.Width / 2, r.Bottom), new PointF(r.Left, r.Bottom), new PointF(r.Left, r.Y + r.Height / 2) };
            float tol = 7 * S;
            for (int i = 0; i < pts.Length; i++) if (Math.Abs(pts[i].X - p.X) <= tol && Math.Abs(pts[i].Y - p.Y) <= tol) return i;
            return -1;
        }

        Rectangle ResizeCrop(Rectangle r, int h, PointF ip)
        {
            int l = r.Left, t = r.Top, rt = r.Right, b = r.Bottom;
            int x = (int)Math.Round(ip.X), y = (int)Math.Round(ip.Y);
            if (h == 0 || h == 6 || h == 7) l = x;
            if (h == 2 || h == 3 || h == 4) rt = x;
            if (h == 0 || h == 1 || h == 2) t = y;
            if (h == 4 || h == 5 || h == 6) b = y;
            return ClampRect(Rectangle.FromLTRB(Math.Min(l, rt), Math.Min(t, b), Math.Max(l, rt), Math.Max(t, b)));
        }

        void MouseDownCrop(Point screen, PointF ip)
        {
            if (hasCrop)
            {
                if (CropBtnRect(0).Contains(screen)) { CommitCrop(); return; }
                if (CropBtnRect(1).Contains(screen)) { CancelCrop(); return; }
                int h = HitCropHandle(screen);
                if (h >= 0) { drag = Drag.CropResize; dragHandle = h; cropAtDown = cropRect; return; }
                if (RectToScreen(cropRect).Contains(screen)) { drag = Drag.CropMove; cropAtDown = cropRect; return; }
            }
            drag = Drag.CropNew; hasCrop = false;
            cropRect = new Rectangle((int)Math.Round(ip.X), (int)Math.Round(ip.Y), 0, 0);
        }

        public bool HasCrop { get { return hasCrop; } }
        public void CommitCrop()
        {
            if (!hasCrop || Doc == null) return;
            var r = cropRect; hasCrop = false;
            Doc.Crop(r);
            ZoomFit(false);
        }
        public void CancelCrop() { hasCrop = false; drag = Drag.None; Invalidate(); RaiseView(); }
        public void SetCropAll() { if (Doc != null) { cropRect = new Rectangle(0, 0, Doc.Width, Doc.Height); hasCrop = true; Invalidate(); } }

        // ------------------------------------------------------------------ cut-out, fill, eraser

        RectangleF CutBandRect()
        {
            float dx = Math.Abs(cutTo.X - downImg.X), dy = Math.Abs(cutTo.Y - downImg.Y);
            cutVertical = dx >= dy;
            if (cutVertical) return RectangleF.FromLTRB(Math.Min(downImg.X, cutTo.X), 0, Math.Max(downImg.X, cutTo.X), Doc.Height);
            return RectangleF.FromLTRB(0, Math.Min(downImg.Y, cutTo.Y), Doc.Width, Math.Max(downImg.Y, cutTo.Y));
        }

        void FinishCut()
        {
            var band = CutBandRect();
            int from = (int)Math.Round(cutVertical ? band.Left : band.Top), to = (int)Math.Round(cutVertical ? band.Right : band.Bottom);
            if (to - from < 2) { Invalidate(); return; }
            Doc.CutBand(cutVertical, from, to);
            ZoomFit(false);
        }

        void DoFill(PointF ip)
        {
            int x = (int)ip.X, y = (int)ip.Y;
            if (x < 0 || y < 0 || x >= Doc.Width || y >= Doc.Height) return;
            var src = Doc.Items.Count > 0 ? null : Doc.Base;
            Doc.Push();
            Doc.Base = Effects.ToArgb(Effects.FloodFill(Doc.Base, x, y, FillColor, FillTolerance));
            Doc.Raise();
        }

        void StartErase()
        {
            Doc.Push();
            Doc.Base = new Bitmap(Doc.Base);
            drag = Drag.Erase;
            EraseTo(downImg);
        }

        void EraseTo(PointF ip)
        {
            using (var g = Graphics.FromImage(Doc.Base))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var pen = new Pen(Color.Transparent, EraserSize) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, lastImg, ip);
                using (var b = new SolidBrush(Color.Transparent)) g.FillEllipse(b, ip.X - EraserSize / 2f, ip.Y - EraserSize / 2f, EraserSize, EraserSize);
            }
            lastImg = ip;
            alphaCheckedFor = null;
            below = null; viewDirty = true; Invalidate();
        }

        // ------------------------------------------------------------------ inline text editing

        public void BeginEdit(Ann a, bool isNew)
        {
            CommitEdit();
            if (!isNew) Doc.Push();
            editAnn = a; editIsNew = isNew; editText0 = a.Text;
            a.Editing = true;
            editBox = new TextBox
            {
                Multiline = true, BorderStyle = BorderStyle.None, AcceptsReturn = true, AcceptsTab = false,
                ScrollBars = ScrollBars.None, WordWrap = !((a.Kind == AnnKind.Text || a.Kind == AnnKind.Callout) && a.AutoSize), Text = a.Text
            };
            editBox.TextAlign = a.Align == 1 || a.Kind == AnnKind.Callout ? HorizontalAlignment.Center : (a.Align == 2 ? HorizontalAlignment.Right : HorizontalAlignment.Left);
            editBox.TextChanged += EditTextChanged;
            editBox.KeyDown += EditKeyDown;
            editBox.LostFocus += delegate { CommitEdit(); };
            Controls.Add(editBox);
            LayoutEditBox();
            editBox.Focus();
            editBox.SelectionStart = editBox.TextLength;
            viewDirty = true; Invalidate();
        }

        void EditTextChanged(object s, EventArgs e)
        {
            if (editAnn == null) return;
            editAnn.Text = editBox.Text;
            if (editAnn.Kind == AnnKind.Text && editAnn.AutoSize || editAnn.Kind == AnnKind.Callout && editAnn.AutoSize) editAnn.FitText();
            LayoutEditBox();
            viewDirty = true; Invalidate();
        }

        void EditKeyDown(object s, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape) { e.Handled = true; e.SuppressKeyPress = true; CommitEdit(); Focus(); }
            else if (e.KeyCode == Keys.Enter && e.Control) { e.Handled = true; e.SuppressKeyPress = true; CommitEdit(); Focus(); }
        }

        void LayoutEditBox()
        {
            if (editBox == null || editAnn == null) return;
            var a = editAnn;
            var r = a.Rect;
            float pad = a.Kind == AnnKind.Callout ? 8 : Ann.Pad;
            float padY = a.Kind == AnnKind.Callout ? 6 : Ann.Pad;
            var inner = new RectangleF(r.X + pad, r.Y + padY, Math.Max(10, r.Width - pad * 2), Math.Max(10, r.Height - padY * 2));
            var sr = RectToScreen(inner);
            editBox.Font = a.MakeFont(Zoom);
            editBox.ForeColor = a.TextColor.A == 0 ? Color.Black : Color.FromArgb(255, a.TextColor);
            Color bg = a.Fill.A > 0 ? Color.FromArgb(255, a.Fill) : (Luma(a.TextColor) > 150 ? Color.FromArgb(40, 40, 40) : Color.White);
            editBox.BackColor = bg;
            int extra = a.Kind == AnnKind.Text && a.AutoSize ? (int)(12 * Zoom) : 0;
            if (a.Kind == AnnKind.Callout)
            {
                // vertically centre the single text block inside the bubble
                int th = TextRenderer.MeasureText(editBox.Text.Length == 0 ? " " : editBox.Text, editBox.Font, new Size((int)sr.Width, 10000), TextFormatFlags.WordBreak).Height;
                th = Math.Min((int)sr.Height, th + 2);
                editBox.SetBounds((int)sr.X, (int)(sr.Y + (sr.Height - th) / 2), (int)sr.Width, th);
            }
            else editBox.SetBounds((int)sr.X, (int)sr.Y, (int)sr.Width + extra, (int)sr.Height);
        }

        static double Luma(Color c) { return 0.299 * c.R + 0.587 * c.G + 0.114 * c.B; }

        public void CommitEdit()
        {
            if (editBox == null) return;
            var box = editBox; var a = editAnn;
            editBox = null; editAnn = null;
            a.Text = box.Text;
            a.Editing = false;
            Controls.Remove(box);
            box.Dispose();
            if (a.Kind == AnnKind.Text && string.IsNullOrEmpty(a.Text))
            {
                Doc.Items.Remove(a);
                Doc.DropLastUndo();
                Selection.Remove(a);
            }
            else if (!editIsNew && a.Text == editText0) Doc.DropLastUndo();
            else if (a.Kind == AnnKind.Text || a.Kind == AnnKind.Callout) { if (a.AutoSize) a.FitText(); }
            below = null; viewDirty = true;
            Doc.Raise();
            if (SelectionChanged != null) SelectionChanged(this, EventArgs.Empty);
            Invalidate();
        }

        public bool IsEditing { get { return editBox != null; } }

        // ------------------------------------------------------------------ keyboard

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (Doc == null) return;
            if (e.KeyCode == Keys.Space) { spaceDown = true; UpdateCursor(mouseScreen); e.Handled = true; return; }
            switch (e.KeyCode)
            {
                case Keys.Delete: case Keys.Back: DeleteSelection(); e.Handled = true; break;
                case Keys.Escape:
                    if (tool == Tool.Crop) CancelCrop();
                    else if (Selection.Count > 0) SetSelection(new List<Ann>());
                    e.Handled = true; break;
                case Keys.Enter:
                    if (tool == Tool.Crop && hasCrop) CommitCrop();
                    else if (Selection.Count == 1 && (Selection[0].Kind == AnnKind.Text || Selection[0].Kind == AnnKind.Callout)) BeginEdit(Selection[0], false);
                    else if (Selection.Count == 1 && Selection[0].Kind == AnnKind.Step && StepEditRequested != null) StepEditRequested(Selection[0]);
                    e.Handled = true; break;
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down:
                    if (Selection.Count > 0)
                    {
                        float st = e.Shift ? 10 : 1;
                        float dx = e.KeyCode == Keys.Left ? -st : e.KeyCode == Keys.Right ? st : 0, dy = e.KeyCode == Keys.Up ? -st : e.KeyCode == Keys.Down ? st : 0;
                        Doc.Push();
                        bool single = Selection.Count == 1;
                        foreach (var a in Selection) a.Move(dx, dy);
                        Doc.Raise();
                        e.Handled = true;
                    }
                    break;
            }
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            base.OnKeyUp(e);
            if (e.KeyCode == Keys.Space) { spaceDown = false; UpdateCursor(mouseScreen); }
        }

        // ------------------------------------------------------------------ object commands

        public void DeleteSelection()
        {
            if (Selection.Count == 0) return;
            Doc.Push();
            foreach (var a in Selection) Doc.Items.Remove(a);
            Selection.Clear();
            Doc.Raise();
        }

        public void CopySelection()
        {
            if (Selection.Count == 0) return;
            clip = Selection.Select(a => a.Clone()).ToList();
        }

        public bool HasObjectClipboard { get { return clip.Count > 0; } }

        public void PasteObjects()
        {
            if (clip.Count == 0 || Doc == null) return;
            Doc.Push();
            var added = new List<Ann>();
            foreach (var a in clip) { var c = a.Clone(); c.Move(14, 14); Doc.Items.Add(c); added.Add(c); }
            clip = clip.Select(a => { var c = a.Clone(); c.Move(14, 14); return c; }).ToList();
            Doc.Raise();
            SetSelection(added);
        }

        public void CutSelection() { CopySelection(); DeleteSelection(); }

        public void DuplicateSelection()
        {
            if (Selection.Count == 0) return;
            Doc.Push();
            var added = new List<Ann>();
            foreach (var a in Selection) { var c = a.Clone(); c.Move(14, 14); Doc.Items.Add(c); added.Add(c); }
            Doc.Raise();
            SetSelection(added);
        }

        public void ChangeOrder(int mode)   // 0 front, 1 back, 2 forward, 3 backward
        {
            if (Selection.Count == 0) return;
            Doc.Push();
            var items = Doc.Items;
            var sel = items.Where(a => Selection.Contains(a)).ToList();
            if (mode == 0) { foreach (var a in sel) items.Remove(a); items.AddRange(sel); }
            else if (mode == 1) { foreach (var a in sel) items.Remove(a); items.InsertRange(0, sel); }
            else if (mode == 2)
                for (int i = items.Count - 2; i >= 0; i--) { if (sel.Contains(items[i]) && !sel.Contains(items[i + 1])) { var t = items[i]; items[i] = items[i + 1]; items[i + 1] = t; } }
            else
                for (int i = 1; i < items.Count; i++) { if (sel.Contains(items[i]) && !sel.Contains(items[i - 1])) { var t = items[i]; items[i] = items[i - 1]; items[i - 1] = t; } }
            Doc.Raise();
        }

        /// <summary>Places a picture (from clipboard or a stamp file) as a movable object in the middle of the view.</summary>
        public void PlaceImage(Bitmap bmp)
        {
            if (Doc == null || bmp == null) return;
            CurrentTool = Tool.Select;
            float w = bmp.Width, h = bmp.Height;
            float maxW = Doc.Width * 0.9f, maxH = Doc.Height * 0.9f;
            float k = Math.Min(1f, Math.Min(maxW / w, maxH / h));
            w *= k; h *= k;
            var c = ToImage(new Point(ClientSize.Width / 2, ClientSize.Height / 2));
            c.X = Math.Max(w / 2, Math.Min(Doc.Width - w / 2, c.X)); c.Y = Math.Max(h / 2, Math.Min(Doc.Height - h / 2, c.Y));
            var a = Ann.Create(AnnKind.Image);
            a.Img = Effects.ToArgb(bmp);
            a.Rect = new RectangleF(c.X - w / 2, c.Y - h / 2, w, h);
            Doc.Push();
            Doc.Items.Add(a);
            Doc.Raise();
            SetSelection(new List<Ann> { a });
        }

        /// <summary>Change a property on the selected objects (with undo) and on the current tool's defaults.</summary>
        public void ApplyProp(Action<Ann> set)
        {
            if (Doc == null) return;
            if (Selection.Count > 0)
            {
                Doc.Push();
                foreach (var a in Selection) { set(a); if (a.HasText) a.FitText(); }
                Doc.Raise();
            }
            Ann d;
            if (Defaults.TryGetValue(tool, out d)) set(d);
            if (Selection.Count == 1)
            {
                // keep defaults of the selected object's own tool in sync too
                foreach (var kv in Defaults)
                    if (kv.Key != tool && KindOf(kv.Key) == Selection[0].Kind && (kv.Key == Tool.Highlighter) == Selection[0].Highlighter && tool == Tool.Select) set(kv.Value);
            }
            Invalidate();
        }

        // ------------------------------------------------------------------ output

        public Bitmap RenderFlat() { return Doc.Render(); }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { ToolDefaultsStore.Changed -= OnDefaultsChanged; if (view != null) view.Dispose(); if (below != null) below.Dispose(); }
            base.Dispose(disposing);
        }
    }
}
