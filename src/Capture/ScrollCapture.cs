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
        const int Segments = 12;      // horizontal slices compared separately when matching frames

        /// <summary>Per-row fingerprints of a frame, one hash for each of several horizontal segments.
        /// Matching segment by segment keeps working when part of the page does not scroll with the rest
        /// (a minimap, a table of contents, an ad, a video, a scrollbar) instead of failing on every row.</summary>
        public sealed class RowSig
        {
            public int Height, Segs;
            public ulong[] Hash;        // Hash[y * Segs + k]
        }

        /// <summary>Fingerprints a frame; the strip at the right edge (the scrollbar) is ignored.</summary>
        public static unsafe RowSig Signature(Bitmap bmp, int ignoreRight, int segs)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var sig = new RowSig { Height = bmp.Height, Segs = segs, Hash = new ulong[bmp.Height * segs] };
                int w = Math.Max(segs, bmp.Width - ignoreRight);
                for (int y = 0; y < bmp.Height; y++)
                {
                    uint* p = (uint*)((byte*)data.Scan0 + y * data.Stride);
                    for (int k = 0; k < segs; k++)
                    {
                        int x0 = w * k / segs, x1 = w * (k + 1) / segs;
                        ulong a = 1469598103934665603UL;
                        for (int x = x0; x < x1; x++) { a ^= p[x]; a *= 1099511628211UL; }
                        sig.Hash[y * segs + k] = a;
                    }
                }
                return sig;
            }
            finally { bmp.UnlockBits(data); }
        }

        /// <summary>
        /// Finds how many pixels 'cur' has been scrolled down relative to 'prev'.
        /// Returns 0 if the page did not move (end of page), -1 if no overlap was found.
        /// A shift is accepted when enough segments (the part that scrolls) agree with it on their distinctive rows;
        /// segments that change or stay fixed while scrolling simply do not vote.
        /// </summary>
        public static int FindShift(RowSig prev, RowSig cur)
        {
            int h = prev.Height, K = prev.Segs;
            long all = (long)h * K, eq = 0;
            for (int i = 0; i < all; i++) if (prev.Hash[i] == cur.Hash[i]) eq++;
            if (eq >= all * 0.995) return 0;                                   // identical: nothing moved

            // per segment, the commonest row value is the flat background: it matches at every shift, so it must not vote
            var mode = new ulong[K]; var hasBg = new bool[K];
            for (int k = 0; k < K; k++)
            {
                var counts = new Dictionary<ulong, int>(); ulong best = 0; int bestN = 0;
                for (int y = 0; y < h; y++) { int n; ulong v = prev.Hash[y * K + k]; counts.TryGetValue(v, out n); counts[v] = ++n; if (n > bestN) { bestN = n; best = v; } }
                mode[k] = best; hasBg[k] = bestN >= h / 5;
            }

            // the page did not move if some segments stay identical row for row while others (a changing side panel) redraw
            long still = 0;
            for (int k = 0; k < K; k++)
            {
                int info = 0, same = 0;
                for (int y = 0; y < h; y++)
                {
                    ulong a = prev.Hash[y * K + k];
                    if (hasBg[k] && a == mode[k]) continue;
                    info++; if (a == cur.Hash[y * K + k]) same++;
                }
                if (info >= 6 && same >= info * 0.98) still += same;
            }
            if (still >= 12) return 0;

            int bestShift = -1; long bestScore = 0;
            int minOverlap = Math.Max(16, h / 10);
            for (int s = 1; s <= h - minOverlap; s++)
            {
                int overlap = h - s; long score = 0;
                for (int k = 0; k < K; k++)
                {
                    int info = 0, match = 0;
                    for (int i = 0; i < overlap; i++)
                    {
                        ulong a = prev.Hash[(s + i) * K + k];
                        if (hasBg[k] && a == mode[k]) continue;
                        ulong b = cur.Hash[i * K + k];
                        if (b == prev.Hash[i * K + k]) continue;               // same place in both frames: a fixed header/footer, says nothing about scrolling
                        info++;
                        if (a == b) match++;
                    }
                    if (info >= 6 && match >= info * 0.8) score += match;      // this segment scrolled by s
                }
                if (score >= 12 && score > bestScore) { bestScore = score; bestShift = s; }
            }
            return bestShift;
        }


        /// <summary>Waits while keeping the UI alive; true as soon as Esc is pressed.</summary>
        static bool WaitOrEsc(int ms)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                if (Native.KeyPressedSince(0x1B)) return true;
                Application.DoEvents();
                Thread.Sleep(15);
            }
            return false;
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

        /// <summary>Scrolls the page under <paramref name="scrollAt"/> (default: the middle of the region) with the mouse wheel until it stops moving
        /// or Esc is pressed, and stitches what was seen. Returns null if nothing could be captured.</summary>
        public static Bitmap Run(Rectangle region, CursorSnap cursorForRestore, Point? scrollAt = null)
        {
            var cfg = AppSettings.Current;
            var strips = new List<Bitmap>();
            var status = new ScrollStatusForm(region);
            status.Show();
            Application.DoEvents();

            Point oldPos = Cursor.Position;
            Point at = scrollAt ?? new Point(region.X + region.Width / 2, region.Y + region.Height / 2);
            at = new Point(Math.Max(region.Left, Math.Min(region.Right - 1, at.X)), Math.Max(region.Top, Math.Min(region.Bottom - 1, at.Y)));
            Native.KeyPressedSince(0x1B);            // forget an Esc pressed earlier (it may have cancelled the region overlay)
            try
            {
                Native.SetCursorPos(at.X, at.Y);
                Thread.Sleep(250);

                Bitmap prev = ScreenGrabber.Grab(region);
                strips.Add(new Bitmap(prev));
                var prevSig = Signature(prev, 24, Segments);
                int notches = 3;
                int totalHeight = prev.Height;
                int stalls = 0;
                bool firstStill = false;                // the very first wheel step moved nothing

                for (int frame = 0; frame < 120; frame++)
                {
                    if (Native.KeyPressedSince(0x1B)) break;   // Esc
                    Native.SetCursorPos(at.X, at.Y);
                    Native.MouseWheel(-notches);
                    if (WaitOrEsc(320)) break;
                    Application.DoEvents();

                    var cur = ScreenGrabber.Grab(region);
                    var curSig = Signature(cur, 24, Segments);
                    int shift = FindShift(prevSig, curSig);
                    if (shift == 0) { cur.Dispose(); if (frame == 0) firstStill = true; break; }   // nothing moved: reached the end
                    if (shift < 0)
                    {
                        // scrolled too far (or animation still running): back off and retry
                        cur.Dispose();
                        Native.MouseWheel(notches);          // scroll back to the previous position
                        if (WaitOrEsc(320)) break;
                        notches = Math.Max(1, notches / 2);
                        if (++stalls > 4) break;
                        continue;
                    }
                    stalls = 0;
                    int newRows = shift;
                    var strip = ScreenGrabber.Crop(cur, new Rectangle(0, cur.Height - newRows, cur.Width, newRows));
                    strips.Add(strip);
                    totalHeight += newRows;
                    prev.Dispose(); prev = cur; prevSig = curSig;

                    // adapt scroll speed so that ~50-75% of the view advances each step
                    if (shift < region.Height * 0.35 && notches < 30) notches++;
                    else if (shift > region.Height * 0.8 && notches > 1) notches--;

                    status.SetInfo(strips.Count, totalHeight);
                    if (totalHeight > 60000) break;                          // sanity limit
                }
                prev.Dispose();
                if (firstStill) App.Balloon(Loc.T("Nothing scrolled here - click on the page content itself (not a toolbar or side panel)."));
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
