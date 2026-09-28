using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace ShotCraft
{
    /// <summary>Minimal AVI (RIFF) muxer for Motion-JPEG video, playable by Windows Media Player, VLC, browsers via ffmpeg, etc.</summary>
    public class AviWriter : IDisposable
    {
        readonly FileStream fs;
        readonly BinaryWriter bw;
        readonly int w, h, fps;
        long totalFramesPos, strhLengthPos, moviSizePos, moviFourCcPos, bufferSizePos1, bufferSizePos2;
        readonly List<int> offsets = new List<int>();
        readonly List<int> sizes = new List<int>();
        int maxFrame;
        bool closed;

        public int FrameCount { get { return offsets.Count; } }

        public AviWriter(string path, int width, int height, int fps)
        {
            w = width; h = height; this.fps = Math.Max(1, fps);
            fs = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
            bw = new BinaryWriter(fs);
            WriteHeader();
        }

        void Fcc(string s) { bw.Write(Encoding.ASCII.GetBytes(s)); }

        void WriteHeader()
        {
            Fcc("RIFF"); bw.Write(0); Fcc("AVI ");
            Fcc("LIST"); bw.Write(192); Fcc("hdrl");
            Fcc("avih"); bw.Write(56);
            bw.Write(1000000 / fps);           // dwMicroSecPerFrame
            bw.Write(0);                       // dwMaxBytesPerSec
            bw.Write(0);                       // dwPaddingGranularity
            bw.Write(0x10);                    // dwFlags: AVIF_HASINDEX
            totalFramesPos = fs.Position; bw.Write(0);   // dwTotalFrames
            bw.Write(0);                       // dwInitialFrames
            bw.Write(1);                       // dwStreams
            bufferSizePos1 = fs.Position; bw.Write(0);   // dwSuggestedBufferSize
            bw.Write(w); bw.Write(h);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            Fcc("LIST"); bw.Write(116); Fcc("strl");
            Fcc("strh"); bw.Write(56);
            Fcc("vids"); Fcc("MJPG");
            bw.Write(0);                       // dwFlags
            bw.Write((short)0); bw.Write((short)0);   // priority, language
            bw.Write(0);                       // initial frames
            bw.Write(1); bw.Write(fps);        // scale, rate
            bw.Write(0);                       // start
            strhLengthPos = fs.Position; bw.Write(0);    // length
            bufferSizePos2 = fs.Position; bw.Write(0);   // suggested buffer
            bw.Write(-1);                      // quality
            bw.Write(0);                       // sample size
            bw.Write((short)0); bw.Write((short)0); bw.Write((short)w); bw.Write((short)h);
            Fcc("strf"); bw.Write(40);
            bw.Write(40); bw.Write(w); bw.Write(h);
            bw.Write((short)1); bw.Write((short)24);
            Fcc("MJPG");
            bw.Write(w * h * 3);
            bw.Write(0); bw.Write(0); bw.Write(0); bw.Write(0);
            Fcc("LIST"); moviSizePos = fs.Position; bw.Write(0); moviFourCcPos = fs.Position; Fcc("movi");
        }

        public void AddFrame(byte[] jpeg)
        {
            long chunkPos = fs.Position;
            Fcc("00dc"); bw.Write(jpeg.Length);
            bw.Write(jpeg);
            if ((jpeg.Length & 1) == 1) bw.Write((byte)0);
            offsets.Add((int)(chunkPos - moviFourCcPos));
            sizes.Add(jpeg.Length);
            if (jpeg.Length > maxFrame) maxFrame = jpeg.Length;
        }

        public void Close()
        {
            if (closed) return;
            closed = true;
            long moviEnd = fs.Position;
            bw.Seek((int)moviSizePos, SeekOrigin.Begin); bw.Write((int)(moviEnd - moviSizePos - 4));
            bw.Seek((int)moviEnd, SeekOrigin.Begin);
            Fcc("idx1"); bw.Write(offsets.Count * 16);
            for (int i = 0; i < offsets.Count; i++) { Fcc("00dc"); bw.Write(0x10); bw.Write(offsets[i]); bw.Write(sizes[i]); }
            long end = fs.Position;
            bw.Seek(4, SeekOrigin.Begin); bw.Write((int)(end - 8));
            bw.Seek((int)totalFramesPos, SeekOrigin.Begin); bw.Write(offsets.Count);
            bw.Seek((int)strhLengthPos, SeekOrigin.Begin); bw.Write(offsets.Count);
            bw.Seek((int)bufferSizePos1, SeekOrigin.Begin); bw.Write(maxFrame);
            bw.Seek((int)bufferSizePos2, SeekOrigin.Begin); bw.Write(maxFrame);
            bw.Flush(); fs.Close();
        }

        public void Dispose() { Close(); }
    }

    public static class AviReader
    {
        /// <summary>Enumerates the JPEG frames of an MJPEG AVI produced by AviWriter.</summary>
        public static IEnumerable<byte[]> Frames(string path)
        {
            using (var fs = File.OpenRead(path))
            using (var br = new BinaryReader(fs))
            {
                if (Fcc(br) != "RIFF") yield break;
                br.ReadInt32(); Fcc(br);   // size, 'AVI '
                while (fs.Position + 8 <= fs.Length)
                {
                    string id = Fcc(br); int size = br.ReadInt32();
                    if (id == "LIST")
                    {
                        string type = Fcc(br);
                        if (type == "movi")
                        {
                            long end = fs.Position + size - 4;
                            while (fs.Position + 8 <= end)
                            {
                                string cid = Fcc(br); int csize = br.ReadInt32();
                                if (cid == "00dc" || cid == "00db") yield return br.ReadBytes(csize);
                                else fs.Seek(csize, SeekOrigin.Current);
                                if ((csize & 1) == 1) fs.Seek(1, SeekOrigin.Current);
                            }
                            yield break;
                        }
                        // hdrl etc: skip the list body
                        fs.Seek(size - 4, SeekOrigin.Current);
                    }
                    else fs.Seek(size + (size & 1), SeekOrigin.Current);
                }
            }
        }

        static string Fcc(BinaryReader br) { return Encoding.ASCII.GetString(br.ReadBytes(4)); }
    }
}
