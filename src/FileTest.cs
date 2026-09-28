using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace ShotCraft
{
    /// <summary>Tests for the editor's File panel: New Image / New from Clipboard / Save / Convert Images, and the panel itself.</summary>
    static class FileTest
    {
        static StringBuilder log = new StringBuilder();
        static int failed;

        static void Expect(string name, bool ok, string info = "")
        {
            log.AppendLine((ok ? "PASS " : "FAIL ") + name + (info.Length > 0 ? "  [" + info + "]" : ""));
            if (!ok) failed++;
        }

        static void Pump(int ms) { var t = DateTime.Now; while ((DateTime.Now - t).TotalMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(15); } }

        static bool Key(Form f, Keys k)
        {
            var m = typeof(Form).GetMethod("ProcessCmdKey", BindingFlags.Instance | BindingFlags.NonPublic);
            var msg = new Message();
            return (bool)m.Invoke(f, new object[] { msg, k });
        }

        public static int Run(string[] args)
        {
            string logPath = args.Length > 1 ? args[1] : null;
            string shot = args.Length > 2 ? args[2] : null;
            string tmp = Path.Combine(Path.GetTempPath(), "sc-filetest-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(tmp);
            string origSaveFolder = AppSettings.Current.SaveFolder;            // exporting during the test moves SaveFolder; put it back afterwards
            var before = new System.Collections.Generic.HashSet<string>();
            foreach (var li in LibraryStore.List()) before.Add(li.Id);
            try
            {
                if (App.AppIcon == null) App.AppIcon = SystemIcons.Application;
                var ed = new EditorForm();
                ed.StartPosition = FormStartPosition.Manual; ed.Location = new Point(60, 40); ed.Size = new Size(1300, 800);
                ed.Show(); Pump(500);

                // ---- default file name: Rndimsx_yyyyMMdd_HHmmss (the old "Capture_" turned its "t" into AM/PM: "CapAure_")
                {
                    var cfgN = AppSettings.Current;
                    string oldPattern = cfgN.FileNamePattern;
                    cfgN.FileNamePattern = AppSettings.DefaultFileNamePattern;
                    string nm = Path.GetFileName(cfgN.NewFileName("png"));
                    Expect("Default file name is Rndimsx_yyyyMMdd_HHmmss", System.Text.RegularExpressions.Regex.IsMatch(nm, @"^Rndimsx_\d{8}_\d{6}\.png$"), nm);
                    cfgN.FileNamePattern = oldPattern;
                }

                // ---- New Image
                ed.CreateBlank(640, 480, Color.White);
                Expect("New Image: document is 640x480", ed.Doc != null && ed.Doc.Width == 640 && ed.Doc.Height == 480);
                using (var r = ed.Doc.Render()) Expect("New Image: white background", r.GetPixel(10, 10).ToArgb() == Color.White.ToArgb());
                ed.CreateBlank(100, 50, Color.Transparent);
                using (var r = ed.Doc.Render()) Expect("New Image: transparent background keeps alpha 0", r.GetPixel(5, 5).A == 0 && ed.Doc.Width == 100);
                ed.CreateBlank(300, 200, Color.FromArgb(255, 30, 120, 200));
                using (var r = ed.Doc.Render()) Expect("New Image: solid colour", r.GetPixel(100, 100).ToArgb() == Color.FromArgb(255, 30, 120, 200).ToArgb());

                // ---- default zoom = fit to the editor view (the editor opens maximized)
                Func<CanvasControl, float> expectFit = cv =>
                {
                    int m = cv.CanvasMargin;
                    float aw = Math.Max(50, cv.ClientSize.Width - 2 * m), ah = Math.Max(50, cv.ClientSize.Height - 2 * m);
                    return Math.Min(1f, Math.Min(aw / cv.Doc.Width, ah / cv.Doc.Height));
                };
                Expect("Editor opens maximized by default", ed.WindowState == FormWindowState.Maximized);
                ed.CreateBlank(4000, 2400, Color.White); Pump(300);
                var cvs = ed.Canvas;
                Expect("Large capture is shown fitted to the maximized editor", Math.Abs(cvs.Zoom - expectFit(cvs)) < 0.002 && cvs.Zoom < 1f, "zoom " + cvs.Zoom.ToString("0.000") + " expected " + expectFit(cvs).ToString("0.000"));
                ed.WindowState = FormWindowState.Normal; Pump(400);
                Expect("Fit follows the window when it is restored / resized", Math.Abs(cvs.Zoom - expectFit(cvs)) < 0.002, "zoom " + cvs.Zoom.ToString("0.000") + " expected " + expectFit(cvs).ToString("0.000"));
                float fitNormal = cvs.Zoom;
                ed.WindowState = FormWindowState.Maximized; Pump(400);
                Expect("Fit follows the window when maximized again", Math.Abs(cvs.Zoom - expectFit(cvs)) < 0.002 && cvs.Zoom > fitNormal, "zoom " + cvs.Zoom.ToString("0.000"));
                cvs.SetZoom(0.5f, null); float manual = cvs.Zoom;
                ed.WindowState = FormWindowState.Normal; Pump(400);
                Expect("A manual zoom is kept when the window is resized", Math.Abs(cvs.Zoom - manual) < 0.0001, "zoom " + cvs.Zoom.ToString("0.000"));
                ed.WindowState = FormWindowState.Maximized; Pump(300);
                ed.CreateBlank(300, 200, Color.White); Pump(200);
                Expect("A small capture stays at 100%", Math.Abs(cvs.Zoom - 1f) < 0.0001);

                // ---- Save (Ctrl+S with a target already chosen writes the file) + project round trip
                var an = Ann.Create(AnnKind.Shape); an.P1 = new PointF(20, 20); an.P2 = new PointF(120, 90); ed.Doc.Items.Add(an); ed.Doc.Raise();
                string png = Path.Combine(tmp, "saved.png");
                ed.Doc.ExportPath = png;
                Key(ed, Keys.Control | Keys.S);
                Expect("Save: Ctrl+S writes the PNG", File.Exists(png) && new FileInfo(png).Length > 100);
                if (File.Exists(png)) using (var im = Image.FromFile(png)) Expect("Save: saved PNG is 300x200 and contains the shape", im.Width == 300 && im.Height == 200);
                string scp = Path.Combine(tmp, "proj.scp");
                string keep = ed.Doc.ProjectPath;
                ed.Doc.SaveProject(scp); ed.Doc.ProjectPath = keep;
                var back = Document.LoadProject(scp);
                Expect("Save project: .scp reloads with its objects", back.Width == 300 && back.Items.Count == 1);

                // ---- New from clipboard
                using (var cb = new Bitmap(77, 55)) { using (var g = Graphics.FromImage(cb)) g.Clear(Color.Orange); Clipboard.SetImage(cb); }
                ed.DebugShowFilePanel(new Point(80, 120)); Pump(400);
                var panel = ed.FilePanel;
                Expect("File panel opens", panel != null && !panel.IsDisposed && panel.Visible);
                if (panel != null && !panel.IsDisposed)
                {
                    if (shot != null) try { using (var b = ScreenGrabber.Grab(panel.Bounds)) b.Save(shot, ImageFormat.Png); } catch { }
                    // Down x3 -> "New from Clipboard", Enter runs it
                    Key(panel, Keys.Down); Key(panel, Keys.Down); Key(panel, Keys.Down); Key(panel, Keys.Enter); Pump(400);
                    Expect("File panel closes after choosing a command", panel.IsDisposed || !panel.Visible);
                    Expect("New from Clipboard: opens the clipboard image (77x55)", ed.Doc != null && ed.Doc.Width == 77 && ed.Doc.Height == 55, ed.Doc == null ? "no doc" : ed.Doc.Width + "x" + ed.Doc.Height);
                }
                ed.DebugShowFilePanel(new Point(80, 120)); Pump(300);
                var p2 = ed.FilePanel;
                if (p2 != null && !p2.IsDisposed) { Key(p2, Keys.Escape); Pump(200); Expect("File panel closes with Esc", p2.IsDisposed || !p2.Visible); }

                // ---- Convert Images
                string src1 = Path.Combine(tmp, "a.png"), src2 = Path.Combine(tmp, "b.png");
                using (var b = new Bitmap(120, 80)) { using (var g = Graphics.FromImage(b)) g.Clear(Color.Crimson); b.Save(src1, ImageFormat.Png); b.Save(src2, ImageFormat.Png); }
                string outDir = Path.Combine(tmp, "out");
                using (var cf = new ConvertImagesForm())
                {
                    string st = cf.TestRun(new[] { src1, src2 }, 1, outDir);
                    Expect("Convert Images: 2/2 converted to JPEG", st.Contains("2/2"), st);
                }
                Expect("Convert Images: JPEG files written", File.Exists(Path.Combine(outDir, "a.jpg")) && File.Exists(Path.Combine(outDir, "b.jpg")));
                if (File.Exists(Path.Combine(outDir, "a.jpg"))) using (var im = Image.FromFile(Path.Combine(outDir, "a.jpg"))) Expect("Convert Images: JPEG decodes at 120x80", im.Width == 120 && im.Height == 80);
                using (var cf = new ConvertImagesForm()) cf.TestRun(new[] { src1 }, 5, outDir);
                Expect("Convert Images: PDF written", File.Exists(Path.Combine(outDir, "a.pdf")) && new FileInfo(Path.Combine(outDir, "a.pdf")).Length > 200);
                using (var cf = new ConvertImagesForm()) cf.TestRun(new[] { scp }, 0, outDir);
                Expect("Convert Images: .scp project converted to PNG", File.Exists(Path.Combine(outDir, "proj.png")));
                using (var cf = new ConvertImagesForm()) cf.TestRun(new[] { src1 }, 0, null);
                Expect("Convert Images: same-format output gets a new name, source untouched", File.Exists(Path.Combine(tmp, "a_converted.png")));

                // ---- configurable default tool properties
                var cfgAll = AppSettings.Current;
                var savedDefaults = new System.Collections.Generic.List<ToolDefaultEntry>();
                foreach (var en in cfgAll.ToolDefaults) savedDefaults.Add(new ToolDefaultEntry { Tool = en.Tool, Xml = en.Xml });
                try
                {
                    ToolDefaultsStore.ResetAll();
                    Expect("Defaults: factory callout is Arial 20px red", ToolDefaultsStore.Load(Tool.Callout).FontSize == 20 && !ToolDefaultsStore.IsCustom(Tool.Callout));
                    var mod = ToolDefaultsStore.Factory(Tool.Callout); mod.FontSize = 28; mod.FontName = "Verdana"; mod.TextColor = Color.FromArgb(10, 60, 200); mod.Width = 6; mod.Shadow = true;
                    ToolDefaultsStore.Save(Tool.Callout, mod);
                    var ld = ToolDefaultsStore.Load(Tool.Callout);
                    Expect("Defaults: saved callout style is loaded back", ld.FontSize == 28 && ld.FontName == "Verdana" && ld.TextColor.B == 200 && ld.Width == 6 && ld.Shadow && ToolDefaultsStore.IsCustom(Tool.Callout));
                    Expect("Defaults: an open editor picks the change up", ed.Canvas.Defaults[Tool.Callout].FontSize == 28);
                    using (var fresh = new CanvasControl())   // what a restarted editor sees
                        Expect("Defaults: a new editor starts with the saved default", fresh.Defaults[Tool.Callout].FontSize == 28 && fresh.Defaults[Tool.Callout].FontName == "Verdana");
                    ToolDefaultsStore.Reset(Tool.Callout);
                    Expect("Defaults: reset restores the factory style", ToolDefaultsStore.Load(Tool.Callout).FontSize == 20 && ed.Canvas.Defaults[Tool.Callout].FontSize == 20);

                    // "Set default" from the canvas: uses the active tool's template, or the selected object
                    ed.CreateBlank(400, 300, Color.White); Pump(200);
                    ed.Canvas.CurrentTool = Tool.Arrow;
                    ed.Canvas.Defaults[Tool.Arrow].Width = 9; ed.Canvas.Defaults[Tool.Arrow].Stroke = Color.FromArgb(0, 128, 0);
                    var tgt = ed.Canvas.SaveAsDefault();
                    Expect("Set default: active tool's template is stored", tgt == Tool.Arrow && ToolDefaultsStore.Load(Tool.Arrow).Width == 9 && ToolDefaultsStore.Load(Tool.Arrow).Stroke.G == 128);
                    var shp = Ann.Create(AnnKind.Shape); shp.P1 = new PointF(20, 20); shp.P2 = new PointF(150, 100); shp.Width = 11; shp.Fill = Color.FromArgb(255, 250, 200, 0);
                    ed.Doc.Items.Add(shp); ed.Doc.Raise();
                    ed.Canvas.CurrentTool = Tool.Select; ed.Canvas.SetSelection(new System.Collections.Generic.List<Ann> { shp });
                    tgt = ed.Canvas.SaveAsDefault();
                    Expect("Set default: the selected object's style is stored for its tool", tgt == Tool.Shape && ToolDefaultsStore.Load(Tool.Shape).Width == 11 && ToolDefaultsStore.Load(Tool.Shape).Fill.G == 200 && ToolDefaultsStore.Load(Tool.Shape).P1 == PointF.Empty);
                    ed.Canvas.ResetDefault();
                    Expect("Set default: reset for the selected object's tool", ToolDefaultsStore.Load(Tool.Shape).Width == 4 && !ToolDefaultsStore.IsCustom(Tool.Shape));
                    ToolDefaultsStore.ResetAll();

                    // every tool editor opens, previews and saves (a timer presses OK)
                    string shotDir = args.Length > 3 ? args[3] : null;
                    foreach (var tl in ToolDefaultsStore.Configurable)
                    {
                        var tt = tl; var timer = new System.Windows.Forms.Timer { Interval = 500 };
                        timer.Tick += delegate
                        {
                            timer.Stop();
                            foreach (Form f in Application.OpenForms)
                                if (f is ParamDialog)
                                {
                                    if (shotDir != null && (tt == Tool.Callout || tt == Tool.Arrow || tt == Tool.Text)) try { using (var b = ScreenGrabber.Grab(f.Bounds)) b.Save(Path.Combine(shotDir, "def_" + tt + ".png"), ImageFormat.Png); } catch { }
                                    f.DialogResult = DialogResult.OK; f.Close();
                                }
                        };
                        timer.Start();
                        bool saved = DefaultsForm.EditTool(ed, tt);
                        timer.Dispose();
                        Expect("Defaults dialog: " + tt + " editor saves", saved && ToolDefaultsStore.IsCustom(tt));
                    }
                    using (var df = new DefaultsForm()) Expect("Defaults dialog builds", df != null);
                }
                finally
                {
                    cfgAll.ToolDefaults.Clear();
                    foreach (var en in savedDefaults) cfgAll.ToolDefaults.Add(en);
                    cfgAll.Save();
                    ToolDefaultsStore.ResetAll(); cfgAll.ToolDefaults.Clear(); foreach (var en in savedDefaults) cfgAll.ToolDefaults.Add(en); cfgAll.Save();   // the raised Changed event reloads the templates
                }

                // ---- capture widget: same height on every page (switching pages must not shrink it under the mouse)
                bool oldAutoHide = AppSettings.Current.WidgetAutoHide;
                AppSettings.Current.WidgetAutoHide = false;
                using (var wg = new CaptureWidget())
                {
                    wg.Show(); Pump(500);
                    var setPage = typeof(CaptureWidget).GetMethod("SetPage", BindingFlags.Instance | BindingFlags.NonPublic);
                    var heights = new System.Collections.Generic.List<int>();
                    for (int p = 0; p < 4; p++) { setPage.Invoke(wg, new object[] { p }); Pump(450); heights.Add(wg.Height); }
                    bool same = heights.TrueForAll(h => h == heights[0]);
                    Expect("Widget keeps the same height on every page", same, string.Join(",", heights.ConvertAll(h => h.ToString()).ToArray()));
                    AppSettings.Current.WidgetAutoHide = oldAutoHide;      // never leave test values in the real settings
                }

                ed.Close();
            }
            catch (Exception ex) { log.AppendLine("FAIL exception " + ex); failed++; }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
                if (AppSettings.Current.SaveFolder != origSaveFolder) { AppSettings.Current.SaveFolder = origSaveFolder; AppSettings.Current.Save(); }
                foreach (var li in LibraryStore.List()) if (!before.Contains(li.Id)) LibraryStore.Delete(li);   // leave the real library as it was
            }
            log.AppendLine(failed == 0 ? "ALL FILE TESTS PASSED" : failed + " FAILED");
            if (logPath != null) File.WriteAllText(logPath, log.ToString());
            return failed == 0 ? 0 : 1;
        }
    }
}
