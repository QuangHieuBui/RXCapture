using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace RXCapture
{
    public static partial class Mp4Writer
    {
        /// <summary>One screen picture in the memory the encoder reads (top row first, BGRA). Fill <see cref="Pixels"/>, call <see cref="Seal"/>,
        /// then hand it to <see cref="Live.Write"/> - the same frame may be written several times. <see cref="Dispose"/> once per owner.</summary>
        public sealed class Frame : IDisposable
        {
            IMFMediaBuffer Buffer;
            internal object Media { get { return Buffer; } }      // typed object: the buffer interface is private to Mp4Writer; Live casts it back
            public IntPtr Pixels { get; private set; }
            public readonly int Stride, Height;
            int owners = 1; bool locked;

            internal Frame(int w, int h)
            {
                Stride = w * 4; Height = h;
                Check(MFCreateMemoryBuffer(Stride * h, out Buffer));
                int max, cur; IntPtr p; Check(Buffer.Lock(out p, out max, out cur)); Pixels = p; locked = true;
            }

            /// <summary>The pixels are complete: unlock the buffer so the encoder may read it.</summary>
            public void Seal()
            {
                if (!locked) return;
                locked = false; Check(Buffer.Unlock()); Check(Buffer.SetCurrentLength(Stride * Height));
            }

            /// <summary>One more owner (e.g. the encoder queue): each owner calls <see cref="Dispose"/> when done.</summary>
            public Frame Retain() { Interlocked.Increment(ref owners); return this; }

            public void Dispose()
            {
                if (Interlocked.Decrement(ref owners) != 0) return;
                if (locked) { locked = false; try { Buffer.Unlock(); } catch { } }
                if (Buffer != null) { Marshal.ReleaseComObject(Buffer); Buffer = null; }
            }
        }

        /// <summary>Records straight to an H.264 MP4 while the screen is captured: nothing is converted afterwards, so stopping and saving take
        /// no time and the picture is encoded once (no intermediate JPEG). Uses a hardware encoder (NVENC / Quick Sync / AMF) when the PC has one,
        /// Windows' software encoder otherwise. <see cref="TryOpen"/> returns null when Windows has no H.264 encoder (N editions).</summary>
        public sealed class Live : IDisposable
        {
            IMFSinkWriter writer;
            AudioFeed feed; WavReader wav;
            readonly int w, h;
            readonly long duration;
            bool started;
            long lastTime = -1;
            int audioStream = -1;
            public readonly string Path;

            Live(string path, int w, int h, int fps) { Path = path; this.w = w; this.h = h; duration = 10000000L / fps; }

            /// <summary>Opens <paramref name="mp4"/> for writing. With <paramref name="withSound"/> an AAC track is added (feed it with
            /// <see cref="AttachSound"/>). Width and height must be even.</summary>
            public static Live TryOpen(string mp4, int w, int h, int fps, bool withSound)
            {
                if (w < 16 || h < 16 || ((w | h) & 1) != 0) return null;
                for (int mode = 1; mode <= 2; mode++)                     // hardware encoders allowed first, then software only
                {
                    if (MFStartup(MfVersion, 0) != 0) return null;
                    var l = new Live(mp4, w, h, fps) { started = true };
                    try
                    {
                        l.writer = Open(mp4, w, h, fps, withSound ? AudioRecorder.Rate : 0, AudioRecorder.Channels, out l.audioStream, mode);
                        return l;
                    }
                    catch { l.Dispose(); try { System.IO.File.Delete(mp4); } catch { } }
                }
                return null;
            }

            /// <summary>The sound comes from <paramref name="wavPath"/>, which <see cref="AudioRecorder"/> is still writing: whatever is there is
            /// added in step with the pictures.</summary>
            public void AttachSound(string wavPath)
            {
                if (audioStream < 0) return;
                wav = new WavReader(wavPath);
                feed = new AudioFeed(wav, writer, audioStream, AudioRecorder.Rate, AudioRecorder.Channels * 2) { Live = true };
            }

            public Frame NewFrame() { return new Frame(w, h); }

            /// <summary>Gives <paramref name="f"/> (sealed) to the encoder as the picture shown from <paramref name="time"/> (100 ns since the start).
            /// A frame that is not later than the previous one is dropped. Also adds the sound recorded so far.</summary>
            public bool Write(Frame f, long time)
            {
                if (writer == null || time <= lastTime) return false;
                lastTime = time;
                IMFSample sample; Check(MFCreateSample(out sample));
                try
                {
                    Check(sample.AddBuffer((IMFMediaBuffer)f.Media));
                    Check(sample.SetSampleTime(time));
                    Check(sample.SetSampleDuration(duration));
                    Check(writer.WriteSample(0, sample));
                }
                finally { Marshal.ReleaseComObject(sample); }
                if (feed != null) feed.Pump(time);
                return true;
            }

            /// <summary>Adds the rest of the sound (call after the <see cref="AudioRecorder"/> has stopped) and closes the file. False when it cannot be completed.</summary>
            public bool Finish(bool anyFrame)
            {
                if (writer == null) return false;
                bool ok = false;
                try
                {
                    if (anyFrame && feed != null) { feed.Live = false; feed.Drain(); }
                    Check(writer.Finalize_());
                    ok = anyFrame;
                }
                catch { ok = false; }
                finally { Dispose(); }
                return ok;
            }

            public void Dispose()
            {
                if (wav != null) { wav.Dispose(); wav = null; }
                if (writer != null) { try { Marshal.ReleaseComObject(writer); } catch { } writer = null; }
                if (started) { started = false; try { MFShutdown(); } catch { } }
            }
        }
    }
}
