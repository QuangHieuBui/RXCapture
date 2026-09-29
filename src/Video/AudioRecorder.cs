using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace RXCapture
{
    /// <summary>Records the microphone and/or the sound the computer plays (WASAPI loopback) into a 48 kHz / 16-bit / stereo WAV.
    /// The WAV follows the recording clock, not the audio devices: it gets exactly as many frames as the clock has run, silence where a
    /// source delivered nothing (loopback sends no data while nothing plays), and nothing while the recording is paused - so it always
    /// lines up with the video.</summary>
    public sealed class AudioRecorder : IDisposable
    {
        public const int Rate = 48000, Channels = 2, WavHeader = 44;

        public bool MicOk { get; private set; }
        public bool SystemOk { get; private set; }
        /// <summary>Why a requested source could not be opened (empty when everything worked).</summary>
        public string Problem { get; private set; }
        /// <summary>Recent loudness 0..1 of each source, for a level meter.</summary>
        public float MicLevel, SystemLevel;
        public long FramesWritten { get { return framesWritten; } }

        Thread thread;
        volatile bool stop, paused;
        Func<long> clockMs;
        string path;
        long framesWritten;
        readonly ManualResetEvent ready = new ManualResetEvent(false);
        bool wantMic, wantSys;

        public bool Paused { get { return paused; } set { paused = value; } }
        /// <summary>Muted sources keep running (so they can be un-muted at any moment) but contribute silence.</summary>
        public volatile bool MicMuted, SystemMuted;

        /// <summary>Opens the requested sources on a worker thread and starts writing. True when at least one source works.</summary>
        public bool Start(string wavPath, bool microphone, bool systemSound, Func<long> recordingClockMs)
        {
            path = wavPath; wantMic = microphone; wantSys = systemSound; clockMs = recordingClockMs;
            thread = new Thread(Run) { IsBackground = true, Name = "RXCapture audio", Priority = ThreadPriority.AboveNormal };
            thread.Start();
            ready.WaitOne(5000);
            return MicOk || SystemOk;
        }

        public void Stop()
        {
            stop = true;
            if (thread != null) { thread.Join(5000); thread = null; }
        }

        public void Dispose() { Stop(); ready.Close(); }

        void Run()
        {
            Source mic = null, sys = null;
            FileStream fs = null;
            var problems = new List<string>();
            try
            {
                if (wantMic) { mic = new Source(); string err; if (mic.Open(false, out err)) MicOk = true; else { problems.Add(err); mic = null; } }
                if (wantSys) { sys = new Source(); string err; if (sys.Open(true, out err)) SystemOk = true; else { problems.Add(err); sys = null; } }
                Problem = string.Join("; ", problems.ToArray());
                if (mic == null && sys == null) { ready.Set(); return; }

                fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                fs.Write(new byte[WavHeader], 0, WavHeader);
                ready.Set();

                var mix = new float[Rate / 2 * Channels];
                var a = new float[mix.Length];
                var bytes = new byte[mix.Length * 2];
                while (true)
                {
                    bool last = stop;
                    if (mic != null) mic.Pump();
                    if (sys != null) sys.Pump();
                    if (paused)
                    {
                        if (mic != null) mic.Clear();
                        if (sys != null) sys.Clear();
                    }
                    else
                    {
                        long need = clockMs() * Rate / 1000 - framesWritten;
                        while (need > 0)
                        {
                            int n = (int)Math.Min(need, Rate / 2);
                            Array.Clear(mix, 0, n * Channels);
                            if (mic != null) { mic.Take(a, n); if (MicMuted) Array.Clear(a, 0, n * Channels); Add(mix, a, n); MicLevel = Peak(a, n); }
                            if (sys != null) { sys.Take(a, n); if (SystemMuted) Array.Clear(a, 0, n * Channels); Add(mix, a, n); SystemLevel = Peak(a, n); }
                            for (int i = 0; i < n * Channels; i++)
                            {
                                float v = mix[i]; if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                                short s = (short)Math.Round(v * 32767f);
                                bytes[i * 2] = (byte)(s & 0xff); bytes[i * 2 + 1] = (byte)((s >> 8) & 0xff);
                            }
                            fs.Write(bytes, 0, n * Channels * 2);
                            framesWritten += n; need -= n;
                        }
                    }
                    if (last) break;
                    Thread.Sleep(15);
                }
            }
            catch (Exception ex) { if (string.IsNullOrEmpty(Problem)) Problem = ex.Message; }
            finally
            {
                ready.Set();
                if (mic != null) mic.Close();
                if (sys != null) sys.Close();
                if (fs != null)
                {
                    try
                    {
                        long data = fs.Length - WavHeader;
                        fs.Seek(0, SeekOrigin.Begin);
                        var w = new BinaryWriter(fs);
                        w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' }); w.Write((int)(36 + data));
                        w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
                        w.Write(16); w.Write((short)1); w.Write((short)Channels); w.Write(Rate); w.Write(Rate * Channels * 2); w.Write((short)(Channels * 2)); w.Write((short)16);
                        w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write((int)data);
                        w.Flush();
                    }
                    catch { }
                    fs.Dispose();
                }
            }
        }

        static void Add(float[] dst, float[] src, int frames) { for (int i = 0; i < frames * Channels; i++) dst[i] += src[i]; }

        static float Peak(float[] s, int frames)
        {
            float p = 0;
            for (int i = 0; i < frames * Channels; i++) { float v = Math.Abs(s[i]); if (v > p) p = v; }
            return Math.Min(1f, p);
        }

        // ------------------------------------------------------------------ one WASAPI source

        sealed class Source
        {
            IAudioClient client; IAudioCaptureClient cap;
            int srcRate, srcCh, bits; bool isFloat;
            float[] queue = new float[Rate * Channels];           // interleaved stereo at 48 kHz, oldest first
            int count;                                            // floats in the queue
            float prevL, prevR; double pos;                       // resampler state

            public bool Open(bool loopback, out string error)
            {
                error = null;
                IntPtr fmt = IntPtr.Zero;
                try
                {
                    var en = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
                    IMMDevice dev;
                    int hr = en.GetDefaultAudioEndpoint(loopback ? 0 : 1, 0, out dev);
                    if (hr < 0 || dev == null) { error = loopback ? "no speakers found" : "no microphone found"; return false; }
                    Guid iid = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
                    object o; hr = dev.Activate(ref iid, 23, IntPtr.Zero, out o);
                    if (hr < 0) { error = Describe(hr, loopback); return false; }
                    client = (IAudioClient)o;
                    hr = client.GetMixFormat(out fmt);
                    if (hr < 0) { error = Describe(hr, loopback); return false; }
                    int tag = Marshal.ReadInt16(fmt, 0);
                    srcCh = Marshal.ReadInt16(fmt, 2); srcRate = Marshal.ReadInt32(fmt, 4); bits = Marshal.ReadInt16(fmt, 14);
                    isFloat = tag == 3;
                    if (tag == unchecked((short)0xFFFE) && Marshal.ReadInt16(fmt, 0) != 0) isFloat = Marshal.ReadInt32(fmt, 24) == 3;
                    if (srcCh < 1 || srcRate < 8000) { error = "unsupported audio format"; return false; }
                    hr = client.Initialize(0, loopback ? 0x00020000 : 0, 10000000L, 0, fmt, IntPtr.Zero);
                    if (hr < 0) { error = Describe(hr, loopback); return false; }
                    Guid cid = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
                    object c; hr = client.GetService(ref cid, out c);
                    if (hr < 0) { error = Describe(hr, loopback); return false; }
                    cap = (IAudioCaptureClient)c;
                    hr = client.Start();
                    if (hr < 0) { error = Describe(hr, loopback); return false; }
                    return true;
                }
                catch (Exception ex) { error = ex.Message; return false; }
                finally { if (fmt != IntPtr.Zero) Marshal.FreeCoTaskMem(fmt); }
            }

            static string Describe(int hr, bool loopback)
            {
                if ((uint)hr == 0x80070005) return loopback ? "access to the speakers was denied" : "microphone access is blocked (Windows Settings > Privacy > Microphone)";
                if ((uint)hr == 0x88890004 || (uint)hr == 0x88890008) return loopback ? "speakers unavailable" : "microphone unavailable";
                return (loopback ? "speakers" : "microphone") + " error 0x" + ((uint)hr).ToString("X8");
            }

            public void Close()
            {
                try { if (client != null) client.Stop(); } catch { }
                if (cap != null) { try { Marshal.ReleaseComObject(cap); } catch { } cap = null; }
                if (client != null) { try { Marshal.ReleaseComObject(client); } catch { } client = null; }
            }

            public void Clear() { count = 0; }

            /// <summary>Moves everything the device has delivered so far into the queue (converted to 48 kHz stereo floats).</summary>
            public void Pump()
            {
                if (cap == null) return;
                while (true)
                {
                    int n;
                    if (cap.GetNextPacketSize(out n) < 0 || n == 0) return;
                    IntPtr data; int frames, flags; long dp, qp;
                    if (cap.GetBuffer(out data, out frames, out flags, out dp, out qp) < 0) return;
                    try
                    {
                        if (frames > 0) Append(data, frames, (flags & 2) != 0);
                    }
                    finally { cap.ReleaseBuffer(frames); }
                }
            }

            void Append(IntPtr data, int frames, bool silent)
            {
                int bytesPer = bits / 8, stride = bytesPer * srcCh;
                var inp = new float[frames * 2];
                if (!silent)
                {
                    var raw = new byte[frames * stride];
                    Marshal.Copy(data, raw, 0, raw.Length);
                    for (int f = 0; f < frames; f++)
                    {
                        inp[f * 2] = Sample(raw, f * stride);
                        inp[f * 2 + 1] = srcCh > 1 ? Sample(raw, f * stride + bytesPer) : inp[f * 2];
                    }
                }
                if (srcRate == Rate) { Push(inp, frames); return; }

                // linear resampling to 48 kHz; prev* is the last input frame of the previous packet
                double step = (double)srcRate / Rate;
                var outp = new List<float>((int)(frames / step * 2) + 8);
                while (pos < frames)
                {
                    int i = (int)Math.Floor(pos); double fr = pos - i;
                    float l0 = i == 0 ? prevL : inp[(i - 1) * 2], r0 = i == 0 ? prevR : inp[(i - 1) * 2 + 1];
                    float l1 = inp[i * 2], r1 = inp[i * 2 + 1];
                    outp.Add((float)(l0 + (l1 - l0) * fr)); outp.Add((float)(r0 + (r1 - r0) * fr));
                    pos += step;
                }
                pos -= frames;
                prevL = inp[(frames - 1) * 2]; prevR = inp[(frames - 1) * 2 + 1];
                var arr = outp.ToArray();
                Push(arr, arr.Length / 2);
            }

            float Sample(byte[] b, int o)
            {
                if (isFloat && bits == 32) return BitConverter.ToSingle(b, o);
                switch (bits)
                {
                    case 16: return BitConverter.ToInt16(b, o) / 32768f;
                    case 24: return (((b[o + 2] << 24) | (b[o + 1] << 16) | (b[o] << 8)) >> 8) / 8388608f;
                    case 32: return BitConverter.ToInt32(b, o) / 2147483648f;
                    default: return 0;
                }
            }

            void Push(float[] s, int frames)
            {
                int n = frames * 2;
                if (count + n > queue.Length)
                {
                    // far behind the clock (a stall): keep the newest second
                    if (n >= queue.Length) { Array.Copy(s, n - queue.Length, queue, 0, queue.Length); count = queue.Length; return; }
                    int drop = count + n - queue.Length;
                    Array.Copy(queue, drop, queue, 0, count - drop); count -= drop;
                }
                Array.Copy(s, 0, queue, count, n); count += n;
            }

            /// <summary>Removes <paramref name="frames"/> stereo frames from the queue into <paramref name="dst"/>, padding with silence when short.</summary>
            public void Take(float[] dst, int frames)
            {
                int n = frames * 2, have = Math.Min(n, count);
                Array.Copy(queue, 0, dst, 0, have);
                if (have < n) Array.Clear(dst, have, n - have);
                Array.Copy(queue, have, queue, 0, count - have); count -= have;
            }
        }

        // ------------------------------------------------------------------ COM

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorComObject { }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        }

        [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioClient
        {
            [PreserveSig] int Initialize(int shareMode, int streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr sessionGuid);
            [PreserveSig] int GetBufferSize(out int frames);
            [PreserveSig] int GetStreamLatency(out long latency);
            [PreserveSig] int GetCurrentPadding(out int padding);
            [PreserveSig] int IsFormatSupported(int shareMode, IntPtr format, out IntPtr closest);
            [PreserveSig] int GetMixFormat(out IntPtr format);
            [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
            [PreserveSig] int Start();
            [PreserveSig] int Stop();
            [PreserveSig] int Reset();
            [PreserveSig] int SetEventHandle(IntPtr handle);
            [PreserveSig] int GetService(ref Guid riid, [MarshalAs(UnmanagedType.IUnknown)] out object service);
        }

        [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IAudioCaptureClient
        {
            [PreserveSig] int GetBuffer(out IntPtr data, out int frames, out int flags, out long devicePosition, out long qpcPosition);
            [PreserveSig] int ReleaseBuffer(int frames);
            [PreserveSig] int GetNextPacketSize(out int frames);
        }
    }
}
