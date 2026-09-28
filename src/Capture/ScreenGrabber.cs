using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace ShotCraft
{
    /// <summary>Cursor state remembered at snapshot time.</summary>
    public class CursorSnap
    {
        public IntPtr Handle;
        public Point Pos;       // screen position of the hot spot
        public Point Hotspot;
        public bool Visible;

        public static CursorSnap Take()
        {
            var c = new CursorSnap();
            var ci = new CURSORINFO();
            ci.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(CURSORINFO));
            if (Native.GetCursorInfo(ref ci) && (ci.flags & Native.CURSOR_SHOWING) != 0)
            {
                c.Visible = true;
                c.Handle = ci.hCursor;
                c.Pos = new Point(ci.ptScreenPos.X, ci.ptScreenPos.Y);
                ICONINFO ii;
                if (Native.GetIconInfo(ci.hCursor, out ii))
                {
                    c.Hotspot = new Point(ii.xHotspot, ii.yHotspot);
                    if (ii.hbmMask != IntPtr.Zero) Native.DeleteObject(ii.hbmMask);
                    if (ii.hbmColor != IntPtr.Zero) Native.DeleteObject(ii.hbmColor);
                }
            }
            return c;
        }

        /// <summary>Draw the cursor onto bmp, whose top-left corresponds to 'origin' in screen coordinates.</summary>
        public void DrawOn(Bitmap bmp, Point origin)
        {
            if (!Visible) return;
            int x = Pos.X - Hotspot.X - origin.X, y = Pos.Y - Hotspot.Y - origin.Y;
            if (x > bmp.Width || y > bmp.Height || x < -128 || y < -128) return;
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { Native.DrawIconEx(hdc, x, y, Handle, 0, 0, 0, IntPtr.Zero, Native.DI_NORMAL); }
                finally { g.ReleaseHdc(hdc); }
            }
        }
    }

    public static class ScreenGrabber
    {
        public static Rectangle VirtualScreen { get { return SystemInformation.VirtualScreen; } }

        /// <summary>Copy a rectangle of the desktop (physical pixels) into a 32bpp bitmap.</summary>
        public static Bitmap Grab(Rectangle r)
        {
            if (r.Width < 1 || r.Height < 1) throw new ArgumentException("empty rectangle");
            var bmp = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
                Native.CopyScreen(g, r);
            MakeOpaque(bmp);
            return bmp;
        }

        /// <summary>Screen BitBlt may leave alpha = 0 on some drivers; force it to 255.</summary>
        public static unsafe void MakeOpaque(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < data.Height; y++)
                {
                    byte* p = (byte*)data.Scan0 + y * data.Stride;
                    for (int x = 0; x < data.Width; x++) p[x * 4 + 3] = 255;
                }
            }
            finally { bmp.UnlockBits(data); }
        }

        public static Rectangle MonitorUnderCursor()
        {
            return Screen.FromPoint(Cursor.Position).Bounds;
        }

        /// <summary>
        /// Renders one top-level window with PrintWindow (PW_RENDERFULLCONTENT) so it can be captured even when other windows cover it.
        /// Returns null when the result looks empty (some GPU-composited windows), so callers can fall back to a screen crop.
        /// </summary>
        public static Bitmap CaptureWindowContent(IntPtr hwnd, Rectangle frame)
        {
            RECT wr;
            if (!Native.GetWindowRect(hwnd, out wr)) return null;
            int w = wr.Right - wr.Left, h = wr.Bottom - wr.Top;
            if (w < 2 || h < 2) return null;
            using (var full = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                bool ok;
                using (var g = Graphics.FromImage(full))
                {
                    g.Clear(Color.White);
                    IntPtr hdc = g.GetHdc();
                    try { ok = Native.PrintWindow(hwnd, hdc, 2); }
                    finally { g.ReleaseHdc(hdc); }
                }
                if (!ok) return null;
                MakeOpaque(full);
                var rel = new Rectangle(frame.X - wr.Left, frame.Y - wr.Top, frame.Width, frame.Height);
                rel.Intersect(new Rectangle(0, 0, w, h));
                if (rel.Width < 2 || rel.Height < 2) return null;
                var crop = Crop(full, rel);
                // reject blank output: every sampled pixel identical and pure black / pure white
                int same = 0, n = 0;
                var first = crop.GetPixel(crop.Width / 2, crop.Height / 2);
                for (int i = 1; i <= 24; i++)
                {
                    var p = crop.GetPixel(Math.Min(crop.Width - 1, i * crop.Width / 25), Math.Min(crop.Height - 1, (i * 7 % 24 + 1) * crop.Height / 25));
                    n++; if (p == first) same++;
                }
                int sum = first.R + first.G + first.B;
                if (same == n && (sum <= 6 || sum >= 762)) { crop.Dispose(); return null; }
                return crop;
            }
        }

        /// <summary>Extract a sub-rectangle from a frozen desktop bitmap.</summary>
        public static Bitmap Crop(Bitmap src, Rectangle r)
        {
            r.Intersect(new Rectangle(0, 0, src.Width, src.Height));
            var dst = new Bitmap(Math.Max(1, r.Width), Math.Max(1, r.Height), PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, r.Width, r.Height), r, GraphicsUnit.Pixel);
            }
            return dst;
        }

        /// <summary>Keep only the pixels inside 'path' (in bitmap coordinates); the rest becomes transparent.</summary>
        public static Bitmap ApplyMask(Bitmap src, GraphicsPath path)
        {
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.SetClip(path);
                g.DrawImageUnscaled(src, 0, 0);
            }
            return dst;
        }
    }
}
