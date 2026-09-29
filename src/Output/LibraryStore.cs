using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;

namespace RXCapture
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
        public string ExportPath;   // the file this item was last saved to (Save / Save As): it is the main file, Save writes there again
        public long ExportTicks;    // that file's last-write time (UTC ticks) right after we wrote it: a different value means someone else changed it
        public bool Closed;         // closed = hidden from the lists; the files stay in the library (Delete is what removes them)
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
        /// <summary>Raised just before an item's files are deleted so viewers can release them.</summary>
        public static event Action<LibItem> Deleting;
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
            File.WriteAllText(MetaPath(id), "dur=" + seconds);
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

        /// <summary>The items that are shown in the lists (closed ones are left out), newest first.</summary>
        public static List<LibItem> List() { return List(false); }

        public static List<LibItem> List(bool includeClosed)
        {
            var list = new List<LibItem>();
            try
            {
                // an item is listed through its thumbnail; a project / video without one (e.g. restored from the Recycle Bin) gets it rebuilt
                var ids = new HashSet<string>();
                foreach (var t in Directory.GetFiles(Dir, "*.thumb.png")) ids.Add(Path.GetFileName(t).Replace(".thumb.png", ""));
                foreach (var pf in Directory.GetFiles(Dir))
                {
                    string name = Path.GetFileName(pf), ext = Path.GetExtension(pf).ToLowerInvariant();
                    if (name.EndsWith(".thumb.png") || (ext != ".scp" && ext != ".mp4" && ext != ".avi" && ext != ".gif")) continue;
                    string pid = Path.GetFileNameWithoutExtension(pf);
                    if (!ids.Contains(pid) && EnsureThumb(pid, pf)) ids.Add(pid);
                }
                foreach (var id in ids)
                {
                    string t = Path.Combine(Dir, id + ".thumb.png");
                    var it = new LibItem { Id = id, ThumbFile = t };
                    string scp = Path.Combine(Dir, id + ".scp");
                    if (File.Exists(scp)) { it.File = scp; it.Ext = "png"; }
                    else
                    {
                        var vf = Directory.GetFiles(Dir, id + ".*").FirstOrDefault(f => !f.EndsWith(".thumb.png") && !f.EndsWith(".meta"));
                        if (vf == null) continue;
                        it.File = vf; it.IsVideo = true; it.Ext = Path.GetExtension(vf).TrimStart('.').ToLowerInvariant();
                    }
                    var meta = ReadMeta(id);
                    string v; long n; int d;
                    if (meta.TryGetValue("dur", out v) && int.TryParse(v, out d)) it.DurationSec = d;
                    if (meta.TryGetValue("export", out v) && v.Length > 0) it.ExportPath = v;
                    if (meta.TryGetValue("exportTime", out v) && long.TryParse(v, out n)) it.ExportTicks = n;
                    if (meta.TryGetValue("closed", out v) && v == "1") it.Closed = true;
                    if (it.Closed && !includeClosed) continue;
                    DateTime dt;
                    it.Created = DateTime.TryParseExact(id, "yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture, DateTimeStyles.None, out dt) ? dt : File.GetCreationTime(it.File);
                    list.Add(it);
                }
            }
            catch { }
            list.Sort((a, b) => b.Created.CompareTo(a.Created));
            return list;
        }

        /// <summary>Rebuilds the thumbnail of a project or video whose thumbnail is missing (and a video's length).</summary>
        static bool EnsureThumb(string id, string file)
        {
            try
            {
                Bitmap src;
                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".scp") src = Document.LoadProject(file).Render();
                else if (ext == ".gif") { using (var im = Image.FromFile(file)) src = new Bitmap(im); }
                else
                {
                    using (var rd = new Mp4Writer.Reader(file))
                    {
                        src = new Bitmap(rd.Width, rd.Height, PixelFormat.Format32bppRgb);
                        TimeSpan ts;
                        if (!rd.ReadFrame(src, out ts)) { src.Dispose(); return false; }
                        var m = ReadMeta(id);
                        if (!m.ContainsKey("dur") && rd.Duration.TotalSeconds > 0) { m["dur"] = ((int)Math.Round(rd.Duration.TotalSeconds)).ToString(); WriteMeta(id, m); }
                    }
                }
                using (src) using (var th = MakeThumb(src, 260, 160)) th.Save(Path.Combine(Dir, id + ".thumb.png"), ImageFormat.Png);
                return true;
            }
            catch { return false; }
        }

        /// <summary>Hides items from the lists (Close) or shows them again. Nothing is deleted.</summary>
        public static void SetClosed(IEnumerable<LibItem> items, bool closed)
        {
            foreach (var it in items)
                try
                {
                    var m = ReadMeta(it.Id);
                    if (closed) m["closed"] = "1"; else m.Remove("closed");
                    WriteMeta(it.Id, m);
                    it.Closed = closed;
                }
                catch { }
            Raise();
        }

        public static int ClosedCount() { return List(true).Count(x => x.Closed); }

        public static void RestoreClosed() { SetClosed(List(true).Where(x => x.Closed).ToList(), false); }

        // ---- the ".meta" side file holds small key=value facts: dur (videos), export + exportTime (the file it was saved to)

        static string MetaPath(string id) { return Path.Combine(Dir, id + ".meta"); }

        static Dictionary<string, string> ReadMeta(string id)
        {
            var d = new Dictionary<string, string>();
            try
            {
                string p = MetaPath(id);
                if (File.Exists(p))
                    foreach (var line in File.ReadAllLines(p)) { int i = line.IndexOf('='); if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim(); }
            }
            catch { }
            return d;
        }

        static void WriteMeta(string id, Dictionary<string, string> d)
        {
            File.WriteAllLines(MetaPath(id), d.Select(kv => kv.Key + "=" + kv.Value).ToArray());
        }

        /// <summary>Makes <paramref name="path"/> the main file of this item: Save writes there again, and the item is found again when that file is opened.</summary>
        public static void SetExport(LibItem it, string path)
        {
            if (it == null || string.IsNullOrEmpty(path)) return;
            try
            {
                long ticks = File.Exists(path) ? File.GetLastWriteTimeUtc(path).Ticks : 0;
                var m = ReadMeta(it.Id);
                m["export"] = Path.GetFullPath(path); m["exportTime"] = ticks.ToString();
                WriteMeta(it.Id, m);
                it.ExportPath = Path.GetFullPath(path); it.ExportTicks = ticks;
                Raise();                                       // the tray shows the file name under the thumbnail
            }
            catch { }
        }

        /// <summary>The image item that was saved to <paramref name="path"/> and whose file is still untouched, or null.</summary>
        public static LibItem FindByExport(string path)
        {
            try
            {
                string full = Path.GetFullPath(path);
                if (!File.Exists(full)) return null;
                long now = File.GetLastWriteTimeUtc(full).Ticks;
                foreach (var it in List(true))
                    if (!it.IsVideo && it.ExportPath != null && string.Equals(Path.GetFullPath(it.ExportPath), full, StringComparison.OrdinalIgnoreCase)
                        && Math.Abs(it.ExportTicks - now) < TimeSpan.FromSeconds(2).Ticks) return it;
            }
            catch { }
            return null;
        }

        /// <summary>Loads an item's project and reconnects it to the file it was saved to.</summary>
        public static Document LoadDoc(LibItem it)
        {
            var d = Document.LoadProject(it.File);
            d.ExportPath = it.ExportPath;
            return d;
        }

        /// <summary>Deletes an item. With <paramref name="recycle"/> the main file (project / video) goes to the Windows Recycle Bin, so a mistake can be undone:
        /// restore it into this folder and the item shows up again (its thumbnail is rebuilt).</summary>
        public static void Delete(LibItem it, bool recycle = false)
        {
            if (Deleting != null) try { Deleting(it); } catch { }
            DeleteFiles(it.Id, recycle);
            Raise();
        }

        /// <summary>Deletes several items with a single change notification.</summary>
        public static void DeleteMany(IEnumerable<LibItem> list, bool recycle = false)
        {
            foreach (var it in list)
            {
                if (Deleting != null) try { Deleting(it); } catch { }
                DeleteFiles(it.Id, recycle);
            }
            Raise();
        }

        static void DeleteFiles(string id, bool recycle)
        {
            try
            {
                foreach (var f in Directory.GetFiles(Dir, id + ".*"))
                {
                    bool main = !f.EndsWith(".thumb.png") && !f.EndsWith(".meta");       // only the main file is worth restoring; thumbnail and notes are rebuilt
                    if (recycle && main)
                    {
                        try { Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(f, Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs, Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin); continue; }
                        catch { }
                    }
                    File.Delete(f);
                }
            }
            catch { }
        }

        /// <summary>Keeps the library at its size limit. Only unsaved items are pruned (oldest first, into the Recycle Bin):
        /// an item that has been saved to a file is never removed automatically.</summary>
        static void Trim()
        {
            try
            {
                var all = List(true);
                int max = Math.Max(20, AppSettings.Current.LibraryMax);
                for (int i = max; i < all.Count; i++)
                    if (string.IsNullOrEmpty(all[i].ExportPath)) Delete(all[i], true);
            }
            catch { }
        }
    }
}
