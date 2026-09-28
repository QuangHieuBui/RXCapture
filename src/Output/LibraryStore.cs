using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ShotCraft
{
    public class LibItem
    {
        public string Id;
        public string File;         // .scp project (images) or video file
        public string ThumbFile;
        public bool IsVideo;
        public string Ext;          // shown as label: png / avi / gif / mp4
        public int DurationSec;
        public DateTime Created;
    }

    /// <summary>
    /// The capture library: every capture is saved automatically as an editable project (.scp) with a thumbnail,
    /// videos are stored next to their thumbnail. Nothing is lost when the editor is closed.
    /// </summary>
    public static class LibraryStore
    {
        public static string Dir
        {
            get
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var old = Path.Combine(root, "ShotCraft");                     // the app used to be called ShotCraft: keep its library
                if (!Directory.Exists(Path.Combine(root, "RXCapture")) && Directory.Exists(old)) { try { Directory.Move(old, Path.Combine(root, "RXCapture")); } catch { } }
                var d = Path.Combine(root, "RXCapture", "Library");
                Directory.CreateDirectory(d);
                return d;
            }
        }

        public static event EventHandler Changed;
        static void Raise() { if (Changed != null) Changed(null, EventArgs.Empty); }

        static string NewId()
        {
            string id = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            return id;
        }

        public static LibItem AddImage(Bitmap bmp, out Document doc)
        {
            doc = new Document(bmp);
            string id = NewId();
            var item = new LibItem { Id = id, File = Path.Combine(Dir, id + ".scp"), ThumbFile = Path.Combine(Dir, id + ".thumb.png"), Ext = "png", Created = DateTime.Now };
            Save(item, doc);
            Trim();
            Raise();
            return item;
        }

        public static void Save(LibItem item, Document doc)
        {
            doc.SaveProject(item.File);
            try
            {
                using (var flat = doc.Render())
                using (var th = MakeThumb(flat, 260, 160))
                    th.Save(item.ThumbFile, ImageFormat.Png);
            }
            catch { }
            Raise();
        }

        public static LibItem AddVideo(string videoFile, Bitmap firstFrame, int seconds)
        {
            string id = NewId();
            string ext = Path.GetExtension(videoFile).TrimStart('.').ToLowerInvariant();
            string dest = Path.Combine(Dir, id + "." + ext);
            File.Move(videoFile, dest);
            var item = new LibItem { Id = id, File = dest, ThumbFile = Path.Combine(Dir, id + ".thumb.png"), IsVideo = true, Ext = ext, DurationSec = seconds, Created = DateTime.Now };
            using (var th = MakeThumb(firstFrame, 260, 160)) th.Save(item.ThumbFile, ImageFormat.Png);
            File.WriteAllText(Path.Combine(Dir, id + ".meta"), "dur=" + seconds);
            Trim();
            Raise();
            return item;
        }

        public static Bitmap MakeThumb(Bitmap src, int maxW, int maxH)
        {
            float k = Math.Min((float)maxW / src.Width, (float)maxH / src.Height);
            if (k > 1) k = 1;
            int w = Math.Max(1, (int)(src.Width * k)), h = Math.Max(1, (int)(src.Height * k));
            var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(b))
            {
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(src, 0, 0, w, h);
            }
            return b;
        }

        public static List<LibItem> List()
        {
            var list = new List<LibItem>();
            try
            {
                foreach (var t in Directory.GetFiles(Dir, "*.thumb.png"))
                {
                    string id = Path.GetFileName(t).Replace(".thumb.png", "");
                    var it = new LibItem { Id = id, ThumbFile = t };
                    string scp = Path.Combine(Dir, id + ".scp");
                    if (File.Exists(scp)) { it.File = scp; it.Ext = "png"; }
                    else
                    {
                        var vf = Directory.GetFiles(Dir, id + ".*").FirstOrDefault(f => !f.EndsWith(".thumb.png") && !f.EndsWith(".meta"));
                        if (vf == null) continue;
                        it.File = vf; it.IsVideo = true; it.Ext = Path.GetExtension(vf).TrimStart('.').ToLowerInvariant();
                        string meta = Path.Combine(Dir, id + ".meta");
                        if (File.Exists(meta))
                        {
                            var line = File.ReadAllText(meta).Trim();
                            int d; if (line.StartsWith("dur=") && int.TryParse(line.Substring(4), out d)) it.DurationSec = d;
                        }
                    }
                    DateTime dt;
                    it.Created = DateTime.TryParseExact(id, "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ? dt : File.GetCreationTime(it.File);
                    list.Add(it);
                }
            }
            catch { }
            list.Sort((a, b) => b.Created.CompareTo(a.Created));
            return list;
        }

        public static void Delete(LibItem it)
        {
            try
            {
                foreach (var f in Directory.GetFiles(Dir, it.Id + ".*")) File.Delete(f);
            }
            catch { }
            Raise();
        }

        static void Trim()
        {
            try
            {
                var all = List();
                int max = Math.Max(20, AppSettings.Current.LibraryMax);
                for (int i = max; i < all.Count; i++) Delete(all[i]);
            }
            catch { }
        }
    }
}
