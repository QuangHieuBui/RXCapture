using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Threading;
using Timer = System.Windows.Forms.Timer;
using System.Windows.Forms;

namespace ShotCraft
{
    public enum CaptureMode { AllInOne, Region, Window, FullScreen, Freehand, Fixed, Scrolling, Video, Repeat, VideoWindow, VideoScreen }

    /// <summary>Global application controller: windows, tray icon, hotkeys and capture entry points.</summary>
    public static class App
    {
        public static bool Running;
        static EditorForm editor;
        static MainForm main;
        static LibraryForm library;
        static CaptureWidget widget;
        static NotifyIcon tray;
        static HotkeyWindow hotkeys;
        public static Icon AppIcon;

        public static EditorForm Editor
        {
            get { if (editor == null || editor.IsDisposed) editor = new EditorForm(); return editor; }
        }

        public static void Start(string[] args)
        {
            Running = true;
            try { AppIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { AppIcon = SystemIcons.Application; }
            SettingsForm.MigrateStartupEntry();
            hotkeys = new HotkeyWindow();
            RegisterHotkeys();
            BuildTray();
            main = new MainForm();
            if (!AppSettings.Current.StartMinimized) main.Show();
            if (AppSettings.Current.ShowWidget) SetWidgetVisible(true);
            foreach (var a in args)
                if (File.Exists(a)) Editor.OpenFile(a);
        }

        // ------------------------------------------------------------------ windows

        public static void ShowMain() { ShowMain(-1); }

        public static void ShowMain(int tab)
        {
            if (main == null || main.IsDisposed) main = new MainForm();
            if (tab >= 0) main.SelectTab(tab);
            main.Show();
            if (main.WindowState == FormWindowState.Minimized) main.WindowState = FormWindowState.Normal;
            main.Activate();
        }

        public static void SetWidgetVisible(bool on)
        {
            AppSettings.Current.ShowWidget = on;
            AppSettings.Current.Save();
            if (on)
            {
                if (widget == null || widget.IsDisposed) widget = new CaptureWidget();
                widget.Show();
            }
            else if (widget != null && !widget.IsDisposed) { widget.Close(); widget = null; }
        }

        public static bool WidgetVisible { get { return widget != null && !widget.IsDisposed && widget.Visible; } }

        public static void ShowEditor()
        {
            var ed = Editor;
            ed.Show();
            if (ed.WindowState == FormWindowState.Minimized) ed.WindowState = FormWindowState.Normal;
            ed.Activate();
        }

        public static void ShowLibrary()
        {
            if (library == null || library.IsDisposed) library = new LibraryForm();
            library.Show();
            if (library.WindowState == FormWindowState.Minimized) library.WindowState = FormWindowState.Normal;
            library.Activate();
        }

        public static void ShowSettings()
        {
            using (var f = new SettingsForm())
                if (f.ShowDialog() == DialogResult.OK) { RegisterHotkeys(); Loc.Init(); SetWidgetVisible(AppSettings.Current.ShowWidget); }
        }

        public static void About()
        {
            MessageBox.Show(
                "RXCapture 1.0\n\n" + Loc.T("A screen capture and image editing tool for Windows.") + "\n\n" +
                Loc.T("Capture: All-in-One, Region, Window, Full screen, Scrolling, Freehand, Fixed region, Video.") + "\n" +
                Loc.T("Edit: arrows, callouts, text, steps, stamps, blur, magnify, spotlight, crop, effects and more."),
                "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        public static void Exit()
        {
            Running = false;
            try { if (editor != null && !editor.IsDisposed) editor.SaveCurrent(); } catch { }
            AppSettings.Current.Save();
            if (hotkeys != null) hotkeys.Unregister();
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            Application.Exit();
        }

        public static IEnumerable<Form> OpenForms()
        {
            if (main != null && !main.IsDisposed) yield return main;
            if (editor != null && !editor.IsDisposed) yield return editor;
            if (library != null && !library.IsDisposed) yield return library;
            if (widget != null && !widget.IsDisposed) yield return widget;
        }

        // ------------------------------------------------------------------ tray

        static void BuildTray()
        {
            tray = new NotifyIcon { Icon = AppIcon, Text = "RXCapture", Visible = AppSettings.Current.ShowTray };
            var m = Theme.Menu();
            m.Items.Add(Theme.Item("All-in-One", "camera", (s, e) => Capture(CaptureMode.AllInOne)));
            m.Items.Add(Theme.Item("Region", "region", (s, e) => Capture(CaptureMode.Region)));
            m.Items.Add(Theme.Item("Window", "window", (s, e) => Capture(CaptureMode.Window)));
            m.Items.Add(Theme.Item("Full Screen", "fullscreen", (s, e) => Capture(CaptureMode.FullScreen)));
            m.Items.Add(Theme.Item("Scrolling", "scroll", (s, e) => Capture(CaptureMode.Scrolling)));
            m.Items.Add(Theme.Item("Freehand", "freehand", (s, e) => Capture(CaptureMode.Freehand)));
            m.Items.Add(Theme.Item("Video", "video", (s, e) => Capture(CaptureMode.Video)));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Theme.Item("Capture window", "camera", (s, e) => ShowMain()));
            m.Items.Add(Theme.Item("Capture widget", "camera", (s, e) => SetWidgetVisible(!WidgetVisible)));
            m.Items.Add(Theme.Item("Editor", "pen", (s, e) => ShowEditor()));
            m.Items.Add(Theme.Item("Library", "library", (s, e) => ShowLibrary()));
            m.Items.Add(Theme.Item("Settings…", "settings", (s, e) => ShowSettings()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Theme.Item("Exit", "close", (s, e) => Exit()));
            tray.ContextMenuStrip = m;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowMain(); };
        }

        public static void Balloon(string text)
        {
            if (tray != null && tray.Visible) tray.ShowBalloonTip(2500, "RXCapture", text, ToolTipIcon.Info);
        }

        // ------------------------------------------------------------------ hotkeys

        public static void RegisterHotkeys()
        {
            var failed = hotkeys.Register();
            if (failed.Count > 0)
                Balloon(Loc.T("Could not register hotkey(s): ") + string.Join(", ", failed.ToArray()) + "\n" + Loc.T("Change them in Settings > Hotkeys."));
        }

        public static void Capture(CaptureMode mode)
        {
            // run after the current message finishes so the triggering menu/hotkey is gone
            var sc = SynchronizationContext.Current;
            Timer t = new Timer { Interval = 30 };
            t.Tick += delegate { t.Stop(); t.Dispose(); CaptureManager.Run(mode); };
            t.Start();
        }

        public static void HandleCommand(string cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return;
            if (cmd == "show") { ShowMain(); return; }
            if (cmd.StartsWith("capture:"))
            {
                try { Capture((CaptureMode)Enum.Parse(typeof(CaptureMode), cmd.Substring(8), true)); } catch { }
                return;
            }
            if (cmd.StartsWith("open:")) { var p = cmd.Substring(5); if (File.Exists(p)) { Editor.OpenFile(p); ShowEditor(); } }
        }
    }

    /// <summary>Hidden window that owns the global hotkeys.</summary>
    class HotkeyWindow : NativeWindow
    {
        readonly Dictionary<int, CaptureMode> map = new Dictionary<int, CaptureMode>();
        readonly Dictionary<int, CapturePreset> presetMap = new Dictionary<int, CapturePreset>();
        readonly List<int> ids = new List<int>();

        public HotkeyWindow() { CreateHandle(new CreateParams()); }

        public List<string> Register()
        {
            Unregister();
            var cfg = AppSettings.Current;
            var failed = new List<string>();
            var defs = new[] {
                new KeyValuePair<string, CaptureMode>(cfg.HkAllInOne, CaptureMode.AllInOne), new KeyValuePair<string, CaptureMode>(cfg.HkFullScreen, CaptureMode.FullScreen),
                new KeyValuePair<string, CaptureMode>(cfg.HkWindow, CaptureMode.Window), new KeyValuePair<string, CaptureMode>(cfg.HkRegion, CaptureMode.Region),
                new KeyValuePair<string, CaptureMode>(cfg.HkScroll, CaptureMode.Scrolling), new KeyValuePair<string, CaptureMode>(cfg.HkFreehand, CaptureMode.Freehand),
                new KeyValuePair<string, CaptureMode>(cfg.HkRepeat, CaptureMode.Repeat), new KeyValuePair<string, CaptureMode>(cfg.HkVideo, CaptureMode.Video) };
            int id = 0x5C00;
            foreach (var d in defs)
            {
                uint mod, vk;
                if (!HotkeyParser.TryParse(d.Key, out mod, out vk)) continue;
                id++;
                if (Native.RegisterHotKey(Handle, id, mod | 0x4000, vk)) { map[id] = d.Value; ids.Add(id); }   // 0x4000 = MOD_NOREPEAT
                else failed.Add(d.Key);
            }
            int pid = 0x5D00;
            foreach (var p in cfg.Presets)
            {
                uint pm, pv;
                if (!HotkeyParser.TryParse(p.Hotkey, out pm, out pv)) continue;
                pid++;
                if (Native.RegisterHotKey(Handle, pid, pm | 0x4000, pv)) { presetMap[pid] = p; ids.Add(pid); }
                else failed.Add(p.Hotkey);
            }
            return failed;
        }

        public void Unregister()
        {
            foreach (var i in ids) Native.UnregisterHotKey(Handle, i);
            ids.Clear(); map.Clear(); presetMap.Clear();
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY)
            {
                CapturePreset preset;
                if (presetMap.TryGetValue((int)m.WParam, out preset))
                {
                    if (VideoRecorder.IsActive) VideoRecorder.RequestStop();
                    else { preset.ApplyTo(AppSettings.Current); App.Capture(preset.Mode); }
                    return;
                }
                CaptureMode mode;
                if (map.TryGetValue((int)m.WParam, out mode))
                {
                    if (VideoRecorder.IsActive) VideoRecorder.RequestStop();
                    else App.Capture(mode);
                }
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>Orchestrates a capture: hide windows, delay, freeze screen, pick area, post-process and deliver.</summary>
    public static class CaptureManager
    {
        static bool busy;

        public static bool Busy { get { return busy; } }

        public static void Run(CaptureMode mode)
        {
            if (busy) return;
            busy = true;
            var hidden = new List<Form>();
            bool opened = false;
            try
            {
                var cfg = AppSettings.Current;
                if (App.Editor != null && App.Editor.HasDocument) App.Editor.SaveCurrent();
                foreach (var f in App.OpenForms()) if (f.Visible) { hidden.Add(f); f.Hide(); }
                Application.DoEvents();
                Thread.Sleep(mode == CaptureMode.Repeat ? 150 : 260);

                if (cfg.DelaySeconds > 0 && mode != CaptureMode.Video)
                    if (!CountdownForm.Run(cfg.DelaySeconds)) return;

                if (mode == CaptureMode.Repeat && cfg.LastRegion.IsEmpty) mode = CaptureMode.AllInOne;

                Rectangle vs = ScreenGrabber.VirtualScreen;
                var cur = CursorSnap.Take();
                var tops = WindowFinder.Snapshot(IntPtr.Zero);
                Rectangle monitor = Screen.FromPoint(Cursor.Position).Bounds;
                using (var frozen = ScreenGrabber.Grab(vs))
                {
                    Bitmap result = null;
                    OverlayResult pick = null;

                    switch (mode)
                    {
                        case CaptureMode.FullScreen:
                            pick = new OverlayResult { Rect = cfg.FullScreenAllMonitors ? vs : monitor, Action = CaptureAction.Image };
                            break;
                        case CaptureMode.VideoScreen:
                            pick = new OverlayResult { Rect = monitor, Action = CaptureAction.Video };
                            break;
                        case CaptureMode.Repeat:
                            pick = new OverlayResult { Rect = cfg.LastRegion, Action = CaptureAction.Image };
                            break;
                        default:
                            {
                                OverlayMode om = OverlayMode.AllInOne; CaptureAction? forced = null;
                                switch (mode)
                                {
                                    case CaptureMode.Region: om = OverlayMode.Region; break;
                                    case CaptureMode.Window: om = OverlayMode.Window; break;
                                    case CaptureMode.Freehand: om = OverlayMode.Freehand; break;
                                    case CaptureMode.Fixed: om = OverlayMode.Fixed; break;
                                    case CaptureMode.Scrolling: forced = CaptureAction.Scroll; break;
                                    case CaptureMode.Video: forced = CaptureAction.Video; break;
                                    case CaptureMode.VideoWindow: om = OverlayMode.Window; forced = CaptureAction.Video; break;
                                }
                                pick = RegionOverlay.Pick(frozen, vs, tops, om, forced);
                                break;
                            }
                    }
                    if (pick == null) return;

                    var rect = Rectangle.Intersect(pick.Rect, vs);
                    if (rect.Width < 2 || rect.Height < 2) return;
                    cfg.LastRegion = rect;

                    if (pick.Action == CaptureAction.Image)
                    {
                        var rel = new Rectangle(rect.X - vs.X, rect.Y - vs.Y, rect.Width, rect.Height);
                        result = ScreenGrabber.Crop(frozen, rel);
                        if (pick.Freeform != null && pick.Freeform.Count > 2)
                        {
                            using (var path = new GraphicsPath())
                            {
                                var pts = new List<Point>();
                                foreach (var p in pick.Freeform) pts.Add(new Point(p.X - rect.X, p.Y - rect.Y));
                                path.AddPolygon(pts.ToArray());
                                var masked = ScreenGrabber.ApplyMask(result, path);
                                result.Dispose(); result = masked;
                            }
                        }
                        bool wholeWindow = pick.Window != null && pick.Freeform == null && pick.Rect == pick.Window.Bounds;
                        if (wholeWindow) result = WindowPost(result, pick.Window, tops, cfg);
                        if (cfg.IncludeCursor) cur.DrawOn(result, rect.Location);
                        if (wholeWindow && cfg.WinShadow)
                        {
                            Point off;
                            var shadowed = Effects.DropShadow(result, 0, 6, 14, Color.Black, 45, out off);
                            result.Dispose(); result = shadowed;
                        }
                    }
                    else if (pick.Action == CaptureAction.Scroll)
                    {
                        Thread.Sleep(200);
                        result = ScrollCapture.Run(rect, cur);
                    }
                    else if (pick.Action == CaptureAction.Video)
                    {
                        VideoRecorder.Run(rect);
                        return;   // recorder delivers to the library itself
                    }

                    if (result != null) { Deliver(result); opened = true; }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                busy = false;
                if (!opened) foreach (var f in hidden) { try { f.Show(); } catch { } }
                else foreach (var f in hidden) { if (f is CaptureWidget) { try { f.Show(); } catch { } } }
            }
        }

        /// <summary>True when another window sits above w (in z-order) and overlaps it.</summary>
        public static bool IsCovered(WinInfo w, List<WinInfo> tops)
        {
            foreach (var t in tops)
            {
                if (t.Handle == w.Handle) return false;
                if (t.Bounds.IntersectsWith(w.Bounds) && t.Class != "Progman") return true;
            }
            return false;
        }

        /// <summary>Window-specific post processing: full content when covered, transparent rounded corners.</summary>
        static Bitmap WindowPost(Bitmap bmp, WinInfo w, List<WinInfo> tops, AppSettings cfg)
        {
            try
            {
                if (cfg.WinFullContent && IsCovered(w, tops))
                {
                    var full = ScreenGrabber.CaptureWindowContent(w.Handle, w.Bounds);
                    if (full != null && full.Width == bmp.Width && full.Height == bmp.Height) { bmp.Dispose(); bmp = full; }
                    else if (full != null) full.Dispose();
                }
                if (cfg.WinRoundCorners && Environment.OSVersion.Version.Build >= 22000 && !Native.IsZoomed(w.Handle))
                {
                    int dpi = 96;
                    try { dpi = (int)Native.GetDpiForWindow(w.Handle); } catch { }
                    var rounded = Effects.RoundCorners(bmp, Math.Max(4, (int)Math.Round(8 * dpi / 96.0)));
                    bmp.Dispose(); bmp = rounded;
                }
            }
            catch { }
            return bmp;
        }

        /// <summary>Applies the configured effect and sends the capture to the library / clipboard / disk / editor.</summary>
        public static void Deliver(Bitmap bmp)
        {
            var cfg = AppSettings.Current;
            Bitmap final = bmp;
            switch (cfg.Effect)
            {
                case AfterEffect.Border: final = Effects.Border(bmp, 2, Color.FromArgb(60, 60, 60)); break;
                case AfterEffect.DropShadow: { Point off; final = Effects.DropShadow(bmp, 5, 5, 8, Color.Black, 55, out off); break; }
                case AfterEffect.TornEdge: final = Effects.EdgeEffect(bmp, true, true, true, true, 8, 14, 2); break;
            }
            if (!ReferenceEquals(final, bmp)) bmp.Dispose();

            if (cfg.PlaySound) System.Media.SystemSounds.Asterisk.Play();
            Document doc;
            var item = LibraryStore.AddImage(final, out doc);
            if (cfg.CopyToClipboard) Exporter.CopyToClipboard(final);
            if (cfg.AutoSaveToFolder)
            {
                try { string p = cfg.NewFileName(cfg.Format == "" ? "png" : cfg.Format); Exporter.Save(final, p); doc.ExportPath = p; }
                catch { }
            }
            final.Dispose();
            if (cfg.OpenEditor)
            {
                var ed = App.Editor;
                ed.OpenNew(item, doc);
                ed.Show();
                if (ed.WindowState == FormWindowState.Minimized) ed.WindowState = FormWindowState.Normal;
                ed.Activate();
                Native.SetForegroundWindow(ed.Handle);
            }
            else App.Balloon(Loc.T("Capture saved to the library"));
        }
    }

    /// <summary>Big "3… 2… 1…" countdown shown before a delayed capture. Returns false if cancelled with Esc.</summary>
    class CountdownForm : Form
    {
        readonly Label lbl = new Label();
        public static bool Run(int seconds)
        {
            using (var f = new CountdownForm())
            {
                f.Show();
                var end = DateTime.UtcNow.AddSeconds(seconds);
                int last = -1;
                while (true)
                {
                    double left = (end - DateTime.UtcNow).TotalSeconds;
                    if (left <= 0) break;
                    int n = (int)Math.Ceiling(left);
                    if (n != last) { last = n; f.lbl.Text = n.ToString(); f.lbl.Refresh(); }
                    Application.DoEvents();
                    if (Native.KeyDown(0x1B)) return false;
                    Thread.Sleep(20);
                }
                f.Hide();
                Application.DoEvents();
                Thread.Sleep(120);
                return true;
            }
        }

        CountdownForm()
        {
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(24, 24, 28); Opacity = 0.85;
            Size = new Size(120, 120);
            lbl.Dock = DockStyle.Fill; lbl.ForeColor = Color.White; lbl.TextAlign = ContentAlignment.MiddleCenter;
            lbl.Font = new Font("Segoe UI", 48f, FontStyle.Bold);
            Controls.Add(lbl);
            var mon = Screen.FromPoint(Cursor.Position).Bounds;
            Location = new Point(mon.X + (mon.Width - Width) / 2, mon.Y + (mon.Height - Height) / 2);
        }

        protected override bool ShowWithoutActivation { get { return true; } }
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW; return cp; } }
        protected override void WndProc(ref Message m) { if (m.Msg == Native.WM_DPICHANGED) return; base.WndProc(ref m); }
    }
}
