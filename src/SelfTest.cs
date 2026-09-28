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
                long[] ph = ScrollCapture.RowHashes(prev, 0);
                int off = 0;
                foreach (int step in new[] { 50, 80, 45, 60 })
                {
                    off += step;
                    var cur = ScreenGrabber.Crop(page, new Rectangle(0, off, W, H));
                    var ch = ScrollCapture.RowHashes(cur, 0);
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
                    using (var b = ScreenGrabber.Grab(r)) b.Save(outPath, ImageFormat.Png);
                }
                catch (Exception ex) { File.WriteAllText(outPath + ".err", ex.ToString()); }
                Application.Exit();
            };
            f.Shown += delegate { t.Start(); };
            Application.Run(f);
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
