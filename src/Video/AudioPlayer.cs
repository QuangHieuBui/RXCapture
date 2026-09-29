using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RXCapture
{
    /// <summary>Plays the sound of an MP4 through the default output (winmm waveOut). The editor's video player draws the pictures
    /// itself, so it starts and stops this player together with its own clock; <see cref="Start"/> begins at any position.</summary>
    public sealed class AudioPlayer : IDisposable
    {
        const int Buffers = 6;
        const uint WaveMapper = 0xFFFFFFFF, WHDR_DONE = 1;

        [StructLayout(LayoutKind.Sequential)] struct WAVEFORMATEX { public ushort tag, channels; public uint rate, bytesPerSec; public ushort blockAlign, bits, size; }
        [StructLayout(LayoutKind.Sequential)] struct WAVEHDR { public IntPtr data; public uint length, recorded; public IntPtr user; public uint flags, loops; public IntPtr next, reserved; }

        [DllImport("winmm.dll")] static extern int waveOutOpen(out IntPtr hwo, uint device, ref WAVEFORMATEX fmt, IntPtr callback, IntPtr instance, uint flags);
        [DllImport("winmm.dll")] static extern int waveOutPrepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutUnprepareHeader(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutWrite(IntPtr hwo, IntPtr hdr, int size);
        [DllImport("winmm.dll")] static extern int waveOutReset(IntPtr hwo);
        [DllImport("winmm.dll")] static extern int waveOutClose(IntPtr hwo);

        string path;                       // the file is opened again on the playing thread: Media Foundation readers must stay on the thread that made them
        WAVEFORMATEX fmt;
        Thread thread;
        sealed class Token { public volatile bool Stop; }
        Token current;                     // one per Start: an old playing thread can never be revived by a later Start
        IntPtr hwo;

        AudioPlayer() { }

        /// <summary>Null when the file has no sound or there is no output device.</summary>
        public static AudioPlayer TryOpen(string path)
        {
            var r = Mp4Writer.AudioReader.TryOpen(path);
            if (r == null) return null;
            var p = new AudioPlayer { path = path };
            p.fmt = new WAVEFORMATEX { tag = 1, channels = (ushort)r.Channels, rate = (uint)r.Rate, bits = 16, blockAlign = (ushort)r.BlockAlign, bytesPerSec = (uint)(r.Rate * r.BlockAlign), size = 0 };
            IntPtr test;
            r.Dispose();
            if (waveOutOpen(out test, WaveMapper, ref p.fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return null;
            waveOutClose(test);
            return p;
        }

        /// <summary>(Re)starts playing at <paramref name="from"/>.</summary>
        public void Start(TimeSpan from)
        {
            Stop();
            if (path == null) return;
            var tk = new Token(); current = tk;
            thread = new Thread(() => Feed(from.Ticks, tk)) { IsBackground = true, Name = "RXCapture audio out" };
            thread.Start();
        }

        /// <summary>Stops at once (the sound cuts off).</summary>
        public bool IsPlaying { get { var t = thread; return t != null && t.IsAlive; } }

        public void Stop()
        {
            var c = current; if (c != null) c.Stop = true;
            var t = thread; thread = null;
            if (t != null) t.Join(2000);
        }

        void Feed(long from, Token tk)
        {
            try
            {
                using (var rd = Mp4Writer.AudioReader.TryOpen(path))
                {
                    if (rd == null) return;
                    rd.Seek(TimeSpan.FromTicks(from));
                    Play(from, rd, tk);
                }
            }
            catch { }          // a sound problem must never take the application down
        }

        void Play(long from, Mp4Writer.AudioReader reader, Token tk)
        {
            IntPtr h;
            if (waveOutOpen(out h, WaveMapper, ref fmt, IntPtr.Zero, IntPtr.Zero, 0) != 0) return;
            hwo = h;
            int size = Marshal.SizeOf(typeof(WAVEHDR)), bufSize = (int)(fmt.bytesPerSec / 10) / fmt.blockAlign * fmt.blockAlign;   // 100 ms
            var hdrs = new IntPtr[Buffers]; var datas = new IntPtr[Buffers]; var used = new bool[Buffers];
            try
            {
                for (int i = 0; i < Buffers; i++)
                {
                    datas[i] = Marshal.AllocHGlobal(bufSize); hdrs[i] = Marshal.AllocHGlobal(size);
                    var hd = new WAVEHDR { data = datas[i], length = (uint)bufSize };
                    Marshal.StructureToPtr(hd, hdrs[i], false);
                    waveOutPrepareHeader(h, hdrs[i], size);
                }
                byte[] pcm = null; int pos = 0; bool ended = false;
                var fill = new byte[bufSize];
                int next = 0;
                while (!tk.Stop)
                {
                    // wait for the next buffer to be free
                    var hd = (WAVEHDR)Marshal.PtrToStructure(hdrs[next], typeof(WAVEHDR));
                    if (used[next] && (hd.flags & WHDR_DONE) == 0) { Thread.Sleep(5); continue; }
                    if (ended && !used[next]) break;
                    // gather one buffer of samples
                    int have = 0;
                    while (have < bufSize && !ended)
                    {
                        if (pcm == null || pos >= pcm.Length)
                        {
                            long t;
                            if (!reader.Next(out pcm, out t)) { ended = true; pcm = null; break; }
                            pos = 0;
                            long end = t + (long)(pcm.Length / reader.BlockAlign) * 10000000L / reader.Rate;
                            if (end <= from) { pcm = null; continue; }                                  // before the start position (the seek lands earlier)
                            if (t < from) pos = (int)((from - t) * reader.Rate / 10000000L) * reader.BlockAlign;
                            if (pos >= pcm.Length) { pcm = null; continue; }
                        }
                        int n = Math.Min(bufSize - have, pcm.Length - pos);
                        Buffer.BlockCopy(pcm, pos, fill, have, n); have += n; pos += n;
                    }
                    if (have == 0) { used[next] = false; if (ended && AllDone(hdrs, used)) break; next = (next + 1) % Buffers; Thread.Sleep(5); continue; }
                    Marshal.Copy(fill, 0, datas[next], have);
                    // keep the prepared flag: only length changes
                    hd.length = (uint)have; Marshal.StructureToPtr(hd, hdrs[next], false);
                    waveOutWrite(h, hdrs[next], size);
                    used[next] = true;
                    next = (next + 1) % Buffers;
                }
                // let what was written finish playing (natural end), or cut it at once when stopped
                while (!tk.Stop && !AllDone(hdrs, used)) Thread.Sleep(10);
            }
            finally
            {
                waveOutReset(h);
                for (int i = 0; i < Buffers; i++)
                {
                    if (hdrs[i] != IntPtr.Zero) { waveOutUnprepareHeader(h, hdrs[i], size); Marshal.FreeHGlobal(hdrs[i]); }
                    if (datas[i] != IntPtr.Zero) Marshal.FreeHGlobal(datas[i]);
                }
                waveOutClose(h); hwo = IntPtr.Zero;
            }
        }

        static bool AllDone(IntPtr[] hdrs, bool[] used)
        {
            for (int i = 0; i < hdrs.Length; i++)
            {
                if (!used[i]) continue;
                var hd = (WAVEHDR)Marshal.PtrToStructure(hdrs[i], typeof(WAVEHDR));
                if ((hd.flags & WHDR_DONE) == 0) return false;
            }
            return true;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
