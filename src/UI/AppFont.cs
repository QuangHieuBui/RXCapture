using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace RXCapture
{
    /// <summary>Makes the Roboto fonts embedded in the exe available to this process (GDI and GDI+), so Font("Roboto") works on PCs that do not have it installed.</summary>
    static class AppFont
    {
        [DllImport("gdi32.dll")] static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);

        public static void Register()
        {
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in new[] { "Roboto-Regular.ttf", "Roboto-Bold.ttf", "Roboto-Medium.ttf" })
            {
                try
                {
                    using (var s = asm.GetManifestResourceStream(name))
                    {
                        if (s == null) continue;
                        var data = new byte[s.Length]; int n = 0;
                        while (n < data.Length) { int r = s.Read(data, n, data.Length - n); if (r <= 0) break; n += r; }
                        IntPtr p = Marshal.AllocCoTaskMem(data.Length);     // must stay allocated: Windows keeps using it until the process ends
                        Marshal.Copy(data, 0, p, data.Length);
                        uint count = 0; AddFontMemResourceEx(p, (uint)data.Length, IntPtr.Zero, ref count);
                    }
                }
                catch { }   // no embedded font: controls fall back to the default font
            }
        }
    }
}
