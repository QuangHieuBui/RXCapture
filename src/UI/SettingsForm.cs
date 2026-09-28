using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace RXCapture
{
    /// <summary>TextBox that records a key combination.</summary>
    public class HotkeyBox : TextBox
    {
        public HotkeyBox() { ReadOnly = true; ShortcutsEnabled = false; }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            var k = keyData & Keys.KeyCode;
            if (k == Keys.Back || k == Keys.Delete) { Text = ""; return true; }
            string s = HotkeyParser.Format(keyData);
            if (s.Length > 0 && k != Keys.ControlKey && k != Keys.ShiftKey && k != Keys.Menu) Text = s;
            return true;
        }
    }

    public class SettingsForm : DarkForm
    {
        readonly AppSettings cfg = AppSettings.Current;
        readonly List<Panel> pages = new List<Panel>();
        readonly ListBox nav = new ListBox();
        int rowY;
        Panel cur;

        ComboBox cbLang, cbFormat, cbEffect, cbVFormat;
        CheckBox chStartup, chTray, chMinTray, chWidget, chEditorMax, chCursor, chMag, chImmediate, chAllMon, chSound, chWShadow, chWCorners, chWFull, chEditor, chClip, chAuto, chVCursor;
        NumericUpDown nLib, nDelay, nFixW, nFixH, nJpeg, nFps, nGif;
        TextBox tbFolder, tbPattern;
        readonly Dictionary<string, HotkeyBox> hk = new Dictionary<string, HotkeyBox>();

        public SettingsForm()
        {
            Text = Loc.T("Settings");
            ClientSize = new Size(640, 470);
            nav.SetBounds(12, 12, 150, 400);
            nav.BorderStyle = BorderStyle.None; nav.IntegralHeight = false; nav.ItemHeight = 28; nav.DrawMode = DrawMode.OwnerDrawFixed;
            nav.DrawItem += DrawNav;
            nav.SelectedIndexChanged += (s, e) => { for (int i = 0; i < pages.Count; i++) pages[i].Visible = i == nav.SelectedIndex; };
            Controls.Add(nav);

            Page("General");
            cbLang = Combo("Language", new[] { "Automatic", "English", "Tiếng Việt" }, cfg.Language == "en" ? 1 : (cfg.Language == "vi" ? 2 : 0));
            chStartup = Check("Start RXCapture when Windows starts", IsStartup());
            chTray = Check("Show the tray icon", cfg.ShowTray);
            chMinTray = Check("Keep running in the tray when windows are closed", cfg.MinimizeToTray);
            chWidget = Check("Show the capture widget at the top of the screen", cfg.ShowWidget);
            nLib = Num("Maximum captures kept in the library", 20, 5000, cfg.LibraryMax);

            Page("Capture");
            chCursor = Check("Include the mouse cursor in image captures", cfg.IncludeCursor);
            nDelay = Num("Delay before capture (seconds)", 0, 60, cfg.DelaySeconds);
            chMag = Check("Show the magnifier while selecting", cfg.ShowMagnifier);
            chImmediate = Check("Capture immediately after selecting (skip the confirm toolbar)", cfg.CaptureImmediately);
            chAllMon = Check("Full screen capture covers all monitors", cfg.FullScreenAllMonitors);
            nFixW = Num("Fixed region width (px)", 16, 10000, cfg.FixedWidth);
            nFixH = Num("Fixed region height (px)", 16, 10000, cfg.FixedHeight);
            chSound = Check("Play a sound after capture", cfg.PlaySound);
            chWShadow = Check("Window capture: add a shadow around the window", cfg.WinShadow);
            chWCorners = Check("Window capture: transparent rounded corners (Windows 11)", cfg.WinRoundCorners);
            chWFull = Check("Window capture: whole window even if covered by others", cfg.WinFullContent);

            Page("Output");
            chEditor = Check("Open captures in the Editor", cfg.OpenEditor);
            chClip = Check("Copy captures to the clipboard", cfg.CopyToClipboard);
            chAuto = Check("Save captures automatically to the folder below", cfg.AutoSaveToFolder);
            tbFolder = Folder("Save folder", cfg.SaveFolder);
            cbFormat = Combo("File format", new[] { "PNG", "JPG", "BMP", "GIF", "TIF", "PDF" }, Math.Max(0, Array.IndexOf(new[] { "png", "jpg", "bmp", "gif", "tif", "pdf" }, cfg.Format)));
            nJpeg = Num("JPEG quality", 10, 100, cfg.JpegQuality);
            tbPattern = TextRow("File name pattern (date format)", cfg.FileNamePattern);
            Note("Put fixed text in single quotes, e.g. 'Rndimsx'_yyyyMMdd_HHmmss. Without quotes, letters such as d, m or t are read as date codes.");
            cbEffect = Combo("Effect applied to new captures", new[] { "None", "Border", "Drop shadow", "Torn edge" }, (int)cfg.Effect);

            Page("Hotkeys");
            Hot("All-in-One", "HkAllInOne", cfg.HkAllInOne); Hot("Region", "HkRegion", cfg.HkRegion); Hot("Window", "HkWindow", cfg.HkWindow);
            Hot("Full Screen", "HkFullScreen", cfg.HkFullScreen); Hot("Scrolling", "HkScroll", cfg.HkScroll); Hot("Freehand", "HkFreehand", cfg.HkFreehand);
            Hot("Repeat last region", "HkRepeat", cfg.HkRepeat); Hot("Video", "HkVideo", cfg.HkVideo);
            Note("Click a box and press the new combination. Backspace clears it.");

            Page("Video");
            nFps = Num("Frames per second", 5, 30, cfg.VideoFps);
            chVCursor = Check("Record the mouse cursor", cfg.VideoCursor);
            cbVFormat = Combo("Output format", new[] { "AVI (Motion-JPEG)", "GIF (animated)", "MP4 (needs ffmpeg.exe)" }, cfg.VideoFormat == "gif" ? 1 : (cfg.VideoFormat == "mp4" ? 2 : 0));
            nGif = Num("GIF maximum width (px)", 160, 4000, cfg.GifMaxWidth);
            Note("Tip: put ffmpeg.exe next to RXCapture.exe to enable MP4 (H.264) output.");

            Page("Editor");
            chEditorMax = Check("Open the editor maximized (full screen)", cfg.EditorMaximized);
            Note("Set the colours, width, font and effects that new objects of each drawing tool start with. In the editor, Set default (Tools > Properties) stores the selected object's style.");
            var bDef = new Button { Text = Loc.T("Default tool properties…"), Left = 4, Top = rowY, Width = 240, Height = 30 };
            bDef.Click += (s, e) => { using (var df = new DefaultsForm()) df.ShowDialog(this); };
            cur.Controls.Add(bDef); rowY += 40;

            nav.SelectedIndex = 0;

            var ok = new Button { Text = Loc.T("OK"), Width = 90, Height = 30, Left = 640 - 200, Top = 428, DialogResult = DialogResult.OK };
            var cancel = new Button { Text = Loc.T("Cancel"), Width = 90, Height = 30, Left = 640 - 104, Top = 428, DialogResult = DialogResult.Cancel };
            ok.Click += (s, e) => Apply();
            Controls.Add(ok); Controls.Add(cancel);
            AcceptButton = ok; CancelButton = cancel;
        }

        void DrawNav(object s, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Accent : Theme.Ribbon)) e.Graphics.FillRectangle(b, e.Bounds);
            TextRenderer.DrawText(e.Graphics, nav.Items[e.Index].ToString(), Font, new Rectangle(e.Bounds.X + 10, e.Bounds.Y, e.Bounds.Width - 10, e.Bounds.Height), Color.White, TextFormatFlags.VerticalCenter | TextFormatFlags.Left);
        }

        // ------------------------------------------------------------------ builders

        void Page(string title)
        {
            var p = new Panel { Left = 176, Top = 12, Width = 452, Height = 404, BackColor = Theme.Ribbon, AutoScroll = true, Visible = false };
            Controls.Add(p);
            pages.Add(p); cur = p; rowY = 6;
            nav.Items.Add(Loc.T(title));
            var h = new Label { Text = Loc.T(title), Left = 4, Top = rowY, Width = 430, Height = 26, Font = new Font("Segoe UI", 12f, FontStyle.Bold) };
            p.Controls.Add(h); rowY += 36;
        }

        CheckBox Check(string text, bool v)
        {
            var c = new CheckBox { Text = Loc.T(text), Checked = v, Left = 4, Top = rowY, Width = 430, Height = 24 };
            cur.Controls.Add(c); rowY += 30; return c;
        }

        NumericUpDown Num(string label, int min, int max, int v)
        {
            cur.Controls.Add(new Label { Text = Loc.T(label), Left = 4, Top = rowY + 3, Width = 300, Height = 22 });
            var n = new NumericUpDown { Minimum = min, Maximum = max, Value = Math.Max(min, Math.Min(max, v)), Left = 310, Top = rowY, Width = 100 };
            cur.Controls.Add(n); rowY += 32; return n;
        }

        ComboBox Combo(string label, string[] items, int sel)
        {
            cur.Controls.Add(new Label { Text = Loc.T(label), Left = 4, Top = rowY + 3, Width = 220, Height = 22 });
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 230, Top = rowY, Width = 200 };
            foreach (var i in items) c.Items.Add(Loc.T(i));
            c.SelectedIndex = Math.Max(0, Math.Min(items.Length - 1, sel));
            cur.Controls.Add(c); rowY += 32; return c;
        }

        TextBox TextRow(string label, string v)
        {
            cur.Controls.Add(new Label { Text = Loc.T(label), Left = 4, Top = rowY + 3, Width = 220, Height = 22 });
            var t = new TextBox { Text = v, Left = 230, Top = rowY, Width = 200 };
            cur.Controls.Add(t); rowY += 32; return t;
        }

        TextBox Folder(string label, string v)
        {
            cur.Controls.Add(new Label { Text = Loc.T(label), Left = 4, Top = rowY + 3, Width = 220, Height = 22 });
            var t = new TextBox { Text = v, Left = 4, Top = rowY + 28, Width = 350 };
            var b = new Button { Text = "…", Left = 360, Top = rowY + 26, Width = 40, Height = 26 };
            b.Click += (s, e) => { using (var fb = new FolderBrowserDialog { SelectedPath = t.Text }) if (fb.ShowDialog(this) == DialogResult.OK) t.Text = fb.SelectedPath; };
            cur.Controls.Add(t); cur.Controls.Add(b); rowY += 64; return t;
        }

        void Hot(string label, string key, string value)
        {
            cur.Controls.Add(new Label { Text = Loc.T(label), Left = 4, Top = rowY + 3, Width = 220, Height = 22 });
            var h = new HotkeyBox { Text = value, Left = 230, Top = rowY, Width = 200 };
            cur.Controls.Add(h); hk[key] = h; rowY += 32;
        }

        void Note(string text)
        {
            cur.Controls.Add(new Label { Text = Loc.T(text), Left = 4, Top = rowY + 6, Width = 430, Height = 40, ForeColor = Theme.TextDim });
            rowY += 46;
        }

        // ------------------------------------------------------------------ persistence

        /// <summary>The Windows start-up entry used to be called ShotCraft; move it to the new name (and the new exe path).</summary>
        public static void MigrateStartupEntry()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"SoftwareMicrosoftWindowsCurrentVersionRun", true))
                {
                    if (k == null || k.GetValue("ShotCraft") == null) return;
                    k.DeleteValue("ShotCraft", false);
                    k.SetValue("RXCapture", "\"" + Application.ExecutablePath + "\" --minimized");
                }
            }
            catch { }
        }

        static bool IsStartup()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run")) return k != null && k.GetValue("RXCapture") != null; }
            catch { return false; }
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (k == null) return;
                    if (on) k.SetValue("RXCapture", "\"" + Application.ExecutablePath + "\" --minimized");
                    else k.DeleteValue("RXCapture", false);
                }
            }
            catch { }
        }

        void Apply()
        {
            cfg.Language = new[] { "auto", "en", "vi" }[cbLang.SelectedIndex];
            SetStartup(chStartup.Checked); cfg.RunAtStartup = chStartup.Checked;
            cfg.ShowTray = chTray.Checked; cfg.MinimizeToTray = chMinTray.Checked; cfg.LibraryMax = (int)nLib.Value;
            cfg.ShowWidget = chWidget.Checked; cfg.EditorMaximized = chEditorMax.Checked;
            cfg.IncludeCursor = chCursor.Checked; cfg.DelaySeconds = (int)nDelay.Value; cfg.ShowMagnifier = chMag.Checked;
            cfg.CaptureImmediately = chImmediate.Checked; cfg.FullScreenAllMonitors = chAllMon.Checked;
            cfg.FixedWidth = (int)nFixW.Value; cfg.FixedHeight = (int)nFixH.Value; cfg.PlaySound = chSound.Checked;
            cfg.WinShadow = chWShadow.Checked; cfg.WinRoundCorners = chWCorners.Checked; cfg.WinFullContent = chWFull.Checked;
            cfg.OpenEditor = chEditor.Checked; cfg.CopyToClipboard = chClip.Checked; cfg.AutoSaveToFolder = chAuto.Checked;
            cfg.SaveFolder = tbFolder.Text; cfg.Format = new[] { "png", "jpg", "bmp", "gif", "tif", "pdf" }[cbFormat.SelectedIndex];
            cfg.JpegQuality = (int)nJpeg.Value; cfg.FileNamePattern = tbPattern.Text; cfg.Effect = (AfterEffect)cbEffect.SelectedIndex;
            cfg.HkAllInOne = hk["HkAllInOne"].Text; cfg.HkRegion = hk["HkRegion"].Text; cfg.HkWindow = hk["HkWindow"].Text; cfg.HkFullScreen = hk["HkFullScreen"].Text;
            cfg.HkScroll = hk["HkScroll"].Text; cfg.HkFreehand = hk["HkFreehand"].Text; cfg.HkRepeat = hk["HkRepeat"].Text; cfg.HkVideo = hk["HkVideo"].Text;
            cfg.VideoFps = (int)nFps.Value; cfg.VideoCursor = chVCursor.Checked; cfg.VideoFormat = new[] { "avi", "gif", "mp4" }[cbVFormat.SelectedIndex]; cfg.GifMaxWidth = (int)nGif.Value;
            cfg.Save();
        }
    }
}
