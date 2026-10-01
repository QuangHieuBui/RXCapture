using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;

namespace RXCapture
{
    /// <summary>High-quality reduction of a picture (Lanczos-3, separable, premultiplied alpha). GDI+ reduces screenshots with a
    /// narrow filter that blurs thin text and aliases it at the same time; this filter keeps small text readable at 40-90 % zoom.</summary>
    public static class Resampler
    {
        sealed class Taps { public int[] First; public int[] Count; public float[] W; public int Max; }

        static float Sinc(double x) { if (x == 0) return 1f; double p = Math.PI * x; return (float)(Math.Sin(p) / p); }

        static float Lanczos3(double x) { x = Math.Abs(x); return x >= 3 ? 0f : Sinc(x) * Sinc(x / 3); }

        /// <summary>For every destination index the source indices and weights (normalised) that make it.</summary>
        static Taps MakeTaps(int srcLen, int dstLen)
        {
            double scale = (double)srcLen / dstLen, filterScale = Math.Max(1.0, scale), support = 3 * filterScale;
            int max = (int)Math.Ceiling(support * 2) + 2;
            var t = new Taps { First = new int[dstLen], Count = new int[dstLen], W = new float[dstLen * max], Max = max };
            for (int d = 0; d < dstLen; d++)
            {
                double center = (d + 0.5) * scale - 0.5;
                int i0 = (int)Math.Floor(center - support) + 1, i1 = (int)Math.Floor(center + support);
                i0 = Math.Max(0, i0); i1 = Math.Min(srcLen - 1, i1);
                int n = Math.Max(1, i1 - i0 + 1);
                double sum = 0;
                for (int k = 0; k < n; k++) { float w = Lanczos3((i0 + k - center) / filterScale); t.W[d * max + k] = w; sum += w; }
                if (Math.Abs(sum) < 1e-9) { for (int k = 0; k < n; k++) t.W[d * max + k] = 1f / n; }
                else for (int k = 0; k < n; k++) t.W[d * max + k] = (float)(t.W[d * max + k] / sum);
                t.First[d] = i0; t.Count[d] = n;
            }
            return t;
        }

        /// <summary>Largest picture (in source pixels) that is reduced with this filter; bigger ones need too much memory.</summary>
        public const long MaxSourcePixels = 36000000L;

        /// <summary>Reduces (or enlarges) <paramref name="src"/> to <paramref name="dw"/> x <paramref name="dh"/>. Null if the picture is too big for this filter.</summary>
        public static unsafe Bitmap Resize(Bitmap src, int dw, int dh)
        {
            int sw = src.Width, sh = src.Height;
            if (dw < 1 || dh < 1 || (long)sw * sh > MaxSourcePixels) return null;
            var tx = MakeTaps(sw, dw);
            var ty = MakeTaps(sh, dh);
            var mid = new float[(long)dw * sh * 4];                   // horizontal pass result: premultiplied B, G, R and A as floats
            var sd = src.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            Bitmap dst = null; BitmapData dd = null;
            try
            {
                IntPtr sBase = sd.Scan0; int sstride = sd.Stride;
                Parallel.For(0, sh, y =>
                {
                    byte* row = (byte*)sBase + (long)y * sstride;
                    long mo = (long)y * dw * 4;
                    for (int x = 0; x < dw; x++)
                    {
                        int first = tx.First[x], n = tx.Count[x], wo = x * tx.Max;
                        float b = 0, g = 0, r = 0, a = 0;
                        for (int k = 0; k < n; k++)
                        {
                            byte* p = row + (first + k) * 4; float w = tx.W[wo + k], pa = p[3] * w;
                            b += p[0] * pa / 255f; g += p[1] * pa / 255f; r += p[2] * pa / 255f; a += pa;
                        }
                        mid[mo + x * 4] = b; mid[mo + x * 4 + 1] = g; mid[mo + x * 4 + 2] = r; mid[mo + x * 4 + 3] = a;
                    }
                });
                dst = new Bitmap(dw, dh, PixelFormat.Format32bppArgb);
                dd = dst.LockBits(new Rectangle(0, 0, dw, dh), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                IntPtr dBase = dd.Scan0; int dstride = dd.Stride;
                Parallel.For(0, dh, y =>
                {
                    byte* row = (byte*)dBase + (long)y * dstride;
                    int first = ty.First[y], n = ty.Count[y], wo = y * ty.Max;
                    for (int x = 0; x < dw; x++)
                    {
                        float b = 0, g = 0, r = 0, a = 0;
                        for (int k = 0; k < n; k++)
                        {
                            long mo = ((long)(first + k) * dw + x) * 4; float w = ty.W[wo + k];
                            b += mid[mo] * w; g += mid[mo + 1] * w; r += mid[mo + 2] * w; a += mid[mo + 3] * w;
                        }
                        float al = Math.Max(0f, Math.Min(255f, a));
                        byte* p = row + x * 4;
                        if (al < 0.5f) { p[0] = p[1] = p[2] = 0; p[3] = 0; continue; }
                        float inv = 255f / al;                                            // un-premultiply
                        p[0] = Clamp(b * inv); p[1] = Clamp(g * inv); p[2] = Clamp(r * inv); p[3] = (byte)(al + 0.5f);
                    }
                });
                dst.UnlockBits(dd); dd = null;
                var result = dst; dst = null;
                return result;
            }
            finally
            {
                src.UnlockBits(sd);
                if (dd != null) dst.UnlockBits(dd);
                if (dst != null) dst.Dispose();
            }
        }

        static byte Clamp(float v) { return v <= 0 ? (byte)0 : (v >= 255 ? (byte)255 : (byte)(v + 0.5f)); }
    }
}
