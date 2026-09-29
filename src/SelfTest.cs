using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>Automated checks of the non-interactive parts (run:  RXCapture.exe --selftest [logfile]).</summary>
    static class SelfTest
    {
        static StringBuilder log = new StringBuilder();
        static int failed;

        static void Check(string name, Func<string> test)
        {
            try
            {
                string r = test();
                log.AppendLine((r == null ? "PASS " : "FAIL ") + name + (r != null ? " -> " + r : ""));
                if (r != null) failed++;
            }
            catch (Exception ex) { failed++; log.AppendLine("FAIL " + name + " -> EXCEPTION " + ex.GetType().Name + ": " + ex.Message + "\n" + ex.StackTrace); }
        }

        public static Bitmap Sample(int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                using (var br = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.FromArgb(255, 240, 246, 255), Color.FromArgb(255, 176, 208, 245), 60f)) g.FillRectangle(br, 0, 0, w, h);
                using (var f = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel))
                    for (int i = 0; i < 6; i++)
                    {
                        g.FillRectangle(Brushes.White, 30, 30 + i * 60, w - 60, 44);
                        g.DrawRectangle(Pens.LightGray, 30, 30 + i * 60, w - 60, 44);
                        g.DrawString("Row " + (i + 1) + "  —  sample text for the editor test", f, Brushes.DimGray, 42, 40 + i * 60);
                    }
            }
            return b;
        }

        public static int Run(string logPath)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "rxcapture_selftest");
            Directory.CreateDirectory(tmp);

            Check("screen grab is opaque and sized", delegate
            {
                using (var b = ScreenGrabber.Grab(new Rectangle(0, 0, 320, 200)))
                {
                    if (b.Width != 320 || b.Height != 200) return "size " + b.Width + "x" + b.Height;
                    var c = b.GetPixel(10, 10);
                    return c.A == 255 ? null : "alpha " + c.A;
                }
            });

            Check("virtual screen reported", delegate { var v = ScreenGrabber.VirtualScreen; log.AppendLine("  virtual screen = " + v + "  primary = " + Screen.PrimaryScreen.Bounds); return v.Width > 0 ? null : "empty"; });

            Check("effects (blur/pixelate/adjust/border/shadow/edge/watermark/fill)", delegate
            {
                using (var s = Sample(300, 200))
                {
                    using (var r = Effects.Pixelate(s, 8)) if (r.Size != s.Size) return "pixelate size";
                    using (var r = Effects.BoxBlur(s, 4)) if (r.Size != s.Size) return "blur size";
                    using (var r = Effects.Adjust(s, 20, 10, -30, 25, 1.2f)) if (r.Size != s.Size) return "adjust size";
                    using (var r = Effects.Border(s, 5, Color.Black)) if (r.Width != 310 || r.Height != 210) return "border size";
                    Point off; using (var r = Effects.DropShadow(s, 6, 6, 8, Color.Black, 60, out off)) if (r.Width <= s.Width || off.X <= 0) return "shadow size";
                    using (var r = Effects.EdgeEffect(s, true, true, true, true, 8, 14, 2)) if (r.GetPixel(0, 0).A != 0 && r.GetPixel(150, 100).A != 255) return "edge alpha";
                    using (var r = Effects.WatermarkText(s, "TEST", "Segoe UI", 30, Color.White, 60, 8, 10, true)) if (r.Size != s.Size) return "watermark";
                    using (var r = Effects.FloodFill(s, 5, 5, Color.Red, 30)) { var c = r.GetPixel(5, 5); if (c.R < 200 || c.G > 60) return "flood fill color " + c; }
                    using (var r = Effects.CutBand(s, true, 100, 150)) if (r.Width != 250) return "cut band";
                    using (var r = Effects.Rotate(s, RotateFlipType.Rotate90FlipNone)) if (r.Width != 200 || r.Height != 300) return "rotate";
                    using (var r = Effects.Reflection(s, 40, 4)) if (r.Height <= s.Height) return "reflection";
                }
                return null;
            });

            Check("document: all annotation kinds render, undo/redo, project round trip", delegate
            {
                using (var s = Sample(600, 400))
                {
                    var doc = new Document(s);
                    foreach (AnnKind k in Enum.GetValues(typeof(AnnKind)))
                    {
                        var a = Ann.Create(k);
                        a.P1 = new PointF(60 + (int)k * 30, 60 + (int)k * 12); a.P2 = new PointF(a.P1.X + 150, a.P1.Y + 70); a.Tail = new PointF(a.P1.X + 20, a.P2.Y + 40);
                        a.Text = "Hello"; if (k == AnnKind.Pen) { a.Pts.Add(a.P1); a.Pts.Add(a.P2); a.Pts.Add(new PointF(a.P2.X, a.P1.Y)); }
                        if (k == AnnKind.Image) a.Img = Sample(40, 40);
                        doc.Push(); doc.Items.Add(a);
                    }
                    using (var flat = doc.Render()) if (flat.Size != s.Size) return "render size";
                    int n = doc.Items.Count;
                    doc.Undo(); if (doc.Items.Count != n - 1) return "undo";
                    doc.Redo(); if (doc.Items.Count != n) return "redo";
                    string p = Path.Combine(tmp, "t.scp");
                    doc.SaveProject(p);
                    var back = Document.LoadProject(p);
                    if (back.Items.Count != n) return "project items " + back.Items.Count + " vs " + n;
                    if (back.Width != 600) return "project size";
                    doc.Crop(new Rectangle(10, 10, 300, 200)); if (doc.Width != 300) return "crop";
                    doc.ResizeImage(150, 100); if (doc.Width != 150) return "resize";
                    doc.CanvasResize(200, 200, 4, Color.Transparent); if (doc.Width != 200) return "canvas";
                    doc.RotateFlip(RotateFlipType.Rotate90FlipNone); if (doc.Items.Count != 0) return "rotate flatten";
                    doc.Undo();
                }
                return null;
            });

            Check("export formats (png jpg bmp gif tif pdf)", delegate
            {
                using (var s = Sample(320, 200))
                    foreach (var ext in new[] { "png", "jpg", "bmp", "gif", "tif", "pdf" })
                    {
                        string p = Path.Combine(tmp, "out." + ext);
                        if (File.Exists(p)) File.Delete(p);
                        Exporter.Save(s, p);
                        var fi = new FileInfo(p);
                        if (!fi.Exists || fi.Length < 100) return ext + " missing/empty";
                        if (ext == "pdf")
                        {
                            var head = Encoding.ASCII.GetString(File.ReadAllBytes(p), 0, 8);
                            if (!head.StartsWith("%PDF-1.4")) return "pdf header " + head;
                        }
                        else using (var im = Image.FromFile(p)) if (im.Width != 320) return ext + " width " + im.Width;
                    }
                return null;
            });

            Check("scrolling capture stitching", delegate
            {
                // long page: random coloured rows; 'view' window slides by fixed steps
                var rnd = new Random(3);
                int W = 200, H = 120, total = 700;
                var page = new Bitmap(W, total, PixelFormat.Format32bppArgb);
                for (int y = 0; y < total; y++) { var c = Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)); for (int x = 0; x < W; x++) page.SetPixel(x, y, (x + y) % 7 == 0 ? Color.White : c); }
                var strips = new List<Bitmap>();
                Bitmap prev = ScreenGrabber.Crop(page, new Rectangle(0, 0, W, H)); strips.Add(new Bitmap(prev));
                var ph = ScrollCapture.Signature(prev, 0, 12);
                int off = 0;
                foreach (int step in new[] { 50, 80, 45, 60 })
                {
                    off += step;
                    var cur = ScreenGrabber.Crop(page, new Rectangle(0, off, W, H));
                    var ch = ScrollCapture.Signature(cur, 0, 12);
                    int sh = ScrollCapture.FindShift(ph, ch);
                    if (sh != step) return "expected shift " + step + " got " + sh;
                    strips.Add(ScreenGrabber.Crop(cur, new Rectangle(0, H - sh, W, sh)));
                    ph = ch;
                }
                var stitched = ScrollCapture.Stitch(strips, W);
                if (stitched.Height != H + 50 + 80 + 45 + 60) return "stitched height " + stitched.Height;
                // compare with original region
                for (int y = 0; y < stitched.Height; y += 17) if (stitched.GetPixel(5, y) != page.GetPixel(5, y)) return "pixel mismatch at row " + y;
                var same = ScrollCapture.FindShift(ph, ph); if (same != 0) return "identical frames should give 0, got " + same;
                return null;
            });

            Check("scrolling capture: side panel that changes while scrolling and a fixed header", delegate
            {
                // page whose right part (a minimap / table of contents) redraws differently on every frame, plus a sticky header
                var rnd = new Random(11);
                int W = 300, H = 160, total = 900, side = 90, header = 14;
                var page = new Bitmap(W, total, PixelFormat.Format32bppArgb);
                for (int y = 0; y < total; y++) { var c = Color.FromArgb(rnd.Next(256), rnd.Next(256), rnd.Next(256)); for (int x = 0; x < W; x++) page.SetPixel(x, y, (x * 3 + y) % 11 == 0 ? Color.White : c); }
                Func<int, Bitmap> frame = off =>
                {
                    var f = ScreenGrabber.Crop(page, new Rectangle(0, off, W, H));
                    var r2 = new Random(off * 7 + 1);
                    using (var g = Graphics.FromImage(f))
                    {
                        for (int y = 0; y < H; y += 4) using (var b = new SolidBrush(Color.FromArgb(r2.Next(256), r2.Next(256), r2.Next(256)))) g.FillRectangle(b, W - side, y, side, 4);   // changes with the scroll offset
                        g.FillRectangle(Brushes.DarkSlateGray, 0, 0, W - side, header);                                                                                            // sticky header
                    }
                    return f;
                };
                var prev = frame(0); var ps = ScrollCapture.Signature(prev, 0, 12);
                int off2 = 0;
                foreach (int step in new[] { 60, 95, 40, 70, 110 })
                {
                    off2 += step;
                    var cur = frame(off2); var cs = ScrollCapture.Signature(cur, 0, 12);
                    int sh = ScrollCapture.FindShift(ps, cs);
                    if (sh != step) return "expected shift " + step + " got " + sh + " with a changing side panel";
                    prev.Dispose(); prev = cur; ps = cs;
                }
                // end of the page: the content stays put but the side panel still redraws
                var end1 = frame(off2); var end2 = ScreenGrabber.Crop(page, new Rectangle(0, off2, W, H));
                using (var g = Graphics.FromImage(end2)) { g.FillRectangle(Brushes.Orange, W - side, 0, side, H); g.FillRectangle(Brushes.DarkSlateGray, 0, 0, W - side, header); }
                int atEnd = ScrollCapture.FindShift(ScrollCapture.Signature(end1, 0, 12), ScrollCapture.Signature(end2, 0, 12));
                if (atEnd != 0) return "unmoved page with a redrawn side panel should give 0, got " + atEnd;
                return null;
            });

            Check("recorder bar stays inside the working area (full screen, taskbar)", delegate
            {
                var size = new Size(624, 62);
                var work = new Rectangle(0, 0, 1920, 1032);   // 48 px taskbar at the bottom
                var full = RecorderForm.BarLocation(new Rectangle(0, 0, 1920, 1080), size, work);
                if (!work.Contains(new Rectangle(full, size))) return "full screen: bar at " + full + " leaves the working area";
                var small = RecorderForm.BarLocation(new Rectangle(400, 200, 640, 360), size, work);
                if (small.Y != 572 || !work.Contains(new Rectangle(small, size))) return "region: bar at " + small + ", expected y=572 below the region";
                var low = RecorderForm.BarLocation(new Rectangle(400, 700, 640, 320), size, work);
                if (low.Y + size.Height > 700 - 12 + 1 || low.Y < 0) return "low region: bar at " + low + ", expected above the region";
                return null;
            });

            Check("MP4 reader: exact duration, every frame, seek, upright picture", delegate
            {
                string avi = Path.Combine(tmp, "r.avi"), mp4 = Path.Combine(tmp, "r.mp4");
                byte[] jpg;
                using (var b = new Bitmap(320, 180))
                {
                    using (var g = Graphics.FromImage(b)) { g.FillRectangle(Brushes.Red, 0, 0, 320, 90); g.FillRectangle(Brushes.Blue, 0, 90, 320, 90); }
                    using (var ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg); jpg = ms.ToArray(); }
                }
                using (var w = new AviWriter(avi, 320, 180, 15)) for (int i = 0; i < 45; i++) w.AddFrame(jpg);
                if (!Mp4Writer.Convert(avi, mp4, 15)) return "MP4 encoder unavailable on this Windows";
                using (var rd = new Mp4Writer.Reader(mp4))
                using (var bmp = new Bitmap(rd.Width, rd.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                {
                    if (Math.Abs(rd.Duration.TotalSeconds - 3.0) > 0.05) return "duration " + rd.Duration.TotalSeconds + "s, expected 3.0";
                    TimeSpan ts; int n = 0;
                    if (!rd.ReadFrame(bmp, out ts)) return "no first frame";
                    Color top = bmp.GetPixel(160, 10), bottom = bmp.GetPixel(160, 170);
                    if (top.R < 200 || top.B > 60) return "top is not red: " + top;
                    if (bottom.B < 200 || bottom.R > 60) return "bottom is not blue: " + bottom;
                    n = 1; while (rd.ReadFrame(bmp, out ts)) n++;
                    if (n != 45) return "decoded " + n + " frames, expected 45";
                    rd.Seek(TimeSpan.FromSeconds(2.0));
                    if (!rd.ReadFrame(bmp, out ts)) return "no frame after seek";
                    if (ts > TimeSpan.FromSeconds(2.0) + TimeSpan.FromMilliseconds(70)) return "seek to 2.0s landed at " + ts.TotalSeconds;
                }
                return null;
            });

            Check("MP4 reader/trim: widths that are not a multiple of 16 are not skewed", delegate
            {
                // regions such as 1508 x 876 are padded row by row by the decoder; a vertical white bar must stay vertical
                foreach (int w in new[] { 322, 378, 1508 })
                {
                    int h = 182;
                    string avi = Path.Combine(tmp, "w" + w + ".avi"), mp4 = Path.Combine(tmp, "w" + w + ".mp4"), cut = Path.Combine(tmp, "w" + w + "-cut.mp4");
                    byte[] jpg;
                    using (var b = new Bitmap(w, h))
                    {
                        using (var g = Graphics.FromImage(b)) { g.Clear(Color.FromArgb(20, 60, 120)); g.FillRectangle(Brushes.White, 100, 0, 12, h); g.FillRectangle(Brushes.Orange, w - 30, 0, 30, h); }
                        using (var ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg); jpg = ms.ToArray(); }
                    }
                    using (var wr = new AviWriter(avi, w, h, 15)) for (int i = 0; i < 20; i++) wr.AddFrame(jpg);
                    if (!Mp4Writer.Convert(avi, mp4, 15)) return "MP4 encoder unavailable on this Windows";
                    Func<Bitmap, string> straight = bmp =>
                    {
                        foreach (int y in new[] { 4, h / 2, h - 5 })
                        {
                            Color bar = bmp.GetPixel(106, y), left = bmp.GetPixel(40, y), edge = bmp.GetPixel(w - 12, y);
                            if (bar.R < 200 || bar.G < 200 || bar.B < 200) return "row " + y + ": the white bar is not at x=106 (" + bar + ") - frame is skewed";
                            if (left.R > 90 || left.B < 80) return "row " + y + ": background wrong at x=40 (" + left + ")";
                            if (edge.R < 200 || edge.B > 90) return "row " + y + ": orange edge not at the right border (" + edge + ")";
                        }
                        return null;
                    };
                    using (var rd = new Mp4Writer.Reader(mp4))
                    using (var bmp = new Bitmap(rd.Width, rd.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                    {
                        TimeSpan ts;
                        if (rd.Width != w || rd.Height != h) return "decoded size " + rd.Width + "x" + rd.Height + ", expected " + w + "x" + h;
                        if (!rd.ReadFrame(bmp, out ts)) return "width " + w + ": no frame";
                        string e = straight(bmp); if (e != null) return "width " + w + " (player): " + e;
                    }
                    Bitmap first; TimeSpan kept;
                    if (!Mp4Writer.Trim(mp4, cut, TimeSpan.FromSeconds(0.2), TimeSpan.FromSeconds(1.0), out first, out kept)) return "width " + w + ": trim failed";
                    using (first) { string e = straight(first); if (e != null) return "width " + w + " (trim first frame): " + e; }
                    using (var rd = new Mp4Writer.Reader(cut))         // and the trimmed file itself must decode straight too
                    using (var bmp = new Bitmap(rd.Width, rd.Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb))
                    {
                        TimeSpan ts;
                        if (!rd.ReadFrame(bmp, out ts)) return "width " + w + ": trimmed file has no frame";
                        string e = straight(bmp); if (e != null) return "width " + w + " (trimmed file): " + e;
                    }
                }
                return null;
            });

            Check("MP4 convert + trim keep the picture upright", delegate
            {
                // top half red, bottom half blue: a flipped or channel-swapped encoder would be caught here
                string avi = Path.Combine(tmp, "m.avi"), mp4 = Path.Combine(tmp, "m.mp4"), cut = Path.Combine(tmp, "m-cut.mp4");
                byte[] jpg;
                using (var b = new Bitmap(320, 180))
                {
                    using (var g = Graphics.FromImage(b)) { g.FillRectangle(Brushes.Red, 0, 0, 320, 90); g.FillRectangle(Brushes.Blue, 0, 90, 320, 90); }
                    using (var ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg); jpg = ms.ToArray(); }
                }
                using (var w = new AviWriter(avi, 320, 180, 15)) for (int i = 0; i < 30; i++) w.AddFrame(jpg);
                if (!Mp4Writer.Convert(avi, mp4, 15)) return "MP4 encoder unavailable on this Windows";
                Bitmap first; TimeSpan kept;
                if (!Mp4Writer.Trim(mp4, cut, TimeSpan.FromSeconds(0.5), TimeSpan.FromSeconds(1.5), out first, out kept)) return "trim failed";
                using (first)
                {
                    Color top = first.GetPixel(160, 10), bottom = first.GetPixel(160, 170);
                    if (top.R < 200 || top.B > 60) return "first frame top is not red: " + top;
                    if (bottom.B < 200 || bottom.R > 60) return "first frame bottom is not blue: " + bottom;
                }
                if (Math.Abs(kept.TotalSeconds - 1.0) > 0.15) return "kept " + kept.TotalSeconds + "s, expected 1s";
                return null;
            });

            Check("AVI writer/reader + GIF encoder", delegate
            {
                string avi = Path.Combine(tmp, "t.avi"), gif = Path.Combine(tmp, "t.gif");
                var codec = VideoRecorder.FindJpeg();
                using (var ep = new EncoderParameters(1))
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 80L);
                    using (var w = new AviWriter(avi, 160, 120, 10))
                        for (int i = 0; i < 12; i++)
                            using (var b = new Bitmap(160, 120))
                            using (var g = Graphics.FromImage(b))
                            using (var ms = new MemoryStream())
                            {
                                g.Clear(Color.FromArgb(255, 20 * i, 200 - 10 * i, 90));
                                g.FillEllipse(Brushes.White, 10 + i * 10, 40, 30, 30);
                                b.Save(ms, codec, ep);
                                w.AddFrame(ms.ToArray());
                            }
                }
                int count = 0; foreach (var f in AviReader.Frames(avi)) { using (var ms = new MemoryStream(f)) using (var im = Image.FromStream(ms)) if (im.Width != 160) return "frame width"; count++; }
                if (count != 12) return "avi frames " + count;
                VideoRecorder.AviToGif(avi, gif, 160, 10);
                using (var im = Image.FromFile(gif))
                {
                    int frames = im.GetFrameCount(FrameDimension.Time);
                    if (frames != 12) return "gif frames " + frames;
                }
                return null;
            });

            Check("hotkey parsing and registration", delegate
            {
                uint m, v;
                if (!HotkeyParser.TryParse("PrintScreen", out m, out v) || v != 0x2C || m != 0) return "PrintScreen parse " + m + "/" + v;
                if (!HotkeyParser.TryParse("Ctrl+Shift+F", out m, out v) || m != 6 || v != 0x46) return "Ctrl+Shift+F parse " + m + "/" + v;
                var hw = new HotkeyWindow();
                var failed2 = hw.Register();
                hw.Unregister();
                log.AppendLine("  hotkeys that failed to register: [" + string.Join(", ", failed2.ToArray()) + "]");
                return null;
            });

            Check("all icons draw", delegate
            {
                string[] names = { "select", "arrow", "line", "shape", "callout", "text", "step", "stamp", "pen", "highlighter", "fill", "blur", "magnify", "spotlight", "eraser", "crop", "cutout", "camera", "video", "region", "window", "fullscreen", "scroll", "freehand", "fixed", "repeat", "new", "open", "save", "copy", "paste", "undo", "redo", "zoomin", "zoomout", "fit", "actual", "settings", "library", "print", "email", "folder", "rotate", "flip", "resize", "canvas", "border", "shadow", "torn", "adjust", "gray", "watermark", "trash", "share", "close", "help", "check", "delay", "pause", "stop", "record", "layers", "bold", "italic", "underline", "textcolor", "font", "alignl", "alignc", "alignr", "tray" };
                foreach (var n in names) using (var b = new Bitmap(Icons.Get(n, 24, false))) { }
                for (int i = 0; i < Icons.StampNames.Length; i++) using (var b = new Bitmap(32, 32)) using (var g = Graphics.FromImage(b)) Icons.DrawStamp(g, i, new RectangleF(0, 0, 32, 32));
                return null;
            });

            Check("library tray: tick several items and delete them together", delegate
            {
                var made = new List<LibItem>();
                try
                {
                    for (int k = 0; k < 3; k++) using (var s = Sample(120, 80)) { Document d; made.Add(LibraryStore.AddImage(s, out d)); }
                    using (var grid = new ThumbGrid { Size = new Size(600, 300) })
                    {
                        var h = grid.Handle;
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        var t = typeof(ThumbGrid);
                        var items = (List<LibItem>)t.GetField("items", flags).GetValue(grid);
                        int[] idx = made.ConvertAll(m => items.FindIndex(x => x.Id == m.Id)).ToArray();
                        if (Array.IndexOf(idx, -1) >= 0) return "new items not listed";
                        int a = idx[0], b = idx[1];                            // items are listed newest first; ties in the same second may reorder them
                        t.GetMethod("SetSelecting", flags).Invoke(grid, new object[] { true });
                        t.GetMethod("Tick", flags).Invoke(grid, new object[] { a });
                        t.GetMethod("TickRange", flags).Invoke(grid, new object[] { b });   // Shift+click: everything from the last ticked cell to b
                        var ticked = (List<LibItem>)t.GetMethod("TickedItems", flags).Invoke(grid, null);
                        if (!grid.Selecting) return "not in selection mode";
                        int lo = Math.Min(a, b), hi = Math.Max(a, b);
                        if (ticked.Count != hi - lo + 1) return "ticked " + ticked.Count + " cells, expected " + (hi - lo + 1);
                        var ours = made.FindAll(m => { int i = items.FindIndex(x => x.Id == m.Id); return i >= lo && i <= hi; });
                        int changes = 0; EventHandler ch = (x, y) => changes++;
                        LibraryStore.Changed += ch;
                        LibraryStore.DeleteMany(ticked);
                        LibraryStore.Changed -= ch;
                        var left = LibraryStore.List();
                        foreach (var m in made)
                        {
                            bool shouldBeGone = ours.Exists(o => o.Id == m.Id);
                            if (shouldBeGone == left.Exists(x => x.Id == m.Id)) return (shouldBeGone ? "ticked" : "unticked") + " item " + (shouldBeGone ? "still in the library" : "was deleted too");
                        }
                        if (changes != 1) return "expected one change notification, got " + changes;
                    }
                }
                finally { foreach (var m in made) LibraryStore.Delete(m); }
                return null;
            });

            Check("editor can save a video (Save / Save As are enabled, the file is copied)", delegate
            {
                string avi = Path.Combine(tmp, "sv.avi"), dest = Path.Combine(tmp, "saved copy.avi");
                byte[] jpg;
                using (var b = new Bitmap(160, 90)) using (var ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg); jpg = ms.ToArray(); }
                using (var w = new AviWriter(avi, 160, 90, 10)) for (int i = 0; i < 10; i++) w.AddFrame(jpg);
                LibItem it; using (var fb = new Bitmap(160, 90)) it = LibraryStore.AddVideo(avi, fb, 1);
                try
                {
                    using (var ed = new EditorForm())
                    {
                        var h = ed.Handle;
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        var can = typeof(EditorForm).GetProperty("CanSave", flags);
                        if ((bool)can.GetValue(ed, null)) return "Save enabled with nothing open";
                        ed.ShowVideo(it);
                        if (!(bool)can.GetValue(ed, null)) return "Save is still disabled while a video is shown";
                        EditorForm.CopyVideoTo(it, dest);
                        if (!File.Exists(dest) || new FileInfo(dest).Length != new FileInfo(it.File).Length) return "copied file differs from the video";
                    }
                }
                finally { LibraryStore.Delete(it); try { File.Delete(dest); } catch { } }
                return null;
            });

            Check("library tray: rubber-band drag ticks the cells it touches, click on empty space clears", delegate
            {
                var made = new List<LibItem>();
                try
                {
                    for (int k = 0; k < 6; k++) using (var s = Sample(120, 80)) { Document d; made.Add(LibraryStore.AddImage(s, out d)); }
                    using (var grid = new ThumbGrid { Size = new Size(600, 300) })
                    {
                        var h = grid.Handle;
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        var t = typeof(ThumbGrid);
                        Action<string, int, int> mouse = (name, x, y) =>
                            typeof(Control).GetMethod(name, flags).Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
                        float sc = Theme.Scale(grid);
                        int cw = (int)(grid.CellW * sc), ch = (int)(grid.CellH * sc);
                        // drag from the top-left corner across the first 2 columns x 2 rows of cells
                        int x1 = cw + cw / 2, y1 = ch + ch / 2;
                        mouse("OnMouseDown", 3, 3); mouse("OnMouseMove", 20, 20); mouse("OnMouseMove", x1, y1); mouse("OnMouseUp", x1, y1);
                        var items = (List<LibItem>)t.GetField("items", flags).GetValue(grid);
                        var ticked = (List<LibItem>)t.GetMethod("TickedItems", flags).Invoke(grid, null);
                        int cols = Math.Max(1, grid.ClientSize.Width / cw);
                        int expect = 0; for (int i = 0; i < items.Count; i++) if (i % cols < 2 && i / cols < 2) expect++;
                        if (!grid.Selecting) return "the drag did not start a selection";
                        if (ticked.Count != expect) return "rubber band ticked " + ticked.Count + " cells, expected " + expect;
                        // a plain click in selection mode selects only that cell (Explorer rules)
                        mouse("OnMouseDown", cw + cw / 2, ch / 2 + 20); mouse("OnMouseUp", cw + cw / 2, ch / 2 + 20);
                        ticked = (List<LibItem>)t.GetMethod("TickedItems", flags).Invoke(grid, null);
                        if (ticked.Count != 1 || ticked[0].Id != items[1].Id) return "plain click should leave just the clicked cell selected, got " + ticked.Count;
                        // a click on empty space below the last row leaves selection mode
                        int emptyY = ch * ((items.Count + cols - 1) / cols) + 10;
                        if (emptyY < grid.ClientSize.Height) { mouse("OnMouseDown", 5, emptyY); mouse("OnMouseUp", 5, emptyY); if (grid.Selecting) return "clicking empty space did not clear the selection"; }
                    }
                }
                finally { foreach (var m in made) LibraryStore.Delete(m); }
                return null;
            });

            Check("video format: old settings holding avi move to mp4 once, later choices stay", delegate
            {
                var old = new AppSettings { VideoFormat = "avi", SettingsVersion = 0 };
                if (!old.ApplyVideoFormatMigration() || old.VideoFormat != "mp4" || old.SettingsVersion != 2) return "avi from an old file was not moved to mp4";
                old.VideoFormat = "avi";                                     // the user picks AVI on purpose afterwards
                if (old.ApplyVideoFormatMigration() || old.VideoFormat != "avi") return "a later AVI choice was overwritten";
                var gif = new AppSettings { VideoFormat = "gif", SettingsVersion = 0 };
                gif.ApplyVideoFormatMigration();
                if (gif.VideoFormat != "gif") return "a gif choice was changed";
                if (new AppSettings().VideoFormat != "mp4") return "the default is not mp4";
                return null;
            });

            Check("start with Windows: on/off, moved exe is re-pointed, other copies do not take it over", delegate
            {
                bool before = Startup.IsEnabled;
                string oldCmd = null;
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) { if (k != null) oldCmd = k.GetValue("RXCapture") as string; }
                try
                {
                    if (Startup.TargetOf("\"C:\\Program Files\\A B\\x.exe\" --minimized") != "C:\\Program Files\\A B\\x.exe" || Startup.TargetOf("C:\\a\\b.exe --minimized") != "C:\\a\\b.exe") return "command parsing";
                    Startup.Set(true);
                    if (!Startup.IsEnabled) return "not enabled after Set(true)";
                    Startup.Refresh();
                    if (!Startup.IsEnabled) return "Refresh removed the entry";
                    Startup.Set(false);
                    if (Startup.IsEnabled) return "still enabled after Set(false)";
                    Startup.Refresh();
                    if (Startup.IsEnabled) return "Refresh created an entry that was off";
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        k.SetValue("RXCapture", "\"" + Path.Combine(Path.GetTempPath(), "gone-" + Guid.NewGuid().ToString("N") + ".exe") + "\" --minimized");
                        Startup.Refresh();
                        string v = (string)k.GetValue("RXCapture");
                        if (Startup.TargetOf(v) != Application.ExecutablePath) return "entry pointing to a missing exe was not re-pointed: " + v;
                        k.SetValue("RXCapture", "\"" + Environment.GetEnvironmentVariable("ComSpec") + "\" --minimized");
                        Startup.Refresh();
                        if (Startup.TargetOf((string)k.GetValue("RXCapture")) != Environment.GetEnvironmentVariable("ComSpec")) return "an entry to an existing exe was overwritten";
                    }
                    return null;
                }
                finally
                {
                    using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                    {
                        if (k != null) { if (before && oldCmd != null) k.SetValue("RXCapture", oldCmd); else k.DeleteValue("RXCapture", false); }
                    }
                }
            });

            Check("saved file becomes the item's main file (remembered, found again on open)", delegate
            {
                string file = Path.Combine(tmp, "main-file.png"), vfile = Path.Combine(tmp, "sv2.avi");
                LibItem it = null, vit = null;
                try
                {
                    using (var s = Sample(200, 120))
                    {
                        Document d; it = LibraryStore.AddImage(s, out d);
                        Exporter.Save(s, file);
                    }
                    LibraryStore.SetExport(it, file);
                    var again = LibraryStore.List().Find(x => x.Id == it.Id);
                    if (again == null || again.ExportPath == null || !string.Equals(again.ExportPath, Path.GetFullPath(file), StringComparison.OrdinalIgnoreCase)) return "the saved path was not remembered after a reload";
                    if (LibraryStore.LoadDoc(again).ExportPath != again.ExportPath) return "reopening the item lost the link to its file";
                    var found = LibraryStore.FindByExport(file);
                    if (found == null || found.Id != it.Id) return "opening the saved file did not find its own item";
                    File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(5));           // somebody else changed the file
                    if (LibraryStore.FindByExport(file) != null) return "a file changed elsewhere still matched the old item";

                    // a video keeps its duration next to the link
                    byte[] jpg; using (var b = new Bitmap(160, 90)) using (var ms = new MemoryStream()) { b.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg); jpg = ms.ToArray(); }
                    using (var w = new AviWriter(vfile, 160, 90, 10)) for (int i = 0; i < 10; i++) w.AddFrame(jpg);
                    using (var fb = new Bitmap(160, 90)) vit = LibraryStore.AddVideo(vfile, fb, 7);
                    string vdest = Path.Combine(tmp, "video-main.avi");
                    LibraryStore.SetExport(vit, vdest);
                    var v2 = LibraryStore.List().Find(x => x.Id == vit.Id);
                    if (v2 == null || v2.DurationSec != 7 || v2.ExportPath == null) return "video lost its duration or its saved path (dur " + (v2 == null ? -1 : v2.DurationSec) + ")";
                }
                finally
                {
                    if (it != null) LibraryStore.Delete(it);
                    if (vit != null) LibraryStore.Delete(vit);
                    try { File.Delete(file); } catch { }
                }
                return null;
            });

            Check("Save overwrites the main file (no dialog, no second file) and the title shows its name", delegate
            {
                string dir = Path.Combine(tmp, "savetest"); Directory.CreateDirectory(dir);
                foreach (var old in Directory.GetFiles(dir)) File.Delete(old);
                string file = Path.Combine(dir, "picture-one.png");
                LibItem it = null;
                try
                {
                    Document d;
                    using (var s = Sample(200, 120)) it = LibraryStore.AddImage(s, out d);
                    using (var ed = new EditorForm())
                    {
                        var h = ed.Handle;
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        ed.OpenNew(it, d);
                        typeof(EditorForm).GetMethod("WriteExport", flags).Invoke(ed, new object[] { file });            // what Save As does after the dialog
                        if (ed.Text.IndexOf("picture-one.png", StringComparison.OrdinalIgnoreCase) < 0) return "the title does not show the saved file: " + ed.Text;
                        File.SetLastWriteTimeUtc(file, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));               // so a rewrite is visible
                        d.ResizeImage(100, 60);                                                                            // edit the picture
                        typeof(EditorForm).GetMethod("SaveQuick", flags).Invoke(ed, null);                                 // Ctrl+S: must not ask for a name
                    }
                    var files = Directory.GetFiles(dir);
                    if (files.Length != 1) return "Save created " + files.Length + " files instead of overwriting the one";
                    if (File.GetLastWriteTimeUtc(file).Year < 2020) return "Save did not rewrite the main file";
                    using (var im = Image.FromFile(file)) if (im.Width != 100 || im.Height != 60) return "the saved file does not hold the edited picture (" + im.Width + "x" + im.Height + ")";
                }
                finally { if (it != null) LibraryStore.Delete(it); }
                return null;
            });

            Check("library tray: X button closes (never deletes), the thumbnail body opens", delegate
            {
                using (var s = Sample(200, 120))
                {
                    Document d; var it = LibraryStore.AddImage(s, out d);
                    try
                    {
                        using (var grid = new ThumbGrid { Size = new Size(600, 120) })
                        {
                            var h = grid.Handle;   // creates the handle, which loads the items
                            LibItem closed = null, removed = null, opened = null;
                            grid.ItemClose += x => closed = x;
                            grid.ItemRemove += x => removed = x;
                            grid.ItemOpen += x => opened = x;
                            var up = typeof(Control).GetMethod("OnMouseUp", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                            Action<int, int> click = (x, y) => up.Invoke(grid, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, y, 0) });
                            float sc = Theme.Scale(grid);
                            click((int)(118 * sc), (int)(14 * sc));            // inside the X of the first cell
                            if (closed == null) return "clicking X did not request a close";
                            if (removed != null) return "clicking X asked to DELETE the item";
                            if (opened != null) return "clicking X also opened the item";
                            click((int)(60 * sc), (int)(45 * sc));             // middle of the first cell
                            if (opened == null) return "clicking the thumbnail did not open it";
                        }
                    }
                    finally { LibraryStore.Delete(it); }
                }
                return null;
            });

            Check("closing hides items without deleting them; restoring and a rebuilt thumbnail bring them back", delegate
            {
                LibItem a = null, b = null;
                try
                {
                    Document d;
                    using (var s = Sample(160, 100)) a = LibraryStore.AddImage(s, out d);
                    using (var s = Sample(170, 100)) b = LibraryStore.AddImage(s, out d);
                    LibraryStore.SetClosed(new[] { a }, true);
                    if (LibraryStore.List().Exists(x => x.Id == a.Id)) return "a closed item is still listed";
                    if (!LibraryStore.List().Exists(x => x.Id == b.Id)) return "closing one item hid another";
                    if (!File.Exists(a.File)) return "closing deleted the project file";
                    if (!LibraryStore.List(true).Exists(x => x.Id == a.Id && x.Closed)) return "the closed item is not kept in the full list";
                    LibraryStore.RestoreClosed();
                    if (!LibraryStore.List().Exists(x => x.Id == a.Id)) return "Restore closed items did not bring it back";
                    // a project whose thumbnail is gone (restored from the Recycle Bin, say) is listed again with a rebuilt thumbnail
                    File.Delete(b.ThumbFile);
                    var again = LibraryStore.List().Find(x => x.Id == b.Id);
                    if (again == null || !File.Exists(b.ThumbFile)) return "an item without a thumbnail was not rebuilt";
                }
                finally
                {
                    if (a != null) LibraryStore.Delete(a);
                    if (b != null) LibraryStore.Delete(b);
                }
                return null;
            });

            Check("library store round trip", delegate
            {
                using (var s = Sample(200, 120))
                {
                    Document d; var it = LibraryStore.AddImage(s, out d);
                    var found = LibraryStore.List().Find(x => x.Id == it.Id);
                    if (found == null) return "item not listed";
                    var back = Document.LoadProject(found.File);
                    if (back.Width != 200) return "loaded width";
                    LibraryStore.Delete(it);
                    if (LibraryStore.List().Exists(x => x.Id == it.Id)) return "delete failed";
                }
                return null;
            });

            log.AppendLine();
            log.AppendLine(failed == 0 ? "ALL TESTS PASSED" : failed + " TEST(S) FAILED");
            try { File.WriteAllText(logPath ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "selftest.log"), log.ToString()); } catch { }
            return failed == 0 ? 0 : 1;
        }
    }

    /// <summary>Renders a window to a PNG so the layout can be inspected:  --uishot editor|editor2|main|settings|library|overlay out.png</summary>
    static class UiShot
    {
        public static int Run(string[] args)
        {
            string what = args.Length > 1 ? args[1] : "editor";
            string outPath = args.Length > 2 ? args[2] : "uishot.png";
            Form f = null;
            Action prepare = null;
            LibItem demoVideo = null;   // removed again after the shot so the user's library stays clean
            switch (what)
            {
                case "editor":
                case "editor2":
                    {
                        var ed = new EditorForm();
                        f = ed;
                        prepare = delegate
                        {
                            Bitmap src;
                            try { var m = Screen.PrimaryScreen.Bounds; src = ScreenGrabber.Grab(new Rectangle(m.X, m.Y, Math.Min(1500, m.Width), Math.Min(900, m.Height))); }
                            catch { src = SelfTest.Sample(1200, 700); }
                            Document d; var it = LibraryStore.AddImage(src, out d);
                            if (what == "editor2") AddDemoAnnotations(d);
                            ed.OpenNew(it, d);
                            int tab = 1; string tool = null;
                            foreach (var a in args) { if (a.StartsWith("tab=")) tab = int.Parse(a.Substring(4)); if (a.StartsWith("tool=")) tool = a.Substring(5); }
                            ed.DebugSet(tab, tool);
                            if (Array.IndexOf(args, "selcallout") >= 0) ed.Canvas.SetSelection(new System.Collections.Generic.List<Ann> { ed.Doc.Items.Find(a => a.Kind == AnnKind.Callout) });
                        };
                        break;
                    }
                case "main":
                    {
                        var mf = new MainForm(); f = mf;
                        foreach (var a in args) if (a.StartsWith("tab=")) { int tb = int.Parse(a.Substring(4)); prepare = delegate { mf.SelectTab(tb); }; }
                        break;
                    }
                case "widget":
                    {
                        // the widget is excluded from screen capture, so it is rendered with DrawToBitmap
                        AppSettings.Current.WidgetAutoHide = Array.IndexOf(args, "anim") >= 0 || Array.IndexOf(args, "tab") >= 0;
                        foreach (var a in args) if (a.StartsWith("page=")) AppSettings.Current.WidgetPage = int.Parse(a.Substring(5));
                        if (App.AppIcon == null) App.AppIcon = SystemIcons.Application;
                        var wf = new CaptureWidget();
                        if (Array.IndexOf(args, "anim") >= 0)
                        {
                            // hover over the tab, record the height while it expands, move away and record the fold
                            AppSettings.Current.WidgetAutoHide = true;
                            var log = new System.Text.StringBuilder();
                            var sw = System.Diagnostics.Stopwatch.StartNew();
                            int phase = 0; long phaseT = 0;
                            var at = new Timer { Interval = 10 };
                            at.Tick += delegate
                            {
                                long ms = sw.ElapsedMilliseconds;
                                if (phase == 0 && ms > 600) { Cursor.Position = new Point(wf.Left + wf.Width / 2, wf.Top + 3); phase = 1; phaseT = ms; log.AppendLine("--- hover at " + ms); }
                                if (phase == 1 && ms > phaseT + 900) { Cursor.Position = new Point(wf.Left + wf.Width / 2, wf.Top + 700); phase = 2; phaseT = ms; log.AppendLine("--- leave at " + ms); }
                                if (phase >= 1) log.AppendLine((ms - phaseT) + "ms  " + wf.Width + "x" + wf.Height);
                                if (phase == 2 && ms > phaseT + 1400) { File.WriteAllText(outPath, log.ToString()); Application.Exit(); }
                            };
                            wf.Shown += delegate { at.Start(); };
                            Application.Run(wf);
                            return 0;
                        }
                        var wt = new Timer { Interval = 900 };
                        wt.Tick += delegate
                        {
                            wt.Stop();
                            try { using (var bmp = new Bitmap(wf.Width, wf.Height)) { wf.DrawToBitmap(bmp, new Rectangle(0, 0, wf.Width, wf.Height)); bmp.Save(outPath, ImageFormat.Png); } }
                            catch (Exception ex) { File.WriteAllText(outPath + ".err", ex.ToString()); }
                            Application.Exit();
                        };
                        wf.Shown += delegate { wt.Start(); };
                        Application.Run(wf);
                        return 0;
                    }
                case "recorder":
                    {
                        // the recorder bar is excluded from screen capture, so it is rendered with DrawToBitmap
                        if (App.AppIcon == null) App.AppIcon = SystemIcons.Application;
                        var rf = new RecorderForm(new Rectangle(300, 300, 640, 360));
                        var rt = new Timer { Interval = 700 };
                        rt.Tick += delegate
                        {
                            rt.Stop();
                            try { using (var bmp = new Bitmap(rf.Width, rf.Height)) { rf.DrawToBitmap(bmp, new Rectangle(0, 0, rf.Width, rf.Height)); bmp.Save(outPath, ImageFormat.Png); } }
                            catch (Exception ex) { File.WriteAllText(outPath + ".err", ex.ToString()); }
                            rf.Close();
                        };
                        rf.Shown += delegate { rt.Start(); };
                        Application.Run(rf);
                        return 0;
                    }
                case "editorvideo":
                    {
                        // an editor showing a freshly made MP4 in the in-editor player
                        var ed = new EditorForm();
                        f = ed;
                        prepare = delegate
                        {
                            string avi = Path.Combine(Path.GetTempPath(), "rxcapture_uishot.avi"), mp4 = Path.ChangeExtension(avi, ".mp4");
                            using (var w = new AviWriter(avi, 640, 360, 15))
                                for (int i = 0; i < 45; i++)
                                    using (var b = new Bitmap(640, 360)) using (var g = Graphics.FromImage(b)) using (var ms = new MemoryStream())
                                    {
                                        g.Clear(Color.FromArgb(30, 60, 110)); g.FillEllipse(Brushes.Orange, 20 + i * 12, 120, 90, 90);
                                        g.DrawString("demo video", new Font("Segoe UI", 28f, FontStyle.Bold), Brushes.White, 200, 20);
                                        b.Save(ms, ImageFormat.Jpeg); w.AddFrame(ms.ToArray());
                                    }
                            Mp4Writer.Convert(avi, mp4, 15); File.Delete(avi);
                            LibItem it; using (var fb = new Bitmap(640, 360)) it = LibraryStore.AddVideo(mp4, fb, 3);
                            demoVideo = it;
                            if (Array.IndexOf(args, "saved") >= 0) LibraryStore.SetExport(it, Path.Combine(Path.GetTempPath(), "Bao_cao_quy_III_ban_cuoi_v2.mp4"));   // a saved item shows its file name
                            Application.DoEvents(); ed.ShowVideo(it);   // DoEvents: let the tray reload so the new item can be selected
                            if (Array.IndexOf(args, "select") >= 0)
                            {
                                // multi-select look: tick the first three cells
                                var fl = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                                var tray = typeof(EditorForm).GetField("tray", fl).GetValue(ed);
                                typeof(ThumbGrid).GetMethod("SetSelecting", fl).Invoke(tray, new object[] { true });
                                for (int k = 0; k < 3; k++) typeof(ThumbGrid).GetMethod("Tick", fl).Invoke(tray, new object[] { k });
                                if (Array.IndexOf(args, "band") >= 0)
                                {
                                    // caught mid rubber-band drag
                                    var tg = typeof(ThumbGrid);
                                    tg.GetField("marquee", fl).SetValue(tray, true);
                                    tg.GetField("downPt", fl).SetValue(tray, new Point(60, 20));
                                    tg.GetField("curPt", fl).SetValue(tray, new Point(380, 100));
                                }
                            }
                        };
                        break;
                    }
                case "overlay":
                    {
                        // full-screen selection on the primary monitor: the button bar must sit above the taskbar
                        var vs = SystemInformation.VirtualScreen;
                        var prim = Screen.PrimaryScreen.Bounds;
                        var frozen = ScreenGrabber.Grab(vs);
                        var ov = new RegionOverlay(frozen, vs, new List<WinInfo>(), OverlayMode.Region, null);
                        var t0 = typeof(RegionOverlay);
                        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                        bool scrollStep = Array.IndexOf(args, "scroll") >= 0;      // the step after pressing Scroll: choose where to start
                        t0.GetField("sel", flags).SetValue(ov, scrollStep ? new Rectangle(prim.X - vs.X + 500, prim.Y - vs.Y + 250, 900, 600) : new Rectangle(prim.X - vs.X, prim.Y - vs.Y, prim.Width, prim.Height));
                        var stField = t0.GetField("st", flags); stField.SetValue(ov, Enum.Parse(stField.FieldType, scrollStep ? "ScrollPick" : "Adjusting"));
                        var ot = new Timer { Interval = 800 };
                        ot.Tick += delegate
                        {
                            ot.Stop();
                            try { using (var b = ScreenGrabber.Grab(prim)) b.Save(outPath, ImageFormat.Png); }
                            catch (Exception ex) { File.WriteAllText(outPath + ".err", ex.ToString()); }
                            ov.Close();
                        };
                        ov.Shown += delegate { t0.GetMethod("Redraw", flags).Invoke(ov, null); ot.Start(); };
                        ov.ShowDialog();
                        return 0;
                    }
                case "watermark":
                    {
                        var src = new Bitmap(600, 400);
                        using (var g = Graphics.FromImage(src)) g.Clear(Color.SteelBlue);
                        var d = new ParamDialog("Watermark", src);
                        d.AddText("Text", "RXCapture"); d.AddNumber("Font size (px)", 6, 400, 32); d.AddCheck("Bold", true); d.AddColor("Colour", Color.White);
                        d.AddSlider("Opacity %", 5, 100, 60);
                        var pos = d.AddCombo("Position", new[] { "Top left", "Top centre", "Top right", "Middle left", "Centre", "Middle right", "Bottom left", "Bottom centre", "Bottom right" }, 5);
                        d.AddNumber("Margin (px)", 0, 500, 16);
                        if (args.Length > 3 && args[3] == "open") prepare = delegate { pos.Focus(); pos.DroppedDown = true; };
                        d.PreviewFn = (p, k) => (Bitmap)p.Clone();
                        d.Finish(430);
                        f = d;
                        break;
                    }
                case "settings": f = new SettingsForm(); break;
                case "defaults": f = new DefaultsForm(); break;
                case "library": f = new LibraryForm(); break;
                default: return 2;
            }
            if (App.AppIcon == null) App.AppIcon = SystemIcons.Application;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(40, 30);
            if (what.StartsWith("editor")) { f.Size = new Size(1500, 900); }
            var t = new Timer { Interval = 1200 };
            bool prepared = false;
            t.Tick += delegate
            {
                if (!prepared && prepare != null) { prepared = true; prepare(); t.Interval = 1200; return; }
                t.Stop();
                try
                {
                    var r = f.Bounds;
                    if (prepare != null && what == "watermark") r.Height += 120;              // an open drop-down list reaches below the dialog
                    using (var b = ScreenGrabber.Grab(r)) b.Save(outPath, ImageFormat.Png);
                }
                catch (Exception ex) { File.WriteAllText(outPath + ".err", ex.ToString()); }
                Application.Exit();
            };
            f.Shown += delegate { t.Start(); };
            Application.Run(f);
            if (demoVideo != null) LibraryStore.Delete(demoVideo);
            return 0;
        }

        static void AddDemoAnnotations(Document d)
        {
            Func<AnnKind, Ann> mk = k => Ann.Create(k);
            var ar = mk(AnnKind.Arrow); ar.P1 = new PointF(300, 300); ar.P2 = new PointF(160, 200); d.Items.Add(ar);
            var sh = mk(AnnKind.Shape); sh.P1 = new PointF(420, 120); sh.P2 = new PointF(700, 240); d.Items.Add(sh);
            var co = mk(AnnKind.Callout); co.P1 = new PointF(500, 330); co.P2 = new PointF(760, 400); co.Tail = new PointF(470, 470); co.Text = "Click here to continue"; d.Items.Add(co);
            var st = mk(AnnKind.Step); st.P1 = new PointF(120, 120); st.P2 = new PointF(156, 156); st.Number = 1; d.Items.Add(st);
            var st2 = mk(AnnKind.Step); st2.P1 = new PointF(820, 120); st2.P2 = new PointF(856, 156); st2.Number = 2; d.Items.Add(st2);
            var bl = mk(AnnKind.Blur); bl.P1 = new PointF(900, 500); bl.P2 = new PointF(1180, 580); d.Items.Add(bl);
            var tx = mk(AnnKind.Text); tx.P1 = new PointF(120, 500); tx.Text = "Important note"; tx.FitText(); d.Items.Add(tx);
            var hl = mk(AnnKind.Pen); hl.Highlighter = true; hl.Stroke = Color.FromArgb(255, 235, 59); hl.Width = 22; hl.Pts.AddRange(new[] { new PointF(150, 620), new PointF(400, 618), new PointF(620, 624) }); d.Items.Add(hl);
            var sp = mk(AnnKind.Stamp); sp.P1 = new PointF(1000, 150); sp.P2 = new PointF(1060, 210); sp.Variant = 0; d.Items.Add(sp);
            var mg = mk(AnnKind.Magnify); mg.P1 = new PointF(880, 250); mg.P2 = new PointF(1080, 400); mg.Variant = 1; d.Items.Add(mg);
        }
    }
}
