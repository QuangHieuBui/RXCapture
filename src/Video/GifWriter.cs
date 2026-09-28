using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace RXCapture
{
    /// <summary>Animated GIF89a encoder with per-frame median-cut palettes and duplicate-frame merging.</summary>
    public class GifWriter : IDisposable
    {
        readonly Stream stream;
        readonly int w, h;
        byte[] pendingRaw;
        int pendingDelay;
        bool closed;

        public GifWriter(Stream s, int width, int height)
        {
            stream = s; w = width; h = height;
            var bw = new BinaryWriter(stream);
            bw.Write(new byte[] { (byte)'G', (byte)'I', (byte)'F', (byte)'8', (byte)'9', (byte)'a' });
            bw.Write((short)w); bw.Write((short)h);
            bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)0);
            // NETSCAPE looping extension: loop forever
            bw.Write(new byte[] { 0x21, 0xFF, 0x0B, (byte)'N', (byte)'E', (byte)'T', (byte)'S', (byte)'C', (byte)'A', (byte)'P', (byte)'E', (byte)'2', (byte)'.', (byte)'0', 0x03, 0x01, 0x00, 0x00, 0x00 });
            bw.Flush();
        }

        static byte[] Raw(Bitmap bmp)
        {
            var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                var bytes = new byte[bmp.Width * bmp.Height * 4];
                for (int y = 0; y < bmp.Height; y++) Marshal.Copy(IntPtr.Add(d.Scan0, y * d.Stride), bytes, y * bmp.Width * 4, bmp.Width * 4);
                return bytes;
            }
            finally { bmp.UnlockBits(d); }
        }

        /// <summary>delayCs: delay in 1/100 s</summary>
        public void AddFrame(Bitmap bmp, int delayCs)
        {
            var raw = Raw(bmp);
            if (pendingRaw != null && Same(pendingRaw, raw)) { pendingDelay += delayCs; return; }
            Flush();
            pendingRaw = raw; pendingDelay = delayCs;
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i += 4) if (a[i] != b[i] || a[i + 1] != b[i + 1] || a[i + 2] != b[i + 2]) return false;
            return true;
        }

        void Flush()
        {
            if (pendingRaw == null) return;
            byte[] palette; byte[] idx;
            Quantize(pendingRaw, out palette, out idx);
            var bw = new BinaryWriter(stream);
            // graphic control extension
            bw.Write(new byte[] { 0x21, 0xF9, 0x04, 0x04 });   // disposal = 1 (leave in place)
            bw.Write((short)Math.Max(2, Math.Min(65535, pendingDelay)));
            bw.Write((byte)0); bw.Write((byte)0);
            // image descriptor with local colour table (256 entries)
            bw.Write((byte)0x2C); bw.Write((short)0); bw.Write((short)0); bw.Write((short)w); bw.Write((short)h);
            bw.Write((byte)(0x80 | 7));
            bw.Write(palette);
            bw.Write((byte)8);
            Lzw(bw, idx);
            bw.Write((byte)0);
            bw.Flush();
            pendingRaw = null;
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            Flush();
            stream.WriteByte(0x3B);
            stream.Flush();
        }

        public void Dispose() { Close(); }

        // ------------------------------------------------------------------ colour quantisation (median cut on 15-bit colours)

        class Box { public int[] Colors; public int Start, End; }

        static void Quantize(byte[] bgra, out byte[] palette, out byte[] indices)
        {
            var hist = new int[32768];
            int n = bgra.Length / 4;
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                hist[((bgra[o + 2] >> 3) << 10) | ((bgra[o + 1] >> 3) << 5) | (bgra[o] >> 3)]++;
            }
            var codes = new List<int>();
            for (int c = 0; c < 32768; c++) if (hist[c] > 0) codes.Add(c);
            var arr = codes.ToArray();

            var boxes = new List<Box> { new Box { Colors = arr, Start = 0, End = arr.Length } };
            while (boxes.Count < 256)
            {
                // split the box with the largest population*range
                Box best = null; int bestScore = 0, bestAxis = 0;
                foreach (var b in boxes)
                {
                    if (b.End - b.Start < 2) continue;
                    int rmin = 31, rmax = 0, gmin = 31, gmax = 0, bmin = 31, bmax = 0; long pop = 0;
                    for (int i = b.Start; i < b.End; i++)
                    {
                        int c = b.Colors[i]; int r = c >> 10, g = (c >> 5) & 31, bl = c & 31;
                        if (r < rmin) rmin = r; if (r > rmax) rmax = r; if (g < gmin) gmin = g; if (g > gmax) gmax = g; if (bl < bmin) bmin = bl; if (bl > bmax) bmax = bl;
                        pop += hist[c];
                    }
                    int rr = rmax - rmin, gr = gmax - gmin, br = bmax - bmin;
                    int range = Math.Max(rr, Math.Max(gr, br));
                    int score = (int)Math.Min(int.MaxValue / 2, (long)range * (long)Math.Sqrt(pop + 1) * 4 + (b.End - b.Start));
                    if (range > 0 && score > bestScore) { best = b; bestScore = score; bestAxis = (rr >= gr && rr >= br) ? 0 : (gr >= br ? 1 : 2); }
                }
                if (best == null) break;
                int axis = bestAxis;
                var slice = new int[best.End - best.Start];
                Array.Copy(best.Colors, best.Start, slice, 0, slice.Length);
                Array.Sort(slice, (x, y) => Comp(x, axis).CompareTo(Comp(y, axis)));
                Array.Copy(slice, 0, best.Colors, best.Start, slice.Length);
                long total = 0; for (int i = best.Start; i < best.End; i++) total += hist[best.Colors[i]];
                long acc = 0; int mid = best.Start + 1;
                for (int i = best.Start; i < best.End - 1; i++) { acc += hist[best.Colors[i]]; if (acc * 2 >= total) { mid = i + 1; break; } mid = i + 1; }
                mid = Math.Max(best.Start + 1, Math.Min(best.End - 1, mid));
                var nb = new Box { Colors = best.Colors, Start = mid, End = best.End };
                best.End = mid;
                boxes.Add(nb);
            }

            palette = new byte[768];
            var lut = new byte[32768];
            for (int bi = 0; bi < boxes.Count; bi++)
            {
                var b = boxes[bi];
                long sr = 0, sg = 0, sb = 0, sw = 0;
                for (int i = b.Start; i < b.End; i++)
                {
                    int c = b.Colors[i]; long cnt = hist[c];
                    sr += (c >> 10) * cnt; sg += ((c >> 5) & 31) * cnt; sb += (c & 31) * cnt; sw += cnt;
                    lut[c] = (byte)bi;
                }
                if (sw == 0) sw = 1;
                palette[bi * 3] = (byte)Math.Min(255, (int)(sr * 255 / (31 * sw)));
                palette[bi * 3 + 1] = (byte)Math.Min(255, (int)(sg * 255 / (31 * sw)));
                palette[bi * 3 + 2] = (byte)Math.Min(255, (int)(sb * 255 / (31 * sw)));
            }
            indices = new byte[n];
            for (int i = 0; i < n; i++)
            {
                int o = i * 4;
                indices[i] = lut[((bgra[o + 2] >> 3) << 10) | ((bgra[o + 1] >> 3) << 5) | (bgra[o] >> 3)];
            }
        }

        static int Comp(int c, int axis) { return axis == 0 ? c >> 10 : (axis == 1 ? (c >> 5) & 31 : c & 31); }

        // ------------------------------------------------------------------ LZW

        static void Lzw(BinaryWriter bw, byte[] data)
        {
            const int clear = 256, eoi = 257;
            var dict = new Dictionary<int, int>(8192);
            int next = eoi + 1, codeSize = 9;
            uint acc = 0; int bits = 0;
            var block = new byte[255]; int blen = 0;

            Action<int> emit = delegate(int code)
            {
                acc |= (uint)code << bits; bits += codeSize;
                while (bits >= 8)
                {
                    block[blen++] = (byte)(acc & 0xFF); acc >>= 8; bits -= 8;
                    if (blen == 255) { bw.Write((byte)255); bw.Write(block, 0, 255); blen = 0; }
                }
            };

            emit(clear);
            int prefix = data[0];
            for (int i = 1; i < data.Length; i++)
            {
                int k = data[i];
                int key = (prefix << 8) | k;
                int found;
                if (dict.TryGetValue(key, out found)) { prefix = found; continue; }
                emit(prefix);
                if (next < 4096)
                {
                    dict[key] = next++;
                    if (next > (1 << codeSize) && codeSize < 12) codeSize++;
                }
                else
                {
                    emit(clear);
                    dict.Clear(); next = eoi + 1; codeSize = 9;
                }
                prefix = k;
            }
            emit(prefix);
            emit(eoi);
            if (bits > 0) { block[blen++] = (byte)(acc & 0xFF); }
            if (blen > 0) { bw.Write((byte)blen); bw.Write(block, 0, blen); }
        }
    }
}
