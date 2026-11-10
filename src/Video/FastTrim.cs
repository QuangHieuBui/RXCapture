using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace RXCapture
{
    public static partial class Mp4Writer
    {
        static readonly Guid FormatNv12 =new Guid("3231564e-0000-0010-8000-00aa00389b71");

        /// <summary>Trim without ever making an RGB picture: the decoder's NV12 frames go straight to the encoder (hardware ones when the PC has them).
        /// Decoding to RGB32 and encoding from it took ~13 ms of every frame; this skips both colour conversions and the copy into a Bitmap.
        /// False (nothing left at <paramref name="dst"/>) when it cannot be done - <see cref="Trim"/> then takes its slower way.</summary>
        static bool TrimFast(string src, string dst, TimeSpan start, TimeSpan end, out TimeSpan kept)
        {
            kept = TimeSpan.Zero;
            if (src.EndsWith(".avi", StringComparison.OrdinalIgnoreCase)) return false;          // AVI working copies are JPEG: the Reader decodes them
            bool started = false, ok = false;
            IPcmReader audio = null; IMFSourceReader reader = null; IMFSinkWriter writer = null;
            try
            {
                if (MFStartup(MfVersion, 0) != 0) return false;
                started = true;
                IMFAttributes attrs; Check(MFCreateAttributes(out attrs, 1));
                Check(attrs.SetUINT32(EnableVideoProcessing, 1));
                Check(MFCreateSourceReaderFromURL(src, attrs, out reader));
                Check(reader.SetStreamSelection(AllStreams, false));
                Check(reader.SetStreamSelection(FirstVideoStream, true));
                IMFMediaType want; Check(MFCreateMediaType(out want));
                Check(want.SetGUID(MajorType, MediaVideo));
                Check(want.SetGUID(Subtype, FormatNv12));
                Check(reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, want));
                IMFMediaType cur; Check(reader.GetCurrentMediaType(FirstVideoStream, out cur));
                ulong size; Check(cur.GetUINT64(FrameSize, out size));
                int w = (int)(size >> 32), h = (int)(size & 0xffffffff), fps = 15;
                ulong rate;
                if (cur.GetUINT64(FrameRate, out rate) == 0 && (uint)(rate & 0xffffffff) != 0) fps = Math.Max(1, (int)Math.Round((double)(rate >> 32) / (uint)(rate & 0xffffffff)));
                if (((w | h) & 1) != 0) return false;
                // The decoder pads NV12 rows (and the rows of the luma plane before the chroma plane) to a multiple of 16: frames are repacked to w x h
                // below, so the encoder is told the rows are w bytes apart.
                Check(cur.SetUINT32(DefaultStride, w));
                long frameDur = 10000000L / fps, s100 = start.Ticks, e100 = end.Ticks;
                if (s100 > 0) SeekReader(reader, s100);                                          // lands on the key frame before it; the frames up to the start are dropped below
                audio = OpenAudio(src);                                                          // null when the video has no sound
                int audioStream = -1;
                for (int mode = 1; mode <= 2 && writer == null; mode++)                          // hardware encoder first, then software
                {
                    try { writer = Open(dst, w, h, fps, audio != null ? audio.Rate : 0, audio != null ? audio.Channels : 0, out audioStream, mode, cur); }
                    catch { if (mode == 2) throw; try { File.Delete(dst); } catch { } }
                }
                AudioFeed feed = audio == null ? null : new AudioFeed(new TrimmedAudio(audio, start, end), writer, audioStream, audio.Rate, audio.BlockAlign);
                int written = 0; long lastOut = 0;
                while (true)
                {
                    int idx, flags; long ts; IMFSample sample;
                    Check(reader.ReadSample(FirstVideoStream, 0, out idx, out flags, out ts, out sample));
                    if ((flags & EndOfStream) != 0) { if (sample != null) Marshal.ReleaseComObject(sample); break; }
                    if (sample == null) continue;
                    try
                    {
                        if (ts >= e100) break;
                        if (ts + frameDur <= s100) continue;
                        long outTime = Math.Max(0, ts - s100);
                        if (feed != null) feed.Pump(outTime);
                        IMFSample packed = PackNv12(sample, w, h, outTime, frameDur);
                        try { Check(writer.WriteSample(0, packed)); }
                        finally { Marshal.ReleaseComObject(packed); }
                        written++; lastOut = outTime;
                    }
                    finally { Marshal.ReleaseComObject(sample); }
                }
                if (written > 0)
                {
                    if (feed != null) feed.Drain();
                    Check(writer.Finalize_());
                    kept = TimeSpan.FromTicks(lastOut + frameDur);
                    ok = true;
                }
            }
            catch { ok = false; }
            finally
            {
                if (audio != null) audio.Dispose();
                if (writer != null) { try { Marshal.ReleaseComObject(writer); } catch { } }
                if (reader != null) { try { Marshal.ReleaseComObject(reader); } catch { } }
                if (started) try { MFShutdown(); } catch { }
                if (!ok) try { File.Delete(dst); } catch { }
            }
            return ok && File.Exists(dst);
        }

        /// <summary>The decoded NV12 frame as a new sample with no padding: w x h luma, then w x h/2 chroma, rows w bytes apart.</summary>
        static unsafe IMFSample PackNv12(IMFSample src, int w, int h, long time, long duration)
        {
            IMFMediaBuffer sb; Check(src.ConvertToContiguousBuffer(out sb));
            var b2 = sb as IMF2DBuffer;
            if (b2 == null) { Marshal.ReleaseComObject(sb); throw new InvalidOperationException("the decoded frame is not a 2-D buffer"); }
            IntPtr scan0; int pitch, len;
            Check(b2.Lock2D(out scan0, out pitch));
            try
            {
                Check(b2.GetContiguousLength(out len));
                int rows = pitch > 0 ? (int)((long)len * 2 / (3L * pitch)) : 0;                  // luma rows the decoder allocated: the chroma plane starts after them
                if (pitch < w || rows < h) throw new InvalidOperationException("unexpected NV12 layout");
                int total = w * h * 3 / 2;
                IMFMediaBuffer db; Check(MFCreateMemoryBuffer(total, out db));
                try
                {
                    IntPtr d; int max, cur; Check(db.Lock(out d, out max, out cur));
                    try
                    {
                        byte* s = (byte*)scan0, o = (byte*)d;
                        for (int y = 0; y < h; y++) Buffer.MemoryCopy(s + (long)y * pitch, o + (long)y * w, w, w);
                        byte* sc = s + (long)rows * pitch, oc = o + (long)w * h;
                        for (int y = 0; y < h / 2; y++) Buffer.MemoryCopy(sc + (long)y * pitch, oc + (long)y * w, w, w);
                    }
                    finally { db.Unlock(); }
                    Check(db.SetCurrentLength(total));
                    IMFSample ns; Check(MFCreateSample(out ns));
                    Check(ns.AddBuffer(db));
                    Check(ns.SetSampleTime(time));
                    Check(ns.SetSampleDuration(duration));
                    return ns;
                }
                finally { Marshal.ReleaseComObject(db); }
            }
            finally { b2.Unlock2D(); Marshal.ReleaseComObject(sb); }
        }

        static void SeekReader(IMFSourceReader reader, long ticks)
        {
            IntPtr pv = Marshal.AllocHGlobal(32);
            try
            {
                for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(pv, i, 0);
                Marshal.WriteInt16(pv, 0, 20);                                                   // VT_I8
                Marshal.WriteInt64(pv, 8, Math.Max(0, ticks));
                Check(reader.SetCurrentPosition(Guid.Empty, pv));
            }
            finally { Marshal.FreeHGlobal(pv); }
        }

        /// <summary>The first picture a trim keeps (the library thumbnail of the new video), or null.</summary>
        static Bitmap FirstKeptFrame(string src, TimeSpan start)
        {
            using (var rd = new Reader(src))
            using (var frame = new Bitmap(rd.Width, rd.Height, PixelFormat.Format32bppRgb))
            {
                long frameDur = 10000000L / rd.Fps; TimeSpan ts;
                if (start > TimeSpan.Zero) rd.Seek(start);
                while (rd.ReadFrame(frame, out ts))
                    if (ts.Ticks + frameDur > start.Ticks) return new Bitmap(frame);
            }
            return null;
        }
    }
}
