using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;

namespace RXCapture
{
    /// <summary>Decodes a video on a thread of its own, so the editor's UI thread only has to paint the frames it is handed.
    /// The Media Foundation reader is created, used and released on that thread only (its COM objects cannot move between apartments).
    /// Frames live in a fixed set of bitmaps: the player takes one with <see cref="TakeReady"/>/<see cref="TakeLanded"/> and gives it back with <see cref="Release"/>.</summary>
    sealed class FramePump : IDisposable
    {
        public sealed class Frame { public Bitmap Bmp; public TimeSpan Ts; }

        const int Buffers = 7;                 // one on screen, one landing, up to three decoded ahead, the rest for the decoder
        readonly object sync = new object();
        readonly Queue<Frame> ready = new Queue<Frame>();
        readonly Stack<Bitmap> pool = new Stack<Bitmap>();
        readonly List<Bitmap> all = new List<Bitmap>();
        readonly string path;
        readonly Thread thread;
        readonly ManualResetEvent opened = new ManualResetEvent(false);
        Frame landed;                          // the picture a seek arrived at
        int gen;                               // counts seeks: work started before the latest one is thrown away
        bool stop, wantSeek, quick, eof, active, hold, failed;
        TimeSpan seekTo;

        public int Width, Height, Fps;
        public TimeSpan Duration;

        public FramePump(string path)
        {
            this.path = path;
            thread = new Thread(Run) { IsBackground = true, Name = "video decoder" };
            thread.Start();
        }

        /// <summary>Waits until the file is open and its first frame decoded (see <see cref="TakeLanded"/>). False when it cannot be played.</summary>
        public bool WaitOpened(int ms) { return opened.WaitOne(ms) && !Failed; }
        public bool Failed { get { lock (sync) return failed; } }
        /// <summary>Every frame has been decoded and handed out.</summary>
        public bool Ended { get { lock (sync) return eof && ready.Count == 0 && !wantSeek; } }

        /// <summary>The next frame shown is the last one at or before <paramref name="t"/> (<paramref name="fast"/>: the first one the decoder reaches).</summary>
        public void Seek(TimeSpan t, bool fast)
        {
            lock (sync) { gen++; seekTo = t; quick = fast; wantSeek = true; eof = false; Monitor.PulseAll(sync); }
        }

        /// <summary>True while playing: the decoder keeps a few frames ready. Otherwise it decodes just the next one.</summary>
        public bool Active { set { lock (sync) { active = value; Monitor.PulseAll(sync); } } }
        /// <summary>True while the seek bar is dragged: no read-ahead, so every seek has the decoder to itself.</summary>
        public bool Hold { set { lock (sync) { hold = value; Monitor.PulseAll(sync); } } }

        public Frame TakeLanded() { lock (sync) { var f = landed; landed = null; return f; } }

        public Frame TakeReady(TimeSpan upTo)
        {
            lock (sync)
            {
                if (ready.Count == 0 || ready.Peek().Ts > upTo) return null;
                var f = ready.Dequeue(); Monitor.PulseAll(sync); return f;
            }
        }

        public void Release(Frame f)
        {
            if (f == null) return;
            lock (sync) { pool.Push(f.Bmp); Monitor.PulseAll(sync); }
        }

        void Run()
        {
            Mp4Writer.Reader rd = null;
            try
            {
                rd = new Mp4Writer.Reader(path);
                Width = rd.Width; Height = rd.Height; Fps = rd.Fps; Duration = rd.Duration;
                lock (sync) for (int i = 0; i < Buffers; i++) { var b = new Bitmap(Width, Height, PixelFormat.Format32bppRgb); all.Add(b); pool.Push(b); }
                Bitmap first; lock (sync) first = pool.Pop();
                TimeSpan t0;
                if (!rd.ReadFrame(first, out t0)) throw new InvalidDataException("no video frames");
                lock (sync) landed = new Frame { Bmp = first, Ts = t0 };
                opened.Set();
                Loop(rd);
            }
            catch { lock (sync) failed = true; }
            finally { if (rd != null) rd.Dispose(); opened.Set(); }
        }

        void Loop(Mp4Writer.Reader rd)
        {
            while (true)
            {
                bool doSeek, fast; TimeSpan st; int g; Bitmap b = null;
                lock (sync)
                {
                    while (true)
                    {
                        if (stop) return;
                        if (wantSeek) break;
                        if (!hold && !eof && pool.Count > 0 && ready.Count < (active ? 3 : 1)) break;
                        Monitor.Wait(sync);
                    }
                    doSeek = wantSeek; wantSeek = false; st = seekTo; fast = quick; g = gen;
                    if (doSeek)
                    {
                        while (ready.Count > 0) pool.Push(ready.Dequeue().Bmp);
                        if (landed != null) { pool.Push(landed.Bmp); landed = null; }
                    }
                    else b = pool.Pop();
                }
                if (doSeek) DoSeek(rd, st, fast, g); else Prefetch(rd, b, g);
            }
        }

        void Prefetch(Mp4Writer.Reader rd, Bitmap b, int g)
        {
            TimeSpan ts; bool ok;
            try { ok = rd.ReadFrame(b, out ts); }
            catch { lock (sync) pool.Push(b); throw; }
            lock (sync)
            {
                if (ok && gen == g) ready.Enqueue(new Frame { Bmp = b, Ts = ts });
                else { pool.Push(b); if (!ok && gen == g) eof = true; }
                Monitor.PulseAll(sync);
            }
        }

        void DoSeek(Mp4Writer.Reader rd, TimeSpan t, bool fast, int g)
        {
            rd.Seek(t);                                                       // lands on the key frame before t
            Frame last = null, ahead = null;
            while (true)
            {
                Bitmap b;
                lock (sync)
                {
                    if (gen != g || stop) { if (last != null) pool.Push(last.Bmp); return; }
                    b = pool.Count > 0 ? pool.Pop() : null;
                }
                if (b == null) { Thread.Sleep(2); continue; }
                TimeSpan ts; bool ok;
                try { ok = rd.ReadFrame(b, out ts); }
                catch { lock (sync) { pool.Push(b); if (last != null) pool.Push(last.Bmp); } throw; }
                if (!ok) { lock (sync) { pool.Push(b); if (gen == g) eof = true; } break; }
                if (ts > t && last != null) { ahead = new Frame { Bmp = b, Ts = ts }; break; }    // the first frame after t stays queued
                if (last != null) lock (sync) pool.Push(last.Bmp);
                last = new Frame { Bmp = b, Ts = ts };
                if (fast) break;
            }
            lock (sync)
            {
                if (gen == g)
                {
                    if (last != null) { if (landed != null) pool.Push(landed.Bmp); landed = last; }
                    if (ahead != null) ready.Enqueue(ahead);
                }
                else { if (last != null) pool.Push(last.Bmp); if (ahead != null) pool.Push(ahead.Bmp); }
                Monitor.PulseAll(sync);
            }
        }

        public void Dispose()
        {
            lock (sync) { stop = true; Monitor.PulseAll(sync); }
            if (Thread.CurrentThread == thread) return;
            if (!thread.Join(5000)) return;                                   // still decoding: leave the bitmaps to the finalizer rather than free them under it
            foreach (var b in all) b.Dispose();
            opened.Close();
        }
    }
}
