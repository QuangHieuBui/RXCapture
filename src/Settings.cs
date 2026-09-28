using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Windows.Forms;
using System.Xml.Serialization;

namespace RXCapture
{
    public enum AfterEffect { None, Border, DropShadow, TornEdge }

    public class AppSettings
    {
        public string Language = "auto";            // auto | en | vi
        public string SaveFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "RXCapture");
        public string Format = "png";                // png | jpg | bmp | gif | tif | pdf
        public int JpegQuality = 92;
        public const string DefaultFileNamePattern = "'Rndimsx'_yyyyMMdd_HHmmss";     // quoted: letters like d, m, t would otherwise be read as date codes
        public string FileNamePattern = DefaultFileNamePattern;

        public bool IncludeCursor = true;
        public int DelaySeconds = 0;
        public bool CaptureImmediately = false;      // capture straight after releasing the mouse (no adjust step)
        public bool ShowMagnifier = true;
        public bool FullScreenAllMonitors = false;
        public int FixedWidth = 800, FixedHeight = 600;
        public Rectangle LastRegion = Rectangle.Empty;
        public bool PlaySound = false;

        public bool OpenEditor = true;
        public bool CopyToClipboard = false;
        public bool AutoSaveToFolder = false;
        public AfterEffect Effect = AfterEffect.None;

        public string HkAllInOne = "PrintScreen";
        public string HkFullScreen = "Ctrl+Shift+F";
        public string HkWindow = "Ctrl+Shift+W";
        public string HkRegion = "Ctrl+Shift+R";
        public string HkScroll = "Ctrl+Shift+S";
        public string HkFreehand = "Ctrl+Shift+D";
        public string HkRepeat = "Ctrl+Shift+L";
        public string HkVideo = "Ctrl+Shift+V";

        public int VideoFps = 15;
        public bool VideoCursor = true;
        public string VideoFormat = "mp4";           // avi | gif | mp4 (H.264 via Windows Media Foundation)
        public int SettingsVersion = 0;              // bumped by the one-time migrations below (0 = file written by an older build)
        public int GifMaxWidth = 800;

        public bool RunAtStartup = false;
        public bool MinimizeToTray = true;
        public bool StartMinimized = false;
        public int LibraryMax = 300;
        public bool ShowTray = true;

        // window capture options
        public bool WinShadow = false;          // add a soft shadow around a captured window (transparent background)
        public bool WinRoundCorners = true;     // transparent rounded corners (Windows 11 windows)
        public bool WinFullContent = true;      // capture the whole window even when other windows cover part of it

        // capture widget (small tab docked to the top edge of the screen)
        public bool EditorMaximized = true;      // the editor opens full screen; remembered when you un-maximize it
        public bool ShowWidget = true;
        public bool WidgetAutoHide = true;
        public bool WidgetListOpen = true;
        public int WidgetCenterX = int.MinValue;
        public int WidgetPage = 0, WidgetIndex = 0;

        public List<ToolDefaultEntry> ToolDefaults = new List<ToolDefaultEntry>();   // configured default style per drawing tool

        public bool PresetsSeeded = false;
        public List<CapturePreset> Presets = new List<CapturePreset>();

        [XmlIgnore] static AppSettings _cur;

        public static string DataDir
        {
            get
            {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                var d = Path.Combine(root, "RXCapture");
                var old = Path.Combine(root, "ShotCraft");                     // the app used to be called ShotCraft: keep its settings
                if (!Directory.Exists(d) && Directory.Exists(old)) { try { Directory.Move(old, d); } catch { } }
                Directory.CreateDirectory(d);
                return d;
            }
        }

        static string FilePath { get { return Path.Combine(DataDir, "settings.xml"); } }

        public static AppSettings Current
        {
            get { if (_cur == null) { _cur = Load(); _cur.EnsurePresets(); _cur.MigrateFileName(); _cur.MigrateVideoFormat(); } return _cur; }
        }

        static AppSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var fs = File.OpenRead(FilePath))
                        return (AppSettings)new XmlSerializer(typeof(AppSettings)).Deserialize(fs);
            }
            catch { }
            return new AppSettings();
        }

        public void Save()
        {
            try
            {
                using (var fs = File.Create(FilePath))
                    new XmlSerializer(typeof(AppSettings)).Serialize(fs, this);
            }
            catch { }
        }

        /// <summary>The old default pattern (Capture_...) turned the "t" of Capture into AM/PM ("CapAure_..."); switch it to the new default.</summary>
        void MigrateFileName()
        {
            if (string.IsNullOrEmpty(FileNamePattern) || FileNamePattern == "Capture_yyyyMMdd_HHmmss" || FileNamePattern == "'Rndimx'_yyyyMMdd_HHmmss") { FileNamePattern = DefaultFileNamePattern; Save(); }
        }

        /// <summary>MP4 is the default video format now. Files saved by older builds hold "avi" (the former default), so switch it once.</summary>
        void MigrateVideoFormat() { if (ApplyVideoFormatMigration()) Save(); }

        /// <summary>The in-memory part of the migration (no file access, so it can be tested). True when something changed.</summary>
        internal bool ApplyVideoFormatMigration()
        {
            if (SettingsVersion >= 2) return false;
            if (VideoFormat == "avi" || string.IsNullOrEmpty(VideoFormat)) VideoFormat = "mp4";
            SettingsVersion = 2;
            return true;
        }

        public void EnsurePresets()
        {
            if (PresetsSeeded) return;
            PresetsSeeded = true;
            if (Presets == null) Presets = new List<CapturePreset>();
            if (Presets.Count > 0) return;
            Presets.Add(new CapturePreset { Name = "Image to Editor", Mode = CaptureMode.AllInOne });
            Presets.Add(new CapturePreset { Name = "Region to Clipboard", Mode = CaptureMode.Region, OpenEditor = false, CopyToClipboard = true });
            Presets.Add(new CapturePreset { Name = "Window to Editor", Mode = CaptureMode.Window, WinShadow = true });
            Presets.Add(new CapturePreset { Name = "Full screen to File", Mode = CaptureMode.FullScreen, OpenEditor = false, AutoSave = true });
            Presets.Add(new CapturePreset { Name = "Scrolling to Editor", Mode = CaptureMode.Scrolling });
            Save();
        }

        public string NewFileName(string ext)
        {
            string name;
            try { name = DateTime.Now.ToString(FileNamePattern, CultureInfo.InvariantCulture); }
            catch { name = "Rndimsx_" + DateTime.Now.ToString("yyyyMMdd_HHmmss"); }
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            Directory.CreateDirectory(SaveFolder);
            string path = Path.Combine(SaveFolder, name + "." + ext);
            int n = 2;
            while (File.Exists(path)) path = Path.Combine(SaveFolder, name + "_" + (n++) + "." + ext);
            return path;
        }
    }

    /// <summary>A saved capture configuration (like a Snagit preset): capture type + output + options, optionally with its own hotkey.</summary>
    public class CapturePreset
    {
        public string Name = "Preset";
        public CaptureMode Mode = CaptureMode.AllInOne;
        public bool OpenEditor = true, CopyToClipboard, AutoSave;
        public AfterEffect Effect = AfterEffect.None;
        public bool IncludeCursor = true;
        public int DelaySeconds;
        public string Hotkey = "";
        public bool WinShadow, WinRoundCorners = true, WinFullContent = true;

        public void ApplyTo(AppSettings c)
        {
            c.OpenEditor = OpenEditor; c.CopyToClipboard = CopyToClipboard; c.AutoSaveToFolder = AutoSave; c.Effect = Effect;
            c.IncludeCursor = IncludeCursor; c.DelaySeconds = DelaySeconds;
            c.WinShadow = WinShadow; c.WinRoundCorners = WinRoundCorners; c.WinFullContent = WinFullContent;
        }

        public static CapturePreset FromCurrent(string name, CaptureMode mode)
        {
            var c = AppSettings.Current;
            return new CapturePreset { Name = name, Mode = mode, OpenEditor = c.OpenEditor, CopyToClipboard = c.CopyToClipboard, AutoSave = c.AutoSaveToFolder, Effect = c.Effect,
                IncludeCursor = c.IncludeCursor, DelaySeconds = c.DelaySeconds, WinShadow = c.WinShadow, WinRoundCorners = c.WinRoundCorners, WinFullContent = c.WinFullContent };
        }

        public string Summary()
        {
            var parts = new List<string> { Loc.T(CaptureModeName(Mode)) };
            if (OpenEditor) parts.Add(Loc.T("Editor"));
            if (CopyToClipboard) parts.Add(Loc.T("Clipboard"));
            if (AutoSave) parts.Add(Loc.T("File"));
            if (Effect != AfterEffect.None) parts.Add(Loc.T(Effect == AfterEffect.Border ? "Border" : (Effect == AfterEffect.DropShadow ? "Drop shadow" : "Torn edge")));
            if (DelaySeconds > 0) parts.Add(DelaySeconds + " s");
            if (!string.IsNullOrEmpty(Hotkey)) parts.Add(Hotkey);
            return string.Join("  •  ", parts.ToArray());
        }

        public static string CaptureModeName(CaptureMode m)
        {
            switch (m)
            {
                case CaptureMode.AllInOne: return "All-in-One";
                case CaptureMode.Region: return "Region";
                case CaptureMode.Window: return "Window";
                case CaptureMode.FullScreen: return "Full Screen";
                case CaptureMode.Freehand: return "Freehand";
                case CaptureMode.Fixed: return "Fixed region";
                case CaptureMode.Scrolling: return "Scrolling";
                case CaptureMode.Video: return "Video";
                case CaptureMode.VideoWindow: return "Video (window)";
                case CaptureMode.VideoScreen: return "Video (screen)";
                default: return "Repeat last region";
            }
        }
    }

    /// <summary>Hotkey text ("Ctrl+Shift+F") &lt;-&gt; Win32 modifiers + virtual key.</summary>
    public static class HotkeyParser
    {
        public static bool TryParse(string s, out uint mod, out uint vk)
        {
            mod = 0; vk = 0;
            if (string.IsNullOrEmpty(s)) return false;
            foreach (var part in s.Split('+'))
            {
                var p = part.Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)) mod |= 2;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) mod |= 1;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) mod |= 4;
                else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)) mod |= 8;
                else
                {
                    try { vk = (uint)(Keys)Enum.Parse(typeof(Keys), p, true); }
                    catch { return false; }
                }
            }
            return vk != 0;
        }

        public static string Format(Keys keyData)
        {
            var parts = new List<string>();
            if ((keyData & Keys.Control) != 0) parts.Add("Ctrl");
            if ((keyData & Keys.Alt) != 0) parts.Add("Alt");
            if ((keyData & Keys.Shift) != 0) parts.Add("Shift");
            var k = keyData & Keys.KeyCode;
            if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.None) return string.Join("+", parts.ToArray());
            parts.Add(k.ToString());
            return string.Join("+", parts.ToArray());
        }
    }

    /// <summary>Tiny localisation layer: English keys, optional Vietnamese translations.</summary>
    public static class Loc
    {
        static Dictionary<string, string> vi;
        static bool useVi;

        public static void Init()
        {
            var l = AppSettings.Current.Language;
            useVi = l == "vi" || (l == "auto" && CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi");
            if (useVi && vi == null) vi = Translations.Vi();
        }

        public static string T(string s)
        {
            string r;
            if (useVi && vi != null && vi.TryGetValue(s, out r)) return r;
            return s;
        }

        public static string T(string s, params object[] a) { return string.Format(T(s), a); }
    }
}
