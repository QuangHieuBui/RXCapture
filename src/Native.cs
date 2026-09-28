using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace RXCapture
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot, yHotspot;
        public IntPtr hbmMask, hbmColor;
    }

    public static class Native
    {
        public const int WM_HOTKEY = 0x0312;
        public const int WM_DPICHANGED = 0x02E0;
        public const int WM_NCHITTEST = 0x0084;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TRANSPARENT = 0x20, WS_EX_LAYERED = 0x80000, WS_EX_TOPMOST = 0x8;
        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14;
        public const int CURSOR_SHOWING = 1;
        public const int DI_NORMAL = 3;
        public const uint MOUSEEVENTF_WHEEL = 0x0800;
        public const uint CWP_SKIPINVISIBLE = 1, CWP_SKIPDISABLED = 2, CWP_SKIPTRANSPARENT = 4;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] public static extern IntPtr ChildWindowFromPointEx(IntPtr parent, POINT pt, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr hWnd, StringBuilder sb, int max);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hWnd, int idx);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] public static extern bool GetIconInfo(IntPtr hIcon, out ICONINFO ii);
        [DllImport("user32.dll")] public static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr hIcon, int cx, int cy, int step, IntPtr brush, int flags);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, int data, UIntPtr extra);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, uint flags);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
        [DllImport("gdi32.dll")] public static extern bool BitBlt(IntPtr hdcDest, int x, int y, int w, int h, IntPtr hdcSrc, int sx, int sy, int rop);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);
        public const int SRCCOPY = 0x00CC0020, CAPTUREBLT = 0x40000000;

        /// <summary>Copies a screen rectangle (physical pixels, including layered windows) into the Graphics target at (0,0).</summary>
        public static void CopyScreen(Graphics g, Rectangle r)
        {
            IntPtr src = GetDC(IntPtr.Zero);
            IntPtr dst = g.GetHdc();
            try { BitBlt(dst, 0, 0, r.Width, r.Height, src, r.X, r.Y, SRCCOPY | CAPTUREBLT); }
            finally { g.ReleaseHdc(dst); ReleaseDC(IntPtr.Zero, src); }
        }
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out RECT r, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attr, out int v, int size);
        [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SHGetKnownFolderPath(ref Guid id, uint flags, IntPtr token, out IntPtr path);

        public static string GetTitle(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetWindowText(h, sb, 256);
            return sb.ToString();
        }

        public static string GetClass(IntPtr h)
        {
            var sb = new StringBuilder(128);
            GetClassName(h, sb, 128);
            return sb.ToString();
        }

        public static bool IsCloaked(IntPtr h)
        {
            int v;
            try { if (DwmGetWindowAttribute(h, DWMWA_CLOAKED, out v, 4) == 0) return v != 0; } catch { }
            return false;
        }

        /// <summary>Visible frame of a top-level window (without the invisible resize border / shadow).</summary>
        public static Rectangle GetFrame(IntPtr h)
        {
            RECT r;
            try { if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out r, Marshal.SizeOf(typeof(RECT))) == 0) return r.ToRectangle(); } catch { }
            GetWindowRect(h, out r);
            return r.ToRectangle();
        }

        public static void MouseWheel(int notches)
        {
            mouse_event(MOUSEEVENTF_WHEEL, 0, 0, notches * 120, UIntPtr.Zero);
        }

        public static bool KeyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
        /// <summary>True if the key is down now or was pressed since the previous call (a quick tap between two polls still counts).</summary>
        public static bool KeyPressedSince(int vk) { return (GetAsyncKeyState(vk) & 0x8001) != 0; }
    }

    /// <summary>A top-level window captured at snapshot time.</summary>
    public class WinInfo
    {
        public IntPtr Handle;
        public Rectangle Bounds;
        public string Title;
        public string Class;
    }

    public static class WindowFinder
    {
        static readonly string[] Keep = { "#32768", "tooltips_class32", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman" };

        /// <summary>Visible top-level windows, front-most first.</summary>
        public static List<WinInfo> Snapshot(IntPtr exclude)
        {
            var list = new List<WinInfo>();
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (h == exclude || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsCloaked(h)) return true;
                var title = Native.GetTitle(h);
                var cls = Native.GetClass(h);
                if (title.Length == 0 && Array.IndexOf(Keep, cls) < 0) return true;
                var r = Native.GetFrame(h);
                if (r.Width < 4 || r.Height < 4) return true;
                list.Add(new WinInfo { Handle = h, Bounds = r, Title = title, Class = cls });
                return true;
            }, IntPtr.Zero);
            return list;
        }

        /// <summary>Chain of nested rectangles under a screen point: top-level window, then child controls.</summary>
        public static List<Rectangle> Chain(Point pt, List<WinInfo> tops, out WinInfo top)
        {
            var chain = new List<Rectangle>();
            top = null;
            foreach (var w in tops)
                if (w.Bounds.Contains(pt)) { top = w; break; }
            if (top == null) return chain;
            chain.Add(top.Bounds);
            if (!Native.IsWindow(top.Handle)) return chain;
            IntPtr cur = top.Handle;
            for (int depth = 0; depth < 6; depth++)
            {
                POINT p = new POINT { X = pt.X, Y = pt.Y };
                Native.ScreenToClient(cur, ref p);
                IntPtr child = Native.ChildWindowFromPointEx(cur, p, Native.CWP_SKIPINVISIBLE | Native.CWP_SKIPTRANSPARENT);
                if (child == IntPtr.Zero || child == cur) break;
                RECT r;
                if (!Native.GetWindowRect(child, out r)) break;
                var rc = r.ToRectangle();
                if (rc.Width >= 8 && rc.Height >= 8 && rc != chain[chain.Count - 1] && top.Bounds.IntersectsWith(rc))
                    chain.Add(Rectangle.Intersect(rc, top.Bounds));
                cur = child;
            }
            return chain;
        }
    }
}
