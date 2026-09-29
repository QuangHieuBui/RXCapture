using System;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RXCapture
{
    /// <summary>The "start with Windows" entry: a value under HKCU\...\Run (no admin rights needed).</summary>
    public static class Startup
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string ValueName = "RXCapture";

        static string Command { get { return "\"" + Application.ExecutablePath + "\" --minimized"; } }

        internal static string TargetOf(string command)
        {
            command = command.Trim();
            if (command.StartsWith("\"")) { int end = command.IndexOf('"', 1); return end > 0 ? command.Substring(1, end - 1) : command.Substring(1); }
            int sp = command.IndexOf(' ');
            return sp > 0 ? command.Substring(0, sp) : command;
        }

        public static bool IsEnabled
        {
            get
            {
                try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(ValueName) != null; }
                catch { return false; }
            }
        }

        public static void Set(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(ValueName, Command);
                    else k.DeleteValue(ValueName, false);
                }
                AppSettings.Current.RunAtStartup = on;
            }
            catch { }
        }

        /// <summary>Called at start-up: renames the old ShotCraft entry and re-points the entry at this exe, so moving or reinstalling the app does not break it.</summary>
        public static void Refresh()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (k.GetValue("ShotCraft") != null) { k.DeleteValue("ShotCraft", false); k.SetValue(ValueName, Command); }
                    else
                    {
                        // only when the recorded exe is gone (moved / reinstalled); a second copy run from elsewhere must not take the entry over
                        var v = k.GetValue(ValueName) as string;
                        if (v != null && !System.IO.File.Exists(TargetOf(v))) k.SetValue(ValueName, Command);
                    }
                }
            }
            catch { }
        }
    }
}
