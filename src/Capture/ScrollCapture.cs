using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Threading;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>
    /// Scrolling capture: repeatedly scrolls the content under a region with the mouse wheel, grabs each
    /// frame and stitches them by locating the vertical overlap between consecutive frames.
    /// </summary>
    public static class ScrollCapture
    {
        /// <summary>Row signatures of a frame (the right-hand scrollbar strip is ignored).</summary>
        public static unsafe long[] RowHashes(Bitmap bmp, int ignoreRight)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var h = new long[bmp.Height];
                int w = Math.Max(1, bmp.Width - ignoreRight);
                for (int y = 0; y < bmp.Height; y++)
                {
                    uint* p = (uint*)((byte*)data.Scan0 + y * data.Stride);
                    ulong a = 1469598103934665603UL;
                    for (int x = 0; x < w; x++) { a ^= p[x]; a *= 1099511628211UL; }
                    h[y] = (long)a;
                }
                return h;
            }
            finally { bmp.UnlockBits(data); }
        }

        /// <summary>
        /// Finds how many pixels 'cur' has been scrolled down relative to 'prev'.
        /// Returns 0 if the frames are identical (end of page), -1 if no overlap was found.
        /// </summary>
        public static int FindShift(long[] prev, long[] cur)
        {
            int h = prev.Length;
            // identical?
            int same = 0;
            for (int i = 0; i < h; i++) if (prev[i] == cur[i]) same++;
            if (same >= h * 0.995) return 0;

            int bestShift = -1; double bestScore = 0;
            int minOverlap = Math.Max(16, h / 10);
            for (int s = 1; s <= h - minOverlap; s++)
            {
                int overlap = h - s, match = 0;
                for (int i = 0; i < overlap; i++) if (prev[s + i] == cur[i]) match++;
                double score = (double)match / overlap;
                // prefer larger absolute matches when scores are close (sticky headers/footers reduce the ratio)
                if (score >= 0.80 && match > bestScore) { bestScore = match; bestShift = s; }
            }
            return bestShift;
        }

        public static Bitmap Stitch(List<Bitmap> strips, int width)
        {
            int total = 0;
            foreach (var s in strips) total += s.Height;
            var result = new Bitmap(width, total, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                int y = 0;
                foreach (var s in strips) { g.DrawImageUnscaled(s, 0, y); y += s.Height; }
            }
            return result;
        }

        /// <summary>Interactive capture of a screen rectangle. Returns null if cancelled/failed.</summary>
        public static Bitmap Run(Rectangle region, CursorSnap cursorForRestore)
        {
            var cfg = AppSettings.Current;
            var strips = new List<Bitmap>();
            var status = new ScrollStatusForm(region);
            status.Show();
            Application.DoEvents();

            Point oldPos = Cursor.Position;
            try
            {
                Native.SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
                Thread.Sleep(250);

                Bitmap prev = ScreenGrabber.Grab(region);
                strips.Add(new Bitmap(prev));
                long[] prevHash = RowHashes(prev, 24);
                int notches = 3;
                int totalHeight = prev.Height;
                int stalls = 0;

                for (int frame = 0; frame < 120; frame++)
                {
                    if (Native.KeyDown(0x1B)) break;   // Esc
                    Native.SetCursorPos(region.X + region.Width / 2, region.Y + region.Height / 2);
                    Native.MouseWheel(-notches);
                    Thread.Sleep(320);
                    Application.DoEvents();

                    var cur = ScreenGrabber.Grab(region);
                    var curHash = RowHashes(cur, 24);
                    int shift = FindShift(prevHash, curHash);
                    if (shift == 0) { cur.Dispose(); break; }               // nothing moved: reached the end
                    if (shift < 0)
                    {
                        // scrolled too far (or animation still running): back off and retry
                        cur.Dispose();
                        Native.MouseWheel(notches);          // scroll back to the previous position
                        Thread.Sleep(320);
                        notches = Math.Max(1, notches / 2);
                        if (++stalls > 4) break;
                        continue;
                    }
                    stalls = 0;
                    int newRows = shift;
                    var strip = ScreenGrabber.Crop(cur, new Rectangle(0, cur.Height - newRows, cur.Width, newRows));
                    strips.Add(strip);
                    totalHeight += newRows;
                    prev.Dispose(); prev = cur; prevHash = curHash;

                    // adapt scroll speed so that ~50-75% of the view advances each step
                    if (shift < region.Height * 0.35 && notches < 30) notches++;
                    else if (shift > region.Height * 0.8 && notches > 1) notches--;

                    status.SetInfo(strips.Count, totalHeight);
                    if (totalHeight > 60000) break;                          // sanity limit
                }
                prev.Dispose();
                if (strips.Count == 0) return null;
                return Stitch(strips, region.Width);
            }
            finally
            {
                Native.SetCursorPos(oldPos.X, oldPos.Y);
                status.Close();
                foreach (var s in strips) s.Dispose();
            }
        }
    }

    /// <summary>Small non-activating notice shown while a scrolling capture is running.</summary>
    class ScrollStatusForm : Form
    {
        readonly Label lbl = new Label();

        public ScrollStatusForm(Rectangle region)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.FromArgb(17, 24, 39);
            Size = new Size(360, 44);
            lbl.Dock = DockStyle.Fill; lbl.ForeColor = Color.White; lbl.TextAlign = ContentAlignment.MiddleCenter;
            lbl.Font = new Font("Segoe UI", 10f);
            lbl.Text = Loc.T("Scrolling capture…  press Esc to stop");
            Controls.Add(lbl);
            var mon = Screen.FromRectangle(region).Bounds;
            int y = region.Top - Height - 8 >= mon.Top ? region.Top - Height - 8 : region.Bottom + 8;
            if (y + Height > mon.Bottom) y = mon.Top + 8;
            int x = Math.Max(mon.Left, Math.Min(mon.Right - Width, region.X + (region.Width - Width) / 2));
            Location = new Point(x, y);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW; return cp; }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_DPICHANGED) return;
            base.WndProc(ref m);
        }

        public void SetInfo(int frames, int height)
        {
            lbl.Text = Loc.T("Scrolling capture…  press Esc to stop") + "   (" + frames + " • " + height + " px)";
            lbl.Refresh();
        }
    }
}
