using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace ShotCraft
{
    /// <summary>Pure image-processing helpers. Every function returns a new bitmap and never mutates its input.</summary>
    public static class Effects
    {
        public static Bitmap ToArgb(Image src)
        {
            var b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            b.SetResolution(96, 96);
            using (var g = Graphics.FromImage(b))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, 0, 0, src.Width, src.Height);
            }
            return b;
        }

        static byte[] GetBytes(Bitmap bmp, out int stride)
        {
            var d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            stride = d.Stride;
            var bytes = new byte[d.Stride * d.Height];
            Marshal.Copy(d.Scan0, bytes, 0, bytes.Length);
            bmp.UnlockBits(d);
            return bytes;
        }

        static Bitmap FromBytes(byte[] bytes, int w, int h, int stride)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var d = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            Marshal.Copy(bytes, 0, d.Scan0, Math.Min(bytes.Length, d.Stride * h));
            bmp.UnlockBits(d);
            return bmp;
        }

        static int Cl(int v, int lo, int hi) { return v < lo ? lo : (v > hi ? hi : v); }

        // ------------------------------------------------------------------ blur / pixelate

        public static Bitmap Pixelate(Bitmap src, int block)
        {
            block = Math.Max(2, block);
            int w = src.Width, h = src.Height, stride;
            var s = GetBytes(src, out stride);
            var d = new byte[s.Length];
            for (int by = 0; by < h; by += block)
                for (int bx = 0; bx < w; bx += block)
                {
                    int x2 = Math.Min(w, bx + block), y2 = Math.Min(h, by + block);
                    long a = 0, r = 0, g = 0, b = 0; int n = 0;
                    for (int y = by; y < y2; y++)
                        for (int x = bx; x < x2; x++)
                        {
                            int i = y * stride + x * 4;
                            b += s[i]; g += s[i + 1]; r += s[i + 2]; a += s[i + 3]; n++;
                        }
                    byte ab = (byte)(a / n), rb = (byte)(r / n), gb = (byte)(g / n), bb = (byte)(b / n);
                    for (int y = by; y < y2; y++)
                        for (int x = bx; x < x2; x++)
                        {
                            int i = y * stride + x * 4;
                            d[i] = bb; d[i + 1] = gb; d[i + 2] = rb; d[i + 3] = ab;
                        }
                }
            return FromBytes(d, w, h, stride);
        }

        public static Bitmap BoxBlur(Bitmap src, int radius)
        {
            radius = Math.Max(1, radius);
            int w = src.Width, h = src.Height, stride;
            var a = GetBytes(src, out stride);
            var b = new byte[a.Length];
            for (int pass = 0; pass < 3; pass++)
            {
                BlurH(a, b, w, h, stride, radius);
                BlurV(b, a, w, h, stride, radius);
            }
            return FromBytes(a, w, h, stride);
        }

        static void BlurH(byte[] s, byte[] d, int w, int h, int stride, int r)
        {
            int win = 2 * r + 1;
            for (int y = 0; y < h; y++)
            {
                int row = y * stride;
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int i = -r; i <= r; i++) sum += s[row + Cl(i, 0, w - 1) * 4 + c];
                    for (int x = 0; x < w; x++)
                    {
                        d[row + x * 4 + c] = (byte)(sum / win);
                        sum += s[row + Cl(x + r + 1, 0, w - 1) * 4 + c] - s[row + Cl(x - r, 0, w - 1) * 4 + c];
                    }
                }
            }
        }

        static void BlurV(byte[] s, byte[] d, int w, int h, int stride, int r)
        {
            int win = 2 * r + 1;
            for (int x = 0; x < w; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int i = -r; i <= r; i++) sum += s[Cl(i, 0, h - 1) * stride + x * 4 + c];
                    for (int y = 0; y < h; y++)
                    {
                        d[y * stride + x * 4 + c] = (byte)(sum / win);
                        sum += s[Cl(y + r + 1, 0, h - 1) * stride + x * 4 + c] - s[Cl(y - r, 0, h - 1) * stride + x * 4 + c];
                    }
                }
        }

        public static Bitmap Convolve(Bitmap src, float[] k, float divisor, float offset)
        {
            int w = src.Width, h = src.Height, stride;
            var s = GetBytes(src, out stride);
            var d = new byte[s.Length];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    float r = 0, g = 0, b = 0;
                    for (int ky = -1; ky <= 1; ky++)
                        for (int kx = -1; kx <= 1; kx++)
                        {
                            int i = Cl(y + ky, 0, h - 1) * stride + Cl(x + kx, 0, w - 1) * 4;
                            float kv = k[(ky + 1) * 3 + kx + 1];
                            b += s[i] * kv; g += s[i + 1] * kv; r += s[i + 2] * kv;
                        }
                    int o = y * stride + x * 4;
                    d[o] = (byte)Cl((int)(b / divisor + offset), 0, 255);
                    d[o + 1] = (byte)Cl((int)(g / divisor + offset), 0, 255);
                    d[o + 2] = (byte)Cl((int)(r / divisor + offset), 0, 255);
                    d[o + 3] = s[o + 3];
                }
            return FromBytes(d, w, h, stride);
        }

        public static Bitmap Sharpen(Bitmap s) { return Convolve(s, new float[] { 0, -1, 0, -1, 5, -1, 0, -1, 0 }, 1, 0); }
        public static Bitmap Emboss(Bitmap s) { return Convolve(s, new float[] { -2, -1, 0, -1, 1, 1, 0, 1, 2 }, 1, 0); }
        public static Bitmap EdgeDetect(Bitmap s) { return Convolve(s, new float[] { -1, -1, -1, -1, 8, -1, -1, -1, -1 }, 1, 0); }

        // ------------------------------------------------------------------ colour

        static float[][] Identity()
        {
            return new float[][] { new float[] { 1, 0, 0, 0, 0 }, new float[] { 0, 1, 0, 0, 0 }, new float[] { 0, 0, 1, 0, 0 }, new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 } };
        }

        static float[][] Mul(float[][] a, float[][] b)
        {
            var r = new float[5][];
            for (int i = 0; i < 5; i++)
            {
                r[i] = new float[5];
                for (int j = 0; j < 5; j++)
                    for (int k = 0; k < 5; k++) r[i][j] += a[i][k] * b[k][j];
            }
            return r;
        }

        public static Bitmap ApplyMatrix(Bitmap src, float[][] m, float gamma)
        {
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix(m));
                if (Math.Abs(gamma - 1f) > 0.01f) ia.SetGamma(gamma);
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            return dst;
        }

        /// <summary>brightness/contrast/saturation/hue in [-100..100] (hue -180..180); gamma 0.2..3</summary>
        public static Bitmap Adjust(Bitmap src, int brightness, int contrast, int saturation, int hue, float gamma)
        {
            float b = brightness / 100f;
            float c = 1f + contrast / 100f;
            float s = 1f + saturation / 100f;
            var m = Identity();
            // saturation
            const float lr = 0.3086f, lg = 0.6094f, lb = 0.0820f;
            var sat = new float[][] {
                new float[] { lr * (1 - s) + s, lr * (1 - s), lr * (1 - s), 0, 0 },
                new float[] { lg * (1 - s), lg * (1 - s) + s, lg * (1 - s), 0, 0 },
                new float[] { lb * (1 - s), lb * (1 - s), lb * (1 - s) + s, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 } };
            m = Mul(m, sat);
            if (hue != 0)
            {
                double a = hue * Math.PI / 180.0; float cs = (float)Math.Cos(a), sn = (float)Math.Sin(a);
                var hm = new float[][] {
                    new float[] { lr + cs * (1 - lr) - sn * lr, lr - cs * lr + sn * 0.143f, lr - cs * lr - sn * (1 - lr), 0, 0 },
                    new float[] { lg - cs * lg - sn * lg, lg + cs * (1 - lg) + sn * 0.140f, lg - cs * lg + sn * lg, 0, 0 },
                    new float[] { lb - cs * lb + sn * (1 - lb), lb - cs * lb - sn * 0.283f, lb + cs * (1 - lb) + sn * lb, 0, 0 },
                    new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 } };
                m = Mul(m, hm);
            }
            float t = 0.5f * (1 - c) + b;
            var cm = new float[][] {
                new float[] { c, 0, 0, 0, 0 }, new float[] { 0, c, 0, 0, 0 }, new float[] { 0, 0, c, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 }, new float[] { t, t, t, 0, 1 } };
            m = Mul(m, cm);
            return ApplyMatrix(src, m, gamma);
        }

        public static Bitmap Grayscale(Bitmap s)
        {
            return ApplyMatrix(s, new float[][] {
                new float[] { .299f, .299f, .299f, 0, 0 }, new float[] { .587f, .587f, .587f, 0, 0 }, new float[] { .114f, .114f, .114f, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 } }, 1f);
        }

        public static Bitmap Invert(Bitmap s)
        {
            return ApplyMatrix(s, new float[][] {
                new float[] { -1, 0, 0, 0, 0 }, new float[] { 0, -1, 0, 0, 0 }, new float[] { 0, 0, -1, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 }, new float[] { 1, 1, 1, 0, 1 } }, 1f);
        }

        public static Bitmap Sepia(Bitmap s)
        {
            return ApplyMatrix(s, new float[][] {
                new float[] { .393f, .349f, .272f, 0, 0 }, new float[] { .769f, .686f, .534f, 0, 0 }, new float[] { .189f, .168f, .131f, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 }, new float[] { 0, 0, 0, 0, 1 } }, 1f);
        }

        // ------------------------------------------------------------------ geometry

        public static Bitmap Resize(Bitmap src, int w, int h)
        {
            w = Math.Max(1, w); h = Math.Max(1, h);
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            using (var ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.CompositingMode = CompositingMode.SourceCopy;
                g.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            return dst;
        }

        public static Bitmap Rotate(Bitmap src, RotateFlipType t)
        {
            var b = new Bitmap(src);
            b.RotateFlip(t);
            return ToArgb(b);
        }

        /// <summary>anchor: 0..8 (row-major, 4 = centre)</summary>
        public static Bitmap CanvasResize(Bitmap src, int nw, int nh, int anchor, Color fill, out Point offset)
        {
            nw = Math.Max(1, nw); nh = Math.Max(1, nh);
            int ax = anchor % 3, ay = anchor / 3;
            int ox = ax == 0 ? 0 : (ax == 1 ? (nw - src.Width) / 2 : nw - src.Width);
            int oy = ay == 0 ? 0 : (ay == 1 ? (nh - src.Height) / 2 : nh - src.Height);
            offset = new Point(ox, oy);
            var dst = new Bitmap(nw, nh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.Clear(fill);
                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImageUnscaled(src, ox, oy);
            }
            return dst;
        }

        public static Rectangle TrimBounds(Bitmap src, int tolerance)
        {
            int w = src.Width, h = src.Height, stride;
            var s = GetBytes(src, out stride);
            Func<int, int, bool> same = null;
            int rb = s[2], gb = s[1], bb = s[0], ab = s[3];
            same = delegate(int x, int y)
            {
                int i = y * stride + x * 4;
                return Math.Abs(s[i] - bb) <= tolerance && Math.Abs(s[i + 1] - gb) <= tolerance && Math.Abs(s[i + 2] - rb) <= tolerance && Math.Abs(s[i + 3] - ab) <= tolerance;
            };
            int top = 0, bottom = h - 1, left = 0, right = w - 1;
            bool found;
            found = false; for (; top < h && !found; top++) for (int x = 0; x < w; x++) if (!same(x, top)) { found = true; break; }
            top--;
            if (!found) return new Rectangle(0, 0, w, h);
            found = false; for (; bottom > top && !found; bottom--) for (int x = 0; x < w; x++) if (!same(x, bottom)) { found = true; break; }
            bottom++;
            found = false; for (; left < w && !found; left++) for (int y = top; y <= bottom; y++) if (!same(left, y)) { found = true; break; }
            left--;
            found = false; for (; right > left && !found; right--) for (int y = top; y <= bottom; y++) if (!same(right, y)) { found = true; break; }
            right++;
            return Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
        }

        // ------------------------------------------------------------------ decorative effects

        public static Bitmap Border(Bitmap src, int width, Color color)
        {
            width = Math.Max(1, width);
            var dst = new Bitmap(src.Width + 2 * width, src.Height + 2 * width, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.Clear(color);
                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImageUnscaled(src, width, width);
            }
            return dst;
        }

        /// <summary>Adds a blurred drop shadow around the image (uses the image's alpha channel as the silhouette).</summary>
        public static Bitmap DropShadow(Bitmap src, int dx, int dy, int blur, Color color, int opacityPct, out Point offset)
        {
            int pad = blur * 2 + Math.Max(Math.Abs(dx), Math.Abs(dy)) + 2;
            int w = src.Width + 2 * pad, h = src.Height + 2 * pad;
            offset = new Point(pad, pad);
            var layer = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(layer))
            using (var ia = new ImageAttributes())
            {
                float op = Math.Max(0, Math.Min(100, opacityPct)) / 100f;
                ia.SetColorMatrix(new ColorMatrix(new float[][] {
                    new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 }, new float[] { 0, 0, 0, 0, 0 },
                    new float[] { 0, 0, 0, op, 0 }, new float[] { color.R / 255f, color.G / 255f, color.B / 255f, 0, 1 } }));
                g.DrawImage(src, new Rectangle(pad + dx, pad + dy, src.Width, src.Height), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel, ia);
            }
            Bitmap blurred = blur > 0 ? BoxBlur(layer, blur) : layer;
            using (var g = Graphics.FromImage(blurred))
            {
                g.CompositingMode = CompositingMode.SourceOver;
                g.DrawImageUnscaled(src, pad, pad);
            }
            if (!ReferenceEquals(blurred, layer)) layer.Dispose();
            return blurred;
        }

        /// <summary>style: 0 zigzag, 1 wave, 2 torn (random). Edges eat into the image by up to 'depth' px.</summary>
        public static Bitmap EdgeEffect(Bitmap src, bool top, bool right, bool bottom, bool left, int depth, int tooth, int style)
        {
            int w = src.Width, h = src.Height;
            depth = Math.Max(2, Math.Min(depth, Math.Min(w, h) / 4));
            tooth = Math.Max(4, tooth);
            var rnd = new Random(7);
            var pts = new List<PointF>();
            Func<int, float> off = delegate(int i)
            {
                switch (style)
                {
                    case 0: return (i % 2 == 0) ? 0 : depth;
                    case 1: return depth * (0.5f + 0.5f * (float)Math.Sin(i * 0.7));
                    default: return (float)rnd.NextDouble() * depth;
                }
            };
            int n = Math.Max(2, w / tooth), m = Math.Max(2, h / tooth);
            for (int i = 0; i <= n; i++) pts.Add(new PointF(w * (float)i / n, top ? off(i) : 0));
            for (int i = 0; i <= m; i++) pts.Add(new PointF(w - (right ? off(i) : 0), h * (float)i / m));
            for (int i = 0; i <= n; i++) pts.Add(new PointF(w - w * (float)i / n, h - (bottom ? off(i) : 0)));
            for (int i = 0; i <= m; i++) pts.Add(new PointF(left ? off(i) : 0, h - h * (float)i / m));
            var dst = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            using (var path = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                path.AddPolygon(pts.ToArray());
                g.SetClip(path);
                g.DrawImageUnscaled(src, 0, 0);
            }
            return dst;
        }

        public static Bitmap RoundCorners(Bitmap src, int radius)
        {
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            radius = Math.Max(1, Math.Min(radius, Math.Min(src.Width, src.Height) / 2));
            using (var g = Graphics.FromImage(dst))
            using (var p = new GraphicsPath())
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int d = radius * 2;
                p.AddArc(0, 0, d, d, 180, 90); p.AddArc(src.Width - d, 0, d, d, 270, 90);
                p.AddArc(src.Width - d, src.Height - d, d, d, 0, 90); p.AddArc(0, src.Height - d, d, d, 90, 90);
                p.CloseFigure();
                g.SetClip(p);
                g.DrawImageUnscaled(src, 0, 0);
            }
            return dst;
        }

        public static Bitmap Reflection(Bitmap src, int heightPct, int gap)
        {
            int rh = Math.Max(1, src.Height * heightPct / 100);
            var dst = new Bitmap(src.Width, src.Height + gap + rh, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.DrawImageUnscaled(src, 0, 0);
                using (var flip = new Bitmap(src))
                {
                    flip.RotateFlip(RotateFlipType.RotateNoneFlipY);
                    using (var part = ScreenGrabber.Crop(flip, new Rectangle(0, 0, src.Width, rh)))
                    {
                        int stride;
                        var bytes = GetBytes(part, out stride);
                        for (int y = 0; y < rh; y++)
                        {
                            float a = 0.55f * (1f - (float)y / rh);
                            for (int x = 0; x < part.Width; x++) { int i = y * stride + x * 4 + 3; bytes[i] = (byte)(bytes[i] * a); }
                        }
                        using (var faded = FromBytes(bytes, part.Width, rh, stride)) g.DrawImageUnscaled(faded, 0, src.Height + gap);
                    }
                }
            }
            return dst;
        }

        public static Bitmap WatermarkText(Bitmap src, string text, string fontName, float fontPx, Color color, int opacityPct, int anchor, int margin, bool bold)
        {
            var dst = new Bitmap(src);
            using (var g = Graphics.FromImage(dst))
            using (var f = new Font(fontName, fontPx, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel))
            {
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                var sz = g.MeasureString(text, f);
                PointF p = AnchorPoint(src.Width, src.Height, sz.Width, sz.Height, anchor, margin);
                using (var b = new SolidBrush(Color.FromArgb(Math.Max(0, Math.Min(255, opacityPct * 255 / 100)), color)))
                    g.DrawString(text, f, b, p);
            }
            return dst;
        }

        public static Bitmap WatermarkImage(Bitmap src, Bitmap mark, int scalePct, int opacityPct, int anchor, int margin)
        {
            var dst = new Bitmap(src);
            int mw = Math.Max(1, mark.Width * scalePct / 100), mh = Math.Max(1, mark.Height * scalePct / 100);
            using (var g = Graphics.FromImage(dst))
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix(new float[][] {
                    new float[] { 1, 0, 0, 0, 0 }, new float[] { 0, 1, 0, 0, 0 }, new float[] { 0, 0, 1, 0, 0 },
                    new float[] { 0, 0, 0, opacityPct / 100f, 0 }, new float[] { 0, 0, 0, 0, 1 } }));
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var p = AnchorPoint(src.Width, src.Height, mw, mh, anchor, margin);
                g.DrawImage(mark, new Rectangle((int)p.X, (int)p.Y, mw, mh), 0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel, ia);
            }
            return dst;
        }

        static PointF AnchorPoint(float W, float H, float w, float h, int anchor, int margin)
        {
            int ax = anchor % 3, ay = anchor / 3;
            float x = ax == 0 ? margin : (ax == 1 ? (W - w) / 2 : W - w - margin);
            float y = ay == 0 ? margin : (ay == 1 ? (H - h) / 2 : H - h - margin);
            return new PointF(x, y);
        }

        // ------------------------------------------------------------------ flood fill

        public static Bitmap FloodFill(Bitmap src, int sx, int sy, Color fill, int tolerance)
        {
            int w = src.Width, h = src.Height, stride;
            if (sx < 0 || sy < 0 || sx >= w || sy >= h) return new Bitmap(src);
            var s = GetBytes(src, out stride);
            int i0 = sy * stride + sx * 4;
            byte tb = s[i0], tg = s[i0 + 1], tr = s[i0 + 2], ta = s[i0 + 3];
            int tol = Math.Max(0, Math.Min(255, tolerance * 255 / 100));
            var visited = new bool[w * h];
            var stack = new Stack<int>();
            stack.Push(sy * w + sx);
            Func<int, int, bool> match = delegate(int x, int y)
            {
                int i = y * stride + x * 4;
                return Math.Abs(s[i] - tb) <= tol && Math.Abs(s[i + 1] - tg) <= tol && Math.Abs(s[i + 2] - tr) <= tol && Math.Abs(s[i + 3] - ta) <= tol;
            };
            var d = (byte[])s.Clone();
            while (stack.Count > 0)
            {
                int p = stack.Pop();
                int x = p % w, y = p / w;
                if (visited[p] || !match(x, y)) continue;
                int xl = x, xr = x;
                while (xl > 0 && !visited[y * w + xl - 1] && match(xl - 1, y)) xl--;
                while (xr < w - 1 && !visited[y * w + xr + 1] && match(xr + 1, y)) xr++;
                for (int xx = xl; xx <= xr; xx++)
                {
                    visited[y * w + xx] = true;
                    int i = y * stride + xx * 4;
                    d[i] = fill.B; d[i + 1] = fill.G; d[i + 2] = fill.R; d[i + 3] = fill.A;
                    if (y > 0 && !visited[(y - 1) * w + xx]) stack.Push((y - 1) * w + xx);
                    if (y < h - 1 && !visited[(y + 1) * w + xx]) stack.Push((y + 1) * w + xx);
                }
            }
            return FromBytes(d, w, h, stride);
        }

        /// <summary>Removes a vertical or horizontal band from the image and joins the two remaining parts.</summary>
        public static Bitmap CutBand(Bitmap src, bool vertical, int from, int to)
        {
            if (to < from) { int t = from; from = to; to = t; }
            if (vertical)
            {
                from = Cl(from, 0, src.Width); to = Cl(to, 0, src.Width);
                int nw = src.Width - (to - from);
                if (nw < 1) return new Bitmap(src);
                var dst = new Bitmap(nw, src.Height, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(dst))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(src, new Rectangle(0, 0, from, src.Height), new Rectangle(0, 0, from, src.Height), GraphicsUnit.Pixel);
                    g.DrawImage(src, new Rectangle(from, 0, src.Width - to, src.Height), new Rectangle(to, 0, src.Width - to, src.Height), GraphicsUnit.Pixel);
                }
                return dst;
            }
            else
            {
                from = Cl(from, 0, src.Height); to = Cl(to, 0, src.Height);
                int nh = src.Height - (to - from);
                if (nh < 1) return new Bitmap(src);
                var dst = new Bitmap(src.Width, nh, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(dst))
                {
                    g.CompositingMode = CompositingMode.SourceCopy;
                    g.DrawImage(src, new Rectangle(0, 0, src.Width, from), new Rectangle(0, 0, src.Width, from), GraphicsUnit.Pixel);
                    g.DrawImage(src, new Rectangle(0, from, src.Width, src.Height - to), new Rectangle(0, to, src.Width, src.Height - to), GraphicsUnit.Pixel);
                }
                return dst;
            }
        }

        /// <summary>Composite the image over a solid background (needed for formats without alpha).</summary>
        public static Bitmap Flatten(Bitmap src, Color back)
        {
            var dst = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dst))
            {
                g.Clear(back);
                g.DrawImageUnscaled(src, 0, 0);
            }
            return dst;
        }

        public static bool HasTransparency(Bitmap src)
        {
            int stride;
            var s = GetBytes(src, out stride);
            for (int y = 0; y < src.Height; y++)
                for (int x = 0; x < src.Width; x++)
                    if (s[y * stride + x * 4 + 3] < 255) return true;
            return false;
        }
    }
}
