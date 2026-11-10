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

        /// <summary>How long the last <see cref="Finish"/> took (stop clicked -> the recording is in the library and the editor shows it).</summary>
        internal static long LastFinishMs;

        /// <summary>The recording is kept as it was written (an AVI, plus its WAV for sound) and opened in the editor at once. Nothing is converted here:
        /// the MP4 / GIF is made when the user saves it (<see cref="VideoExport"/>).</summary>
        static void Finish(RecordingResult r)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var item = LibraryStore.AddVideo(r.Mp4Path ?? r.AviPath, r.FirstFrame, (int)Math.Round(r.Seconds), r.WavPath);
                App.ShowEditor();
                if (item != null) App.Editor.ShowNewVideo(item);                      // open the new recording, not whatever was open before
                App.Balloon(Loc.T(r.Mp4Path != null ? "Recording is in the library" : "Recording is in the library - Save it to make an MP4"));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally
            {
                if (r.FirstFrame != null) r.FirstFrame.Dispose();
                if (r.WavPath != null) try { File.Delete(r.WavPath); } catch { }      // already moved with the video; only a failed add leaves it here
                LastFinishMs = clock.ElapsedMilliseconds;
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

        internal static bool RunFfmpeg(string ff, string input, string output)
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
        public string Mp4Path;          // recorded straight to MP4 (sound included); null when the old AVI working-copy path was used
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
            var l = new Label { Text = text, ForeColor = Theme.Text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Roboto", 12f) };
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

    /// <summary>Icon button of the recorder bar, drawn as a circle (Round) or a rounded square: fill = BackColor, lighter under the mouse,
    /// darker when pressed, a soft ring on hover. <see cref="Level"/> (0..1) draws a thin level meter along the bottom; -1 hides it.</summary>
    class BarButton : Button
    {
        float level = -1f;
        public float Level { get { return level; } set { if (Math.Abs(value - level) > 0.02f || (value < 0) != (level < 0)) { level = value; Invalidate(); } } }
        public bool Round;
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

        internal static Color Mix(Color a, Color b, float t)
        {
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        internal static GraphicsPath Pill(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var back = Parent != null ? Parent.BackColor : Color.Black;
            g.Clear(back);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            Color fill = BackColor;
            if (!Enabled) fill = Mix(fill, back, 0.6f);
            else if (down) fill = Mix(fill, Color.Black, 0.2f);
            else if (hot) fill = Mix(fill, Color.White, 0.14f);
            var r = new RectangleF(1, 1, Width - 2, Height - 2);
            using (var b = new SolidBrush(fill))
            {
                if (Round) g.FillEllipse(b, r);
                else using (var path = Pill(r, 10)) g.FillPath(b, path);
            }
            if (hot && Enabled)
                using (var p = new Pen(Color.FromArgb(70, 255, 255, 255), 1f))
                {
                    if (Round) g.DrawEllipse(p, r);
                    else using (var path = Pill(r, 10)) g.DrawPath(p, path);
                }

            int lift = Level >= 0 ? 3 : 0;                                    // make room for the meter
            if (Image != null)
            {
                var dst = new Rectangle((Width - Image.Width) / 2, (Height - Image.Height) / 2 - lift, Image.Width, Image.Height);
                if (Enabled) g.DrawImage(Image, dst);
                else using (var ia = new System.Drawing.Imaging.ImageAttributes())
                {
                    ia.SetColorMatrix(new System.Drawing.Imaging.ColorMatrix { Matrix33 = 0.4f });
                    g.DrawImage(Image, dst, 0, 0, Image.Width, Image.Height, GraphicsUnit.Pixel, ia);
                }
            }

            if (Level >= 0)
            {
                var track = new RectangleF(10, Height - 8, Width - 20, 3);
                using (var b = new SolidBrush(Color.FromArgb(60, 255, 255, 255))) g.FillRectangle(b, track);
                float w = Math.Max(0, Math.Min(1f, (float)Math.Sqrt(Level))) * track.Width;                  // sqrt: quiet sounds still show
                if (w > 0) using (var b = new SolidBrush(Level > 0.85f ? Color.FromArgb(255, 190, 60) : Color.FromArgb(96, 214, 120))) g.FillRectangle(b, track.X, track.Y, w, track.Height);
            }
        }
    }

    /// <summary>The clock of the recorder bar: the time in large figures, and under it a status dot with a caption (REC / PAUSED / READY + size).</summary>
    class TimeBadge : Control
    {
        Color dot = Color.Empty;
        string caption = "";
        public Color Dot { get { return dot; } set { if (value != dot) { dot = value; Invalidate(); } } }
        public string Caption { get { return caption; } set { value = value ?? ""; if (value != caption) { caption = value; Invalidate(); } } }
        public TimeBadge() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
        protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
        protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Invalidate(); }
        protected override void OnForeColorChanged(EventArgs e) { base.OnForeColorChanged(e); Invalidate(); }

        static readonly Font CapFont = new Font("Roboto Medium", 8f);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Color.Black);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            const TextFormatFlags F = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            bool two = caption.Length > 0 || Dot != Color.Empty;
            TextRenderer.DrawText(g, Text ?? "", Font, new Rectangle(4, two ? 6 : 0, Width - 4, two ? 28 : Height), ForeColor, F);
            if (!two) return;
            int x = 6, cy = Height - 14;
            if (Dot != Color.Empty)
            {
                using (var b = new SolidBrush(Dot)) g.FillEllipse(b, x, cy - 3.5f, 7, 7);
                x += 13;
            }
            TextRenderer.DrawText(g, caption, CapFont, new Rectangle(x, cy - 8, Width - x, 16), Color.FromArgb(150, 152, 160), F);
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
        readonly ToolTip tips = new ToolTip();
        float micShown, spkShown;                 // smoothed input levels for the meters on the Mic / Speaker buttons
        readonly Timer ui = new Timer { Interval = 100 };
        readonly Stopwatch sw = new Stopwatch();

        Thread worker;
        volatile bool stopFlag, paused, cancelled;
        bool started;
        string aviPath;
        Bitmap first;
        long framesWritten;

        // recording straight to MP4: the capture thread (worker) fills frames, this thread hands them to the encoder, so a slow encoder never stalls the capture
        volatile Mp4Writer.Live live;
        string mp4Path;
        Thread encoder;
        readonly ManualResetEvent audioDone = new ManualResetEvent(false);
        System.Collections.Concurrent.BlockingCollection<KeyValuePair<Mp4Writer.Frame, long>> queue;
        volatile bool liveFailed;
        volatile bool liveDone;
        bool liveUsed;
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint ms);
        int Fps { get { return Math.Max(5, Math.Min(30, cfg.VideoFps)); } }
        int countdown;
        readonly Timer cd = new Timer { Interval = 1000 };

        public RecorderForm(Rectangle region)
        {
            this.region = region;
            FormBorderStyle = FormBorderStyle.None; ShowInTaskbar = false; TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = BarBack;
            Font = new Font("Roboto", 10.25f);
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            Size = new Size(386, 56);

            // Floating pill: round Record/Pause + Stop | big clock with status | Speaker + Mic | small Discard (thin dividers between the groups)
            btnRec.SetBounds(8, 8, 40, 40); btnStop.SetBounds(54, 8, 40, 40);
            btnRec.Round = true; btnStop.Round = true; btnCancel.Round = true;
            btnSpk.SetBounds(246, 8, 40, 40); btnMic.SetBounds(292, 8, 40, 40);
            btnCancel.SetBounds(350, 14, 28, 28);
            Setup(btnRec, "record", Loc.T("Record"), Loc.T("Record / Pause"), RecRed);
            Setup(btnStop, "stop", Loc.T("Stop & save"), Loc.T("Stop and save"), Neutral);
            Setup(btnCancel, "close", Loc.T("Discard"), Loc.T("Discard"), BarBack);
            btnCancel.Image = Icons.Get("close", 16, true);
            Setup(btnMic, "mic", Loc.T("Mic"), Loc.T("Record the microphone (before you press Record)"), Neutral);
            Setup(btnSpk, "speaker", Loc.T("Speaker"), Loc.T("Record the computer sound (before you press Record)"), Neutral);
            btnMic.Click += (s, e) => { if (started) { if (audio != null && audio.MicOk) audio.MicMuted = !audio.MicMuted; } else { cfg.RecordMic = !cfg.RecordMic; cfg.Save(); } ShowToggles(); };
            btnSpk.Click += (s, e) => { if (started) { if (audio != null && audio.SystemOk) audio.SystemMuted = !audio.SystemMuted; } else { cfg.RecordSystemSound = !cfg.RecordSystemSound; cfg.Save(); } ShowToggles(); };
            ShowToggles();
            btnStop.Enabled = false;
            btnRec.Click += (s, e) => RecClicked();
            btnStop.Click += (s, e) => StopClicked();
            btnCancel.Click += (s, e) => { cancelled = true; StopWorker(); Close(); };
            lblTime.SetBounds(110, 0, 124, 56); ShowReady();
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

        static readonly Font FontCount = new Font("Roboto Medium", 23.75f), FontClock = new Font("Roboto Medium", 18.25f);
        static readonly Color BarBack = Color.FromArgb(30, 31, 35), Neutral = Color.FromArgb(52, 54, 60), Dim = Color.FromArgb(40, 41, 46), RecRed = Color.FromArgb(226, 52, 52);

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BarBack);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Color.FromArgb(66, 68, 76), 1f)) foreach (int x in new[] { 102, 238, 340 }) g.DrawLine(p, x, 14, x, Height - 14);
            if (!dwmRound) using (var p = new Pen(Color.FromArgb(70, 72, 80), 2f)) using (var path = BarButton.Pill(new RectangleF(0, 0, Width, Height), 16)) g.DrawPath(p, path);   // the region clips the outer half
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        bool dwmRound;

        /// <summary>Windows 11 rounds and outlines the borderless bar itself (smooth edges). Older Windows: a clipping region, which has hard edges.</summary>
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2, border = 80 << 16 | 72 << 8 | 70;    // DWMWCP_ROUND, border colour (BGR)
            try { dwmRound = DwmSetWindowAttribute(Handle, 33, ref round, 4) == 0; if (dwmRound) DwmSetWindowAttribute(Handle, 34, ref border, 4); } catch { dwmRound = false; }
            if (!dwmRound) Region = new Region(BarButton.Pill(new RectangleF(0, 0, Width, Height), 16));
        }

        void Setup(BarButton b, string icon, string text, string tip, Color fill)
        {
            b.BackColor = fill; b.ForeColor = Color.White;
            b.Font = new Font("Roboto Medium", 10.75f);
            b.Image = Icons.Get(icon == "record" ? "record_w" : icon, 24, true); b.Text = "";      // icon only: the tooltip names it
            b.Cursor = Cursors.Hand; b.UseMnemonic = false;   // "Stop & save" must not turn & into a shortcut key
            tips.SetToolTip(b, tip);
        }

        /// <summary>The Record button turns into Pause (grey) while recording and back into Resume (red) when paused.</summary>
        void SetRec(string icon, string text)
        {
            btnRec.Image = Icons.Get(icon == "record" ? "record_w" : icon, 24, true);
            btnRec.BackColor = icon == "pause" ? Neutral : RecRed;
            tips.SetToolTip(btnRec, text);
        }

        void ShowReady()
        {
            lblTime.Dot = Color.FromArgb(120, 122, 130);
            lblTime.Font = FontClock; lblTime.ForeColor = Color.FromArgb(130, 132, 140);
            lblTime.Text = "00:00";
            lblTime.Caption = Loc.T("Ready").ToUpper() + "  " + region.Width + "×" + region.Height;
        }

        void ShowCount()
        {
            lblTime.Dot = Color.Empty; lblTime.Caption = "";
            lblTime.Font = FontCount; lblTime.ForeColor = Color.White;
            lblTime.Text = countdown.ToString();
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
            b.Image = Icons.Get(on ? icon : icon + "_off", 24, true);      // on / off is the icon itself (struck through when off)
            b.BackColor = on && !unavailable ? Neutral : Dim;
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
            OpenLive();
            sw.Start();
            worker = new Thread(live != null ? (ThreadStart)LiveLoop : CaptureLoop) { IsBackground = true, Priority = ThreadPriority.AboveNormal };
            worker.Start();
            ui.Start();
        }

        /// <summary>Opens the MP4 for a real-time recording. Stays null (the AVI working-copy path is used) when Windows has no H.264 encoder.</summary>
        void OpenLive()
        {
            mp4Path = Path.Combine(Path.GetTempPath(), "rxcapture_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".mp4");
            Mp4Writer.Encoder.Quality = AppSettings.QualityFor(cfg.VideoQuality);
            queue = new System.Collections.Concurrent.BlockingCollection<KeyValuePair<Mp4Writer.Frame, long>>(6);
            // Media Foundation's writer cannot be called from another apartment than the one that made it: the encoder thread (MTA) opens it,
            // writes to it and closes it, and nothing else touches it.
            var opened = new ManualResetEvent(false);
            encoder = new Thread(() => EncodeLoop(opened)) { IsBackground = true, Name = "RXCapture encoder" };
            encoder.SetApartmentState(ApartmentState.MTA);
            encoder.Start();
            opened.WaitOne(15000);
            liveUsed = live != null;
            if (!liveUsed)
            {
                if (encoder != null) { encoder.Join(2000); encoder = null; }
                queue.Dispose(); queue = null;
                try { if (File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
                mp4Path = null;
            }
        }

        void UpdateTime()
        {
            var t = sw.Elapsed;
            lblTime.Font = FontClock; lblTime.ForeColor = Color.White;
            lblTime.Dot = paused ? Color.FromArgb(240, 170, 40) : ((int)(t.TotalSeconds * 2) % 2 == 0 ? Color.FromArgb(232, 48, 48) : Color.FromArgb(120, 40, 44));   // blinks while recording
            lblTime.Text = string.Format("{0:00}:{1:00}", (int)t.TotalMinutes, t.Seconds);
            lblTime.Caption = (paused ? Loc.T("Paused") : "REC").ToUpper() + "  " + region.Width + "×" + region.Height;
            // level meters on the Mic / Speaker buttons (fall off smoothly)
            if (audio != null && audio.MicOk && !audio.MicMuted && !paused) { micShown = Math.Max(audio.MicLevel, micShown * 0.7f); btnMic.Level = micShown; } else btnMic.Level = -1;
            if (audio != null && audio.SystemOk && !audio.SystemMuted && !paused) { spkShown = Math.Max(audio.SystemLevel, spkShown * 0.7f); btnSpk.Level = spkShown; } else btnSpk.Level = -1;
        }

        internal const long FrameJpegQuality = 95;

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
                    ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, FrameJpegQuality);   // the MP4 is made from these frames, so they must stay clean (text edges)
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

        /// <summary>Capture thread of the real-time MP4 path. One picture per tick of the recording clock: DXGI (GPU) when available, GDI otherwise.
        /// A tick that is missed leaves a gap in the time stamps (the previous picture stays on screen) instead of piling up work.</summary>
        void LiveLoop()
        {
            int fps = Fps, w = region.Width, h = region.Height;
            DxgiCapture cap = null;
            try { timeBeginPeriod(1); } catch { }                  // Sleep(1) really sleeps 1 ms
            try
            {
                cap = DxgiCapture.TryCreate(region);
                long lastK = -1, count = 0, lastWrite = 0;
                Point lastCur = Point.Empty;
                while (!stopFlag && !liveFailed)
                {
                    if (paused) { Thread.Sleep(20); continue; }
                    long k = sw.ElapsedMilliseconds * fps / 1000;
                    if (k <= lastK) { Thread.Sleep(1); continue; }
                    bool changed = true, dxgi = false;
                    if (cap != null)
                    {
                        if (cap.Update(0, out changed)) dxgi = true;
                        else if (cap.Broken) { cap.Dispose(); cap = null; }          // DXGI gave up: GDI for the rest of the recording
                    }
                    // like Snagit: a still screen costs no picture (the file gets fewer, longer frames); one at least every second keeps seeking and sound in step
                    var cp = System.Windows.Forms.Cursor.Position;
                    bool moved = cfg.VideoCursor && cp != lastCur;
                    if (dxgi && !changed && !moved && count > 0 && sw.ElapsedMilliseconds - lastWrite < 1000) { lastK = k; continue; }
                    var f = live.NewFrame();
                    bool got = dxgi && cap.CopyTo(f.Pixels, f.Stride);
                    using (var bmp = new Bitmap(w, h, f.Stride, PixelFormat.Format32bppRgb, f.Pixels))
                    {
                        if (!got) using (var g = Graphics.FromImage(bmp)) Native.CopyScreen(g, region);
                        if (cfg.VideoCursor) CursorSnap.Take().DrawOn(bmp, region.Location);
                        if (first == null) first = new Bitmap(bmp);
                    }
                    f.Seal();
                    lastK = k;
                    lastCur = cp; lastWrite = sw.ElapsedMilliseconds;
                    if (queue.TryAdd(new KeyValuePair<Mp4Writer.Frame, long>(f, k * 10000000L / fps))) count++;
                    else f.Dispose();                                                 // the encoder is behind: skip this picture
                }
                framesWritten = count;
            }
            catch { liveFailed = true; }
            finally
            {
                if (cap != null) cap.Dispose();
                queue.CompleteAdding();
                try { timeEndPeriod(1); } catch { }
            }
        }

        /// <summary>Encoder thread: opens the MP4, writes the queued pictures (and the sound recorded so far) in time order, and once the capture and the
        /// sound have stopped adds the rest of the sound and closes the file. <paramref name="opened"/> is set as soon as it is known whether the MP4 could be opened.</summary>
        void EncodeLoop(ManualResetEvent opened)
        {
            Mp4Writer.Live l = null;
            try
            {
                bool sound = audio != null && wavPath != null;
                l = Mp4Writer.Live.TryOpen(mp4Path, region.Width, region.Height, Fps, sound);
                if (l != null && sound) l.AttachSound(wavPath);
                live = l;
            }
            catch { if (l != null) { l.Dispose(); l = null; } }
            finally { opened.Set(); }
            if (l == null) return;
            foreach (var kv in queue.GetConsumingEnumerable())
            {
                try { if (!liveFailed) l.Write(kv.Key, kv.Value); }
                catch { liveFailed = true; }
                finally { kv.Key.Dispose(); }
            }
            audioDone.WaitOne(20000);                                  // the WAV is complete once the AudioRecorder has stopped
            bool ok = false;
            try { ok = !cancelled && !liveFailed && framesWritten > 0 && l.Finish(true); }
            catch { }
            if (!ok) { l.Dispose(); try { File.Delete(mp4Path); } catch { } }
            liveDone = ok;
        }

        /// <summary>Ends the real-time MP4: tells the encoder thread that the sound is complete and waits until the file is closed
        /// (a cancelled recording is dropped instead). Call after the capture thread and the audio recorder have stopped.</summary>
        void FinishLive()
        {
            if (encoder == null) return;
            audioDone.Set();
            encoder.Join(30000); encoder = null;
            if (queue != null) { queue.Dispose(); queue = null; }
        }

        void StopWorker()
        {
            stopFlag = true; sw.Stop();
            if (worker != null) worker.Join(5000);
            if (audio != null) audio.Stop();                       // writes the last of the sound up to the stopped clock, closes the WAV
            FinishLive();
        }

        public void StopClicked()
        {
            if (!started) { cancelled = true; Close(); return; }
            StopWorker();
            ui.Stop();
            bool complete = liveUsed ? liveDone && File.Exists(mp4Path) : File.Exists(aviPath);
            if (!cancelled && framesWritten > 0 && complete)
            {
                Result = new RecordingResult { FirstFrame = first, Seconds = Math.Max(1, sw.Elapsed.TotalSeconds), Fps = Fps };
                if (liveUsed) Result.Mp4Path = mp4Path;
                else
                {
                    Result.AviPath = aviPath;
                    if (audio != null && audio.FramesWritten > 0 && wavPath != null && File.Exists(wavPath)) Result.WavPath = wavPath;
                }
                first = null;
            }
            else if (!cancelled && liveUsed) App.Balloon(Loc.T("Could not save the recording"));
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
                try { if (worker != null) worker.Join(3000); FinishLive(); if (aviPath != null && File.Exists(aviPath)) File.Delete(aviPath); } catch { }
                try { if (mp4Path != null && File.Exists(mp4Path)) File.Delete(mp4Path); } catch { }
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
