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
            [PreserveSig] int GetSampleFlags(); [PreserveSig] int SetSampleFlags(); [PreserveSig] int GetSampleTime();
            [PreserveSig] int SetSampleTime(long time);
            [PreserveSig] int GetSampleDuration();
            [PreserveSig] int SetSampleDuration(long duration);
            [PreserveSig] int GetBufferCount(); [PreserveSig] int GetBufferByIndex(); [PreserveSig] int ConvertToContiguousBuffer();
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
    }
}
