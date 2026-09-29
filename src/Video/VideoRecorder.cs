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
                if (r.WavPath != null) fmt = "mp4";                    // only MP4 carries sound
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
                        done = Mp4Writer.Convert(r.AviPath, mp4, r.Fps, r.WavPath);           // Windows' own H.264 (+ AAC) encoder
                        if (!done && r.WavPath != null)
                        {
                            done = Mp4Writer.Convert(r.AviPath, mp4, r.Fps);                   // no AAC encoder: keep the picture at least
                            if (done) App.Balloon(Loc.T("The sound could not be added to the video."));
                        }
                        string ff = done ? null : FindFfmpeg();                                // fallback when Windows has none (N editions)
                        if (!done && ff != null) done = RunFfmpeg(ff, r.AviPath, mp4);
                    }
                    if (done) { File.Delete(r.AviPath); path = mp4; }
                    else App.Balloon(Loc.T("MP4 encoding is not available - the video was saved as AVI."));
                }
                var item = LibraryStore.AddVideo(path, r.FirstFrame, (int)Math.Round(r.Seconds));
                App.ShowEditor();
                if (item != null) App.Editor.ShowNewVideo(item);                      // open the new recording, not whatever was open before
                App.Balloon(Loc.T("Video saved to the library"));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                if (r.FirstFrame != null) r.FirstFrame.Dispose();
                if (r.WavPath != null) try { File.Delete(r.WavPath); } catch { }
            }
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
        public string WavPath;          // the recorded sound (48 kHz stereo WAV), null for a silent recording
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

    /// <summary>Rounded, anti-aliased button of the recorder bar: fill colour = BackColor, lighter under the mouse, darker when pressed.
    /// <see cref="Level"/> (0..1) draws a thin level meter along the bottom edge; -1 hides it.</summary>
    class BarButton : Button
    {
        float level = -1f;
        public float Level { get { return level; } set { if (Math.Abs(value - level) > 0.02f || (value < 0) != (level < 0)) { level = value; Invalidate(); } } }
        bool hot, down;

        public BarButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            FlatStyle = FlatStyle.Flat; TabStop = false;
        }


        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }

        static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var back = Parent != null ? Parent.BackColor : Color.Black;
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Color fill = BackColor;
            if (!Enabled) fill = Mix(fill, back, 0.55f);
            else if (down) fill = Mix(fill, Color.Black, 0.18f);
            else if (hot) fill = Mix(fill, Color.White, 0.16f);
            var r = new RectangleF(0.5f, 0.5f, Width - 2, Height - 2);
            using (var p = Theme.RoundRect(Rectangle.Round(r), 10))
            {
                using (var b = new SolidBrush(fill)) g.FillPath(b, p);
                using (var pen = new Pen(Color.FromArgb(hot && Enabled ? 70 : 34, 255, 255, 255))) g.DrawPath(pen, p);      // fine light edge
            }

            string text = Text ?? "";
            Size ts = TextRenderer.MeasureText(g, text, Font, new Size(1000, 100), TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
            int gap = Image != null && text.Length > 0 ? 8 : 0, iw = Image != null ? Image.Width : 0;
            int x = (Width - (iw + gap + ts.Width)) / 2;
            int lift = Level >= 0 ? 2 : 0;                                    // make room for the meter
            if (Image != null)
            {
                if (Enabled) g.DrawImage(Image, x, (Height - Image.Height) / 2 - lift, Image.Width, Image.Height);
                else using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.4f });
                    g.DrawImage(Image, new Rectangle(x, (Height - Image.Height) / 2 - lift, Image.Width, Image.Height), 0, 0, Image.Width, Image.Height, GraphicsUnit.Pixel, ia);
                }
            }
            TextRenderer.DrawText(g, text, Font, new Rectangle(x + iw + gap, -lift, ts.Width + 4, Height), Enabled ? ForeColor : Color.FromArgb(125, 125, 132),
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

            if (Level >= 0)
            {
                var track = new RectangleF(14, Height - 8, Width - 28, 3);
                using (var b = new SolidBrush(Color.FromArgb(70, 255, 255, 255))) g.FillRectangle(b, track);
                float w = Math.Max(0, Math.Min(1f, (float)Math.Sqrt(Level))) * track.Width;                  // sqrt: quiet sounds still show
                if (w > 0) using (var b = new SolidBrush(Level > 0.85f ? Color.FromArgb(255, 190, 60) : Color.FromArgb(96, 214, 120))) g.FillRectangle(b, track.X, track.Y, w, track.Height);
            }
        }
    }

    /// <summary>The clock area of the recorder bar: a status dot and the text (Ready / countdown / mm:ss).</summary>
    class TimeBadge : Control
    {
        Color dot = Color.Empty;
        public Color Dot { get { return dot; } set { if (value != dot) { dot = value; Invalidate(); } } }
        public TimeBadge() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Invalidate(); }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int x = 6;
            if (Dot != Color.Empty)
            {
                using (var b = new SolidBrush(Dot)) g.FillEllipse(b, x, Height / 2f - 5, 10, 10);
                x += 18;
            }
            TextRenderer.DrawText(g, Text ?? "", Font, new Rectangle(x, 0, Width - x, Height), ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
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
        readonly BarButton btnRec = new BarButton(), btnStop = new BarButton(), btnCancel = new BarButton(), btnMic = new BarButton(), btnSpk = new BarButton();
        AudioRecorder audio;
        string wavPath;
        readonly TimeBadge lblTime = new TimeBadge();
        float micShown, spkShown;                 // smoothed input levels for the meters on the Mic / Speaker buttons
        readonly Timer ui = new Timer { Interval = 100 };
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
            BackColor = BarBack;
            Font = new Font("Segoe UI", 9.5f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(806, 56);
            using (var rp = Theme.RoundRect(new Rectangle(0, 0, Width, Height), 14)) Region = new Region(rp);      // rounded corners

            // record group | separator | sound group | clock
            btnRec.SetBounds(8, 8, 116, 40); btnStop.SetBounds(128, 8, 150, 40); btnCancel.SetBounds(282, 8, 114, 40);
            btnMic.SetBounds(412, 8, 90, 40); btnSpk.SetBounds(506, 8, 120, 40);
            Setup(btnRec, "record", Loc.T("Record"), Loc.T("Record / Pause"), Color.FromArgb(196, 46, 46));
            Setup(btnStop, "stop", Loc.T("Stop & save"), Loc.T("Stop and save"), Color.FromArgb(58, 58, 64));
            Setup(btnCancel, "close", Loc.T("Discard"), Loc.T("Discard"), Color.FromArgb(58, 58, 64));
            Setup(btnMic, "mic", Loc.T("Mic"), Loc.T("Record the microphone (before you press Record)"), Color.FromArgb(58, 58, 64));
            Setup(btnSpk, "speaker", Loc.T("Speaker"), Loc.T("Record the computer sound (before you press Record)"), Color.FromArgb(58, 58, 64));
            btnMic.Click += (s, e) => { if (started) { if (audio != null && audio.MicOk) audio.MicMuted = !audio.MicMuted; } else { cfg.RecordMic = !cfg.RecordMic; cfg.Save(); } ShowToggles(); };
            btnSpk.Click += (s, e) => { if (started) { if (audio != null && audio.SystemOk) audio.SystemMuted = !audio.SystemMuted; } else { cfg.RecordSystemSound = !cfg.RecordSystemSound; cfg.Save(); } ShowToggles(); };
            ShowToggles();
            btnStop.Enabled = false;
            btnRec.Click += (s, e) => RecClicked();
            btnStop.Click += (s, e) => StopClicked();
            btnCancel.Click += (s, e) => { cancelled = true; StopWorker(); Close(); };
            lblTime.SetBounds(644, 0, 156, 56); ShowReady();
            lblTime.MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } };   // drag the bar by its label
            lblTime.Cursor = Cursors.SizeAll;
            MouseDown += (s, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); } };      // or by any empty part of the bar
            Controls.Add(btnRec); Controls.Add(btnStop); Controls.Add(btnCancel); Controls.Add(btnMic); Controls.Add(btnSpk); Controls.Add(lblTime);

            frame = new FrameForm(region, 3);
            Location = BarLocation(region, Size, Screen.FromRectangle(region).WorkingArea);
            ui.Tick += (s, e) => UpdateTime();
            cd.Tick += (s, e) => CountdownTick();
        }

        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>Where the control bar sits: under the region, else above it, else inside the region at the bottom. It always
        /// stays inside the working area, so the taskbar (also topmost) can never cover it, e.g. when recording the full screen.</summary>
        public static Point BarLocation(Rectangle region, Size size, Rectangle workArea)
        {
            int x = region.X + (region.Width - size.Width) / 2, y = region.Bottom + 12;
            if (y + size.Height > workArea.Bottom) y = region.Top - size.Height - 12;
            if (y < workArea.Top) y = region.Bottom - size.Height - 12;      // hidden from the recording by display affinity
            y = Math.Max(workArea.Top, Math.Min(y, workArea.Bottom - size.Height));
            x = Math.Max(workArea.Left, Math.Min(x, workArea.Right - size.Width));
            return new Point(x, y);
        }

        static readonly Font FontReady = new Font("Segoe UI", 10f), FontCount = new Font("Segoe UI Semibold", 20f), FontClock = new Font("Segoe UI Semibold", 15f);
        static readonly Color BarBack = Color.FromArgb(30, 30, 34), BarLine = Color.FromArgb(74, 74, 82), Neutral = Color.FromArgb(58, 58, 64);

        void Setup(BarButton b, string icon, string text, string tip, Color fill)
        {
            b.BackColor = fill; b.ForeColor = Color.White;
            b.Font = new Font("Segoe UI Semibold", 10f);
            b.Image = Icons.Get(icon == "record" ? "record_w" : icon, 22, true); b.Text = Up(text);
            b.Cursor = Cursors.Hand; b.UseMnemonic = false;   // "Stop & save" must not turn & into a shortcut key
            new ToolTip().SetToolTip(b, tip);
        }

        /// <summary>The Record button turns into Pause (grey) while recording and back into Resume (red) when paused.</summary>
        void SetRec(string icon, string text)
        {
            btnRec.Image = Icons.Get(icon == "record" ? "record_w" : icon, 22, true); btnRec.Text = Up(text);
            btnRec.BackColor = icon == "pause" ? Neutral : Color.FromArgb(196, 46, 46);
        }

        void ShowReady()
        {
            lblTime.Dot = Color.FromArgb(120, 120, 128);
            lblTime.Font = FontReady; lblTime.ForeColor = Color.FromArgb(200, 200, 206);
            lblTime.Text = Loc.T("Ready") + "  " + region.Width + "×" + region.Height;
        }

        void ShowCount()
        {
            lblTime.Dot = Color.Empty;
            lblTime.Font = FontCount; lblTime.ForeColor = Color.White;
            lblTime.Text = countdown.ToString();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Theme.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 14))
            using (var pen = new Pen(BarLine)) g.DrawPath(pen, p);
            using (var pen = new Pen(Color.FromArgb(70, 70, 78)))
            {
                g.DrawLine(pen, 404, 12, 404, Height - 13);                    // record group | sound group
                g.DrawLine(pen, 636, 12, 636, Height - 13);                    // sound group | clock
            }
        }

        static string Up(string s) { return s == null ? "" : s.ToUpper(); }        // button captions are upper case

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
                ShowCount();
                cd.Start();
                return;
            }
            paused = !paused;
            if (paused) { if (audio != null) audio.Paused = true; sw.Stop(); SetRec("record", Loc.T("Resume")); frame.Recording = false; }
            else { sw.Start(); if (audio != null) audio.Paused = false; SetRec("pause", Loc.T("Pause")); frame.Recording = true; }
            frame.Invalidate();
        }

        /// <summary>Mic / Speaker buttons. Before recording they switch the source on or off; while recording they mute / un-mute the
        /// sources that were opened (a source that was off at the start cannot be added any more).</summary>
        void ShowToggles()
        {
            bool micOn = started ? (audio != null && audio.MicOk && !audio.MicMuted) : cfg.RecordMic;
            bool spkOn = started ? (audio != null && audio.SystemOk && !audio.SystemMuted) : cfg.RecordSystemSound;
            Toggle(btnMic, "mic", micOn, started && !(audio != null && audio.MicOk));
            Toggle(btnSpk, "speaker", spkOn, started && !(audio != null && audio.SystemOk));
        }

        static void Toggle(BarButton b, string icon, bool on, bool unavailable)
        {
            b.Image = Icons.Get(on ? icon : icon + "_off", 22, true);
            b.BackColor = on ? Color.FromArgb(36, 112, 196) : Neutral;
            b.ForeColor = on ? Color.White : Color.FromArgb(175, 175, 180);
            b.Enabled = !unavailable;
            if (!on || unavailable) b.Level = -1;
        }

        void StartAudio()
        {
            wavPath = Path.Combine(Path.GetTempPath(), "rxcapture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".wav");
            audio = new AudioRecorder();
            bool ok = audio.Start(wavPath, cfg.RecordMic, cfg.RecordSystemSound, () => sw.ElapsedMilliseconds);
            if (!string.IsNullOrEmpty(audio.Problem)) App.Balloon(Loc.T("Sound: ") + audio.Problem);
            if (!ok)
            {
                audio.Dispose(); audio = null;
                try { File.Delete(wavPath); } catch { }
                wavPath = null;
            }
        }

        void CountdownTick()
        {
            countdown--;
            if (countdown > 0) { ShowCount(); return; }
            cd.Stop();
            StartRecording();
        }

        void StartRecording()
        {
            started = true;
            btnRec.Enabled = true; btnStop.Enabled = true;
            SetRec("pause", Loc.T("Pause"));
            frame.Recording = true; frame.Invalidate();
            aviPath = Path.Combine(Path.GetTempPath(), "rxcapture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".avi");
            if (cfg.RecordMic || cfg.RecordSystemSound) StartAudio();      // before the clock starts: opening the devices takes a moment
            ShowToggles();
            sw.Start();
            worker = new Thread(CaptureLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            worker.Start();
            ui.Start();
        }

        void UpdateTime()
        {
            var t = sw.Elapsed;
            lblTime.Font = FontClock; lblTime.ForeColor = Color.White;
            lblTime.Dot = paused ? Color.FromArgb(240, 170, 40) : ((int)(t.TotalSeconds * 2) % 2 == 0 ? Color.FromArgb(232, 48, 48) : Color.FromArgb(120, 40, 44));   // blinks while recording
            lblTime.Text = string.Format("{0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            // level meters on the Mic / Speaker buttons (fall off smoothly)
            if (audio != null && audio.MicOk && !audio.MicMuted && !paused) { micShown = Math.Max(audio.MicLevel, micShown * 0.7f); btnMic.Level = micShown; } else btnMic.Level = -1;
            if (audio != null && audio.SystemOk && !audio.SystemMuted && !paused) { spkShown = Math.Max(audio.SystemLevel, spkShown * 0.7f); btnSpk.Level = spkShown; } else btnSpk.Level = -1;
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
            if (audio != null) audio.Stop();                       // writes the last of the sound up to the stopped clock, closes the WAV
        }

        public void StopClicked()
        {
            if (!started) { cancelled = true; Close(); return; }
            StopWorker();
            ui.Stop();
            if (!cancelled && File.Exists(aviPath) && framesWritten > 0)
            {
                Result = new RecordingResult { AviPath = aviPath, FirstFrame = first, Seconds = Math.Max(1, sw.Elapsed.TotalSeconds), Fps = Math.Max(5, Math.Min(30, cfg.VideoFps)) };
                if (audio != null && audio.FramesWritten > 0 && wavPath != null && File.Exists(wavPath)) Result.WavPath = wavPath;
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
            if (audio != null) { try { audio.Dispose(); } catch { } }
            if (Result == null || Result.WavPath == null)
            {
                try { if (wavPath != null && File.Exists(wavPath)) File.Delete(wavPath); } catch { }
            }
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
