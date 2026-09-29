using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>In-editor video player shown in place of the canvas. MP4 / AVI frames are decoded by Media Foundation
    /// (<see cref="Mp4Writer.Reader"/>) and painted here, so durations and seeking are exact; animated GIFs play in a PictureBox.
    /// Below the picture: play/pause, seek bar, and the trim controls (Set start / Set end / Trim video).</summary>
    public class VideoPlayerPanel : Panel
    {
        public event Action CloseRequested;
        /// <summary>The user chose to keep only [start, end] of the video.</summary>
        public event Action<TimeSpan, TimeSpan> TrimRequested;

        readonly Panel stage = new Panel();
        readonly Panel bar = new Panel();
        readonly Button btnPlay = new Button();
        readonly Button btnExternal = new Button();
        readonly Button btnClose = new Button();
        readonly Button btnStart = new Button();
        readonly Button btnEnd = new Button();
        readonly Button btnTrim = new Button();
        readonly Label lblRange = new Label();
        readonly SeekBar seek = new SeekBar();
        readonly Label lblTime = new Label();
        readonly Label lblError = new Label();
        readonly VideoView view = new VideoView();
        readonly Timer ticker = new Timer { Interval = 10 };
        readonly Stopwatch clock = new Stopwatch();

        Mp4Writer.Reader reader;
        AudioPlayer audio;                // the sound of the file (null for a silent video); started and stopped together with the clock
        Bitmap cur, next;                 // the frame on screen and the next decoded one, waiting for its time
        TimeSpan curTs, nextTs;
        bool haveNext;
        TimeSpan clockBase, pos;          // pos = playback position; while playing it is clockBase + clock.Elapsed
        PictureBox gifBox;
        MemoryStream gifStream;
        string path, lastLabel = "";
        bool playing, opened, dragging, durationKnown;
        int lastProgress;
        TimeSpan duration = TimeSpan.Zero;
        TimeSpan trimStart = TimeSpan.Zero, trimEnd = TimeSpan.Zero;

        /// <summary>True once the current file has been opened and its first frame decoded.</summary>
        public bool IsOpened { get { return opened; } }
        /// <summary>True when the file could not be opened.</summary>
        public bool Failed { get; private set; }
        public TimeSpan Duration { get { return duration; } }
        public TimeSpan Position { get { return pos; } }
        /// <summary>The part of the video Trim would keep (the green span on the seek bar).</summary>
        public TimeSpan TrimStart { get { return trimStart; } }
        public TimeSpan TrimEnd { get { return trimEnd; } }
        public string CurrentPath { get { return path; } }

        public VideoPlayerPanel()
        {
            Dock = DockStyle.Fill; BackColor = Color.Black; Visible = false;
            bar.Dock = DockStyle.Bottom; bar.Height = 88; bar.BackColor = Theme.Ribbon;
            stage.Dock = DockStyle.Fill; stage.BackColor = Color.Black;
            Controls.Add(stage); Controls.Add(bar);

            view.Dock = DockStyle.Fill; view.Visible = false; view.Click += (s, e) => TogglePlay();
            stage.Controls.Add(view);

            Style(btnPlay, "▶"); btnPlay.Font = new Font("Segoe UI Symbol", 11f); btnPlay.SetBounds(10, 6, 42, 32);
            btnPlay.Click += (s, e) => TogglePlay();

            lblTime.AutoSize = false; lblTime.SetBounds(58, 6, 100, 32); lblTime.TextAlign = ContentAlignment.MiddleLeft;
            lblTime.ForeColor = Theme.Text; lblTime.Font = new Font("Segoe UI", 9f); lblTime.Text = "0:00 / 0:00";

            btnClose.Font = new Font("Segoe UI", 9f); Style(btnClose, Loc.T("Close video")); btnClose.Size = new Size(110, 34);
            btnClose.Click += (s, e) => { if (CloseRequested != null) CloseRequested(); };
            btnExternal.Font = new Font("Segoe UI", 9f); Style(btnExternal, Loc.T("Open in default player")); btnExternal.Size = new Size(190, 34);
            btnExternal.Click += (s, e) => { if (path != null) try { System.Diagnostics.Process.Start(path); } catch { } };

            btnStart.Font = new Font("Segoe UI", 9f); Style(btnStart, "[  " + Loc.T("Set start")); btnStart.Size = new Size(120, 34);
            btnStart.Click += (s, e) => MarkStart();
            btnEnd.Font = new Font("Segoe UI", 9f); Style(btnEnd, "]  " + Loc.T("Set end")); btnEnd.Size = new Size(120, 34);
            btnEnd.Click += (s, e) => MarkEnd();
            btnTrim.Font = new Font("Segoe UI", 9f, FontStyle.Bold); Style(btnTrim, "✂  " + Loc.T("Trim video")); btnTrim.Size = new Size(150, 34);
            btnTrim.BackColor = Theme.Accent; btnTrim.FlatAppearance.BorderColor = Theme.AccentLight;
            btnTrim.Click += (s, e) => { if (CanTrim && TrimRequested != null) TrimRequested(trimStart, trimEnd); };
            lblRange.AutoSize = false; lblRange.TextAlign = ContentAlignment.MiddleLeft; lblRange.ForeColor = Theme.TextDim; lblRange.Font = new Font("Segoe UI", 9f);

            seek.Seeked += f => { SeekTo(TimeSpan.FromTicks((long)(duration.Ticks * Math.Max(0, Math.Min(1, f))))); };
            seek.DragChanged += d => { dragging = d; if (d) { if (audio != null) audio.Stop(); } else if (playing && audio != null) audio.Start(pos); };
            seek.RangeDragged += OnRangeDragged;
            bar.Controls.AddRange(new Control[] { btnPlay, lblTime, seek, btnStart, btnEnd, lblRange, btnTrim, btnExternal, btnClose });
            bar.Resize += (s, e) => LayoutBar();
            LayoutBar(); UpdateRange();

            lblError.Dock = DockStyle.Fill; lblError.ForeColor = Theme.TextDim; lblError.BackColor = Color.Black;
            lblError.TextAlign = ContentAlignment.MiddleCenter; lblError.Font = new Font("Segoe UI", 11f); lblError.Visible = false;
            stage.Controls.Add(lblError);

            ticker.Tick += (s, e) => Tick();
            VisibleChanged += (s, e) => { if (!Visible) Pause(); };
        }

        static void Style(Button b, string text)
        {
            b.Text = text; b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderColor = Theme.Border;
            b.FlatAppearance.MouseOverBackColor = Theme.Hover; b.FlatAppearance.MouseDownBackColor = Theme.Checked;
            b.BackColor = Theme.Field; b.ForeColor = Theme.Text; b.UseVisualStyleBackColor = false; b.TabStop = false; b.UseMnemonic = false;
        }

        void LayoutBar()
        {
            int w = bar.ClientSize.Width;
            seek.SetBounds(164, 6, Math.Max(40, w - 164 - 12), 34);
            btnStart.Location = new Point(10, 46); btnEnd.Location = new Point(btnStart.Right + 6, 46);
            btnClose.Location = new Point(w - btnClose.Width - 10, 46);
            btnExternal.Location = new Point(btnClose.Left - btnExternal.Width - 6, 46);
            btnTrim.Location = new Point(btnExternal.Left - btnTrim.Width - 6, 46);
            lblRange.SetBounds(btnEnd.Right + 10, 46, Math.Max(20, btnTrim.Left - btnEnd.Right - 16), 34);
        }

        bool CanTrim
        {
            get { return reader != null && duration > TimeSpan.Zero && trimEnd - trimStart >= TimeSpan.FromMilliseconds(200) && (trimStart > TimeSpan.Zero || trimEnd < duration); }
        }

        void MarkStart()
        {
            if (reader == null || duration <= TimeSpan.Zero) return;
            trimStart = pos;
            if (trimEnd <= trimStart) trimEnd = duration;
            UpdateRange();
        }

        void MarkEnd()
        {
            if (reader == null || duration <= TimeSpan.Zero) return;
            trimEnd = pos;
            if (trimEnd <= trimStart) trimStart = TimeSpan.Zero;
            UpdateRange();
        }

        void UpdateRange()
        {
            double d = duration.TotalSeconds;
            seek.SetRange(d > 0 ? trimStart.TotalSeconds / d : 0, d > 0 ? trimEnd.TotalSeconds / d : 1, d > 0 && reader != null);
            seek.MinGap = d > 0 ? Math.Min(0.5, 0.2 / d) : 0.02;   // at least 0.2 s stays selected
            lblRange.Text = duration > TimeSpan.Zero ? Loc.T("Keep") + ": " + Fmt(trimStart) + " - " + Fmt(trimEnd) + "  (" + Fmt(trimEnd - trimStart) + ")" : "";
            btnTrim.Enabled = CanTrim;
        }

        /// <summary>The start or end grip of the green span was dragged: update the kept range and show the frame at that grip.</summary>
        void OnRangeDragged(double start, double end, bool endGrip)
        {
            if (reader == null || duration <= TimeSpan.Zero) return;
            Pause();
            trimStart = TimeSpan.FromTicks((long)(duration.Ticks * start));
            trimEnd = TimeSpan.FromTicks((long)(duration.Ticks * end));
            UpdateRange();
            SeekTo(endGrip ? trimEnd : trimStart);
        }

        public void Open(string file, string ext)
        {
            Stop();
            path = file; Failed = false; opened = false; duration = TimeSpan.Zero; trimStart = trimEnd = TimeSpan.Zero; pos = TimeSpan.Zero;
            lblError.Visible = false; seek.Value = 0; lblTime.Text = "0:00 / 0:00"; lastLabel = "";
            bool gif = string.Equals(ext, "gif", StringComparison.OrdinalIgnoreCase);
            btnPlay.Visible = seek.Visible = lblTime.Visible = btnStart.Visible = btnEnd.Visible = btnTrim.Visible = lblRange.Visible = !gif;
            UpdateRange();
            try
            {
                if (gif)
                {
                    gifStream = new MemoryStream(File.ReadAllBytes(file));
                    gifBox = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Image = Image.FromStream(gifStream) };
                    stage.Controls.Add(gifBox); gifBox.BringToFront();
                    opened = true;
                    return;
                }
                reader = new Mp4Writer.Reader(file);
                audio = AudioPlayer.TryOpen(file);
                cur = new Bitmap(reader.Width, reader.Height, PixelFormat.Format32bppRgb);
                next = new Bitmap(reader.Width, reader.Height, PixelFormat.Format32bppRgb);
                duration = reader.Duration;
                if (!reader.ReadFrame(cur, out curTs)) throw new InvalidDataException("no video frames");
                durationKnown = duration > TimeSpan.Zero;
                if (!durationKnown) duration = curTs + TimeSpan.FromSeconds(1);      // unknown: corrected when the end is reached
                view.SetImage(cur); view.Visible = true; view.BringToFront();
                haveNext = reader.ReadFrame(next, out nextTs);
                trimStart = TimeSpan.Zero; trimEnd = duration;
                opened = true;
                UpdateRange();
                StartPlaying();
                ticker.Start();
            }
            catch { Fail(); }
        }

        void Fail()
        {
            Failed = true; playing = false; opened = false; ticker.Stop(); clock.Stop(); if (audio != null) audio.Stop();
            view.Visible = false;
            lblError.Text = Loc.T("This video cannot be played here."); lblError.Visible = true; lblError.BringToFront();
        }

        void SetPlayGlyph() { btnPlay.Text = playing ? "❚❚" : "▶"; }

        void StartPlaying() { clockBase = pos; clock.Restart(); playing = true; SetPlayGlyph(); if (audio != null) audio.Start(pos); }

        public void TogglePlay()
        {
            if (reader == null || Failed) return;
            if (playing) Pause();
            else
            {
                if (pos >= duration) SeekTo(TimeSpan.Zero);
                StartPlaying();
            }
        }

        public void Pause()
        {
            if (!playing) return;
            pos = Clamp(clockBase + clock.Elapsed);
            clock.Stop(); playing = false; SetPlayGlyph(); if (audio != null) audio.Stop();
        }

        TimeSpan Clamp(TimeSpan t) { return t < TimeSpan.Zero ? TimeSpan.Zero : (t > duration ? duration : t); }

        /// <summary>Shows the frame at <paramref name="t"/> (the last one at or before it) and continues from there.</summary>
        public void SeekTo(TimeSpan t)
        {
            if (reader == null) return;
            t = Clamp(t);
            try
            {
                reader.Seek(t);
                TimeSpan ts; bool got = false;
                haveNext = false;
                while (reader.ReadFrame(next, out ts))
                {
                    if (ts > t && got) { nextTs = ts; haveNext = true; break; }    // first frame after t stays queued
                    Swap(); curTs = ts; got = true;
                    if (ts > t) { haveNext = reader.ReadFrame(next, out nextTs); break; }
                }
                view.SetImage(cur);
            }
            catch { Fail(); return; }
            pos = t; clockBase = t;
            if (playing) { clock.Restart(); if (audio != null && !dragging) audio.Start(t); }
            UpdateProgress(true);
        }

        void Swap() { var b = cur; cur = next; next = b; }

        void Tick()
        {
            if (reader == null || Failed) return;
            try
            {
                if (playing)
                {
                    pos = clockBase + clock.Elapsed;
                    bool changed = false;
                    while (haveNext && nextTs <= pos)
                    {
                        Swap(); curTs = nextTs; changed = true;
                        haveNext = reader.ReadFrame(next, out nextTs);
                    }
                    if (changed) view.SetImage(cur);
                    if (!haveNext && pos >= curTs + TimeSpan.FromMilliseconds(80))
                    {
                        // reached the end: show the first frame again, ready to replay
                        if (!durationKnown) { duration = curTs; durationKnown = true; trimEnd = duration; UpdateRange(); }
                        playing = false; clock.Stop(); SetPlayGlyph(); if (audio != null) audio.Stop();
                        SeekTo(TimeSpan.Zero);
                        return;
                    }
                    if (pos > duration) pos = duration;
                }
            }
            catch { Fail(); return; }
            int now = Environment.TickCount;
            if (now - lastProgress >= 50) { lastProgress = now; UpdateProgress(false); }
        }

        void UpdateProgress(bool force)
        {
            if (!dragging) seek.Value = duration > TimeSpan.Zero ? Math.Min(1.0, pos.TotalSeconds / duration.TotalSeconds) : 0;
            string text = Fmt(pos) + " / " + Fmt(duration);
            if (force || text != lastLabel) { lblTime.Text = text; lastLabel = text; }
        }

        static string Fmt(TimeSpan t) { int s = (int)Math.Round(t.TotalSeconds); return (s / 60) + ":" + (s % 60).ToString("00"); }

        /// <summary>Stops playback and releases the file (so it can be deleted).</summary>
        public void Stop()
        {
            ticker.Stop(); clock.Stop(); playing = false; opened = false; haveNext = false;
            view.SetImage(null); view.Visible = false;
            if (audio != null) { audio.Dispose(); audio = null; }
            if (reader != null) { reader.Dispose(); reader = null; }
            if (cur != null) { cur.Dispose(); cur = null; }
            if (next != null) { next.Dispose(); next = null; }
            if (gifBox != null) { stage.Controls.Remove(gifBox); if (gifBox.Image != null) gifBox.Image.Dispose(); gifBox.Dispose(); gifBox = null; }
            if (gifStream != null) { gifStream.Dispose(); gifStream = null; }
            lblError.Visible = false; SetPlayGlyph(); path = null;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (Visible && keyData == Keys.Space && reader != null) { TogglePlay(); return true; }
            if (Visible && keyData == Keys.Escape) { if (CloseRequested != null) CloseRequested(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) { Stop(); ticker.Dispose(); }
            base.Dispose(disposing);
        }

        /// <summary>Paints one bitmap scaled to fit, on black.</summary>
        class VideoView : Control
        {
            Bitmap img;
            public VideoView()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Color.Black; Cursor = Cursors.Hand;
            }
            public void SetImage(Bitmap b) { img = b; Invalidate(); }
            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Color.Black);
                if (img == null || ClientSize.Width < 2 || ClientSize.Height < 2) return;
                float k = Math.Min((float)ClientSize.Width / img.Width, (float)ClientSize.Height / img.Height);
                int w = Math.Max(1, (int)(img.Width * k)), h = Math.Max(1, (int)(img.Height * k));
                g.InterpolationMode = k < 1 ? InterpolationMode.HighQualityBilinear : InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(img, (ClientSize.Width - w) / 2, (ClientSize.Height - h) / 2, w, h);
            }
        }

        /// <summary>Owner-drawn seek slider (0..1). The green span is the part of the video that Trim keeps: drag its two
        /// grips to change it, or click/drag anywhere else on the bar to move the playhead.</summary>
        class SeekBar : Control
        {
            public event Action<double> Seeked;
            public event Action<bool> DragChanged;
            /// <summary>A grip was dragged: (start, end, true when it was the end grip), all as fractions 0..1.</summary>
            public event Action<double, double, bool> RangeDragged;
            public double MinGap = 0.02;               // the kept span never gets narrower than this fraction
            double value, rs, re = 1;
            bool down, ranged;
            int grip;                                  // 0 = none, 1 = start grip, 2 = end grip being dragged

            public SeekBar() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); Cursor = Cursors.Hand; }
            public void SetRange(double start, double end, bool show) { rs = start; re = end; ranged = show; Invalidate(); }
            public double Value { get { return value; } set { if (down) return; this.value = value; Invalidate(); } }

            const int Pad = 8, GripHit = 9;
            int XOf(double f) { return Pad + (int)((Width - 2 * Pad) * f); }
            double At(int px) { return Math.Max(0, Math.Min(1, (px - (double)Pad) / Math.Max(1, Width - 2 * Pad))); }

            int GripAt(int px)
            {
                if (!ranged) return 0;
                int ds = Math.Abs(px - XOf(rs)), de = Math.Abs(px - XOf(re));
                if (ds > GripHit && de > GripHit) return 0;
                if (ds == de) return px < XOf(rs) ? 1 : 2;
                return ds < de ? 1 : 2;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                var g = e.Graphics; g.Clear(Theme.Ribbon);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int cy = Height / 2, l = Pad, r = Width - Pad, x = XOf(value);
                using (var b = new SolidBrush(Theme.Field)) g.FillRectangle(b, l, cy - 2, r - l, 4);
                if (ranged)
                {
                    int a = XOf(rs), z = XOf(re);
                    using (var b = new SolidBrush(Color.FromArgb(70, 76, 190, 96))) g.FillRectangle(b, a, cy - 10, Math.Max(2, z - a), 20);
                    using (var pn = new Pen(Color.FromArgb(76, 190, 96), 2)) { g.DrawLine(pn, a, cy - 10, z, cy - 10); g.DrawLine(pn, a, cy + 10, z, cy + 10); }
                }
                using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, l, cy - 2, Math.Max(0, x - l), 4);
                using (var b = new SolidBrush(Theme.AccentLight)) g.FillEllipse(b, x - 6, cy - 6, 12, 12);
                if (ranged)
                {
                    // the two grips go on top so they stay grabbable even when the playhead sits under them
                    foreach (int gx in new[] { XOf(rs), XOf(re) })
                    {
                        var gr = new Rectangle(gx - 4, cy - 13, 8, 26);
                        using (var p = new GraphicsPath())
                        {
                            p.AddArc(gr.X, gr.Y, 6, 6, 180, 90); p.AddArc(gr.Right - 6, gr.Y, 6, 6, 270, 90);
                            p.AddArc(gr.Right - 6, gr.Bottom - 6, 6, 6, 0, 90); p.AddArc(gr.X, gr.Bottom - 6, 6, 6, 90, 90); p.CloseFigure();
                            using (var b = new SolidBrush(Color.FromArgb(76, 190, 96))) g.FillPath(b, p);
                            using (var pn = new Pen(Color.White, 1.5f)) g.DrawPath(pn, p);
                        }
                    }
                }
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                if (e.Button != MouseButtons.Left) return;
                grip = GripAt(e.X);
                down = true; Capture = true;
                if (DragChanged != null) DragChanged(true);
                if (grip != 0) return;
                value = At(e.X); Invalidate(); if (Seeked != null) Seeked(value);
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                if (!down) { Cursor = GripAt(e.X) != 0 ? Cursors.SizeWE : Cursors.Hand; return; }
                double f = At(e.X);
                if (grip == 1) { rs = Math.Max(0, Math.Min(f, re - MinGap)); Invalidate(); if (RangeDragged != null) RangeDragged(rs, re, false); }
                else if (grip == 2) { re = Math.Min(1, Math.Max(f, rs + MinGap)); Invalidate(); if (RangeDragged != null) RangeDragged(rs, re, true); }
                else { value = f; Invalidate(); if (Seeked != null) Seeked(value); }
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                if (!down) return;
                down = false; grip = 0; Capture = false; if (DragChanged != null) DragChanged(false);
            }
        }
    }
}
