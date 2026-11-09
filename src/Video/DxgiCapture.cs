using System;
using System.Drawing;
using System.Runtime.InteropServices;

namespace RXCapture
{
    /// <summary>Grabs a rectangle of one monitor through DXGI Desktop Duplication: the GPU hands over the finished desktop image, so a
    /// 2560x1440 frame costs a few milliseconds where GDI's BitBlt needs about 30 (which capped the recorder below 30 fps).
    /// <see cref="TryCreate"/> returns null whenever DXGI cannot serve the region (it spans several monitors, the monitor is rotated, the
    /// desktop is HDR, an old Windows, a locked session...): the caller then keeps using GDI. Everything must be used on one thread.</summary>
    public sealed unsafe class DxgiCapture : IDisposable
    {
        public readonly int Width, Height;
        readonly Rectangle region;                // screen coordinates
        IntPtr device, context, staging, outputPtr;
        IDXGIOutput1 output;
        IDXGIOutputDuplication dup;
        Rectangle outputRect;
        bool hasFrame;
        int failures;
        CopySubresourceRegionFn copyRegion; MapFn map; UnmapFn unmap;

        public bool HasFrame { get { return hasFrame; } }

        DxgiCapture(Rectangle region) { this.region = region; Width = region.Width; Height = region.Height; }

        /// <summary>Opens duplication of the monitor that contains <paramref name="region"/> entirely; null when that is not possible.</summary>
        public static DxgiCapture TryCreate(Rectangle region)
        {
            if (region.Width < 1 || region.Height < 1) return null;
            var c = new DxgiCapture(region);
            try
            {
                if (!c.Open()) { c.Dispose(); return null; }
                bool changed;
                // The first frame DXGI hands out can be empty (black) until the desktop is really redrawn, so <see cref="HasFrame"/> may still be
                // false here: the caller takes a GDI picture until the first real one arrives.
                if (!c.Update(200, out changed)) { c.Dispose(); return null; }
                return c;
            }
            catch { c.Dispose(); return null; }
        }

        bool Open()
        {
            Guid fid = typeof(IDXGIFactory1).GUID;
            IntPtr fp;
            if (CreateDXGIFactory1(ref fid, out fp) < 0) return false;
            var factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(fp); Marshal.Release(fp);
            try
            {
                for (uint a = 0; ; a++)
                {
                    IDXGIAdapter1 adapter;
                    if (factory.EnumAdapters1(a, out adapter) < 0) return false;
                    try
                    {
                        for (uint o = 0; ; o++)
                        {
                            IntPtr op;
                            if (adapter.EnumOutputs(o, out op) < 0) break;
                            IDXGIOutput1 candidate = null; bool keep = false;
                            try
                            {
                                candidate = (IDXGIOutput1)Marshal.GetObjectForIUnknown(op);
                                OutputDesc d;
                                if (candidate.GetDesc(out d) < 0 || d.Attached == 0 || (d.Rotation != 0 && d.Rotation != 1)) continue;
                                var r = Rectangle.FromLTRB(d.Left, d.Top, d.Right, d.Bottom);
                                if (!r.Contains(region)) continue;
                                IntPtr ap = Marshal.GetComInterfaceForObject(adapter, typeof(IDXGIAdapter1));
                                IntPtr dev, ctx; int level;
                                int hr = D3D11CreateDevice(ap, 0, IntPtr.Zero, 0x20, IntPtr.Zero, 0, 7, out dev, out level, out ctx);   // 0x20: BGRA support
                                Marshal.Release(ap);
                                if (hr < 0) continue;
                                device = dev; context = ctx; output = candidate; outputPtr = op; outputRect = r; keep = true;
                            }
                            finally
                            {
                                if (!keep) { if (candidate != null) Marshal.ReleaseComObject(candidate); Marshal.Release(op); }
                            }
                            if (keep) return Prepare();
                        }
                    }
                    finally { Marshal.ReleaseComObject(adapter); }
                }
            }
            finally { Marshal.ReleaseComObject(factory); }
        }

        bool Prepare()
        {
            // ID3D11Device::CreateTexture2D is slot 5; ID3D11DeviceContext: Map 14, Unmap 15, CopySubresourceRegion 46
            var create = Fn<CreateTexture2DFn>(device, 5);
            copyRegion = Fn<CopySubresourceRegionFn>(context, 46); map = Fn<MapFn>(context, 14); unmap = Fn<UnmapFn>(context, 15);
            var desc = new TexDesc { Width = (uint)Width, Height = (uint)Height, MipLevels = 1, ArraySize = 1, Format = Bgra8, SampleCount = 1, Usage = 3 /* staging */, CpuAccess = 0x20000 /* read */ };
            if (create(device, ref desc, IntPtr.Zero, out staging) < 0) return false;
            return Duplicate();
        }

        bool Duplicate()
        {
            if (dup != null) { Marshal.ReleaseComObject(dup); dup = null; }
            IDXGIOutputDuplication d;
            if (output.DuplicateOutput(device, out d) < 0) return false;
            dup = d;
            return true;
        }

        /// <summary>Waits up to <paramref name="timeoutMs"/> for the desktop to change. True when duplication works (also when nothing changed:
        /// <paramref name="changed"/> is then false and the previous image is still the current one); false when it is unavailable right now
        /// (a lost session, the secure desktop, a mode switch) - use GDI for that frame.</summary>
        public bool Update(int timeoutMs, out bool changed)
        {
            changed = false;
            if (dup == null && !Duplicate()) return Fail();
            FrameInfo info; IntPtr res;
            int hr = dup.AcquireNextFrame((uint)timeoutMs, out info, out res);
            if (hr == WaitTimeout) { failures = 0; return true; }
            if (hr == AccessLost) { Marshal.ReleaseComObject(dup); dup = null; return Fail(); }
            if (hr < 0) return Fail();
            try
            {
                if (info.LastPresentTime != 0)                              // 0 = only the mouse pointer moved: the texture has no picture then
                {
                    IntPtr tex;
                    Guid tid = Texture2DId;
                    if (Marshal.QueryInterface(res, ref tid, out tex) < 0) return Fail();
                    try
                    {
                        var box = new Box { Left = (uint)(region.Left - outputRect.Left), Top = (uint)(region.Top - outputRect.Top), Front = 0, Right = (uint)(region.Right - outputRect.Left), Bottom = (uint)(region.Bottom - outputRect.Top), Back = 1 };
                        copyRegion(context, staging, 0, 0, 0, 0, tex, 0, ref box);
                        hasFrame = true; changed = true;
                    }
                    finally { Marshal.Release(tex); }
                }
            }
            finally { Marshal.Release(res); dup.ReleaseFrame(); }
            failures = 0;
            return true;
        }

        bool Fail() { failures++; return false; }

        /// <summary>Many failures in a row: the caller should stop trying DXGI for this recording.</summary>
        public bool Broken { get { return failures > 200; } }

        /// <summary>Copies the latest image (top row first, BGRA, <see cref="Width"/> x <see cref="Height"/>) to <paramref name="dst"/>.</summary>
        public bool CopyTo(IntPtr dst, int dstStride)
        {
            if (!hasFrame || staging == IntPtr.Zero) return false;
            Mapped m;
            if (map(context, staging, 0, 1 /* read */, 0, out m) < 0) return false;
            try
            {
                int row = Width * 4;
                for (int y = 0; y < Height; y++)
                    Buffer.MemoryCopy((byte*)m.Data + (long)y * m.RowPitch, (byte*)dst + (long)y * dstStride, row, row);
            }
            finally { unmap(context, staging, 0); }
            return true;
        }

        public void Dispose()
        {
            if (dup != null) { try { Marshal.ReleaseComObject(dup); } catch { } dup = null; }
            if (staging != IntPtr.Zero) { Marshal.Release(staging); staging = IntPtr.Zero; }
            if (output != null) { try { Marshal.ReleaseComObject(output); } catch { } output = null; }
            if (outputPtr != IntPtr.Zero) { Marshal.Release(outputPtr); outputPtr = IntPtr.Zero; }
            if (context != IntPtr.Zero) { Marshal.Release(context); context = IntPtr.Zero; }
            if (device != IntPtr.Zero) { Marshal.Release(device); device = IntPtr.Zero; }
        }

        // ------------------------------------------------------------------ interop

        const uint Bgra8 = 87;
        const int WaitTimeout = unchecked((int)0x887A0027), AccessLost = unchecked((int)0x887A0026);
        static readonly Guid Texture2DId = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

        [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
        [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint levels, uint sdk, out IntPtr device, out int level, out IntPtr context);

        // The D3D11 objects are called through their vtable slots: the context interface has over 100 methods, not worth declaring.
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture2DFn(IntPtr device, ref TexDesc desc, IntPtr initial, out IntPtr texture);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopySubresourceRegionFn(IntPtr ctx, IntPtr dst, uint dstSub, uint x, uint y, uint z, IntPtr src, uint srcSub, ref Box box);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapFn(IntPtr ctx, IntPtr resource, uint sub, uint mapType, uint flags, out Mapped mapped);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapFn(IntPtr ctx, IntPtr resource, uint sub);

        static T Fn<T>(IntPtr obj, int slot) where T : class
        {
            IntPtr vtbl = Marshal.ReadIntPtr(obj);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(vtbl, slot * IntPtr.Size), typeof(T));
        }

        [StructLayout(LayoutKind.Sequential)]
        struct TexDesc { public uint Width, Height, MipLevels, ArraySize, Format, SampleCount, SampleQuality, Usage, BindFlags, CpuAccess, Misc; }
        [StructLayout(LayoutKind.Sequential)]
        struct Box { public uint Left, Top, Front, Right, Bottom, Back; }
        [StructLayout(LayoutKind.Sequential)]
        struct Mapped { public IntPtr Data; public uint RowPitch, DepthPitch; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct OutputDesc
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
            public int Left, Top, Right, Bottom, Attached, Rotation; public IntPtr Monitor;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct FrameInfo
        {
            public long LastPresentTime, LastMouseUpdateTime; public uint AccumulatedFrames; public int RectsCoalesced, ProtectedMasked;
            public int PointerX, PointerY, PointerVisible; public uint MetadataSize, PointerShapeSize;
        }

        // slot order matters, unused slots are placeholders (IDXGIObject has 4 methods before the interface's own)
        [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIFactory1
        {
            [PreserveSig] int SetPrivateData(); [PreserveSig] int SetPrivateDataInterface(); [PreserveSig] int GetPrivateData(); [PreserveSig] int GetParent();
            [PreserveSig] int EnumAdapters(); [PreserveSig] int MakeWindowAssociation(); [PreserveSig] int GetWindowAssociation(); [PreserveSig] int CreateSwapChain(); [PreserveSig] int CreateSoftwareAdapter();
            [PreserveSig] int EnumAdapters1(uint index, out IDXGIAdapter1 adapter);
        }

        [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIAdapter1
        {
            [PreserveSig] int SetPrivateData(); [PreserveSig] int SetPrivateDataInterface(); [PreserveSig] int GetPrivateData(); [PreserveSig] int GetParent();
            [PreserveSig] int EnumOutputs(uint index, out IntPtr output);
            [PreserveSig] int GetDesc(); [PreserveSig] int CheckInterfaceSupport(); [PreserveSig] int GetDesc1();
        }

        [ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutput1
        {
            [PreserveSig] int SetPrivateData(); [PreserveSig] int SetPrivateDataInterface(); [PreserveSig] int GetPrivateData(); [PreserveSig] int GetParent();
            [PreserveSig] int GetDesc(out OutputDesc desc);
            [PreserveSig] int GetDisplayModeList(); [PreserveSig] int FindClosestMatchingMode(); [PreserveSig] int WaitForVBlank(); [PreserveSig] int TakeOwnership(); [PreserveSig] int ReleaseOwnership();
            [PreserveSig] int GetGammaControlCapabilities(); [PreserveSig] int SetGammaControl(); [PreserveSig] int GetGammaControl(); [PreserveSig] int SetDisplaySurface();
            [PreserveSig] int GetDisplaySurfaceData(); [PreserveSig] int GetFrameStatistics();
            [PreserveSig] int GetDisplayModeList1(); [PreserveSig] int FindClosestMatchingMode1(); [PreserveSig] int GetDisplaySurfaceData1();
            [PreserveSig] int DuplicateOutput(IntPtr device, out IDXGIOutputDuplication duplication);
        }

        [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        interface IDXGIOutputDuplication
        {
            [PreserveSig] int SetPrivateData(); [PreserveSig] int SetPrivateDataInterface(); [PreserveSig] int GetPrivateData(); [PreserveSig] int GetParent();
            [PreserveSig] int GetDesc();
            [PreserveSig] int AcquireNextFrame(uint timeoutMs, out FrameInfo info, out IntPtr resource);
            [PreserveSig] int GetFrameDirtyRects(); [PreserveSig] int GetFrameMoveRects(); [PreserveSig] int GetFramePointerShape(); [PreserveSig] int MapDesktopSurface(); [PreserveSig] int UnMapDesktopSurface();
            [PreserveSig] int ReleaseFrame();
        }
    }
}
