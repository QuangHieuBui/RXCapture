using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;

namespace RXCapture
{
    /// <summary>Size-specific Fluent Regular artwork, embedded so rendering needs no font, SVG engine or network.</summary>
    static class FluentIcons
    {
        static readonly int[] Sizes = { 12, 14, 16, 18, 20, 24, 26, 28, 30, 32, 36, 40, 42, 48, 56, 64, 72, 80, 96, 128 };
        static readonly string[] Names = ReadNames();
        static readonly Dictionary<int, Bitmap> sheets = new Dictionary<int, Bitmap>();

        static string[] ReadNames()
        {
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("fluent-names.txt"))
            {
                if (stream == null) return new string[0];
                using (var reader = new StreamReader(stream)) return reader.ReadToEnd().Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            }
        }

        public static Bitmap Create(string name, int size, bool light)
        {
            int index = Array.IndexOf(Names, name);
            if (index < 0) return null;
            int native = Sizes[Sizes.Length - 1];
            foreach (int candidate in Sizes) if (candidate >= size) { native = candidate; break; }
            Bitmap sheet;
            if (!sheets.TryGetValue(native, out sheet))
            {
                using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("fluent-" + native + ".png"))
                {
                    if (stream == null) return null;
                    using (var image = new Bitmap(stream)) sheet = image.Clone(new Rectangle(0, 0, image.Width, image.Height), PixelFormat.Format32bppArgb);
                }
                sheets[native] = sheet;
            }
            var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            var ink = light ? Theme.Text : Icons.Ink;
            using (var g = Graphics.FromImage(result))
            using (var attributes = new ImageAttributes())
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.InterpolationMode = native == size ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                var matrix = new ColorMatrix();
                matrix.Matrix00 = ink.R / 255f; matrix.Matrix11 = ink.G / 255f; matrix.Matrix22 = ink.B / 255f;
                attributes.SetColorMatrix(matrix);
                g.DrawImage(sheet, new Rectangle(0, 0, size, size), (index % 8) * native, (index / 8) * native, native, native, GraphicsUnit.Pixel, attributes);
            }
            return result;
        }
    }
}
