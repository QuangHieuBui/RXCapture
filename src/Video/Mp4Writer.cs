using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace RXCapture
{
    /// <summary>H.264 MP4 output through the Windows Media Foundation sink writer (no ffmpeg needed). Windows N editions
    /// without the Media Feature Pack have no H.264 encoder; <see cref="Convert"/> then returns false.</summary>
    public static class Mp4Writer
    {
        const int MfVersion = 0x00020070;
        static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        static readonly Guid AvgBitrate = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
        static readonly Guid InterlaceMode = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
        static readonly Guid FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d");
        static readonly Guid FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
        static readonly Guid PixelAspect = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
        static readonly Guid MediaVideo = new Guid("73646976-0000-0010-8000-00aa00389b71");
        static readonly Guid FormatH264 = new Guid("34363248-0000-0010-8000-00aa00389b71");
        static readonly Guid FormatRgb32 = new Guid("00000016-0000-0010-8000-00aa00389b71");
        static readonly Guid MediaAudio = new Guid("73647561-0000-0010-8000-00aa00389b71");
        static readonly Guid FormatAac = new Guid("00001610-0000-0010-8000-00aa00389b71");
        static readonly Guid FormatPcm = new Guid("00000001-0000-0010-8000-00aa00389b71");
        static readonly Guid AudioChannels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        static readonly Guid AudioRate = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        static readonly Guid AudioBits = new Guid("f2deb57f-40fa-4764-aa33-ed4f2d1ff669");
        static readonly Guid AudioBytesPerSec = new Guid("1aab75c8-cfef-451c-ab95-ac034b8e1731");
        static readonly Guid AudioBlockAlign = new Guid("322de230-9eeb-43bd-ab7a-ff412251541d");
        static readonly Guid AacPayload = new Guid("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        static readonly Guid AacProfileLevel = new Guid("7632f0e6-9538-4d61-acda-ea29c8c14456");

        static readonly Guid CodecRateControlMode = new Guid("1c0608e9-370c-4710-8a58-cb6181c42423");
        static readonly Guid CodecQuality = new Guid("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");

        /// <summary>How the H.264 video is encoded. Public so tests can compare settings.</summary>
        public static class Encoder
        {
            /// <summary>Bits per pixel per second at 15 fps for constant-rate encoding (Quality = 0).</summary>
            public static double BitsPerPixel = 1.5;
            /// <summary>1..100: quality-based variable bit rate (a still screen costs little, motion gets the bits); 0 = constant bit rate (BitsPerPixel).
            /// 45 = small file, 55 = balanced, 60 = high quality. Values above ~65 grow very fast.</summary>
            public static int Quality = 55;

            public static int Bitrate(int w, int h, int fps)
            {
                double b = (double)w * h * BitsPerPixel * Math.Sqrt(fps / 15.0);      // faster frame rates need fewer extra bits: frames are alike
                return (int)Math.Min(20000000.0, Math.Max(400000.0, b));
            }
        }

        /// <summary>Re-encodes the MJPEG frames of an AVI written by AviWriter as H.264 MP4. With <paramref name="wav"/> (an
        /// <see cref="AudioRecorder"/> file) the sound is added as an AAC track.</summary>
        public static bool Convert(string avi, string mp4, int fps, string wav = null)
        {
            fps = Math.Max(1, fps);
            bool started = false, ok = false;
            WavSource wavSource = null;
            try
            {
                if (MFStartup(MfVersion, 0) != 0) return false;
                started = true;
                IMFSinkWriter writer = null;
                AudioFeed feed = null;
                int frameNo = 0, w = 0, h = 0;
                long duration = 10000000L / fps;
                if (wav != null && File.Exists(wav)) wavSource = new WavSource(wav);
                // JPEG decoding is the slow part (about 70% of the time): several frames are decoded at once on the other cores while the
                // encoder works on the previous batch, so the two overlap instead of following each other
                var jpgs = AviReader.Frames(avi).GetEnumerator();
                int batchSize = 4;
                Func<System.Collections.Generic.List<byte[]>> readBatch = delegate
                {
                    var l = new System.Collections.Generic.List<byte[]>();
                    while (l.Count < batchSize && jpgs.MoveNext()) l.Add(jpgs.Current);
                    return l;
                };
                System.Threading.Tasks.Task<Raw[]> pending = null;
                var firstBatch = readBatch();
                if (firstBatch.Count > 0)
                {
                    var probe = DecodeFrame(firstBatch[0]);                                   // its size decides the batch size and opens the writer
                    w = probe.W; h = probe.H;
                    batchSize = (int)Math.Max(2, Math.Min(8, 96L * 1024 * 1024 / ((long)w * h * 4)));       // at most ~100 MB of raw frames in flight
                    int audioStream;
                    writer = Open(mp4, w, h, fps, wavSource != null ? AudioRecorder.Rate : 0, AudioRecorder.Channels, out audioStream);
                    if (wavSource != null) feed = new AudioFeed(wavSource, writer, audioStream, AudioRecorder.Rate, AudioRecorder.Channels * 2);
                    var rest = firstBatch.GetRange(1, firstBatch.Count - 1);
                    pending = StartDecode(rest);
                    if (feed != null) feed.Pump(0);
                    WriteRaw(writer, probe, 0, duration); frameNo = 1;
                    while (pending != null)
                    {
                        var frames = pending.Result;
                        var more = readBatch();
                        pending = more.Count > 0 ? StartDecode(more) : null;                  // decode the next batch while this one is encoded
                        foreach (var raw in frames)
                        {
                            if (raw == null || raw.W != w || raw.H != h) continue;             // recordings have a fixed size; ignore strays
                            if (feed != null) feed.Pump(frameNo * duration);                   // keep the sound level with the pictures
                            WriteRaw(writer, raw, frameNo * duration, duration);
                            frameNo++;
                        }
                    }
                }
                if (writer != null)
                {
                    if (frameNo > 0 && wavSource != null && feed != null) feed.Drain();
                    Check(writer.Finalize_());
                    Marshal.ReleaseComObject(writer);
                }
                ok = frameNo > 0;
            }
            catch { ok = false; }
            finally
            {
                if (wavSource != null) wavSource.Dispose();
                if (started) try { MFShutdown(); } catch { }
                if (!ok) try { File.Delete(mp4); } catch { }
            }
            return ok && File.Exists(mp4);
        }

        /// <summary>One decoded recording frame in the layout the encoder wants (RGB32, bottom row first).</summary>
        sealed class Raw { public int W, H; public byte[] Data; }

        /// <summary>Decodes one MJPEG frame (thread-safe: called on several threads at once). Odd sizes lose their last row / column.</summary>
        static Raw DecodeFrame(byte[] jpg)
        {
            try
            {
                using (var ms = new MemoryStream(jpg))
                using (var img = new Bitmap(ms))
                {
                    int w = img.Width & ~1, h = img.Height & ~1, stride = w * 4;
                    var raw = new Raw { W = w, H = h, Data = new byte[stride * h] };
                    var data = img.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        for (int y = 0; y < h; y++)
                            Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), raw.Data, (h - 1 - y) * stride, stride);
                    }
                    finally { img.UnlockBits(data); }
                    return raw;
                }
            }
            catch { return null; }
        }

        static System.Threading.Tasks.Task<Raw[]> StartDecode(System.Collections.Generic.List<byte[]> jpgs)
        {
            return System.Threading.Tasks.Task.Factory.StartNew(delegate
            {
                var res = new Raw[jpgs.Count];
                System.Threading.Tasks.Parallel.For(0, jpgs.Count, i => res[i] = DecodeFrame(jpgs[i]));
                return res;
            });
        }

        /// <summary>Hands one already flipped frame to the sink writer.</summary>
        static void WriteRaw(IMFSinkWriter writer, Raw raw, long time, long duration)
        {
            IMFMediaBuffer buf; Check(MFCreateMemoryBuffer(raw.Data.Length, out buf));
            IntPtr dst; int max, cur; Check(buf.Lock(out dst, out max, out cur));
            Marshal.Copy(raw.Data, 0, dst, raw.Data.Length);
            Check(buf.Unlock());
            Check(buf.SetCurrentLength(raw.Data.Length));
            IMFSample sample; Check(MFCreateSample(out sample));
            Check(sample.AddBuffer(buf));
            Check(sample.SetSampleTime(time));
            Check(sample.SetSampleDuration(duration));
            Check(writer.WriteSample(0, sample));
            Marshal.ReleaseComObject(sample); Marshal.ReleaseComObject(buf);
        }

        // ------------------------------------------------------------------ audio track

        /// <summary>PCM to be written as the audio track: chunks of 16-bit samples with their start time (100 ns).</summary>
        public interface IAudioSource { bool Next(out byte[] pcm, out long time); }

        /// <summary>The WAV written by <see cref="AudioRecorder"/> (44-byte header, then PCM), handed out in 100 ms chunks.</summary>
        sealed class WavSource : IAudioSource, IDisposable
        {
            FileStream fs; long frames;
            public WavSource(string path) { fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); fs.Seek(AudioRecorder.WavHeader, SeekOrigin.Begin); }
            public bool Next(out byte[] pcm, out long time)
            {
                int align = AudioRecorder.Channels * 2, want = AudioRecorder.Rate / 10 * align;
                var b = new byte[want];
                int got = 0, n;
                while (got < want && (n = fs.Read(b, got, want - got)) > 0) got += n;
                got -= got % align;
                time = frames * 10000000L / AudioRecorder.Rate;
                if (got <= 0) { pcm = null; return false; }
                if (got < want) Array.Resize(ref b, got);
                pcm = b; frames += got / align;
                return true;
            }
            public void Dispose() { if (fs != null) { fs.Dispose(); fs = null; } }
        }

        /// <summary>Hands audio to the sink writer in step with the pictures (the writer wants both streams in time order).</summary>
        sealed class AudioFeed
        {
            readonly IAudioSource src; readonly IMFSinkWriter writer; readonly int stream, rate, blockAlign;
            byte[] pend; long pendTime; bool has, done;

            public AudioFeed(IAudioSource src, IMFSinkWriter writer, int stream, int rate, int blockAlign)
            { this.src = src; this.writer = writer; this.stream = stream; this.rate = rate; this.blockAlign = blockAlign; }

            /// <summary>Writes every chunk that starts at or before <paramref name="upTo"/> (100 ns).</summary>
            public void Pump(long upTo)
            {
                while (true)
                {
                    if (!has)
                    {
                        if (done) return;
                        if (!src.Next(out pend, out pendTime)) { done = true; return; }
                        has = true;
                    }
                    if (pendTime > upTo) return;
                    Write(pend, pendTime); has = false;
                }
            }

            public void Drain() { Pump(long.MaxValue); }

            void Write(byte[] pcm, long time)
            {
                IMFMediaBuffer buf; Check(MFCreateMemoryBuffer(pcm.Length, out buf));
                IntPtr dst; int max, cur; Check(buf.Lock(out dst, out max, out cur));
                Marshal.Copy(pcm, 0, dst, pcm.Length);
                Check(buf.Unlock()); Check(buf.SetCurrentLength(pcm.Length));
                IMFSample sample; Check(MFCreateSample(out sample));
                Check(sample.AddBuffer(buf));
                Check(sample.SetSampleTime(time));
                Check(sample.SetSampleDuration((long)(pcm.Length / blockAlign) * 10000000L / rate));
                Check(writer.WriteSample(stream, sample));
                Marshal.ReleaseComObject(sample); Marshal.ReleaseComObject(buf);
            }
        }

        const int FirstAudioStream = -3;

        /// <summary>Decodes the sound of an MP4 to 16-bit PCM (for trimming and for playback). <see cref="TryOpen"/> gives null for a file without sound.</summary>
        public sealed class AudioReader : IDisposable, IAudioSource
        {
            IMFSourceReader reader; bool started;
            public int Rate { get; private set; }
            public int Channels { get; private set; }
            public int BlockAlign { get { return Channels * 2; } }

            AudioReader() { }

            public static AudioReader TryOpen(string path)
            {
                var a = new AudioReader();
                try
                {
                    if (MFStartup(MfVersion, 0) != 0) return null;
                    a.started = true;
                    IMFAttributes attrs; Check(MFCreateAttributes(out attrs, 1));
                    if (MFCreateSourceReaderFromURL(path, attrs, out a.reader) < 0) { a.Dispose(); return null; }
                    if (a.reader.SetStreamSelection(AllStreams, false) < 0 || a.reader.SetStreamSelection(FirstAudioStream, true) < 0) { a.Dispose(); return null; }
                    IMFMediaType want; Check(MFCreateMediaType(out want));
                    Check(want.SetGUID(MajorType, MediaAudio));
                    Check(want.SetGUID(Subtype, FormatPcm));
                    Check(want.SetUINT32(AudioBits, 16));
                    if (a.reader.SetCurrentMediaType(FirstAudioStream, IntPtr.Zero, want) < 0) { a.Dispose(); return null; }
                    IMFMediaType cur; Check(a.reader.GetCurrentMediaType(FirstAudioStream, out cur));
                    int rate, ch, bits;
                    Check(cur.GetUINT32(AudioRate, out rate)); Check(cur.GetUINT32(AudioChannels, out ch));
                    if (cur.GetUINT32(AudioBits, out bits) != 0) bits = 16;
                    if (bits != 16 || ch < 1 || ch > 2 || rate < 8000) { a.Dispose(); return null; }
                    a.Rate = rate; a.Channels = ch;
                    return a;
                }
                catch { a.Dispose(); return null; }
            }

            public void Seek(TimeSpan t)
            {
                IntPtr pv = Marshal.AllocHGlobal(32);
                try
                {
                    for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(pv, i, 0);
                    Marshal.WriteInt16(pv, 0, 20);                       // VT_I8
                    Marshal.WriteInt64(pv, 8, Math.Max(0, t.Ticks));
                    Check(reader.SetCurrentPosition(Guid.Empty, pv));
                }
                finally { Marshal.FreeHGlobal(pv); }
            }

            /// <summary>The next decoded chunk and its start time (100 ns). False at the end.</summary>
            public bool Next(out byte[] pcm, out long time)
            {
                pcm = null; time = 0;
                while (true)
                {
                    int idx, flags; long ts; IMFSample sample;
                    Check(reader.ReadSample(FirstAudioStream, 0, out idx, out flags, out ts, out sample));
                    if ((flags & EndOfStream) != 0) { if (sample != null) Marshal.ReleaseComObject(sample); return false; }
                    if (sample == null) continue;
                    IMFMediaBuffer buf = null;
                    try
                    {
                        Check(sample.ConvertToContiguousBuffer(out buf));
                        IntPtr p; int max, len; Check(buf.Lock(out p, out max, out len));
                        try { pcm = new byte[len]; Marshal.Copy(p, pcm, 0, len); } finally { buf.Unlock(); }
                    }
                    finally { if (buf != null) Marshal.ReleaseComObject(buf); Marshal.ReleaseComObject(sample); }
                    time = ts;
                    return true;
                }
            }

            public void Dispose()
            {
                if (reader != null) { try { Marshal.ReleaseComObject(reader); } catch { } reader = null; }
                if (started) { started = false; try { MFShutdown(); } catch { } }
            }
        }

        /// <summary>The part [start, end) of a file's sound, moved to start at 0 - what a trim keeps.</summary>
        sealed class TrimmedAudio : IAudioSource
        {
            readonly AudioReader src; readonly long start, end;
            public TrimmedAudio(AudioReader src, TimeSpan start, TimeSpan end) { this.src = src; this.start = start.Ticks; this.end = end.Ticks; src.Seek(start); }
            public bool Next(out byte[] pcm, out long time)
            {
                while (src.Next(out pcm, out time))
                {
                    int align = src.BlockAlign;
                    long dur = (long)(pcm.Length / align) * 10000000L / src.Rate;
                    if (time + dur <= start) continue;
                    if (time >= end) return false;
                    int from = 0, to = pcm.Length / align;
                    if (time < start) from = (int)((start - time) * src.Rate / 10000000L);
                    if (time + dur > end) to = (int)((end - time) * src.Rate / 10000000L);
                    if (to <= from) continue;
                    if (from > 0 || to < pcm.Length / align)
                    {
                        var cut = new byte[(to - from) * align];
                        Buffer.BlockCopy(pcm, from * align, cut, 0, cut.Length);
                        pcm = cut;
                    }
                    time = Math.Max(time, start) - start;
                    return true;
                }
                return false;
            }
        }

        static readonly Guid EnableVideoProcessing = new Guid("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");
        static readonly Guid DefaultStride = new Guid("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
        const int FirstVideoStream = -4, AllStreams = -2, EndOfStream = 2;

        /// <summary>Keeps only [start, end) of a video (MP4 or AVI) and writes it as a new H.264 MP4. Returns the first kept frame in <paramref name="first"/>.
        /// The frames are decoded by <see cref="Reader"/> (which knows the real row layout) and encoded again like a fresh recording.</summary>
        public static bool Trim(string src, string dst, TimeSpan start, TimeSpan end, out Bitmap first, out TimeSpan kept)
        {
            first = null; kept = TimeSpan.Zero;
            bool started = false, ok = false;
            AudioReader audio = null;
            try
            {
                if (MFStartup(MfVersion, 0) != 0) return false;
                started = true;
                using (var rd = new Reader(src))
                using (var frame = new Bitmap(rd.Width, rd.Height, PixelFormat.Format32bppRgb))
                {
                    int w = rd.Width, h = rd.Height, fps = rd.Fps;
                    long frameDur = 10000000L / fps, s100 = start.Ticks, e100 = end.Ticks;
                    var row = new byte[w * 4];
                    audio = AudioReader.TryOpen(src);                       // null when the video has no sound
                    int audioStream;
                    var writer = Open(dst, w, h, fps, audio != null ? audio.Rate : 0, audio != null ? audio.Channels : 0, out audioStream);
                    AudioFeed feed = audio == null ? null : new AudioFeed(new TrimmedAudio(audio, start, end), writer, audioStream, audio.Rate, audio.BlockAlign);
                    int written = 0; TimeSpan ts;
                    while (rd.ReadFrame(frame, out ts))
                    {
                        if (ts.Ticks >= e100) break;
                        if (ts.Ticks + frameDur <= s100) continue;
                        if (first == null) first = new Bitmap(frame);
                        long outTime = Math.Max(0, ts.Ticks - s100);
                        if (feed != null) feed.Pump(outTime);
                        WriteFrame(writer, frame, row, outTime, frameDur);
                        written++;
                    }
                    if (feed != null && written > 0) feed.Drain();
                    Check(writer.Finalize_());
                    Marshal.ReleaseComObject(writer);
                    kept = TimeSpan.FromTicks(written * frameDur);
                    ok = written > 0;
                }
            }
            catch { ok = false; }
            finally
            {
                if (audio != null) audio.Dispose();
                if (started) try { MFShutdown(); } catch { }
                if (!ok) { try { File.Delete(dst); } catch { } if (first != null) { first.Dispose(); first = null; } }
            }
            return ok && File.Exists(dst);
        }

        /// <summary>Hands one frame to the sink writer. The buffer is tightly packed and bottom-up, which is how RGB32 is described to the writer.</summary>
        static void WriteFrame(IMFSinkWriter writer, Bitmap bmp, byte[] row, long time, long duration)
        {
            int w = bmp.Width, h = bmp.Height, stride = w * 4;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                IMFMediaBuffer buf; Check(MFCreateMemoryBuffer(stride * h, out buf));
                IntPtr dst; int max, cur; Check(buf.Lock(out dst, out max, out cur));
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, stride);
                    Marshal.Copy(row, 0, IntPtr.Add(dst, (h - 1 - y) * stride), stride);
                }
                Check(buf.Unlock());
                Check(buf.SetCurrentLength(stride * h));
                IMFSample sample; Check(MFCreateSample(out sample));
                Check(sample.AddBuffer(buf));
                Check(sample.SetSampleTime(time));
                Check(sample.SetSampleDuration(duration));
                Check(writer.WriteSample(0, sample));
                Marshal.ReleaseComObject(sample); Marshal.ReleaseComObject(buf);
            }
            finally { bmp.UnlockBits(data); }
        }

        static readonly Guid PdDuration = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
        const int MediaSourceStream = -1;

        /// <summary>Frame-accurate video decoder for playback (MP4 or AVI): the editor's player draws these frames itself,
        /// because the WPF MediaElement rounds durations down to whole seconds and would cut the end off a video.</summary>
        public sealed class Reader : IDisposable
        {
            IMFSourceReader reader;
            bool started, topDown;
            int srcStride;                              // bytes from one row to the next in the decoded frame (padded, not always Width * 4)
            public int Width { get; private set; }
            public int Height { get; private set; }
            public TimeSpan Duration { get; private set; }
            public int Fps { get; private set; }         // nominal frame rate (15 when the file does not say)

            public Reader(string path)
            {
                if (MFStartup(MfVersion, 0) != 0) throw new InvalidOperationException("Media Foundation is not available");
                started = true;
                try
                {
                    IMFAttributes attrs; Check(MFCreateAttributes(out attrs, 1));
                    Check(attrs.SetUINT32(EnableVideoProcessing, 1));
                    Check(MFCreateSourceReaderFromURL(path, attrs, out reader));
                    Check(reader.SetStreamSelection(AllStreams, false));
                    Check(reader.SetStreamSelection(FirstVideoStream, true));
                    IMFMediaType want; Check(MFCreateMediaType(out want));
                    Check(want.SetGUID(MajorType, MediaVideo));
                    Check(want.SetGUID(Subtype, FormatRgb32));
                    Check(reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, want));
                    IMFMediaType cur; Check(reader.GetCurrentMediaType(FirstVideoStream, out cur));
                    ulong size; Check(cur.GetUINT64(FrameSize, out size));
                    Width = (int)(size >> 32); Height = (int)(size & 0xffffffff);
                    ulong rate; Fps = 15;
                    if (cur.GetUINT64(FrameRate, out rate) == 0 && (uint)(rate & 0xffffffff) != 0) Fps = Math.Max(1, (int)Math.Round((double)(rate >> 32) / (uint)(rate & 0xffffffff)));
                    int stride; bool haveStride = cur.GetUINT32(DefaultStride, out stride) == 0;
                    topDown = !(haveStride && stride < 0);
                    srcStride = haveStride && stride != 0 ? Math.Abs(stride) : Width * 4;
                    Duration = ReadDuration();
                }
                catch { Dispose(); throw; }
            }

            TimeSpan ReadDuration()
            {
                IntPtr pv = Marshal.AllocHGlobal(32);
                try
                {
                    for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(pv, i, 0);
                    if (reader.GetPresentationAttribute(MediaSourceStream, PdDuration, pv) != 0) return TimeSpan.Zero;
                    return TimeSpan.FromTicks(Marshal.ReadInt64(pv, 8));      // VT_UI8, 100 ns units
                }
                finally { Marshal.FreeHGlobal(pv); }
            }

            /// <summary>Moves so that the next frame read is at or shortly before <paramref name="t"/> (it lands on an earlier key frame).</summary>
            public void Seek(TimeSpan t)
            {
                IntPtr pv = Marshal.AllocHGlobal(32);
                try
                {
                    for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(pv, i, 0);
                    Marshal.WriteInt16(pv, 0, 20);                       // VT_I8
                    Marshal.WriteInt64(pv, 8, Math.Max(0, t.Ticks));
                    Check(reader.SetCurrentPosition(Guid.Empty, pv));
                }
                finally { Marshal.FreeHGlobal(pv); }
            }

            /// <summary>Decodes the next frame into <paramref name="dst"/> (Format32bppRgb, Width x Height). False at the end of the video.</summary>
            public bool ReadFrame(Bitmap dst, out TimeSpan time)
            {
                time = TimeSpan.Zero;
                while (true)
                {
                    int idx, flags; long ts; IMFSample sample;
                    Check(reader.ReadSample(FirstVideoStream, 0, out idx, out flags, out ts, out sample));
                    if ((flags & EndOfStream) != 0) { if (sample != null) Marshal.ReleaseComObject(sample); return false; }
                    if (sample == null) continue;
                    try { CopyFrame(sample, dst); }
                    finally { Marshal.ReleaseComObject(sample); }
                    time = TimeSpan.FromTicks(ts);
                    return true;
                }
            }

            void CopyFrame(IMFSample sample, Bitmap dst)
            {
                IMFMediaBuffer buf; Check(sample.ConvertToContiguousBuffer(out buf));
                IMF2DBuffer buf2d = buf as IMF2DBuffer;
                IntPtr scan0 = IntPtr.Zero, p = IntPtr.Zero; int pitch = 0; bool locked2d = false, locked = false;
                try
                {
                    int w = Width, h = Height, rowBytes = w * 4;
                    // Rows are padded (the width of 1508 becomes 1520 or more), and RGB32 may be bottom-up: ask the buffer where the
                    // top row is and how far apart rows are. Lock2D returns scan0 = the top row and a pitch that is negative when bottom-up.
                    if (buf2d != null && buf2d.Lock2D(out scan0, out pitch) == 0) locked2d = true;
                    else
                    {
                        int max, len; Check(buf.Lock(out p, out max, out len)); locked = true;
                        // The decoder's buffer is allocated for a height (and width) rounded up to a multiple of 16: 1508 x 876 arrives as
                        // 1520 pixels per row and 880 rows. The type says Width * 4, which is wrong, so work the pitch out from the buffer size.
                        int row = 0;
                        foreach (int rows in new[] { (h + 15) & ~15, h, (h + 31) & ~31, (h + 63) & ~63 })
                            if (len % rows == 0 && len / rows >= rowBytes) { row = len / rows; break; }
                        if (row == 0) row = srcStride;
                        if (row < rowBytes || len < row * h) return;
                        scan0 = topDown ? p : IntPtr.Add(p, (h - 1) * row); pitch = topDown ? row : -row;
                    }
                    if (Math.Abs(pitch) < rowBytes) return;
                    var data = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        var buffer = new byte[rowBytes];
                        for (int y = 0; y < h; y++)
                        {
                            Marshal.Copy(IntPtr.Add(scan0, y * pitch), buffer, 0, rowBytes);
                            Marshal.Copy(buffer, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                        }
                    }
                    finally { dst.UnlockBits(data); }
                }
                finally
                {
                    if (locked2d) buf2d.Unlock2D();
                    if (locked) buf.Unlock();
                    Marshal.ReleaseComObject(buf);
                }
            }

            public void Dispose()
            {
                if (reader != null) { try { Marshal.ReleaseComObject(reader); } catch { } reader = null; }
                if (started) { started = false; try { MFShutdown(); } catch { } }
            }
        }

        /// <summary>Opens the MP4 writer: stream 0 is H.264 video; with <paramref name="audioRate"/> &gt; 0 a second stream takes AAC sound.</summary>
        static IMFSinkWriter Open(string mp4, int w, int h, int fps, int audioRate, int audioChannels, out int audioStream)
        {
            audioStream = -1;
            IMFSinkWriter writer;
            Check(MFCreateSinkWriterFromURL(mp4, IntPtr.Zero, IntPtr.Zero, out writer));
            int bitrate = Encoder.Bitrate(w, h, fps);

            IMFMediaType output; Check(MFCreateMediaType(out output));
            Check(output.SetGUID(MajorType, MediaVideo));
            Check(output.SetGUID(Subtype, FormatH264));
            Check(output.SetUINT32(AvgBitrate, bitrate));
            Check(output.SetUINT32(InterlaceMode, 2));   // progressive
            Check(output.SetUINT64(FrameSize, ((ulong)w << 32) | (uint)h));
            Check(output.SetUINT64(FrameRate, ((ulong)fps << 32) | 1u));
            Check(output.SetUINT64(PixelAspect, (1UL << 32) | 1u));
            int stream; Check(writer.AddStream(output, out stream));

            IMFMediaType input; Check(MFCreateMediaType(out input));
            Check(input.SetGUID(MajorType, MediaVideo));
            Check(input.SetGUID(Subtype, FormatRgb32));
            Check(input.SetUINT32(InterlaceMode, 2));
            Check(input.SetUINT64(FrameSize, ((ulong)w << 32) | (uint)h));
            Check(input.SetUINT64(FrameRate, ((ulong)fps << 32) | 1u));
            Check(input.SetUINT64(PixelAspect, (1UL << 32) | 1u));
            IMFAttributes enc = null;
            if (Encoder.Quality > 0)
            {
                // quality-based variable bit rate: a still screen costs almost nothing, motion gets the bits (instead of a constant rate)
                Check(MFCreateAttributes(out enc, 2));
                Check(enc.SetUINT32(CodecRateControlMode, 3));
                Check(enc.SetUINT32(CodecQuality, Encoder.Quality));
            }
            int hrIn = writer.SetInputMediaType(stream, input, enc);
            if (hrIn < 0 && enc != null) hrIn = writer.SetInputMediaType(stream, input, null);                 // this encoder does not know quality mode: constant rate
            Check(hrIn);
            if (enc != null) Marshal.ReleaseComObject(enc);
            Marshal.ReleaseComObject(output); Marshal.ReleaseComObject(input);

            if (audioRate > 0)
            {
                // AAC-LC, 160 kbit/s (the encoder accepts 96/128/160/192 kbit/s)
                IMFMediaType aout; Check(MFCreateMediaType(out aout));
                Check(aout.SetGUID(MajorType, MediaAudio));
                Check(aout.SetGUID(Subtype, FormatAac));
                Check(aout.SetUINT32(AudioBits, 16));
                Check(aout.SetUINT32(AudioRate, audioRate));
                Check(aout.SetUINT32(AudioChannels, audioChannels));
                Check(aout.SetUINT32(AudioBytesPerSec, 20000));
                Check(aout.SetUINT32(AudioBlockAlign, 1));
                Check(aout.SetUINT32(AacPayload, 0));
                Check(aout.SetUINT32(AacProfileLevel, 0x29));
                Check(writer.AddStream(aout, out audioStream));

                IMFMediaType ain; Check(MFCreateMediaType(out ain));
                Check(ain.SetGUID(MajorType, MediaAudio));
                Check(ain.SetGUID(Subtype, FormatPcm));
                Check(ain.SetUINT32(AudioBits, 16));
                Check(ain.SetUINT32(AudioRate, audioRate));
                Check(ain.SetUINT32(AudioChannels, audioChannels));
                Check(ain.SetUINT32(AudioBlockAlign, audioChannels * 2));
                Check(ain.SetUINT32(AudioBytesPerSec, audioRate * audioChannels * 2));
                Check(writer.SetInputMediaType(audioStream, ain, null));
                Marshal.ReleaseComObject(aout); Marshal.ReleaseComObject(ain);
            }
            Check(writer.BeginWriting());
            return writer;
        }

        static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }

        [DllImport("mfplat.dll")] static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll")] static extern int MFShutdown();
        [DllImport("mfplat.dll")] static extern int MFCreateMediaType(out IMFMediaType type);
        [DllImport("mfplat.dll")] static extern int MFCreateSample(out IMFSample sample);
        [DllImport("mfplat.dll")] static extern int MFCreateAttributes(out IMFAttributes attributes, int initialSize);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        static extern int MFCreateSourceReaderFromURL(string url, IMFAttributes attributes, out IMFSourceReader reader);
        [DllImport("mfplat.dll")] static extern int MFCreateMemoryBuffer(int maxLength, out IMFMediaBuffer buffer);
        [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
        static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IMFSinkWriter writer);

        // COM interfaces: slot order matters, unused slots are placeholders (vtable position only).
        [ComImport, Guid("2cd2d921-c447-44a7-a13c-4adabfc247e3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFAttributes
        {
            [PreserveSig] int GetItem(); [PreserveSig] int GetItemType(); [PreserveSig] int CompareItem(); [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32(); [PreserveSig] int GetUINT64(); [PreserveSig] int GetDouble(); [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength(); [PreserveSig] int GetString(); [PreserveSig] int GetAllocatedString();
            [PreserveSig] int GetBlobSize(); [PreserveSig] int GetBlob(); [PreserveSig] int GetAllocatedBlob(); [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem(); [PreserveSig] int DeleteItem(); [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
            [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
            [PreserveSig] int SetString(); [PreserveSig] int SetBlob(); [PreserveSig] int SetUnknown();
            [PreserveSig] int LockStore(); [PreserveSig] int UnlockStore(); [PreserveSig] int GetCount(); [PreserveSig] int GetItemByIndex(); [PreserveSig] int CopyAllItems();
        }

        [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaType
        {
            [PreserveSig] int GetItem(); [PreserveSig] int GetItemType(); [PreserveSig] int CompareItem(); [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out int value); [PreserveSig] int GetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, out ulong value); [PreserveSig] int GetDouble(); [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength(); [PreserveSig] int GetString(); [PreserveSig] int GetAllocatedString();
            [PreserveSig] int GetBlobSize(); [PreserveSig] int GetBlob(); [PreserveSig] int GetAllocatedBlob(); [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem(); [PreserveSig] int DeleteItem(); [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
            [PreserveSig] int SetUINT64([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, ulong value);
            [PreserveSig] int SetDouble();
            [PreserveSig] int SetGUID([In, MarshalAs(UnmanagedType.LPStruct)] Guid key, [In, MarshalAs(UnmanagedType.LPStruct)] Guid value);
            [PreserveSig] int SetString(); [PreserveSig] int SetBlob(); [PreserveSig] int SetUnknown();
            [PreserveSig] int LockStore(); [PreserveSig] int UnlockStore(); [PreserveSig] int GetCount(); [PreserveSig] int GetItemByIndex(); [PreserveSig] int CopyAllItems();
            [PreserveSig] int GetMajorType(); [PreserveSig] int IsCompressedFormat(); [PreserveSig] int IsEqual(); [PreserveSig] int GetRepresentation(); [PreserveSig] int FreeRepresentation();
        }

        [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSample
        {
            [PreserveSig] int GetItem(); [PreserveSig] int GetItemType(); [PreserveSig] int CompareItem(); [PreserveSig] int Compare();
            [PreserveSig] int GetUINT32(); [PreserveSig] int GetUINT64(); [PreserveSig] int GetDouble(); [PreserveSig] int GetGUID();
            [PreserveSig] int GetStringLength(); [PreserveSig] int GetString(); [PreserveSig] int GetAllocatedString();
            [PreserveSig] int GetBlobSize(); [PreserveSig] int GetBlob(); [PreserveSig] int GetAllocatedBlob(); [PreserveSig] int GetUnknown();
            [PreserveSig] int SetItem(); [PreserveSig] int DeleteItem(); [PreserveSig] int DeleteAllItems();
            [PreserveSig] int SetUINT32(); [PreserveSig] int SetUINT64(); [PreserveSig] int SetDouble(); [PreserveSig] int SetGUID();
            [PreserveSig] int SetString(); [PreserveSig] int SetBlob(); [PreserveSig] int SetUnknown();
            [PreserveSig] int LockStore(); [PreserveSig] int UnlockStore(); [PreserveSig] int GetCount(); [PreserveSig] int GetItemByIndex(); [PreserveSig] int CopyAllItems();
            [PreserveSig] int GetSampleFlags(); [PreserveSig] int SetSampleFlags(); [PreserveSig] int GetSampleTime(out long time);
            [PreserveSig] int SetSampleTime(long time);
            [PreserveSig] int GetSampleDuration();
            [PreserveSig] int SetSampleDuration(long duration);
            [PreserveSig] int GetBufferCount(); [PreserveSig] int GetBufferByIndex(); [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
            [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
            [PreserveSig] int RemoveBufferByIndex(); [PreserveSig] int RemoveAllBuffers(); [PreserveSig] int GetTotalLength(); [PreserveSig] int CopyToBuffer();
        }

        [ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFMediaBuffer
        {
            [PreserveSig] int Lock(out IntPtr buffer, out int maxLength, out int currentLength);
            [PreserveSig] int Unlock();
            [PreserveSig] int GetCurrentLength();
            [PreserveSig] int SetCurrentLength(int length);
            [PreserveSig] int GetMaxLength();
        }

        [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSinkWriter
        {
            [PreserveSig] int AddStream(IMFMediaType targetMediaType, out int streamIndex);
            [PreserveSig] int SetInputMediaType(int streamIndex, IMFMediaType inputMediaType, IMFAttributes encodingParameters);
            [PreserveSig] int BeginWriting();
            [PreserveSig] int WriteSample(int streamIndex, IMFSample sample);
            [PreserveSig] int SendStreamTick(); [PreserveSig] int PlaceMarker(); [PreserveSig] int NotifyEndOfSegment(); [PreserveSig] int Flush();
            [PreserveSig] int Finalize_();
            [PreserveSig] int GetServiceForStream(); [PreserveSig] int GetStatistics();
        }

        [ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMFSourceReader
        {
            [PreserveSig] int GetStreamSelection();
            [PreserveSig] int SetStreamSelection(int streamIndex, [MarshalAs(UnmanagedType.Bool)] bool selected);
            [PreserveSig] int GetNativeMediaType();
            [PreserveSig] int GetCurrentMediaType(int streamIndex, out IMFMediaType mediaType);
            [PreserveSig] int SetCurrentMediaType(int streamIndex, IntPtr reserved, IMFMediaType mediaType);
            [PreserveSig] int SetCurrentPosition([In, MarshalAs(UnmanagedType.LPStruct)] Guid timeFormat, IntPtr position);
            [PreserveSig] int ReadSample(int streamIndex, int controlFlags, out int actualStreamIndex, out int streamFlags, out long timestamp, out IMFSample sample);
            [PreserveSig] int Flush(); [PreserveSig] int GetServiceForStream();
            [PreserveSig] int GetPresentationAttribute(int streamIndex, [In, MarshalAs(UnmanagedType.LPStruct)] Guid key, IntPtr value);
        }

        [ComImport, Guid("7dc9d5f9-9ed9-44ec-9bbf-0600bb589fbb"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMF2DBuffer
        {
            [PreserveSig] int Lock2D(out IntPtr scanline0, out int pitch);
            [PreserveSig] int Unlock2D();
            [PreserveSig] int GetScanline0AndPitch(out IntPtr scanline0, out int pitch);
            [PreserveSig] int IsContiguousFormat([MarshalAs(UnmanagedType.Bool)] out bool contiguous);
            [PreserveSig] int GetContiguousLength(out int length);
            [PreserveSig] int ContiguousCopyTo(IntPtr dest, int destLength);
            [PreserveSig] int ContiguousCopyFrom(IntPtr src, int srcLength);
        }
    }
}
