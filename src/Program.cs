using System;
using System.Drawing;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace RXCapture
{
    static class Program
    {
        const string PipeName = "RXCapture.Pipe.v1";

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => LogError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError(e.ExceptionObject as Exception);

            Loc.Init();

            // ---- headless helpers (used for automation / testing)
            if (args.Length >= 1)
            {
                switch (args[0])
                {
                    case "--selftest": return SelfTest.Run(args.Length > 1 ? args[1] : null);
                    case "--uishot": return UiShot.Run(args);
                    case "--interact": return InteractTest.Run(args.Length > 1 ? args[1] : null);
                    case "--overlaytest": return OverlayTest.Run(args.Length > 1 ? args[1] : null);
                    case "--scrolltest": return ScrollTest.Run(args);
                    case "--windowtest": return WindowTest.Run(args.Length > 1 ? args[1] : null);
                    case "--videotest": return VideoTest.Run(args.Length > 1 ? args[1] : null);
                    case "--filetest": return FileTest.Run(args);
                    case "--grab": return Grab(args);
                }
            }

            bool created;
            using (var mutex = new Mutex(true, "Local\\RXCapture.Single.Instance", out created))
            {
                if (!created) { SendToRunning(args); return 0; }

                var marshal = new Control();
                marshal.CreateControl();
                var t = new Thread(() => PipeServer(marshal)) { IsBackground = true, Name = "RXCapture pipe" };
                t.Start();

                bool minimized = false;
                var rest = new System.Collections.Generic.List<string>();
                foreach (var a in args) { if (a == "--minimized") minimized = true; else rest.Add(a); }
                if (minimized) App.StartHidden = true;

                App.Start(rest.FindAll(a => !a.StartsWith("--")).ToArray());
                foreach (var cmd in CommandsFrom(rest.ToArray())) App.HandleCommand(cmd);
                Application.Run();
            }
            return 0;
        }

        static System.Collections.Generic.IEnumerable<string> CommandsFrom(string[] args)
        {
            for (int i = 0; i < args.Length; i++)
                if (args[i] == "--capture" && i + 1 < args.Length) yield return "capture:" + args[i + 1];
        }

        static void SendToRunning(string[] args)
        {
            string cmd = "show";
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--capture" && i + 1 < args.Length) { cmd = "capture:" + args[i + 1]; break; }
                if (!args[i].StartsWith("--") && File.Exists(args[i])) { cmd = "open:" + Path.GetFullPath(args[i]); break; }
            }
            try
            {
                using (var c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(3000);
                    var b = Encoding.UTF8.GetBytes(cmd);
                    c.Write(b, 0, b.Length);
                }
            }
            catch { }
        }

        static void PipeServer(Control marshal)
        {
            while (true)
            {
                try
                {
                    using (var s = new NamedPipeServerStream(PipeName, PipeDirection.In, 1))
                    {
                        s.WaitForConnection();
                        var buf = new byte[4096];
                        int n = s.Read(buf, 0, buf.Length);
                        string cmd = Encoding.UTF8.GetString(buf, 0, n);
                        marshal.BeginInvoke((Action)(() => App.HandleCommand(cmd)));
                    }
                }
                catch { Thread.Sleep(200); }
            }
        }

        /// <summary>--grab x,y,w,h out.png : capture a screen rectangle without any UI.</summary>
        static int Grab(string[] args)
        {
            try
            {
                var p = args[1].Split(',');
                var r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
                using (var b = ScreenGrabber.Grab(r)) b.Save(args[2], System.Drawing.Imaging.ImageFormat.Png);
                return 0;
            }
            catch (Exception ex) { LogError(ex); return 1; }
        }

        static void LogError(Exception ex)
        {
            try
            {
                File.AppendAllText(Path.Combine(AppSettings.DataDir, "error.log"), DateTime.Now + "\r\n" + ex + "\r\n\r\n");
            }
            catch { }
        }
    }
}
