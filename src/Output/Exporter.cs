using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Printing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>Saving, clipboard, printing, e-mail and "open with" helpers.</summary>
    public static class Exporter
    {
        public const string FileFilter =
            "PNG image (*.png)|*.png|JPEG image (*.jpg)|*.jpg;*.jpeg|Bitmap (*.bmp)|*.bmp|GIF image (*.gif)|*.gif|TIFF image (*.tif)|*.tif;*.tiff|PDF document (*.pdf)|*.pdf";

        static readonly string[] FilterExt = { "png", "jpg", "bmp", "gif", "tif", "pdf" };

        public static string ExtForFilterIndex(int idx1) { return FilterExt[Math.Max(0, Math.Min(FilterExt.Length - 1, idx1 - 1))]; }
        public static int FilterIndexFor(string ext) { int i = Array.IndexOf(FilterExt, ext.ToLowerInvariant().TrimStart('.').Replace("jpeg", "jpg").Replace("tiff", "tif")); return i < 0 ? 1 : i + 1; }

        public static void Save(Bitmap bmp, string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant().TrimStart('.');
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            switch (ext)
            {
                case "jpg":
                case "jpeg": SaveJpeg(bmp, path, AppSettings.Current.JpegQuality); break;
                case "bmp": using (var f = Effects.Flatten(bmp, Color.White)) f.Save(path, ImageFormat.Bmp); break;
                case "gif": using (var f = Effects.Flatten(bmp, Color.White)) f.Save(path, ImageFormat.Gif); break;
                case "tif":
                case "tiff": bmp.Save(path, ImageFormat.Tiff); break;
                case "pdf": SavePdf(bmp, path); break;
                default: bmp.Save(path, ImageFormat.Png); break;
            }
        }

        static ImageCodecInfo Codec(ImageFormat f)
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders()) if (c.FormatID == f.Guid) return c;
            return null;
        }

        public static byte[] JpegBytes(Bitmap bmp, int quality)
        {
            using (var flat = Effects.Flatten(bmp, Color.White))
            using (var ms = new MemoryStream())
            using (var ep = new EncoderParameters(1))
            {
                ep.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Max(1, Math.Min(100, quality)));
                flat.Save(ms, Codec(ImageFormat.Jpeg), ep);
                return ms.ToArray();
            }
        }

        static void SaveJpeg(Bitmap bmp, string path, int quality) { File.WriteAllBytes(path, JpegBytes(bmp, quality)); }

        /// <summary>Single-page PDF whose page is exactly the image size (96 dpi), image embedded as JPEG.</summary>
        static void SavePdf(Bitmap bmp, string path)
        {
            byte[] jpg = JpegBytes(bmp, 92);
            double wpt = bmp.Width * 0.75, hpt = bmp.Height * 0.75;
            var offsets = new List<long>();
            using (var fs = File.Create(path))
            {
                Action<string> w = delegate(string s) { var b = Encoding.ASCII.GetBytes(s); fs.Write(b, 0, b.Length); };
                w("%PDF-1.4\n%âãÏÓ\n");
                offsets.Add(fs.Position); w("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
                offsets.Add(fs.Position); w("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
                offsets.Add(fs.Position);
                w(string.Format(System.Globalization.CultureInfo.InvariantCulture,
                    "3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {0:0.##} {1:0.##}] /Resources << /XObject << /Im0 4 0 R >> >> /Contents 5 0 R >>\nendobj\n", wpt, hpt));
                offsets.Add(fs.Position);
                w(string.Format("4 0 obj\n<< /Type /XObject /Subtype /Image /Width {0} /Height {1} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode /Length {2} >>\nstream\n", bmp.Width, bmp.Height, jpg.Length));
                fs.Write(jpg, 0, jpg.Length);
                w("\nendstream\nendobj\n");
                string content = string.Format(System.Globalization.CultureInfo.InvariantCulture, "q {0:0.##} 0 0 {1:0.##} 0 0 cm /Im0 Do Q", wpt, hpt);
                offsets.Add(fs.Position);
                w(string.Format("5 0 obj\n<< /Length {0} >>\nstream\n{1}\nendstream\nendobj\n", content.Length, content));
                long xref = fs.Position;
                w("xref\n0 6\n0000000000 65535 f \n");
                foreach (var o in offsets) w(o.ToString("D10") + " 00000 n \n");
                w(string.Format("trailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n{0}\n%%EOF\n", xref));
            }
        }

        public static void CopyToClipboard(Bitmap bmp)
        {
            try
            {
                var data = new DataObject();
                using (var flat = Effects.Flatten(bmp, Color.White))
                    data.SetData(DataFormats.Bitmap, true, new Bitmap(flat));
                var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Png);
                data.SetData("PNG", false, ms);
                Clipboard.SetDataObject(data, true, 5, 100);
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "RXCapture"); }
        }

        /// <summary>Image from the clipboard (PNG with alpha preferred).</summary>
        public static Bitmap ClipboardImage()
        {
            try
            {
                var d = Clipboard.GetDataObject();
                if (d == null) return null;
                if (d.GetDataPresent("PNG"))
                {
                    var ms = d.GetData("PNG") as MemoryStream;
                    if (ms != null) { ms.Position = 0; using (var im = Image.FromStream(ms)) return Effects.ToArgb(im); }
                }
                if (Clipboard.ContainsImage()) { using (var im = Clipboard.GetImage()) if (im != null) return Effects.ToArgb(im); }
                if (Clipboard.ContainsFileDropList())
                    foreach (string f in Clipboard.GetFileDropList())
                    {
                        string e = Path.GetExtension(f).ToLowerInvariant();
                        if (e == ".png" || e == ".jpg" || e == ".jpeg" || e == ".bmp" || e == ".gif" || e == ".tif")
                            using (var im = Image.FromFile(f)) return Effects.ToArgb(im);
                    }
            }
            catch { }
            return null;
        }

        public static void Print(Bitmap bmp, IWin32Window owner)
        {
            using (var pd = new PrintDocument())
            using (var dlg = new PrintDialog { Document = pd, UseEXDialog = true })
            {
                pd.DocumentName = "RXCapture";
                pd.PrintPage += delegate(object s, PrintPageEventArgs e)
                {
                    var area = e.MarginBounds;
                    float k = Math.Min((float)area.Width / bmp.Width, (float)area.Height / bmp.Height);
                    if (k > 1) k = 1;
                    float w = bmp.Width * k, h = bmp.Height * k;
                    using (var flat = Effects.Flatten(bmp, Color.White))
                        e.Graphics.DrawImage(flat, area.X, area.Y, w, h);
                    e.HasMorePages = false;
                };
                if (dlg.ShowDialog(owner) == DialogResult.OK) pd.Print();
            }
        }

        public static void OpenInPaint(Bitmap bmp)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "RXCapture_" + DateTime.Now.ToString("HHmmssfff") + ".png");
            bmp.Save(tmp, ImageFormat.Png);
            try { Process.Start("mspaint.exe", "\"" + tmp + "\""); } catch { Process.Start(tmp); }
        }

        public static void Reveal(string path)
        {
            try { Process.Start("explorer.exe", "/select,\"" + path + "\""); } catch { }
        }

        // ---------------------------------------------------------------- e-mail via Simple MAPI

        [StructLayout(LayoutKind.Sequential)]
        class MapiFileDesc { public int reserved; public int flags; public int position; public string path; public string name; public IntPtr type; }

        [StructLayout(LayoutKind.Sequential)]
        class MapiMessage
        {
            public int reserved; public string subject; public string noteText; public string messageType; public string dateReceived;
            public string conversationID; public int flags; public IntPtr originator; public int recipCount; public IntPtr recips; public int fileCount; public IntPtr files;
        }

        [DllImport("MAPI32.DLL")] static extern int MAPISendMail(IntPtr session, IntPtr uiParam, MapiMessage message, int flags, int reserved);

        public static bool Email(Bitmap bmp, string subject)
        {
            string tmp = Path.Combine(Path.GetTempPath(), "Rndimsx_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png");
            bmp.Save(tmp, ImageFormat.Png);
            IntPtr fd = IntPtr.Zero, files = IntPtr.Zero;
            try
            {
                var desc = new MapiFileDesc { position = -1, path = tmp, name = Path.GetFileName(tmp) };
                fd = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(MapiFileDesc)));
                Marshal.StructureToPtr(desc, fd, false);
                var msg = new MapiMessage { subject = subject, noteText = "", fileCount = 1, files = fd };
                int rc = MAPISendMail(IntPtr.Zero, IntPtr.Zero, msg, 0x9, 0);   // MAPI_LOGON_UI | MAPI_DIALOG
                return rc == 0 || rc == 1;   // 1 = user abort
            }
            catch { return false; }
            finally { if (fd != IntPtr.Zero) Marshal.FreeHGlobal(fd); }
        }
    }
}
