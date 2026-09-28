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

                        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                        try
                        {
                            IMFMediaBuffer buf; Check(MFCreateMemoryBuffer(stride * h, out buf));
                            IntPtr dst; int max, cur; Check(buf.Lock(out dst, out max, out cur));
                            for (int y = 0; y < h; y++)   // RGB32 in Media Foundation is bottom-up
                            {
                                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), row, 0, stride);
                                Marshal.Copy(row, 0, IntPtr.Add(dst, (h - 1 - y) * stride), stride);
                            }
                            Check(buf.Unlock());
                            Check(buf.SetCurrentLength(stride * h));
                            IMFSample sample; Check(MFCreateSample(out sample));
                            Check(sample.AddBuffer(buf));
                            Check(sample.SetSampleTime(frameNo * duration));
                            Check(sample.SetSampleDuration(duration));
                            Check(writer.WriteSample(0, sample));
                            Marshal.ReleaseComObject(sample); Marshal.ReleaseComObject(buf);
                        }
                        finally { bmp.UnlockBits(data); }
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

        /// <summary>Keeps only [start, end) of a video (MP4 or AVI) and writes it as a new H.264 MP4. Returns the first kept frame in <paramref name="first"/>.</summary>
        public static bool Trim(string src, string dst, TimeSpan start, TimeSpan end, out Bitmap first, out TimeSpan kept)
        {
            first = null; kept = TimeSpan.Zero;
            bool started = false, ok = false;
            try
            {
                if (MFStartup(MfVersion, 0) != 0) return false;
                started = true;
                IMFAttributes attrs; Check(MFCreateAttributes(out attrs, 1));
                Check(attrs.SetUINT32(EnableVideoProcessing, 1));      // lets the reader convert whatever the codec gives us to RGB32
                IMFSourceReader reader; Check(MFCreateSourceReaderFromURL(src, attrs, out reader));
                Check(reader.SetStreamSelection(AllStreams, false));
                Check(reader.SetStreamSelection(FirstVideoStream, true));
                IMFMediaType want; Check(MFCreateMediaType(out want));
                Check(want.SetGUID(MajorType, MediaVideo));
                Check(want.SetGUID(Subtype, FormatRgb32));
                Check(reader.SetCurrentMediaType(FirstVideoStream, IntPtr.Zero, want));
                IMFMediaType cur; Check(reader.GetCurrentMediaType(FirstVideoStream, out cur));
                ulong size, rate;
                Check(cur.GetUINT64(FrameSize, out size));
                int w = (int)(size >> 32), h = (int)(size & 0xffffffff);
                int fps = 15;
                if (cur.GetUINT64(FrameRate, out rate) == 0 && (uint)(rate & 0xffffffff) != 0) fps = Math.Max(1, (int)Math.Round((double)(rate >> 32) / (uint)(rate & 0xffffffff)));
                int stride; bool topDown = !(cur.GetUINT32(DefaultStride, out stride) == 0 && stride < 0);   // the reader normally hands out top-down frames
                long frameDur = 10000000L / fps, s100 = start.Ticks, e100 = end.Ticks;
                var writer = Open(dst, w, h, fps, topDown);
                int written = 0;
                while (true)
                {
                    int idx, flags; long ts; IMFSample sample;
                    Check(reader.ReadSample(FirstVideoStream, 0, out idx, out flags, out ts, out sample));
                    if ((flags & EndOfStream) != 0) { if (sample != null) Marshal.ReleaseComObject(sample); break; }
                    if (sample == null) continue;
                    if (ts >= e100) { Marshal.ReleaseComObject(sample); break; }
                    if (ts + frameDur <= s100) { Marshal.ReleaseComObject(sample); continue; }
                    if (first == null) first = FrameToBitmap(sample, w, h, topDown);
                    Check(sample.SetSampleTime(Math.Max(0, ts - s100)));
                    Check(sample.SetSampleDuration(frameDur));
                    Check(writer.WriteSample(0, sample));
                    Marshal.ReleaseComObject(sample);
                    written++;
                }
                Check(writer.Finalize_());
                Marshal.ReleaseComObject(writer); Marshal.ReleaseComObject(reader);
                kept = TimeSpan.FromTicks(written * frameDur);
                ok = written > 0;
            }
            catch { ok = false; }
            finally
            {
                if (started) try { MFShutdown(); } catch { }
                if (!ok) { try { File.Delete(dst); } catch { } if (first != null) { first.Dispose(); first = null; } }
            }
            return ok && File.Exists(dst);
        }

        /// <summary>Copies an RGB32 frame into a Bitmap, flipping rows when the frame is bottom-up.</summary>
        static Bitmap FrameToBitmap(IMFSample sample, int w, int h, bool topDown)
        {
            IMFMediaBuffer buf; Check(sample.ConvertToContiguousBuffer(out buf));
            IntPtr p; int max, cur; Check(buf.Lock(out p, out max, out cur));
            try
            {
                var bmp = new Bitmap(w, h, PixelFormat.Format32bppRgb);
                var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                try
                {
                    var row = new byte[w * 4];
                    for (int y = 0; y < h; y++)
                    {
                        Marshal.Copy(IntPtr.Add(p, (topDown ? y : h - 1 - y) * w * 4), row, 0, row.Length);
                        Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), row.Length);
                    }
                }
                finally { bmp.UnlockBits(data); }
                return bmp;
            }
            finally { buf.Unlock(); Marshal.ReleaseComObject(buf); }
        }

        static readonly Guid PdDuration = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
        const int MediaSourceStream = -1;

        /// <summary>Frame-accurate video decoder for playback (MP4 or AVI): the editor's player draws these frames itself,
        /// because the WPF MediaElement rounds durations down to whole seconds and would cut the end off a video.</summary>
        public sealed class Reader : IDisposable
        {
            IMFSourceReader reader;
            bool started, topDown;
            public int Width { get; private set; }
            public int Height { get; private set; }
            public TimeSpan Duration { get; private set; }

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
                    int stride; topDown = !(cur.GetUINT32(DefaultStride, out stride) == 0 && stride < 0);
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
                IntPtr p; int max, len; Check(buf.Lock(out p, out max, out len));
                try
                {
                    int w = Width, h = Height, rowBytes = w * 4;
                    if (len < rowBytes * h) return;
                    var data = dst.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppRgb);
                    try
                    {
                        var row = new byte[rowBytes];
                        for (int y = 0; y < h; y++)
                        {
                            Marshal.Copy(IntPtr.Add(p, (topDown ? y : h - 1 - y) * rowBytes), row, 0, rowBytes);
                            Marshal.Copy(row, 0, IntPtr.Add(data.Scan0, y * data.Stride), rowBytes);
                        }
                    }
                    finally { dst.UnlockBits(data); }
                }
                finally { buf.Unlock(); Marshal.ReleaseComObject(buf); }
            }

            public void Dispose()
            {
                if (reader != null) { try { Marshal.ReleaseComObject(reader); } catch { } reader = null; }
                if (started) { started = false; try { MFShutdown(); } catch { } }
            }
        }

        static IMFSinkWriter Open(string mp4, int w, int h, int fps, bool topDown = false)
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
            if (topDown) Check(input.SetUINT32(DefaultStride, w * 4));   // positive stride = rows run top to bottom
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
    }
}
