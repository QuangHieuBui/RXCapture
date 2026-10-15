using System;
using System.IO;

namespace RXCapture
{
    /// <summary>Turns a working-copy recording (the library's AVI and its WAV) into the file the user asks for. The encoding happens here, when the
    /// recording is saved, not when it is recorded.</summary>
    public static class VideoExport
    {
        /// <summary>The nominal frame rate of a recording (15 when the file does not say).</summary>
        public static int FpsOf(string file)
        {
            try { using (var rd = new Mp4Writer.Reader(file)) return Math.Max(1, rd.Fps); }
            catch { return 15; }
        }

        /// <summary>Writes the MP4 (Windows' H.264 and AAC encoders, ffmpeg when Windows has none). A recording whose sound cannot be added still gets its picture
        /// (<paramref name="soundDropped"/>). Nothing is left at <paramref name="mp4"/> when this returns false.</summary>
        public static bool WriteMp4(string avi, string wavOrNull, string mp4, out bool soundDropped)
        {
            soundDropped = false;
            Mp4Writer.Encoder.Quality = AppSettings.QualityFor(AppSettings.Current.VideoQuality);       // how small the MP4 gets
            int fps = FpsOf(avi);
            bool done = Mp4Writer.Convert(avi, mp4, fps, wavOrNull);
            if (!done && wavOrNull != null)
            {
                done = Mp4Writer.Convert(avi, mp4, fps);                                              // no AAC encoder: keep the picture at least
                soundDropped = done;
            }
            if (!done)
            {
                string ff = VideoRecorder.FindFfmpeg();                                               // fallback when Windows has none (N editions)
                if (ff != null && VideoRecorder.RunFfmpeg(ff, avi, mp4)) { done = true; soundDropped = wavOrNull != null; }
            }
            if (!done) try { File.Delete(mp4); } catch { }
            return done;
        }

        /// <summary>Saves a working copy as <paramref name="dest"/>; <paramref name="format"/> is "mp4" or "gif".
        /// MP4: the item becomes an MP4 item (its file is <c>id.mp4</c> in the library) and the working AVI and WAV are deleted.
        /// GIF: the working copy stays (a GIF cannot be edited or turned into an MP4 again).
        /// On any failure the item, its AVI and WAV are untouched and nothing is left behind; false is returned.</summary>
        public static bool SaveWorkingCopy(LibItem it, string dest, string format, out bool soundDropped)
        {
            soundDropped = false;
            if (it == null || !it.IsVideo || !File.Exists(it.File)) return false;
            string libMp4 = null, part = dest + ".part";
            try
            {
                string avi = it.File;
                if (format == "gif")
                {
                    VideoRecorder.AviToGif(avi, part, AppSettings.Current.GifMaxWidth, FpsOf(avi));
                    Place(part, dest);
                    LibraryStore.SetExport(it, dest);
                    return true;
                }
                libMp4 = Path.Combine(LibraryStore.Dir, it.Id + ".mp4");
                if (!WriteMp4(avi, LibraryStore.SoundFile(it), libMp4, out soundDropped)) return false;
                File.Copy(libMp4, part, true);
                Place(part, dest);
                LibraryStore.ReplaceVideoFile(it, libMp4);
                LibraryStore.SetExport(it, dest);
                return true;
            }
            catch
            {
                try { if (libMp4 != null && !string.Equals(libMp4, it.File, StringComparison.OrdinalIgnoreCase)) File.Delete(libMp4); } catch { }
                try { File.Delete(part); } catch { }
                return false;
            }
        }

        /// <summary>Moves the finished temporary file into place (replacing an older file), so a failed write never damages the destination.</summary>
        static void Place(string part, string dest)
        {
            if (File.Exists(dest)) File.Delete(dest);
            File.Move(part, dest);
        }
    }
}
