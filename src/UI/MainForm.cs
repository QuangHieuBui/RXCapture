using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>Large red "Capture" button.</summary>
    public class BigButton : Control
    {
        bool hover, down;
        public string Sub = "";
        public BigButton() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true); Cursor = Cursors.Hand; }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Parent != null ? Parent.BackColor : Theme.Ribbon);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
            var r = new Rectangle(1, 1, Width - 3, Height - 3);
            Color top = down ? Color.FromArgb(178, 20, 20) : (hover ? Color.FromArgb(240, 60, 60) : Color.FromArgb(222, 40, 40));
            Color bot = down ? Color.FromArgb(140, 12, 12) : (hover ? Color.FromArgb(196, 28, 28) : Color.FromArgb(180, 24, 24));
            using (var p = Theme.RoundRect(r, 8))
            using (var b = new LinearGradientBrush(r, top, bot, 90f))
                g.FillPath(b, p);
            float s = Theme.Scale(this);
            var ic = Icons.Get("camera", (int)(34 * s), true);
            g.DrawImage(ic, (int)(18 * s), (Height - ic.Height) / 2);
            using (var f = new Font("Segoe UI", 15f, FontStyle.Bold))
                TextRenderer.DrawText(g, Text, f, new Rectangle((int)(60 * s), (int)(6 * s), Width - (int)(70 * s), Height / 2 + (int)(4 * s)), Color.White, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, Sub, Font, new Rectangle((int)(60 * s), Height / 2 + (int)(6 * s), Width - (int)(70 * s), Height / 2 - (int)(10 * s)), Color.FromArgb(255, 220, 220), TextFormatFlags.Left | TextFormatFlags.Top | TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }
    }

    /// <summary>Top tab strip of the capture window (All-in-One / Image / Video / Presets).</summary>
    public class TabStrip : Control
    {
        public string[] Tabs = { "All-in-One", "Image", "Video", "Presets" };
        public int Selected;
        public event EventHandler SelectedChanged;
        public TabStrip() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Cursor = Cursors.Hand; }
        Rectangle Cell(int i) { int w = Width / Tabs.Length; return new Rectangle(i * w, 0, w, Height); }
        public void Select(int i) { if (i == Selected) return; Selected = i; Invalidate(); if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Theme.TabBar);
            for (int i = 0; i < Tabs.Length; i++)
            {
                var c = Cell(i);
                bool sel = i == Selected;
                if (sel) using (var b = new SolidBrush(Theme.Ribbon)) g.FillRectangle(b, c);
                TextRenderer.DrawText(g, Loc.T(Tabs[i]), Font, c, sel ? Color.White : Color.FromArgb(180, 180, 186), TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (sel) using (var b = new SolidBrush(Theme.Accent)) g.FillRectangle(b, c.X, c.Bottom - 3, c.Width, 3);
            }
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            for (int i = 0; i < Tabs.Length; i++) if (Cell(i).Contains(e.Location)) Select(i);
        }
    }

    /// <summary>
    /// The Capture window, laid out like Snagit's: All-in-One / Image / Video / Presets tabs, a Selection choice,
    /// Share / Effects / Cursor / Timer options, and a big red Capture button.
    /// </summary>
    public class MainForm : DarkForm
    {
        readonly AppSettings cfg = AppSettings.Current;
        readonly TabStrip tabs = new TabStrip();
        readonly BigButton capture = new BigButton();
        readonly Panel pAll = new Panel(), pImage = new Panel(), pVideo = new Panel(), pPresets = new Panel(), pShared = new Panel();
        ComboBox cbSel, cbVSel, cbFormat, cbFps, cbShare, cbEffect, cbDelay;
        CheckBox chShadow, chCorners, chFull, chVCursor, chCursor;
        Label lblImageNote;
        ListBox lb;
        Button bSave, bDelete, bKey, bRename;
        bool loading;
        CaptureMode lastMode = CaptureMode.AllInOne;   // capture type of the last non-Presets tab, stored by "Save current"

        static readonly CaptureMode[] ImageModes = { CaptureMode.Region, CaptureMode.Window, CaptureMode.FullScreen, CaptureMode.Scrolling, CaptureMode.Freehand, CaptureMode.Fixed, CaptureMode.Repeat };
        static readonly CaptureMode[] VideoModes = { CaptureMode.Video, CaptureMode.VideoWindow, CaptureMode.VideoScreen };
        static readonly int[] Delays = { 0, 1, 2, 3, 5, 10, 15, 30 };

        public MainForm()
        {
            Text = "RXCapture";
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MinimizeBox = true; ShowInTaskbar = true;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(400, 500);
            try { Icon = App.AppIcon; } catch { }
            BackColor = Theme.Ribbon;

            tabs.SetBounds(0, 0, 400, 38);
            tabs.SelectedChanged += (s, e) => ShowPage();
            Controls.Add(tabs);

            // ---------- pages (all share the same area)
            foreach (var p in new[] { pAll, pImage, pVideo }) { p.SetBounds(16, 48, 368, 136); p.BackColor = Theme.Ribbon; Controls.Add(p); }
            pPresets.SetBounds(16, 48, 368, 296); pPresets.BackColor = Theme.Ribbon; Controls.Add(pPresets);

            // All-in-One
            pAll.Controls.Add(new Label { Text = Loc.T("Select any window, object or region of the screen, then choose to capture an image, a scrolling image or a video."), Left = 0, Top = 4, Width = 368, Height = 60, ForeColor = Theme.TextDim });
            pAll.Controls.Add(new Label { Text = Loc.T("Tip: while selecting, roll the mouse wheel to choose a parent or child object; press C to copy the colour under the cursor."), Left = 0, Top = 70, Width = 368, Height = 54, ForeColor = Theme.TextDim });

            // Image
            AddLabel(pImage, "Selection", 8);
            cbSel = AddCombo(pImage, 4, new[] { "Region", "Window", "Full Screen", "Scrolling window", "Freehand", "Fixed region", "Repeat last region" });
            cbSel.SelectedIndexChanged += (s, e) => { UpdateImageOptions(); UpdateHotkeyText(); };
            chShadow = AddCheck(pImage, "Add a shadow around the window", 40, v => cfg.WinShadow = v);
            chCorners = AddCheck(pImage, "Transparent rounded corners (Windows 11)", 66, v => cfg.WinRoundCorners = v);
            chFull = AddCheck(pImage, "Capture the whole window even if covered", 92, v => cfg.WinFullContent = v);
            lblImageNote = new Label { Left = 0, Top = 44, Width = 368, Height = 60, ForeColor = Theme.TextDim };
            pImage.Controls.Add(lblImageNote);

            // Video
            AddLabel(pVideo, "Selection", 8);
            cbVSel = AddCombo(pVideo, 4, new[] { "Region", "Window", "Full Screen" });
            cbVSel.SelectedIndexChanged += (s, e) => UpdateHotkeyText();
            AddLabel(pVideo, "Format", 42);
            cbFormat = AddCombo(pVideo, 38, new[] { "AVI (Motion-JPEG)", "GIF (animated)", "MP4 (needs ffmpeg.exe)" });
            cbFormat.SelectedIndexChanged += (s, e) => { if (loading) return; cfg.VideoFormat = new[] { "avi", "gif", "mp4" }[cbFormat.SelectedIndex]; cfg.Save(); };
            AddLabel(pVideo, "Frames per second", 76);
            cbFps = AddCombo(pVideo, 72, new[] { "10", "15", "20", "25", "30" });
            cbFps.SelectedIndexChanged += (s, e) => { if (loading) return; cfg.VideoFps = int.Parse((string)cbFps.SelectedItem); cfg.Save(); };
            chVCursor = AddCheck(pVideo, "Record the mouse cursor", 106, v => cfg.VideoCursor = v);

            // Presets
            lb = new ListBox { Left = 0, Top = 0, Width = 368, Height = 236, DrawMode = DrawMode.OwnerDrawFixed, ItemHeight = 46, BorderStyle = BorderStyle.None, IntegralHeight = false };
            lb.DrawItem += DrawPreset;
            lb.SelectedIndexChanged += (s, e) => UpdateHotkeyText();
            lb.DoubleClick += (s, e) => RunSelectedPreset();
            pPresets.Controls.Add(lb);
            bSave = PresetButton("Save current…", 0, 110, (s, e) => SavePreset());
            bRename = PresetButton("Rename…", 116, 80, (s, e) => RenamePreset());
            bKey = PresetButton("Hotkey…", 202, 80, (s, e) => SetPresetHotkey());
            bDelete = PresetButton("Delete", 288, 80, (s, e) => DeletePreset());

            // ---------- shared options (Share / Effects / Cursor / Timer)
            pShared.SetBounds(16, 190, 368, 150); pShared.BackColor = Theme.Ribbon; Controls.Add(pShared);
            pShared.Controls.Add(new Label { Text = Loc.T("Output"), Left = 0, Top = 0, Width = 368, Height = 20, Font = new Font("Segoe UI", 9f, FontStyle.Bold), ForeColor = Theme.TextDim });
            AddLabel(pShared, "Share", 30);
            cbShare = AddCombo(pShared, 26, new[] { "Open in Editor", "Copy to clipboard", "Save to file", "Editor + Clipboard" });
            cbShare.SelectedIndexChanged += (s, e) => { if (loading) return; SetShare(cbShare.SelectedIndex); cfg.Save(); };
            AddLabel(pShared, "Effects", 62);
            cbEffect = AddCombo(pShared, 58, new[] { "None", "Border", "Drop shadow", "Torn edge" });
            cbEffect.SelectedIndexChanged += (s, e) => { if (loading) return; cfg.Effect = (AfterEffect)cbEffect.SelectedIndex; cfg.Save(); };
            AddLabel(pShared, "Timer", 94);
            cbDelay = AddCombo(pShared, 90, Array.ConvertAll(Delays, d => d == 0 ? "Off" : d + " s"));
            cbDelay.SelectedIndexChanged += (s, e) => { if (loading) return; cfg.DelaySeconds = Delays[cbDelay.SelectedIndex]; cfg.Save(); };
            chCursor = AddCheck(pShared, "Include the mouse cursor", 122, v => cfg.IncludeCursor = v);

            capture.SetBounds(16, 352, 368, 62);
            capture.Text = Loc.T("Capture");
            capture.Click += (s, e) => StartCapture();
            Controls.Add(capture);

            var links = new FlowLayoutPanel { Left = 12, Top = 424, Width = 376, Height = 28, BackColor = Theme.Ribbon };
            links.Controls.Add(Link("Editor", (s, e) => App.ShowEditor()));
            links.Controls.Add(Link("Library", (s, e) => App.ShowLibrary()));
            links.Controls.Add(Link("Settings", (s, e) => App.ShowSettings()));
            links.Controls.Add(Link("Exit", (s, e) => App.Exit()));
            Controls.Add(links);
            ClientSize = new Size(400, 462);

            LoadUi();
            ShowPage();
            VisibleChanged += (s, e) => { if (Visible) { LoadUi(); UpdateHotkeyText(); } };
        }

        // ------------------------------------------------------------------ small builders

        void AddLabel(Panel p, string text, int y) { p.Controls.Add(new Label { Text = Loc.T(text), Left = 0, Top = y + 4, Width = 120, Height = 22 }); }

        ComboBox AddCombo(Panel p, int y, string[] items)
        {
            var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 122, Top = y, Width = 246 };
            foreach (var i in items) c.Items.Add(Loc.T(i));
            if (c.Items.Count > 0) c.SelectedIndex = 0;
            p.Controls.Add(c);
            return c;
        }

        CheckBox AddCheck(Panel p, string text, int y, Action<bool> set)
        {
            var c = new CheckBox { Text = Loc.T(text), Left = 0, Top = y, Width = 368, Height = 22 };
            c.CheckedChanged += (s, e) => { if (loading) return; set(c.Checked); cfg.Save(); };
            p.Controls.Add(c);
            return c;
        }

        Button PresetButton(string text, int x, int w, EventHandler h)
        {
            var b = new Button { Text = Loc.T(text), Left = x, Top = 250, Width = w, Height = 28 };
            b.Click += h;
            pPresets.Controls.Add(b);
            return b;
        }

        LinkLabel Link(string text, EventHandler h)
        {
            var l = new LinkLabel { Text = Loc.T(text), AutoSize = true, Margin = new Padding(6, 4, 14, 0), Font = new Font("Segoe UI", 9.5f) };
            l.LinkClicked += (s, e) => h(s, e);
            return l;
        }

        // ------------------------------------------------------------------ state <-> UI

        void LoadUi()
        {
            loading = true;
            try
            {
                chShadow.Checked = cfg.WinShadow; chCorners.Checked = cfg.WinRoundCorners; chFull.Checked = cfg.WinFullContent;
                chCursor.Checked = cfg.IncludeCursor; chVCursor.Checked = cfg.VideoCursor;
                cbEffect.SelectedIndex = (int)cfg.Effect;
                int di = Array.IndexOf(Delays, cfg.DelaySeconds); cbDelay.SelectedIndex = di < 0 ? 0 : di;
                cbShare.SelectedIndex = ShareIndex();
                cbFormat.SelectedIndex = cfg.VideoFormat == "gif" ? 1 : (cfg.VideoFormat == "mp4" ? 2 : 0);
                cbFps.SelectedItem = cfg.VideoFps.ToString(); if (cbFps.SelectedIndex < 0) cbFps.SelectedIndex = 1;
                RefreshPresetList();
                UpdateImageOptions();
            }
            finally { loading = false; }
        }

        int ShareIndex()
        {
            if (cfg.OpenEditor && cfg.CopyToClipboard) return 3;
            if (cfg.OpenEditor) return 0;
            if (cfg.CopyToClipboard) return 1;
            if (cfg.AutoSaveToFolder) return 2;
            return 0;
        }

        void SetShare(int i)
        {
            cfg.OpenEditor = i == 0 || i == 3;
            cfg.CopyToClipboard = i == 1 || i == 3;
            cfg.AutoSaveToFolder = i == 2;
        }

        void UpdateImageOptions()
        {
            var m = ImageModes[Math.Max(0, cbSel.SelectedIndex)];
            bool win = m == CaptureMode.Window;
            chShadow.Visible = chCorners.Visible = chFull.Visible = win;
            lblImageNote.Visible = !win;
            switch (m)
            {
                case CaptureMode.Region: lblImageNote.Text = Loc.T("Drag to select any area of the screen."); break;
                case CaptureMode.FullScreen: lblImageNote.Text = Loc.T("Captures the whole monitor under the mouse (all monitors can be enabled in Settings > Capture)."); break;
                case CaptureMode.Scrolling: lblImageNote.Text = Loc.T("Select a scrollable area; the page is scrolled automatically and stitched into one tall image. Press Esc to stop."); break;
                case CaptureMode.Freehand: lblImageNote.Text = Loc.T("Draw any shape; everything outside it becomes transparent."); break;
                case CaptureMode.Fixed: lblImageNote.Text = Loc.T("Fixed region size is set in Settings > Capture."); break;
                default: lblImageNote.Text = Loc.T("Repeats the last captured region without asking."); break;
            }
        }

        void ShowPage()
        {
            int t = tabs.Selected;
            pAll.Visible = t == 0; pImage.Visible = t == 1; pVideo.Visible = t == 2; pPresets.Visible = t == 3;
            pShared.Visible = t == 0 || t == 1;
            LoadUi();
            if (t != 3) lastMode = CurrentMode();
            UpdateHotkeyText();
        }

        CaptureMode CurrentMode()
        {
            switch (tabs.Selected)
            {
                case 0: return CaptureMode.AllInOne;
                case 2: return VideoModes[Math.Max(0, cbVSel.SelectedIndex)];
                case 3: { var p = SelectedPreset(); return p != null ? p.Mode : CaptureMode.AllInOne; }
                default: return ImageModes[Math.Max(0, cbSel.SelectedIndex)];
            }
        }

        string HotkeyFor(CaptureMode m)
        {
            switch (m)
            {
                case CaptureMode.AllInOne: return cfg.HkAllInOne;
                case CaptureMode.Region: return cfg.HkRegion;
                case CaptureMode.Window: return cfg.HkWindow;
                case CaptureMode.FullScreen: return cfg.HkFullScreen;
                case CaptureMode.Scrolling: return cfg.HkScroll;
                case CaptureMode.Freehand: return cfg.HkFreehand;
                case CaptureMode.Repeat: return cfg.HkRepeat;
                case CaptureMode.Video: return cfg.HkVideo;
                default: return "";
            }
        }

        void UpdateHotkeyText()
        {
            string hk;
            if (tabs.Selected == 3)
            {
                var p = SelectedPreset();
                hk = p != null ? p.Hotkey : "";
                capture.Text = Loc.T("Capture with preset");
            }
            else { hk = HotkeyFor(CurrentMode()); capture.Text = Loc.T("Capture"); }
            capture.Sub = hk != null && hk.Length > 0 ? Loc.T("or press") + " " + hk : "";
            capture.Invalidate();
        }

        void StartCapture()
        {
            if (tabs.Selected == 3) { RunSelectedPreset(); return; }
            cfg.Save();
            App.Capture(CurrentMode());
        }

        // ------------------------------------------------------------------ presets

        public void SelectTab(int i) { tabs.Select(i); }

        CapturePreset SelectedPreset()
        {
            return lb != null && lb.SelectedIndex >= 0 && lb.SelectedIndex < cfg.Presets.Count ? cfg.Presets[lb.SelectedIndex] : null;
        }

        void RefreshPresetList()
        {
            int sel = lb.SelectedIndex;
            lb.BeginUpdate();
            lb.Items.Clear();
            foreach (var p in cfg.Presets) lb.Items.Add(p.Name);
            lb.EndUpdate();
            if (lb.Items.Count > 0) lb.SelectedIndex = Math.Max(0, Math.Min(sel, lb.Items.Count - 1));
        }

        void DrawPreset(object s, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= cfg.Presets.Count) return;
            var p = cfg.Presets[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            using (var b = new SolidBrush(sel ? Theme.Accent : (e.Index % 2 == 0 ? Theme.Field : Color.FromArgb(46, 46, 50)))) e.Graphics.FillRectangle(b, e.Bounds);
            var ic = Icons.Get(p.Mode == CaptureMode.Window || p.Mode == CaptureMode.VideoWindow ? "window" :
                p.Mode == CaptureMode.FullScreen || p.Mode == CaptureMode.VideoScreen ? "fullscreen" :
                p.Mode == CaptureMode.Scrolling ? "scroll" : p.Mode == CaptureMode.Freehand ? "freehand" :
                p.Mode == CaptureMode.Video ? "video" : p.Mode == CaptureMode.Region || p.Mode == CaptureMode.Fixed || p.Mode == CaptureMode.Repeat ? "region" : "camera", 24, true);
            e.Graphics.DrawImage(ic, e.Bounds.X + 8, e.Bounds.Y + (e.Bounds.Height - 24) / 2, 24, 24);
            using (var f = new Font("Segoe UI", 9.5f, FontStyle.Bold))
                TextRenderer.DrawText(e.Graphics, p.Name, f, new Rectangle(e.Bounds.X + 40, e.Bounds.Y + 4, e.Bounds.Width - 48, 20), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(e.Graphics, p.Summary(), Font, new Rectangle(e.Bounds.X + 40, e.Bounds.Y + 25, e.Bounds.Width - 48, 18), sel ? Color.FromArgb(220, 235, 250) : Theme.TextDim, TextFormatFlags.NoPadding | TextFormatFlags.EndEllipsis);
        }

        void RunSelectedPreset()
        {
            var p = SelectedPreset();
            if (p == null) return;
            p.ApplyTo(cfg);
            cfg.Save();
            App.Capture(p.Mode);
        }

        void SavePreset()
        {
            var name = Dlg.Prompt(this, "Save preset", "Preset name", "My preset " + (cfg.Presets.Count + 1));
            if (string.IsNullOrEmpty(name)) return;
            // the preset stores the capture type of the last used Image / Video / All-in-One tab
            cfg.Presets.Add(CapturePreset.FromCurrent(name, tabs.Selected == 3 ? lastMode : CurrentMode()));
            cfg.Save();
            RefreshPresetList();
            lb.SelectedIndex = lb.Items.Count - 1;
        }

        void RenamePreset()
        {
            var p = SelectedPreset(); if (p == null) return;
            var name = Dlg.Prompt(this, "Rename preset", "Preset name", p.Name);
            if (string.IsNullOrEmpty(name)) return;
            p.Name = name; cfg.Save(); RefreshPresetList();
        }

        void SetPresetHotkey()
        {
            var p = SelectedPreset(); if (p == null) return;
            var hk = Dlg.HotkeyPrompt(this, "Preset hotkey", p.Hotkey);
            if (hk == null) return;
            p.Hotkey = hk; cfg.Save();
            App.RegisterHotkeys();
            RefreshPresetList(); UpdateHotkeyText();
        }

        void DeletePreset()
        {
            var p = SelectedPreset(); if (p == null) return;
            if (MessageBox.Show(this, Loc.T("Delete this preset?") + "\n" + p.Name, "RXCapture", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            cfg.Presets.Remove(p); cfg.Save();
            App.RegisterHotkeys();
            RefreshPresetList(); UpdateHotkeyText();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && App.Running)
            {
                if (cfg.ShowTray && cfg.MinimizeToTray) { e.Cancel = true; Hide(); }
                else { App.Exit(); }
            }
            base.OnFormClosing(e);
        }
    }

    /// <summary>Library window: all captures as thumbnails, click to open in the editor.</summary>
    public class LibraryForm : Form
    {
        readonly ThumbGrid grid = new ThumbGrid();

        public LibraryForm()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "RXCapture - " + Loc.T("Library");
            BackColor = Theme.Back; ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(860, 600);
            StartPosition = FormStartPosition.CenterScreen;
            try { Icon = App.AppIcon; } catch { }
            grid.Dock = DockStyle.Fill;
            grid.CellW = 190; grid.CellH = 130;
            grid.ItemOpen += it =>
            {
                if (it.IsVideo) { App.Editor.ShowVideo(it); App.ShowEditor(); return; }
                try
                {
                    var d = LibraryStore.LoadDoc(it);
                    var ed = App.Editor;
                    ed.OpenNew(it, d);
                    App.ShowEditor();
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            };
            Controls.Add(grid);
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitle(this); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && App.Running) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }
    }
}
