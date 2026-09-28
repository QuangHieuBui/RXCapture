using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml.Linq;

namespace RXCapture
{
    /// <summary>The image being edited: an immutable base bitmap plus a list of annotation objects, with undo/redo.</summary>
    public class Document
    {
        public Bitmap Base;
        public List<Ann> Items = new List<Ann>();
        public string ProjectPath;      // .scp file in the library
        public string ExportPath;       // last "Save As" target
        public bool Dirty;
        public DateTime Created = DateTime.Now;

        class State { public Bitmap Base; public List<Ann> Items; }
        readonly List<State> undo = new List<State>();
        readonly List<State> redo = new List<State>();
        const int MaxUndo = 40;

        public event EventHandler Changed;

        public Document(Bitmap b)
        {
            Base = Effects.ToArgb(b);
        }

        public void Raise() { Dirty = true; if (Changed != null) Changed(this, EventArgs.Empty); }

        // ------------------------------------------------------------ undo

        State Snapshot()
        {
            return new State { Base = Base, Items = Items.Select(a => a.Clone()).ToList() };
        }

        /// <summary>Call BEFORE modifying the document.</summary>
        public void Push()
        {
            undo.Add(Snapshot());
            if (undo.Count > MaxUndo) undo.RemoveAt(0);
            redo.Clear();
        }

        public bool CanUndo { get { return undo.Count > 0; } }
        public bool CanRedo { get { return redo.Count > 0; } }

        public void Undo()
        {
            if (undo.Count == 0) return;
            redo.Add(Snapshot());
            var s = undo[undo.Count - 1]; undo.RemoveAt(undo.Count - 1);
            Base = s.Base; Items = s.Items;
            Raise();
        }

        public void Redo()
        {
            if (redo.Count == 0) return;
            undo.Add(Snapshot());
            var s = redo[redo.Count - 1]; redo.RemoveAt(redo.Count - 1);
            Base = s.Base; Items = s.Items;
            Raise();
        }

        public int Width { get { return Base.Width; } }
        public int Height { get { return Base.Height; } }

        /// <summary>When above 0 the next step created gets this number (set by "Restart sequence"); it is used once.</summary>
        public int StepNext;

        /// <summary>Number for a new step: the last step's number + 1, or the restart value if a restart is pending.</summary>
        public int NextStepNumber()
        {
            if (StepNext > 0) { int n = StepNext; StepNext = 0; return n; }
            Ann last = null;
            foreach (var a in Items) if (a.Kind == AnnKind.Step) last = a;
            return last == null ? 1 : last.Number + 1;
        }

        /// <summary>Sets the value of a step; with following = true every later step (in creation / stacking order) continues counting from it.</summary>
        public void RenumberFrom(Ann step, int value, bool following)
        {
            int idx = Items.IndexOf(step);
            if (idx < 0) return;
            Push();
            Items[idx].Number = value;
            if (following)
            {
                int n = value;
                for (int i = idx + 1; i < Items.Count; i++)
                    if (Items[i].Kind == AnnKind.Step) Items[i].Number = ++n;
            }
            Raise();
        }

        /// <summary>Restarts the sequence at this step: it becomes 1 and the steps after it 2, 3 ...</summary>
        public void RestartSequenceAt(Ann step) { RenumberFrom(step, 1, true); }

        // ------------------------------------------------------------ rendering

        /// <summary>Renders the base plus objects[0..count) into a new bitmap.</summary>
        public Bitmap Render(int count = -1, IList<Ann> exclude = null)
        {
            var bmp = new Bitmap(Base.Width, Base.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImageUnscaled(Base, 0, 0);
                g.CompositingMode = CompositingMode.SourceOver;
            }
            DrawItems(bmp, 0, count, exclude);
            return bmp;
        }

        /// <summary>Draws objects [from..to) onto an existing bitmap.</summary>
        public void DrawItems(Bitmap target, int from, int to, IList<Ann> exclude = null)
        {
            if (to < 0 || to > Items.Count) to = Items.Count;
            using (var g = Graphics.FromImage(target))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = from; i < to; i++)
                {
                    var a = Items[i];
                    if (exclude != null && exclude.Contains(a)) continue;
                    try { a.Draw(g, target); }
                    catch { /* a degenerate object must never break rendering of the rest */ }
                }
            }
        }

        // ------------------------------------------------------------ destructive operations (all push undo)

        public void ReplaceBase(Bitmap b, Point shiftItems)
        {
            Push();
            Base = Effects.ToArgb(b);
            if (shiftItems != Point.Empty) foreach (var a in Items) a.Move(shiftItems.X, shiftItems.Y);
            Raise();
        }

        public void Flatten()
        {
            if (Items.Count == 0) return;
            Push();
            var r = Render();
            Base = r; Items = new List<Ann>();
            Raise();
        }

        public void Crop(Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, Base.Width, Base.Height));
            if (r.Width < 1 || r.Height < 1) return;
            Push();
            Base = ScreenGrabber.Crop(Base, r);
            foreach (var a in Items) a.Move(-r.X, -r.Y);
            Raise();
        }

        /// <summary>Drag-resize the canvas edges (positive = grow, negative = shrink).</summary>
        public void ResizeEdges(int left, int top, int right, int bottom, Color fill)
        {
            int nw = Base.Width + left + right, nh = Base.Height + top + bottom;
            if (nw < 1 || nh < 1) return;
            Push();
            var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.Clear(fill);
                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImageUnscaled(Base, left, top);
            }
            Base = dst;
            foreach (var a in Items) a.Move(left, top);
            Raise();
        }

        public void ResizeImage(int w, int h)
        {
            if (w < 1 || h < 1) return;
            Push();
            float sx = (float)w / Base.Width, sy = (float)h / Base.Height;
            Base = Effects.Resize(Base, w, h);
            foreach (var a in Items) a.ScaleAll(sx, sy);
            Raise();
        }

        public void CanvasResize(int w, int h, int anchor, Color fill)
        {
            Point off;
            var nb = Effects.CanvasResize(Base, w, h, anchor, fill, out off);
            ReplaceBase(nb, off);
        }

        public void RotateFlip(RotateFlipType t)
        {
            Push();
            var r = Items.Count > 0 ? Render() : Base;
            Base = Effects.Rotate(r, t);
            Items = new List<Ann>();
            Raise();
        }

        public void Trim(int tol)
        {
            var flat = Items.Count > 0 ? Render() : Base;
            var r = Effects.TrimBounds(flat, tol);
            if (r.Width == flat.Width && r.Height == flat.Height) return;
            Push();
            Base = ScreenGrabber.Crop(flat, r);
            Items = Items.Count > 0 ? new List<Ann>() : Items;
            Raise();
        }

        public void CutBand(bool vertical, int from, int to)
        {
            Push();
            var flat = Items.Count > 0 ? Render() : Base;
            Items = new List<Ann>();
            Base = Effects.CutBand(flat, vertical, from, to);
            Raise();
        }

        /// <summary>Apply a filter to the base image only (annotations stay untouched).</summary>
        public void ApplyToBase(Func<Bitmap, Bitmap> f)
        {
            Push();
            Base = Effects.ToArgb(f(Base));
            Raise();
        }

        /// <summary>Apply a filter to the flattened image (base + annotations).</summary>
        public void ApplyToFlattened(Func<Bitmap, Bitmap> f)
        {
            Push();
            var flat = Items.Count > 0 ? Render() : Base;
            Base = Effects.ToArgb(f(flat));
            Items = new List<Ann>();
            Raise();
        }

        public void DropLastUndo() { if (undo.Count > 0) undo.RemoveAt(undo.Count - 1); }

        /// <summary>Filters that grow the canvas (border, shadow): f receives the base and reports the offset of the old image.</summary>
        public void ShadowOrFrame(Func<Bitmap, Point[], Bitmap> f, bool flatten)
        {
            Push();
            var off = new Point[1];
            var src = flatten && Items.Count > 0 ? Render() : Base;
            var nb = f(src, off);
            Base = Effects.ToArgb(nb);
            if (flatten) Items = new List<Ann>();
            else foreach (var a in Items) a.Move(off[0].X, off[0].Y);
            Raise();
        }

        // ------------------------------------------------------------ project file (.scp = zip)

        public void SaveProject(string path)
        {
            string tmp = path + ".tmp";
            using (var fs = File.Create(tmp))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                var e = zip.CreateEntry("base.png", CompressionLevel.Fastest);
                using (var s = e.Open()) Base.Save(s, ImageFormat.Png);
                var x = new XElement("doc", new XAttribute("created", Created.ToString("o")), Items.Select(a => a.ToXml()));
                var e2 = zip.CreateEntry("annotations.xml");
                using (var s = e2.Open())
                {
                    var bytes = Encoding.UTF8.GetBytes(x.ToString());
                    s.Write(bytes, 0, bytes.Length);
                }
            }
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            ProjectPath = path;
            Dirty = false;
        }

        public static Document LoadProject(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
            {
                Bitmap b;
                using (var s = zip.GetEntry("base.png").Open())
                using (var ms = new MemoryStream())
                {
                    s.CopyTo(ms); ms.Position = 0;
                    using (var im = Image.FromStream(ms)) b = Effects.ToArgb(im);
                }
                var doc = new Document(b);
                var ae = zip.GetEntry("annotations.xml");
                if (ae != null)
                    using (var s = ae.Open())
                    {
                        var x = XElement.Load(s);
                        foreach (var e in x.Elements("a")) doc.Items.Add(Ann.FromXml(e));
                        var c = (string)x.Attribute("created");
                        DateTime dt; if (c != null && DateTime.TryParse(c, null, System.Globalization.DateTimeStyles.RoundtripKind, out dt)) doc.Created = dt;
                    }
                doc.ProjectPath = path;
                doc.Dirty = false;
                return doc;
            }
        }
    }
}
