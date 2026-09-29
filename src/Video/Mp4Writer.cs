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

        /// <summary>Re-encodes the MJPEG frames of an AVI written by AviWriter as H.264 MP4.</summary>
        public static bool Convert(string avi, string mp4, int fps)
        {
            fps = Math.Max(1, fps);
            bool started = false, ok = false;
            try
            {
                if (MFStartup(MfVersion, 0) != 0) return false;
                started = true;
                IMFSinkWriter writer = null;
                int frameNo = 0, w = 0, h = 0, stride = 0;
                long duration = 10000000L / fps;
                byte[] row = null;
                foreach (var jpg in AviReader.Frames(avi))
                {
                    using (var ms = new MemoryStream(jpg))
                    using (var img = Image.FromStream(ms))
                    using (var bmp = new Bitmap(img.Width & ~1, img.Height & ~1, PixelFormat.Format32bppRgb))
                    {
                        using (var g = Graphics.FromImage(bmp)) g.DrawImage(img, 0, 0, bmp.Width, bmp.Height);
                        if (writer == null)
                        {
                            w = bmp.Width; h = bmp.Height; stride = w * 4; row = new byte[stride];
                            writer = Open(mp4, w, h, fps);
                        }
                        else if (bmp.Width != w || bmp.Height != h) continue;   // recordings have a fixed size; ignore strays

                        WriteFrame(writer, bmp, row, frameNo * duration, duration);
                        frameNo++;
                    }
                }
                if (writer != null)
                {
                    Check(writer.Finalize_());
                    Marshal.ReleaseComObject(writer);
                }
                ok = frameNo > 0;
            }
            catch { ok = false; }
            finally
            {
                if (started) try { MFShutdown(); } catch { }
                if (!ok) try { File.Delete(mp4); } catch { }
            }
            return ok && File.Exists(mp4);
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
                    var writer = Open(dst, w, h, fps);
                    int written = 0; TimeSpan ts;
                    while (rd.ReadFrame(frame, out ts))
                    {
                        if (ts.Ticks >= e100) break;
                        if (ts.Ticks + frameDur <= s100) continue;
                        if (first == null) first = new Bitmap(frame);
                        WriteFrame(writer, frame, row, Math.Max(0, ts.Ticks - s100), frameDur);
                        written++;
                    }
                    Check(writer.Finalize_());
                    Marshal.ReleaseComObject(writer);
                    kept = TimeSpan.FromTicks(written * frameDur);
                    ok = written > 0;
                }
            }
            catch { ok = false; }
            finally
            {
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

        static IMFSinkWriter Open(string mp4, int w, int h, int fps)
        {
            IMFSinkWriter writer;
            Check(MFCreateSinkWriterFromURL(mp4, IntPtr.Zero, IntPtr.Zero, out writer));
            int bitrate = (int)Math.Min(40000000L, Math.Max(1000000L, (long)w * h * fps / 4));   // ~0.25 bit per pixel: screen text stays crisp

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
            Check(writer.SetInputMediaType(stream, input, null));
            Check(writer.BeginWriting());
            Marshal.ReleaseComObject(output); Marshal.ReleaseComObject(input);
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
