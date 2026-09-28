using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Timer = System.Windows.Forms.Timer;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>Screen video recording: MJPEG AVI, optionally converted to GIF (built in) or MP4 (H.264).</summary>
    public static class VideoRecorder
    {
        public static bool IsActive;
        static RecorderForm current;

        public static void RequestStop()
        {
            var f = current;
            if (f != null && !f.IsDisposed) f.BeginInvoke((Action)f.StopClicked);
        }

        public static void Run(Rectangle region)
        {
            if (IsActive) return;
            IsActive = true;
            try
            {
                // even dimensions keep every decoder happy
                region.Width &= ~1; region.Height &= ~1;
                if (region.Width < 16 || region.Height < 16) return;
                using (var f = new RecorderForm(region))
                {
                    current = f;
                    f.ShowDialog();
                    current = null;
                    if (f.Result != null) Finish(f.Result);
                }
            }
            finally { IsActive = false; current = null; }
        }

        static void Finish(RecordingResult r)
        {
            var cfg = AppSettings.Current;
            string path = r.AviPath;
            try
            {
                string fmt = cfg.VideoFormat;
                if (fmt == "gif")
                {
                    string gif = Path.ChangeExtension(r.AviPath, ".gif");
                    using (var wait = new BusyForm(Loc.T("Creating GIF…"))) { wait.Show(); Application.DoEvents(); AviToGif(r.AviPath, gif, cfg.GifMaxWidth, r.Fps); }
                    File.Delete(r.AviPath); path = gif;
                }
                else if (fmt == "mp4")
                {
                    string mp4 = Path.ChangeExtension(r.AviPath, ".mp4");
                    bool done;
                    using (var wait = new BusyForm(Loc.T("Converting to MP4…")))
                    {
                        wait.Show(); Application.DoEvents();
                        done = Mp4Writer.Convert(r.AviPath, mp4, r.Fps);                       // Windows' own H.264 encoder
                        string ff = done ? null : FindFfmpeg();                                // fallback when Windows has none (N editions)
                        if (!done && ff != null) done = RunFfmpeg(ff, r.AviPath, mp4);
                    }
                    if (done) { File.Delete(r.AviPath); path = mp4; }
                    else App.Balloon(Loc.T("MP4 encoding is not available - the video was saved as AVI."));
                }
                var item = LibraryStore.AddVideo(path, r.FirstFrame, (int)Math.Round(r.Seconds));
                App.ShowEditor();
                App.Balloon(Loc.T("Video saved to the library"));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { if (r.FirstFrame != null) r.FirstFrame.Dispose(); }
        }

        public static string FindFfmpeg()
        {
            string local = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
            if (File.Exists(local)) return local;
            foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
                try { var p = Path.Combine(dir.Trim(), "ffmpeg.exe"); if (File.Exists(p)) return p; } catch { }
            return null;
        }

        static bool RunFfmpeg(string ff, string input, string output)
        {
            try
            {
                var psi = new ProcessStartInfo(ff, "-y -i \"" + input + "\" -c:v libx264 -pix_fmt yuv420p -crf 23 -movflags +faststart \"" + output + "\"")
                { CreateNoWindow = true, UseShellExecute = false, RedirectStandardError = true };
                using (var p = Process.Start(psi))
                {
                    p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    return p.ExitCode == 0 && File.Exists(output);
                }
            }
            catch { return false; }
        }

        public static void AviToGif(string avi, string gif, int maxWidth, int fps)
        {
            var codec = FindJpeg();
            using (var fs = File.Create(gif))
            {
                GifWriter gw = null;
                int delay = Math.Max(2, (int)Math.Round(100.0 / Math.Max(1, fps)));
                int frame = 0;
                foreach (var jpg in AviReader.Frames(avi))
                {
                    using (var ms = new MemoryStream(jpg))
                    using (var img = Image.FromStream(ms))
                    {
                        int w = img.Width, h = img.Height;
                        if (maxWidth > 0 && w > maxWidth) { h = (int)((long)h * maxWidth / w); w = maxWidth; }
                        using (var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                        {
                            using (var g = Graphics.FromImage(bmp)) { g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.DrawImage(img, 0, 0, w, h); }
                            if (gw == null) gw = new GifWriter(fs, w, h);
                            gw.AddFrame(bmp, delay);
                        }
                    }
                    if ((++frame & 7) == 0) Application.DoEvents();
                }
                if (gw != null) gw.Close();
            }
        }

        public static ImageCodecInfo FindJpeg()
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
            return null;
        }
    }

    public class RecordingResult
    {
        public string AviPath;
        public Bitmap FirstFrame;
        public double Seconds;
        public int Fps;
    }

    class BusyForm : Form
    {
        public BusyForm(string text)
        {
            FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; TopMost = true; ShowInTaskbar = false;
            BackColor = Theme.Ribbon; Size = new Size(300, 70);
            var l = new Label { Text = text, ForeColor = Theme.Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI", 11f) };
            Controls.Add(l);
        }
    }

    /// <summary>Click-through red frame drawn around the recorded area.</summary>
    class FrameForm : Form
    {
        readonly int border;
        public bool Recording;

        public FrameForm(Rectangle region, int border)
        {
            this.border = border;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Magenta; TransparencyKey = Color.Magenta;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
            Bounds = new Rectangle(region.X - border, region.Y - border, region.Width + 2 * border, region.Height + 2 * border);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TRANSPARENT | Native.WS_EX_LAYERED; return cp; }
        }

        protected override void WndProc(ref Message m) { if (m.Msg == Native.WM_DPICHANGED) return; base.WndProc(ref m); }

        protected override void OnPaint(PaintEventArgs e)
        {
            using (var p = new Pen(Recording ? Color.FromArgb(232, 30, 30) : Color.FromArgb(0, 168, 255), border) { DashStyle = Recording ? DashStyle.Solid : DashStyle.Dash })
                e.Graphics.DrawRectangle(p, border / 2f, border / 2f, Width - border, Height - border);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RecorderForm.ExcludeFromCapture(Handle);
        }
    }

    /// <summary>Toolbar + capture loop for one recording session.</summary>
    class RecorderForm : Form
    {
        [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

        public static void ExcludeFromCapture(IntPtr h) { try { SetWindowDisplayAffinity(h, 0x11); } catch { } }

        public RecordingResult Result;

        readonly Rectangle region;
        readonly AppSettings cfg = AppSettings.Current;
        readonly FrameForm frame;
        readonly Button btnRec = new Button(), btnStop = new Button(), btnCancel = new Button();
        readonly Label lblTime = new Label();
        readonly Timer ui = new Timer { Interval = 200 };
        readonly Stopwatch sw = new Stopwatch();

        Thread worker;
        volatile bool stopFlag, paused, cancelled;
        bool started;
        string aviPath;
        Bitmap first;
        long framesWritten;
        int countdown;
        readonly Timer cd = new Timer { Interval = 1000 };

        public RecorderForm(Rectangle region)
        {
            this.region = region;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(37, 37, 40);
            Font = new Font("Segoe UI", 9.5f);
            Size = new Size(330, 46);

            btnRec.SetBounds(6, 6, 34, 34); btnStop.SetBounds(44, 6, 34, 34); btnCancel.SetBounds(82, 6, 34, 34);
            Setup(btnRec, "record", Loc.T("Record / Pause")); Setup(btnStop, "stop", Loc.T("Stop and save")); Setup(btnCancel, "close", Loc.T("Discard"));
            btnStop.Enabled = false;
            btnRec.Click += (s, e) => RecClicked();
            btnStop.Click += (s, e) => StopClicked();
            btnCancel.Click += (s, e) => { cancelled = true; StopWorker(); Close(); };
            lblTime.SetBounds(124, 0, 200, 46); lblTime.ForeColor = Color.White; lblTime.TextAlign = ContentAlignment.MiddleLeft;
            lblTime.Font = new Font("Segoe UI", 11f, FontStyle.Bold);
            lblTime.Text = Loc.T("Ready") + "  " + region.Width + "×" + region.Height;
            Controls.Add(btnRec); Controls.Add(btnStop); Controls.Add(btnCancel); Controls.Add(lblTime);

            frame = new FrameForm(region, 3);
            var mon = Screen.FromRectangle(region).Bounds;
            int x = region.X + (region.Width - Width) / 2, y = region.Bottom + 12;
            if (y + Height > mon.Bottom) y = region.Top - Height - 12;
            if (y < mon.Top) y = region.Bottom - Height - 12;      // hidden from the recording by display affinity
            x = Math.Max(mon.Left, Math.Min(x, mon.Right - Width));
            Location = new Point(x, y);
            ui.Tick += (s, e) => UpdateTime();
            cd.Tick += (s, e) => CountdownTick();
        }

        void Setup(Button b, string icon, string tip)
        {
            b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0; b.BackColor = Color.FromArgb(60, 60, 64);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(84, 84, 90);
            b.Image = Icons.Get(icon, 22, true);
            new ToolTip().SetToolTip(b, tip);
        }

        protected override bool ShowWithoutActivation { get { return false; } }
        protected override void WndProc(ref Message m) { if (m.Msg == Native.WM_DPICHANGED) return; base.WndProc(ref m); }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            ExcludeFromCapture(Handle);
            frame.Show();
            Native.SetWindowPos(frame.Handle, Native.HWND_TOPMOST, region.X - 3, region.Y - 3, region.Width + 6, region.Height + 6, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, Left, Top, Width, Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
        }

        internal void RecClicked()
        {
            if (!started)
            {
                // 3-2-1 countdown then start
                countdown = 3; btnRec.Enabled = false; btnCancel.Enabled = true;
                lblTime.Text = countdown.ToString();
                cd.Start();
                return;
            }
            paused = !paused;
            if (paused) { sw.Stop(); btnRec.Image = Icons.Get("record", 22, true); frame.Recording = false; }
            else { sw.Start(); btnRec.Image = Icons.Get("pause", 22, true); frame.Recording = true; }
            frame.Invalidate();
        }

        void CountdownTick()
        {
            countdown--;
            if (countdown > 0) { lblTime.Text = countdown.ToString(); return; }
            cd.Stop();
            StartRecording();
        }

        void StartRecording()
        {
            started = true;
            btnRec.Enabled = true; btnStop.Enabled = true;
            btnRec.Image = Icons.Get("pause", 22, true);
            frame.Recording = true; frame.Invalidate();
            aviPath = Path.Combine(Path.GetTempPath(), "rxcapture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".avi");
            sw.Start();
            worker = new Thread(CaptureLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            worker.Start();
            ui.Start();
        }

        void UpdateTime()
        {
            var t = sw.Elapsed;
            lblTime.Text = (paused ? "❚❚ " : "● ") + string.Format("{0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        void CaptureLoop()
        {
            int fps = Math.Max(5, Math.Min(30, cfg.VideoFps));
            var codec = VideoRecorder.FindJpeg();
            AviWriter writer = null;
            try
            {
                writer = new AviWriter(aviPath, region.Width, region.Height, fps);
                using (var ep = new EncoderParameters(1))
                using (var bmp = new Bitmap(region.Width, region.Height, PixelFormat.Format32bppArgb))
                using (var g = Graphics.FromImage(bmp))
                using (var ms = new MemoryStream())
                {
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 82L);
                    byte[] cached = null;
                    long written = 0;
                    while (!stopFlag)
                    {
                        if (paused) { Thread.Sleep(20); continue; }
                        long due = sw.ElapsedMilliseconds * fps / 1000 + 1;
                        if (written >= due) { Thread.Sleep(1); continue; }
                        Native.CopyScreen(g, region);
                        if (cfg.VideoCursor) CursorSnap.Take().DrawOn(bmp, region.Location);
                        if (first == null) first = new Bitmap(bmp);
                        ms.SetLength(0);
                        bmp.Save(ms, codec, ep);
                        cached = ms.ToArray();
                        int dup = 0;
                        while (written < due && dup < 12) { writer.AddFrame(cached); written++; dup++; }
                        if (written < due) written = due;   // hopelessly behind: skip
                    }
                    framesWritten = written;
                }
            }
            catch { }
            finally { if (writer != null) writer.Close(); }
        }

        void StopWorker()
        {
            stopFlag = true; sw.Stop();
            if (worker != null) worker.Join(5000);
        }

        public void StopClicked()
        {
            if (!started) { cancelled = true; Close(); return; }
            StopWorker();
            ui.Stop();
            if (!cancelled && File.Exists(aviPath) && framesWritten > 0)
            {
                Result = new RecordingResult { AviPath = aviPath, FirstFrame = first, Seconds = Math.Max(1, sw.Elapsed.TotalSeconds), Fps = Math.Max(5, Math.Min(30, cfg.VideoFps)) };
                first = null;
            }
            Close();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            cd.Stop(); ui.Stop();
            stopFlag = true;
            frame.Close();
            if (Result == null)
            {
                try { if (worker != null) worker.Join(3000); if (aviPath != null && File.Exists(aviPath)) File.Delete(aviPath); } catch { }
                if (first != null) first.Dispose();
            }
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape) { cancelled = true; StopWorker(); Close(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }
}
