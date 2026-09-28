using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>The image editor window, laid out like the Snagit 12 editor (dark ribbon, canvas, library tray, status bar).</summary>
    public class EditorForm : Form
    {
        readonly Ribbon ribbon = new Ribbon();
        readonly CanvasControl canvas = new CanvasControl();
        readonly ThumbGrid tray = new ThumbGrid();
        readonly VideoPlayerPanel player = new VideoPlayerPanel();
        readonly Panel grip = new Panel();
        readonly EditorStatus status = new EditorStatus();
        readonly Timer autosave = new Timer { Interval = 15000 };
        Document doc;
        LibItem item;
        string lastCtx = "";
        RGroup optionsGroup, stylesGroup, textGroup;
        int gripY;

        static readonly Color CRed = Color.FromArgb(229, 57, 53), CBlue = Color.FromArgb(33, 101, 235), CGreen = Color.FromArgb(67, 160, 71),
            CBlack = Color.FromArgb(20, 20, 20), CYellow = Color.FromArgb(255, 235, 59), CPink = Color.FromArgb(244, 143, 177), CCyan = Color.FromArgb(38, 198, 218);

        public EditorForm()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "RXCapture Editor";
            BackColor = Theme.Back;
            ForeColor = Theme.Text;
            Font = new Font("Segoe UI", 9f);
            ClientSize = new Size(1240, 780);
            MinimumSize = new Size(820, 520);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = false;
            AllowDrop = true;
            if (AppSettings.Current.EditorMaximized) WindowState = FormWindowState.Maximized;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            canvas.Dock = DockStyle.Fill;
            grip.Dock = DockStyle.Bottom; grip.Height = 7; grip.BackColor = Theme.Ribbon; grip.Cursor = Cursors.SizeNS;
            grip.Paint += GripPaint; grip.MouseDown += (s, e) => { gripY = Cursor.Position.Y; };
            grip.MouseMove += GripMove;
            tray.Dock = DockStyle.Bottom; tray.Height = 118;
            status.Dock = DockStyle.Bottom;

            Controls.Add(canvas);
            Controls.Add(grip);
            Controls.Add(tray);
            Controls.Add(status);
            Controls.Add(ribbon);
            Controls.Add(player);
            Controls.SetChildIndex(player, 0);   // front of the z-order: it docks last and covers the canvas while a video plays
            player.CloseRequested += CloseVideo;
            player.TrimRequested += TrimVideo;
            LibraryStore.Deleting += OnLibraryDeleting;

            BuildRibbon();

            canvas.SelectionChanged += (s, e) => ContextChanged();
            canvas.ToolChanged += (s, e) => ContextChanged();
            canvas.ViewChanged += (s, e) => { UpdateStatus(); ribbon.Invalidate(); };
            canvas.ZoomChanged += (s, e) => UpdateStatus();
            canvas.ContextRequested += ShowObjectMenu;
            canvas.StepEditRequested += s => EditStepValue();
            status.ZoomRequested += z => canvas.SetZoom(z, null);
            status.FitRequested += () => canvas.ZoomFit(true);
            tray.ItemOpen += OpenLibItem;
            tray.ItemRemove += RemoveFromTray;
            tray.ItemsRemove += RemoveManyFromTray;
            autosave.Tick += (s, e) => SaveCurrent();
            autosave.Start();
            ribbon.FileMenu = BuildFileMenu;
            ribbon.FilePopup = ShowFilePanel;
            UpdateStatus();
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Theme.DarkTitle(this); }

        protected override void OnShown(EventArgs e) { base.OnShown(e); canvas.Focus(); }

        // ------------------------------------------------------------------ document handling

        public bool HasDocument { get { return doc != null; } }
        public CanvasControl Canvas { get { return canvas; } }
        public Document Doc { get { return doc; } }

        // used by the UI screenshot tool
        public void DebugSet(int tab, string tool)
        {
            ribbon.Active = tab; ribbon.Relayout();
            if (!string.IsNullOrEmpty(tool)) canvas.CurrentTool = (Tool)Enum.Parse(typeof(Tool), tool);
            ContextChanged();
        }

        public void OpenNew(LibItem it, Document d)
        {
            HidePlayer();
            SaveCurrent();
            item = it; doc = d;
            doc.Changed += (s, e) => { UpdateStatus(); ribbon.Invalidate(); };
            canvas.SetDocument(doc);
            canvas.CurrentTool = Tool.Select;
            UpdateTitle();
            tray.Reload();
            tray.Select(it.Id);
            ContextChanged();
            UpdateStatus();
            ribbon.Invalidate();
        }

        void OpenLibItem(LibItem it)
        {
            if (item != null && it.Id == item.Id) return;
            if (it.IsVideo) { ShowVideo(it); return; }
            HidePlayer();
            try
            {
                var d = Document.LoadProject(it.File);
                SaveCurrent();
                item = it; doc = d;
                doc.Changed += (s, e) => { UpdateStatus(); ribbon.Invalidate(); };
                canvas.SetDocument(doc);
                canvas.CurrentTool = Tool.Select;
                UpdateTitle(); ContextChanged(); UpdateStatus(); ribbon.Invalidate();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "RXCapture"); }
        }

        // ---- in-editor video playback (replaces the canvas while a video is selected)

        public void ShowVideo(LibItem it)
        {
            SaveCurrent();
            item = it; doc = null;
            canvas.SetDocument(null);
            player.Open(it.File, it.Ext);
            player.Visible = true;
            player.Focus();
            UpdateTitle(); UpdateStatus(); ContextChanged(); ribbon.Invalidate();
            tray.Select(it.Id);
        }

        /// <summary>Stops the player and shows the canvas again (does not change the current item).</summary>
        void HidePlayer()
        {
            if (!player.Visible) return;
            player.Stop(); player.Visible = false;
            if (item != null && item.IsVideo) { item = null; UpdateTitle(); }
        }

        void CloseVideo()
        {
            HidePlayer();
            var next = LibraryStore.List().FirstOrDefault(i => !i.IsVideo);
            if (next != null) OpenLibItem(next);
            else { canvas.SetDocument(null); UpdateTitle(); UpdateStatus(); ribbon.Invalidate(); }
        }

        /// <summary>Keeps only [start, end] of the video being played and saves it as a new video in the library.</summary>
        void TrimVideo(TimeSpan start, TimeSpan end)
        {
            var src = item;
            if (src == null || !src.IsVideo) return;
            player.Pause();
            string tmp = Path.Combine(Path.GetTempPath(), "rxcapture_trim_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".mp4");
            Bitmap first = null; TimeSpan kept = TimeSpan.Zero; bool ok = false;
            var worker = new System.Threading.Thread(() => { ok = Mp4Writer.Trim(src.File, tmp, start, end, out first, out kept); }) { IsBackground = true };
            using (var wait = new BusyForm(Loc.T("Trimming video…")))
            {
                wait.Show(this); worker.Start();
                while (!worker.Join(30)) Application.DoEvents();
            }
            if (!ok || first == null)
            {
                try { File.Delete(tmp); } catch { }
                MessageBox.Show(this, Loc.T("Could not trim this video."), "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            LibItem made;
            using (first) made = LibraryStore.AddVideo(tmp, first, Math.Max(1, (int)Math.Round(kept.TotalSeconds)));
            tray.Reload();
            ShowVideo(made);
        }

        // the media engine keeps the file open: let go of it before the library deletes it
        void OnLibraryDeleting(LibItem it)
        {
            if (!player.Visible || player.CurrentPath != it.File) return;
            player.Stop(); player.Visible = false;
            if (item != null && item.Id == it.Id) { item = null; doc = null; UpdateTitle(); UpdateStatus(); ribbon.Invalidate(); }
        }

        public void OpenFile(string path)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                Document d; LibItem it;
                if (ext == ".scp")
                {
                    d = Document.LoadProject(path);
                    using (var flat = d.Render()) it = LibraryStore.AddImage(flat, out d);
                    // AddImage created a fresh doc from the flattened image; keep editability by reloading the project instead
                    d = Document.LoadProject(path);
                    it.File = path; d.ProjectPath = path;
                    LibraryStore.Save(it, d);
                }
                else using (var im = Image.FromFile(path)) it = LibraryStore.AddImage(Effects.ToArgb(im), out d);
                OpenNew(it, d);
                if (!Visible) Show();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "RXCapture"); }
        }

        public void SaveCurrent()
        {
            try { if (doc != null && item != null && doc.Dirty) LibraryStore.Save(item, doc); } catch { }
        }

        void UpdateTitle()
        {
            DateTime? when = doc != null ? doc.Created : (item != null && item.IsVideo ? (DateTime?)item.Created : null);
            Text = "RXCapture Editor" + (when != null ? " - [" + when.Value.ToString("MMM d, yyyy h:mm:ss tt", CultureInfo.CurrentCulture) + "]" : "");
        }

        void UpdateStatus()
        {
            if (doc == null) { status.SetInfo("", "", canvas.Zoom); return; }
            status.SetInfo(doc.Width + " x " + doc.Height, canvas.SelectionInfo(), canvas.Zoom);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            canvas.CommitEdit();
            SaveCurrent();
            if (WindowState != FormWindowState.Minimized && AppSettings.Current.EditorMaximized != (WindowState == FormWindowState.Maximized))
            { AppSettings.Current.EditorMaximized = WindowState == FormWindowState.Maximized; AppSettings.Current.Save(); }
            if (e.CloseReason == CloseReason.UserClosing && AppSettings.Current.MinimizeToTray && App.Running) { e.Cancel = true; Hide(); }
            base.OnFormClosing(e);
        }

        protected override void OnDragEnter(DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; }

        protected override void OnDragDrop(DragEventArgs e)
        {
            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null) foreach (var f in files) OpenFile(f);
        }

        // ------------------------------------------------------------------ tray grip

        void GripPaint(object s, PaintEventArgs e)
        {
            using (var b = new SolidBrush(Color.FromArgb(120, 120, 126)))
                for (int i = 0; i < 4; i++) e.Graphics.FillRectangle(b, 6 + i * 4, 2, 2, 2);
            using (var p = new Pen(Color.FromArgb(28, 28, 28))) e.Graphics.DrawLine(p, 0, 0, grip.Width, 0);
        }

        void GripMove(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            int y = Cursor.Position.Y, dy = gripY - y;
            gripY = y;
            int nh = Math.Max((int)(60 * Theme.Scale(this)), Math.Min(ClientSize.Height / 2, tray.Height + dy));
            tray.Height = nh;
        }

        // ------------------------------------------------------------------ context (tool / selection) handling

        AnnKind? CtxKind()
        {
            if (canvas.Selection.Count > 0) return canvas.Selection[0].Kind;
            return CanvasControl.KindOf(canvas.CurrentTool);
        }

        bool CtxHighlighter()
        {
            if (canvas.Selection.Count > 0) return canvas.Selection[0].Highlighter;
            return canvas.CurrentTool == Tool.Highlighter;
        }

        void ContextChanged()
        {
            string key = canvas.Selection.Count > 0 ? "k:" + canvas.Selection[0].Kind + canvas.Selection[0].Highlighter : "t:" + canvas.CurrentTool;
            if (key != lastCtx)
            {
                lastCtx = key;
                RebuildOptions();
                ribbon.Relayout();
            }
            UpdateStatus();
            ribbon.Invalidate();
        }

        Ann P { get { return canvas.PropAnn; } }

        // ------------------------------------------------------------------ ribbon construction

        RBtn ToolBtn(string text, string icon, Tool t)
        {
            var b = new RBtn(text, icon, BtnStyle.Tool, delegate { canvas.CurrentTool = t; });
            b.IsChecked = () => canvas.CurrentTool == t;
            b.IsEnabled = () => doc != null;
            b.Tip = text;
            return b;
        }

        RBtn Big(string text, string icon, Action a, Func<bool> en = null)
        {
            var b = new RBtn(text, icon, BtnStyle.Big, a);
            b.IsEnabled = en ?? (() => doc != null);
            return b;
        }

        RBtn Small(string text, string icon, Action a, Func<bool> en = null)
        {
            var b = new RBtn(text, icon, BtnStyle.Small, a);
            b.IsEnabled = en ?? (() => doc != null);
            return b;
        }

        RBtn Drop(string text, string icon, Func<ContextMenuStrip> menu, Func<bool> en = null)
        {
            var b = new RBtn(text, icon, BtnStyle.Small, null);
            b.DropDown = menu;
            b.IsEnabled = en ?? (() => doc != null);
            return b;
        }

        void BuildRibbon()
        {
            // quick access toolbar
            Func<string, string, Action, Func<bool>, RBtn> q = (tip, icon, act, en) => { var b = new RBtn(tip, icon, BtnStyle.Icon, act); b.IsEnabled = en; b.Tip = tip; return b; };
            ribbon.Qat.Add(q("Save", "save", SaveQuick, () => CanSave));
            ribbon.Qat.Add(q("Undo", "undo", () => { canvas.CommitEdit(); if (doc != null) doc.Undo(); }, () => doc != null && doc.CanUndo));
            ribbon.Qat.Add(q("Redo", "redo", () => { if (doc != null) doc.Redo(); }, () => doc != null && doc.CanRedo));

            var file = new RTab("File", true);
            var tools = new RTab("Tools");
            var image = new RTab("Image");
            var share = new RTab("Share");
            var lib = new RTab("Library");
            ribbon.Tabs.Add(file); ribbon.Tabs.Add(tools); ribbon.Tabs.Add(image); ribbon.Tabs.Add(share); ribbon.Tabs.Add(lib);
            ribbon.Active = 1;

            // ---------------- Tools tab
            var dt = tools.Group("Drawing Tools");
            dt.Col(ToolBtn("Select", "select", Tool.Select), ToolBtn("Callout", "callout", Tool.Callout));
            dt.Col(ToolBtn("Arrow", "arrow", Tool.Arrow), ToolBtn("Line", "line", Tool.Line));
            dt.Col(ToolBtn("Stamp", "stamp", Tool.Stamp), ToolBtn("Shape", "shape", Tool.Shape));
            dt.Col(ToolBtn("Pen", "pen", Tool.Pen), ToolBtn("Fill", "fill", Tool.Fill));
            dt.Col(ToolBtn("Highlight", "highlighter", Tool.Highlighter), ToolBtn("Eraser", "eraser", Tool.Eraser));
            dt.Col(ToolBtn("Blur", "blur", Tool.Blur), ToolBtn("Step", "step", Tool.Step));
            dt.Col(ToolBtn("Text", "text", Tool.Text), ToolBtn("Magnify", "magnify", Tool.Magnify));
            dt.Col(ToolBtn("Spotlight", "spotlight", Tool.Spotlight), ToolBtn("Crop", "crop", Tool.Crop));

            stylesGroup = tools.Group("Styles");
            stylesGroup.Visible = () => PresetsFor(CtxKind(), CtxHighlighter()).Count > 0;
            var gal = new RGallery
            {
                Count = () => PresetsFor(CtxKind(), CtxHighlighter()).Count,
                Selected = SelectedPreset,
                DrawCell = DrawPresetCell,
                Pick = i => { var l = PresetsFor(CtxKind(), CtxHighlighter()); if (i < l.Count) { canvas.ApplyProp(l[i]); ribbon.Invalidate(); } }
            };
            stylesGroup.Col(gal);

            var props = tools.Group("Properties");
            var outline = Drop("Outline", "border", OutlineMenu, () => doc != null && HasOutline());
            outline.Swatch = () => P != null ? P.Stroke : CRed;
            var fill = Drop("Fill", "fill", FillMenu, () => doc != null && HasFill());
            fill.Swatch = () => canvas.CurrentTool == Tool.Fill ? canvas.FillColor : (P != null ? P.Fill : Color.Transparent);
            var eff = Drop("Effects", "shadow", EffectsMenu, () => doc != null && HasEffects());
            props.Col(outline, fill, eff);
            props.Col(Small("Set default", "check", SetAsDefault, () => doc != null && canvas.DefaultTarget != null),
                      Small("Defaults…", "settings", () => { using (var df = new DefaultsForm()) df.ShowDialog(this); }, () => true));

            textGroup = tools.Group("Text");
            textGroup.Visible = () => P != null && (CtxKind() == AnnKind.Text || CtxKind() == AnnKind.Callout);
            var font = Drop("Font", "font", FontMenu); font.DynamicText = () => P != null ? P.FontName : "Font";
            var size = Drop("Size", "text", SizeMenu); size.DynamicText = () => P != null ? ((int)P.FontSize) + " px" : "Size";
            textGroup.Col(font, size);
            Func<string, string, Action<Ann, bool>, Func<Ann, bool>, RBtn> tog = (tip, icon, set, get) =>
            {
                var b = new RBtn(tip, icon, BtnStyle.Icon, delegate { bool v = P != null && get(P); canvas.ApplyProp(a => set(a, !v)); });
                b.IsChecked = () => P != null && get(P); b.IsEnabled = () => doc != null; b.Tip = tip; return b;
            };
            textGroup.Col(tog("Bold", "bold", (a, v) => a.Bold = v, a => a.Bold), tog("Italic", "italic", (a, v) => a.Italic = v, a => a.Italic));
            textGroup.Col(tog("Underline", "underline", (a, v) => a.Underline = v, a => a.Underline));
            var tcol = Small("Colour", "textcolor", null);
            tcol.Click = null; tcol.DropDown = TextColorMenu; tcol.Swatch = () => P != null ? P.TextColor : Color.Black; tcol.IsEnabled = () => doc != null;
            textGroup.Columns[textGroup.Columns.Count - 1].Add(tcol);
            var align = Drop("Align", "alignl", AlignMenu);
            textGroup.Col(align);

            optionsGroup = tools.Group("Options");
            optionsGroup.Visible = () => optionsGroup.Columns.Count > 0;

            var clip = tools.Group("Clipboard");
            clip.Col(Big("Copy\nAll", "copy", CopyAll));
            clip.Col(Small("Cut", "cutout", () => canvas.CutSelection(), () => canvas.Selection.Count > 0),
                     Small("Copy", "copy", CopySmart, () => doc != null),
                     Small("Paste", "paste", PasteSmart, () => doc != null));

            var sh = tools.Group("Share");
            sh.Col(Big("Save\nAs", "save", SaveAs));
            var shareBtn = Big("Share", "share", null); shareBtn.Click = null; shareBtn.DropDown = ShareMenu;
            sh.Col(shareBtn);

            // ---------------- Image tab
            var g1 = image.Group("Modify");
            g1.Col(Big("Crop", "crop", () => { canvas.CurrentTool = Tool.Crop; canvas.SetCropAll(); ribbon.Active = 1; ribbon.Relayout(); }));
            g1.Col(Big("Cut\nOut", "cutout", () => { canvas.CurrentTool = Tool.CutOut; MessageHint("Drag across the image: a horizontal drag removes a vertical strip, a vertical drag removes a horizontal strip."); }));
            g1.Col(Small("Resize Image", "resize", DoResize), Small("Canvas Size", "canvas", DoCanvas), Small("Trim", "crop", () => { doc.Trim(8); canvas.ZoomFit(false); }));
            g1.Col(Drop("Rotate", "rotate", RotateMenu), Drop("Flip", "flip", FlipMenu));

            var g2 = image.Group("Effects");
            g2.ColEach(Big("Border", "border", DoBorder), Big("Shadow", "shadow", DoShadow), Big("Torn\nEdge", "torn", DoEdges));
            g2.Col(Small("Rounded corners", "canvas", DoRound), Small("Reflection", "layers", () => { doc.ApplyToFlattened(b => Effects.Reflection(b, 40, 4)); canvas.ZoomFit(false); }));

            var g3 = image.Group("Filters");
            g3.Col(Small("Adjust colours…", "adjust", DoAdjust), Small("Grayscale", "gray", () => doc.ApplyToBase(Effects.Grayscale)), Small("Invert", "adjust", () => doc.ApplyToBase(Effects.Invert)));
            g3.Col(Small("Sepia", "gray", () => doc.ApplyToBase(Effects.Sepia)), Small("Blur", "blur", () => doc.ApplyToBase(b => Effects.BoxBlur(b, 3))), Small("Sharpen", "adjust", () => doc.ApplyToBase(Effects.Sharpen)));
            g3.Col(Small("Emboss", "layers", () => doc.ApplyToBase(Effects.Emboss)), Small("Edges", "layers", () => doc.ApplyToBase(Effects.EdgeDetect)));

            var g4 = image.Group("Watermark");
            g4.Col(Big("Watermark", "watermark", DoWatermark));

            // ---------------- Share tab
            var s1 = share.Group("Save");
            s1.ColEach(Big("Save", "save", SaveQuick), Big("Save\nAs…", "save", SaveAs));
            var s2 = share.Group("Copy");
            s2.Col(Big("Copy\nAll", "copy", CopyAll));
            var s3 = share.Group("Send");
            s3.ColEach(Big("Email", "email", DoEmail), Big("Print", "print", DoPrint), Big("Open in\nPaint", "pen", DoPaint));
            var s4 = share.Group("Files");
            s4.ColEach(Big("Show in\nfolder", "folder", DoReveal), Big("Export\nPDF", "save", DoPdf));

            // ---------------- Library tab
            var l1 = lib.Group("Capture");
            l1.ColEach(Big("All-in-One", "camera", () => App.Capture(CaptureMode.AllInOne), () => true), Big("Region", "region", () => App.Capture(CaptureMode.Region), () => true),
                   Big("Window", "window", () => App.Capture(CaptureMode.Window), () => true), Big("Full\nScreen", "fullscreen", () => App.Capture(CaptureMode.FullScreen), () => true),
                   Big("Scrolling", "scroll", () => App.Capture(CaptureMode.Scrolling), () => true), Big("Video", "video", () => App.Capture(CaptureMode.Video), () => true));
            var l2 = lib.Group("Library");
            l2.Col(Big("Open\nLibrary", "library", () => App.ShowLibrary(), () => true), Big("Open\nImage…", "open", OpenFileDialog, () => true), Big("Paste as\nnew image", "paste", PasteAsNew, () => true));
            l2.Col(Big("Delete", "trash", DeleteCurrent, () => item != null));

            RebuildOptions();
        }

        void MessageHint(string s) { status.Hint(Loc.T(s)); }

        // ------------------------------------------------------------------ presets (Styles gallery)

        List<Action<Ann>> PresetsFor(AnnKind? kind, bool highlighter)
        {
            var l = new List<Action<Ann>>();
            if (kind == null) return l;
            Color[] cols = { CRed, CBlue, CGreen, CBlack };
            switch (kind.Value)
            {
                case AnnKind.Arrow: case AnnKind.Line:
                    foreach (var c in cols) { var cc = c; l.Add(a => a.Stroke = cc); }
                    break;
                case AnnKind.Pen:
                    if (highlighter) foreach (var c in new[] { CYellow, Color.FromArgb(129, 199, 132), CPink, CCyan }) { var cc = c; l.Add(a => a.Stroke = cc); }
                    else foreach (var c in cols) { var cc = c; l.Add(a => a.Stroke = cc); }
                    break;
                case AnnKind.Shape:
                    l.Add(a => { a.Stroke = CRed; a.Fill = Color.Transparent; a.Variant = 0; });
                    l.Add(a => { a.Stroke = CBlue; a.Fill = Color.Transparent; a.Variant = 0; });
                    l.Add(a => { a.Stroke = CGreen; a.Fill = Color.Transparent; a.Variant = 2; });
                    l.Add(a => { a.Stroke = CBlack; a.Fill = CBlack; a.Variant = 0; });
                    break;
                case AnnKind.Callout:
                    foreach (var c in new[] { CRed, CBlue, CGreen }) { var cc = c; l.Add(a => { a.Stroke = cc; a.Fill = Color.White; a.TextColor = cc; }); }
                    l.Add(a => { a.Stroke = Color.FromArgb(255, 179, 0); a.Fill = Color.FromArgb(255, 249, 196); a.TextColor = Color.FromArgb(200, 120, 0); });
                    break;
                case AnnKind.Text:
                    foreach (var c in cols) { var cc = c; l.Add(a => { a.TextColor = cc; a.Fill = Color.Transparent; }); }
                    break;
                case AnnKind.Step:
                    foreach (var c in cols) { var cc = c; l.Add(a => { a.Fill = cc; a.TextColor = Color.White; }); }
                    break;
                case AnnKind.Blur:
                    l.Add(a => a.Variant = 0); l.Add(a => a.Variant = 1);
                    break;
                case AnnKind.Magnify:
                    l.Add(a => { a.Variant = 0; a.Zoom = 2; }); l.Add(a => { a.Variant = 1; a.Zoom = 2; });
                    l.Add(a => { a.Variant = 0; a.Zoom = 3; }); l.Add(a => { a.Variant = 1; a.Zoom = 3; });
                    break;
                case AnnKind.Spotlight:
                    l.Add(a => { a.Variant = 0; a.Fill = Color.FromArgb(150, 0, 0, 0); }); l.Add(a => { a.Variant = 1; a.Fill = Color.FromArgb(150, 0, 0, 0); });
                    l.Add(a => { a.Variant = 0; a.Fill = Color.FromArgb(215, 0, 0, 0); }); l.Add(a => { a.Variant = 1; a.Fill = Color.FromArgb(215, 0, 0, 0); });
                    break;
                case AnnKind.Stamp:
                    foreach (int v in new[] { 0, 1, 2, 5 }) { int vv = v; l.Add(a => a.Variant = vv); }
                    break;
            }
            return l;
        }

        static string Sig(Ann a)
        {
            return a.Stroke.ToArgb() + "|" + a.Fill.ToArgb() + "|" + a.TextColor.ToArgb() + "|" + a.Variant + "|" + a.Zoom;
        }

        int SelectedPreset()
        {
            var p = P; if (p == null) return -1;
            var l = PresetsFor(CtxKind(), CtxHighlighter());
            for (int i = 0; i < l.Count; i++)
            {
                var t = p.Clone(); string before = Sig(t); l[i](t);
                if (Sig(t) == before) return i;
            }
            return -1;
        }

        void DrawPresetCell(Graphics g, Rectangle r, int idx)
        {
            var kind = CtxKind(); if (kind == null) return;
            var l = PresetsFor(kind, CtxHighlighter());
            if (idx >= l.Count) return;
            Ann tpl;
            var baseAnn = P != null ? P.Clone() : Ann.Create(kind.Value);
            if (canvas.Defaults.TryGetValue(canvas.CurrentTool, out tpl) && canvas.Selection.Count == 0) baseAnn = tpl.Clone();
            var a = baseAnn;
            l[idx](a);
            a.Shadow = false; a.Opacity = 1; a.Editing = false;
            a.Width = Math.Min(a.Width, 6);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var box = new RectangleF(r.X + 6, r.Y + 6, r.Width - 12, r.Height - 12);
            switch (a.Kind)
            {
                case AnnKind.Arrow: case AnnKind.Line:
                    a.P1 = new PointF(box.X + 2, box.Bottom - 2); a.P2 = new PointF(box.Right - 2, box.Y + 2); a.Draw(g, null); break;
                case AnnKind.Pen:
                    a.Pts = new List<PointF> { new PointF(box.X, box.Bottom - 4), new PointF(box.X + box.Width * 0.3f, box.Y + 6), new PointF(box.X + box.Width * 0.6f, box.Bottom - 6), new PointF(box.Right, box.Y + 4) };
                    if (a.Highlighter) a.Width = 12;
                    a.Draw(g, null); break;
                case AnnKind.Shape:
                    a.Rect = box; a.Width = 3; a.Draw(g, null); break;
                case AnnKind.Callout:
                    a.Rect = new RectangleF(box.X - 2, box.Y - 2, box.Width + 4, box.Height - 10); a.Tail = new PointF(box.X + 8, box.Bottom + 2); a.Text = "Abc"; a.FontSize = 14; a.Align = 1; a.AutoSize = true; a.Width = 2; a.Draw(g, null); break;
                case AnnKind.Text:
                    a.Rect = box; a.Text = "Abc"; a.FontSize = 24; a.Align = 1; a.AutoSize = false; a.Draw(g, null); break;
                case AnnKind.Step:
                    { float d = Math.Min(box.Width, box.Height); a.Rect = new RectangleF(box.X + (box.Width - d) / 2, box.Y + (box.Height - d) / 2, d, d); a.Number = 1; a.Draw(g, null); break; }
                case AnnKind.Stamp:
                    { float d = Math.Min(box.Width, box.Height); a.Rect = new RectangleF(box.X + (box.Width - d) / 2, box.Y + (box.Height - d) / 2, d, d); a.Draw(g, null); break; }
                case AnnKind.Blur:
                    for (int i = 0; i < 4; i++) for (int j = 0; j < 3; j++)
                        using (var b = new SolidBrush(Color.FromArgb(a.Variant == 0 ? (80 + ((i + j) % 2) * 100) : 120, 160, 170, 190)))
                            g.FillRectangle(b, box.X + i * box.Width / 4, box.Y + j * box.Height / 3, box.Width / 4 - (a.Variant == 0 ? 2 : 0), box.Height / 3 - (a.Variant == 0 ? 2 : 0));
                    break;
                case AnnKind.Magnify:
                    {
                        var rr = new RectangleF(box.X + box.Width / 2 - 20, box.Y + box.Height / 2 - 16, 40, 32);
                        using (var b = new SolidBrush(Color.FromArgb(230, 230, 235)))
                            if (a.Variant == 1) g.FillEllipse(b, rr); else g.FillRectangle(b, rr);
                        using (var p = new Pen(Color.FromArgb(60, 60, 60), 2f)) { if (a.Variant == 1) g.DrawEllipse(p, rr); else g.DrawRectangle(p, rr.X, rr.Y, rr.Width, rr.Height); }
                        TextRenderer.DrawText(g, a.Zoom + "x", Font, Rectangle.Round(rr), Color.Black, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                        break;
                    }
                case AnnKind.Spotlight:
                    {
                        using (var b = new SolidBrush(a.Fill)) g.FillRectangle(b, r);
                        var rr = new RectangleF(box.X + 8, box.Y + 6, box.Width - 16, box.Height - 12);
                        using (var b = new SolidBrush(Color.FromArgb(235, 235, 240))) { if (a.Variant == 1) g.FillEllipse(b, rr); else g.FillRectangle(b, rr); }
                        break;
                    }
            }
        }

        // ------------------------------------------------------------------ property menus

        bool HasOutline()
        {
            if (canvas.CurrentTool == Tool.Fill) return false;
            var k = CtxKind();
            return k == AnnKind.Arrow || k == AnnKind.Line || k == AnnKind.Shape || k == AnnKind.Callout || k == AnnKind.Text || k == AnnKind.Step || k == AnnKind.Pen || k == AnnKind.Magnify;
        }

        bool HasFill()
        {
            if (canvas.CurrentTool == Tool.Fill) return true;
            var k = CtxKind();
            return k == AnnKind.Shape || k == AnnKind.Callout || k == AnnKind.Text || k == AnnKind.Step || k == AnnKind.Spotlight;
        }

        bool HasEffects()
        {
            var k = CtxKind();
            return k != null && k != AnnKind.Blur && k != AnnKind.Spotlight;
        }

        ContextMenuStrip OutlineMenu()
        {
            var p = P;
            var m = ColorPalette.Menu(p != null ? p.Stroke : CRed, false, c => { canvas.ApplyProp(a => { a.Stroke = c; if (a.Kind == AnnKind.Text) a.Variant = 1; }); ribbon.Invalidate(); }, null);
            m.Items.Add(new ToolStripSeparator());
            var k = CtxKind();
            var wm = new ToolStripMenuItem(Loc.T("Width")) { ForeColor = Theme.Text };
            bool canNone = k == AnnKind.Shape || k == AnnKind.Callout || k == AnnKind.Text || k == AnnKind.Step || k == AnnKind.Magnify;
            var widths = new List<int>(); if (canNone) widths.Add(0);
            widths.AddRange(new[] { 1, 2, 3, 4, 5, 6, 8, 10, 12, 16, 24 });
            foreach (int w in widths)
            {
                int ww = w;
                var it = new ToolStripMenuItem(w == 0 ? Loc.T("None") : w + " px") { ForeColor = Theme.Text, Checked = p != null && (int)p.Width == w };
                it.Click += delegate { canvas.ApplyProp(a => { a.Width = ww; if (a.Kind == AnnKind.Text) a.Variant = ww > 0 ? 1 : 0; }); ribbon.Invalidate(); };
                wm.DropDownItems.Add(it);
            }
            m.Items.Add(wm);
            if (k == AnnKind.Arrow || k == AnnKind.Line || k == AnnKind.Shape || k == AnnKind.Pen || k == AnnKind.Callout)
            {
                var dm = new ToolStripMenuItem(Loc.T("Line type")) { ForeColor = Theme.Text };
                string[] names = { "Solid", "Dash", "Dot", "Dash-dot" };
                DashStyle[] styles = { DashStyle.Solid, DashStyle.Dash, DashStyle.Dot, DashStyle.DashDot };
                for (int i = 0; i < names.Length; i++)
                {
                    var ds = styles[i];
                    var it = new ToolStripMenuItem(Loc.T(names[i])) { ForeColor = Theme.Text, Checked = p != null && p.Dash == ds };
                    it.Click += delegate { canvas.ApplyProp(a => a.Dash = ds); };
                    dm.DropDownItems.Add(it);
                }
                m.Items.Add(dm);
            }
            return m;
        }

        ContextMenuStrip FillMenu()
        {
            if (canvas.CurrentTool == Tool.Fill)
                return ColorPalette.Menu(canvas.FillColor, false, c => { canvas.FillColor = c; ribbon.Invalidate(); }, null);
            var p = P;
            bool spot = CtxKind() == AnnKind.Spotlight;
            var m = ColorPalette.Menu(p != null ? p.Fill : Color.Transparent, !spot,
                c => { canvas.ApplyProp(a => a.Fill = spot ? Color.FromArgb(Math.Max(60, (int)a.Fill.A), c) : c); ribbon.Invalidate(); },
                () => { canvas.ApplyProp(a => a.Fill = Color.Transparent); ribbon.Invalidate(); });
            return m;
        }

        ContextMenuStrip EffectsMenu()
        {
            var p = P;
            var m = Theme.Menu();
            var sh = new ToolStripMenuItem(Loc.T("Shadow")) { ForeColor = Theme.Text, Checked = p != null && p.Shadow, CheckOnClick = false };
            sh.Click += delegate { bool v = p != null && p.Shadow; canvas.ApplyProp(a => a.Shadow = !v); };
            m.Items.Add(sh);
            var om = new ToolStripMenuItem(Loc.T("Opacity")) { ForeColor = Theme.Text };
            foreach (int o in new[] { 25, 50, 75, 100 })
            {
                int oo = o;
                var it = new ToolStripMenuItem(o + "%") { ForeColor = Theme.Text, Checked = p != null && (int)Math.Round(p.Opacity * 100) == o };
                it.Click += delegate { canvas.ApplyProp(a => a.Opacity = oo / 100f); };
                om.DropDownItems.Add(it);
            }
            m.Items.Add(om);
            return m;
        }

        ContextMenuStrip FontMenu()
        {
            var m = Theme.Menu();
            string[] common = { "Segoe UI", "Arial", "Calibri", "Cambria", "Consolas", "Courier New", "Georgia", "Impact", "Tahoma", "Times New Roman", "Trebuchet MS", "Verdana", "Comic Sans MS", "Segoe Script" };
            var installed = new HashSet<string>(FontFamily.Families.Select(f => f.Name));
            foreach (var f in common)
            {
                if (!installed.Contains(f)) continue;
                string ff = f;
                var it = new ToolStripMenuItem(f) { ForeColor = Theme.Text, Checked = P != null && P.FontName == f };
                it.Click += delegate { canvas.ApplyProp(a => a.FontName = ff); ribbon.Invalidate(); };
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            var more = new ToolStripMenuItem(Loc.T("More fonts…")) { ForeColor = Theme.Text };
            more.Click += delegate
            {
                using (var fd = new FontDialog { ShowEffects = false, FontMustExist = true })
                    if (fd.ShowDialog(this) == DialogResult.OK) { string n = fd.Font.Name; canvas.ApplyProp(a => a.FontName = n); ribbon.Invalidate(); }
            };
            m.Items.Add(more);
            return m;
        }

        ContextMenuStrip SizeMenu()
        {
            var sizes = new[] { 10, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 64, 72, 96 };
            return MenuUtil.ValueMenu(sizes.Select(s => s + " px"), i => P != null && (int)P.FontSize == sizes[i],
                i => { canvas.ApplyProp(a => a.FontSize = sizes[i]); ribbon.Invalidate(); });
        }

        ContextMenuStrip TextColorMenu()
        {
            return ColorPalette.Menu(P != null ? P.TextColor : Color.Black, false, c => { canvas.ApplyProp(a => a.TextColor = c); ribbon.Invalidate(); }, null);
        }

        ContextMenuStrip AlignMenu()
        {
            var m = Theme.Menu();
            string[] names = { "Left", "Centre", "Right" }; string[] icons = { "alignl", "alignc", "alignr" };
            for (int i = 0; i < 3; i++)
            {
                int ii = i;
                var it = Theme.Item(names[i], icons[i], delegate { canvas.ApplyProp(a => a.Align = ii); });
                it.Checked = P != null && P.Align == i;
                m.Items.Add(it);
            }
            return m;
        }

        // options group (varies with the tool / selected object)
        void RebuildOptions()
        {
            var cols = optionsGroup.Columns;
            cols.Clear();
            var items = new List<RItem>();
            var k = CtxKind();
            Func<string, string, string[], Func<int> , Action<Ann, int>, RBtn> menuBtn = (label, icon, names, cur, set) =>
            {
                var b = Drop(label, icon, () => MenuUtil.ValueMenu(names.Select(n => Loc.T(n)), i => P != null && cur() == i, i => { canvas.ApplyProp(a => set(a, i)); ribbon.Invalidate(); }));
                return b;
            };
            if (canvas.CurrentTool == Tool.Fill)
            {
                int[] tol = { 0, 10, 20, 40, 60 };
                items.Add(Drop("Tolerance", "adjust", () => MenuUtil.ValueMenu(tol.Select(t => t + "%"), i => canvas.FillTolerance == tol[i], i => canvas.FillTolerance = tol[i])));
            }
            else if (canvas.CurrentTool == Tool.Eraser)
            {
                int[] sz = { 6, 12, 24, 40, 64, 100 };
                items.Add(Drop("Size", "eraser", () => MenuUtil.ValueMenu(sz.Select(t => t + " px"), i => canvas.EraserSize == sz[i], i => canvas.EraserSize = sz[i])));
            }
            else if (k != null)
            {
                switch (k.Value)
                {
                    case AnnKind.Arrow:
                        items.Add(menuBtn("Head", "arrow", new[] { "Filled arrow", "Arrow on both ends", "Open arrow" }, () => P.Variant, (a, i) => a.Variant = i)); break;
                    case AnnKind.Shape:
                        items.Add(menuBtn("Shape", "shape", new[] { "Rectangle", "Rounded rectangle", "Ellipse" }, () => P.Variant, (a, i) => a.Variant = i)); break;
                    case AnnKind.Blur:
                        items.Add(menuBtn("Mode", "blur", new[] { "Pixelate", "Blur" }, () => P.Variant, (a, i) => a.Variant = i));
                        { int[] amt = { 6, 10, 16, 24, 36 }; items.Add(menuBtn("Amount", "adjust", new[] { "Low", "Medium", "High", "Higher", "Maximum" }, () => Array.IndexOf(amt, (int)P.Width), (a, i) => a.Width = amt[i])); }
                        break;
                    case AnnKind.Magnify:
                        items.Add(menuBtn("Lens", "magnify", new[] { "Rectangle", "Circle" }, () => P.Variant, (a, i) => a.Variant = i));
                        { float[] z = { 1.5f, 2f, 3f, 4f }; items.Add(menuBtn("Zoom", "zoomin", new[] { "150%", "200%", "300%", "400%" }, () => Array.IndexOf(z, P.Zoom), (a, i) => a.Zoom = z[i])); }
                        break;
                    case AnnKind.Spotlight:
                        items.Add(menuBtn("Shape", "spotlight", new[] { "Rectangle", "Ellipse" }, () => P.Variant, (a, i) => a.Variant = i));
                        { int[] al = { 90, 130, 170, 210, 240 }; items.Add(menuBtn("Darkness", "adjust", new[] { "35%", "50%", "65%", "80%", "95%" }, () => { int cur = P.Fill.A; int best = 0; for (int i = 0; i < al.Length; i++) if (Math.Abs(al[i] - cur) < Math.Abs(al[best] - cur)) best = i; return best; }, (a, i) => a.Fill = Color.FromArgb(al[i], 0, 0, 0))); }
                        break;
                    case AnnKind.Step:
                        items.Add(Small("Edit value…", "step", EditStepValue, () => doc != null && SelectedStep() != null));
                        items.Add(Small("Restart sequence", "repeat", RestartSequence, () => doc != null));
                        { int[] sz = { 24, 32, 40, 48, 60, 72, 96 }; items.Add(Drop("Size", "step", () => MenuUtil.ValueMenu(sz.Select(s => s + " px"), i => P != null && (int)Math.Round(Math.Max(P.Rect.Width, P.Rect.Height)) == sz[i], i => StepSize(sz[i])))); }
                        break;
                    case AnnKind.Stamp:
                        items.Add(Drop("Stamp", "stamp", StampMenu));
                        break;
                    case AnnKind.Pen:
                        { int[] w = canvas.CurrentTool == Tool.Highlighter || (canvas.Selection.Count > 0 && canvas.Selection[0].Highlighter) ? new[] { 10, 16, 22, 30, 40 } : new[] { 2, 3, 4, 6, 8, 12 };
                          items.Add(Drop("Size", "pen", () => MenuUtil.ValueMenu(w.Select(s => s + " px"), i => P != null && (int)P.Width == w[i], i => canvas.ApplyProp(a => a.Width = w[i])))); }
                        break;
                }
            }
            if (items.Count > 0) cols.Add(items);
        }

        void StepSize(int d)
        {
            Ann tpl;
            if (canvas.Defaults.TryGetValue(Tool.Step, out tpl)) tpl.Zoom = d;      // new steps start at this size
            StepSizeSelected(d);
            status.Hint(Loc.T("New steps start at") + " " + d + " px");
        }

        Ann SelectedStep()
        {
            foreach (var a in canvas.Selection) if (a.Kind == AnnKind.Step) return a;
            return null;
        }

        /// <summary>Changes the number of the selected step, optionally renumbering the steps that follow it.</summary>
        void EditStepValue()
        {
            var s = SelectedStep();
            if (doc == null || s == null) return;
            using (var d = new ParamDialog("Edit step value", null))
            {
                var nv = d.AddNumber("Value", 0, 9999, s.Number);
                var following = d.AddCheck("Renumber the steps that follow", true);
                d.Finish(400);
                if (d.ShowDialog(this) != DialogResult.OK) return;
                doc.RenumberFrom(s, (int)nv.Value, following.Checked);
                status.Hint(Loc.T("Step value set to") + " " + (int)nv.Value);
            }
        }

        /// <summary>With a step selected: it becomes 1 and the steps after it continue 2, 3 ... Otherwise the next step you add is 1.</summary>
        void RestartSequence()
        {
            if (doc == null) return;
            var s = SelectedStep();
            if (s != null) { doc.RestartSequenceAt(s); status.Hint(Loc.T("Sequence restarted at this step")); }
            else { doc.StepNext = 1; status.Hint(Loc.T("The next step you add will be 1")); }
        }

        void StepSizeSelected(int d)
        {
            if (canvas.Selection.Count > 0)
            {
                doc.Push();
                foreach (var a in canvas.Selection) if (a.Kind == AnnKind.Step) { var r = a.Rect; a.Rect = new RectangleF(r.X, r.Y, d, d); }
                doc.Raise();
            }
        }

        ContextMenuStrip StampMenu()
        {
            var m = Theme.Menu();
            for (int i = 0; i < Icons.StampNames.Length; i++)
            {
                int v = i;
                var bmp = new Bitmap(20, 20, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp)) Icons.DrawStamp(g, i, new RectangleF(0, 0, 20, 20));
                var it = new ToolStripMenuItem(Loc.T(Icons.StampNames[i]), bmp) { ForeColor = Theme.Text, Checked = P != null && P.Variant == i };
                it.Click += delegate { canvas.ApplyProp(a => { a.Variant = v; a.Kind = AnnKind.Stamp; }); ribbon.Invalidate(); };
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Theme.Item("From file…", "open", delegate
            {
                using (var ofd = new OpenFileDialog { Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico|All files|*.*" })
                    if (ofd.ShowDialog(this) == DialogResult.OK)
                        try { using (var im = Image.FromFile(ofd.FileName)) canvas.PlaceImage(Effects.ToArgb(im)); } catch (Exception ex) { MessageBox.Show(this, ex.Message); }
            }));
            return m;
        }

        // ------------------------------------------------------------------ menus

        ContextMenuStrip BuildFileMenu()
        {
            var m = Theme.Menu();
            m.Items.Add(Theme.Item("Open Image…", "open", (s, e) => OpenFileDialog()));
            m.Items.Add(Theme.Item("New from clipboard", "paste", (s, e) => PasteAsNew()));
            m.Items.Add(new ToolStripSeparator());
            var save = Theme.Item("Save", "save", (s, e) => SaveQuick()); save.Enabled = CanSave; m.Items.Add(save);
            var sas = Theme.Item("Save As…", "save", (s, e) => SaveAs()); sas.Enabled = CanSave; m.Items.Add(sas);
            var pdf = Theme.Item("Export as PDF…", "save", (s, e) => DoPdf()); pdf.Enabled = doc != null; m.Items.Add(pdf);
            var prn = Theme.Item("Print…", "print", (s, e) => DoPrint()); prn.Enabled = doc != null; m.Items.Add(prn);
            m.Items.Add(new ToolStripSeparator());
            var cap = new ToolStripMenuItem(Loc.T("New capture")) { ForeColor = Theme.Text, Image = Icons.Get("camera", 16, true) };
            cap.DropDownItems.Add(Theme.Item("All-in-One", "camera", (s, e) => App.Capture(CaptureMode.AllInOne)));
            cap.DropDownItems.Add(Theme.Item("Region", "region", (s, e) => App.Capture(CaptureMode.Region)));
            cap.DropDownItems.Add(Theme.Item("Window", "window", (s, e) => App.Capture(CaptureMode.Window)));
            cap.DropDownItems.Add(Theme.Item("Full Screen", "fullscreen", (s, e) => App.Capture(CaptureMode.FullScreen)));
            cap.DropDownItems.Add(Theme.Item("Scrolling", "scroll", (s, e) => App.Capture(CaptureMode.Scrolling)));
            cap.DropDownItems.Add(Theme.Item("Freehand", "freehand", (s, e) => App.Capture(CaptureMode.Freehand)));
            cap.DropDownItems.Add(Theme.Item("Video", "video", (s, e) => App.Capture(CaptureMode.Video)));
            m.Items.Add(cap);
            m.Items.Add(Theme.Item("Capture window", "camera", (s, e) => App.ShowMain()));
            m.Items.Add(Theme.Item("Library", "library", (s, e) => App.ShowLibrary()));
            m.Items.Add(Theme.Item("Settings…", "settings", (s, e) => App.ShowSettings()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(Theme.Item("About RXCapture", "help", (s, e) => App.About()));
            m.Items.Add(Theme.Item("Close editor", "close", (s, e) => Close()));
            return m;
        }

        // ---- Snagit-style File panel

        FileMenuForm filePanel;
        public FileMenuForm FilePanel { get { return filePanel; } }
        public void DebugShowFilePanel(Point at) { ShowFilePanel(at); }

        void ShowFilePanel(Point at)
        {
            bool has = doc != null;
            var items = new List<FileEntry>();
            items.Add(new FileEntry("New Image", "new", NewImage));
            items.Add(new FileEntry("New Capture", "camera", () => App.Capture(CaptureMode.AllInOne)));
            items.Add(new FileEntry("New from Clipboard", "paste", PasteAsNew));
            items.Add(new FileEntry("Open", "open", OpenFileDialog));
            items.Add(new FileEntry("Save", "save", SaveQuick) { Enabled = CanSave, SepAbove = true });
            items.Add(new FileEntry("Save As", "save", null)
            {
                Enabled = CanSave,
                Sub = VideoShown
                    ? new List<FileEntry> { new FileEntry("Video file…", "save", SaveAs) }
                    : new List<FileEntry> {
                        new FileEntry("Image file…", "save", SaveAs),
                        new FileEntry("PDF document…", "save", DoPdf),
                        new FileEntry("RXCapture project (.scp)…", "library", SaveProjectAs) }
            });
            items.Add(new FileEntry("Convert Images", "resize", ConvertImages) { SepAbove = true });
            items.Add(new FileEntry("Print", "print", null)
            {
                Enabled = has,
                Sub = new List<FileEntry> { new FileEntry("Print…", "print", DoPrint), new FileEntry("Print to PDF…", "save", DoPdf) }
            });
            items.Add(new FileEntry("Help", "help", null)
            {
                SepAbove = true,
                Sub = new List<FileEntry> {
                    new FileEntry("About RXCapture", "help", App.About),
                    new FileEntry("Keyboard shortcuts", "help", ShowShortcuts),
                    new FileEntry("Open data folder", "folder", () => { try { Process.Start(AppSettings.DataDir); } catch { } }) }
            });

            var recents = new List<FileRecent>();
            foreach (var li in LibraryStore.List())
            {
                if (li.IsVideo) continue;
                var it = li;
                recents.Add(new FileRecent(li.Created.ToString("MMM d, yyyy h:mm:ss tt", CultureInfo.CurrentCulture), () => OpenLibItem(it)));
                if (recents.Count >= 6) break;
            }

            var panel = new FileMenuForm(this, items, recents,
                new FileEntry("Editor Options…", "settings", App.ShowSettings), new FileEntry("Exit Editor", "close", () => Close()));
            var wa = Screen.FromPoint(at).WorkingArea;
            panel.Location = new Point(Math.Max(wa.Left, Math.Min(at.X, wa.Right - panel.Width)), Math.Max(wa.Top, Math.Min(at.Y, wa.Bottom - panel.Height)));
            filePanel = panel;
            panel.Show(this);
        }

        /// <summary>Stores the selected object's (or the active tool's) style as the permanent default for that tool.</summary>
        void SetAsDefault()
        {
            var t = canvas.SaveAsDefault();
            if (t != null) status.Hint(Loc.T("Saved as the default for") + " " + Loc.T(t.Value.ToString()));
        }

        void ShowShortcuts()
        {
            MessageBox.Show(this,
                "Ctrl+N  " + Loc.T("New Image") + "\nCtrl+O  " + Loc.T("Open") + "\nCtrl+S  " + Loc.T("Save") + "\nCtrl+Shift+S  " + Loc.T("Save As") +
                "\nCtrl+C / Ctrl+X / Ctrl+V  " + Loc.T("Copy / Cut / Paste") + "\nCtrl+D  " + Loc.T("Duplicate") + "\nCtrl+Z / Ctrl+Y  " + Loc.T("Undo / Redo") +
                "\nCtrl+A  " + Loc.T("Select all") + "\nDelete  " + Loc.T("Delete") + "\n\nPrint Screen  " + Loc.T("All-in-One") + "\nCtrl+Shift+R / W / F / S / D / L / V  " + Loc.T("Region / Window / Full Screen / Scrolling / Freehand / Repeat / Video"),
                "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        static readonly int[][] NewSizes = { new[] { 800, 600 }, new[] { 1024, 768 }, new[] { 1280, 720 }, new[] { 1920, 1080 }, new[] { 2480, 3508 } };

        /// <summary>File > New Image: an empty canvas of a chosen size and background.</summary>
        void NewImage()
        {
            using (var d = new ParamDialog("New Image", null))
            {
                var pre = d.AddCombo("Size", new[] { "800 x 600", "1024 x 768", "1280 x 720 (HD)", "1920 x 1080 (Full HD)", "2480 x 3508 (A4, 300 dpi)", "Custom" }, 2);
                var nw = d.AddNumber("Width", 1, 20000, 1280);
                var nh = d.AddNumber("Height", 1, 20000, 720);
                var col = d.AddColor("Background", Color.White, true);
                bool busy = false;
                pre.SelectedIndexChanged += delegate
                {
                    if (pre.SelectedIndex >= NewSizes.Length) return;
                    busy = true; nw.Value = NewSizes[pre.SelectedIndex][0]; nh.Value = NewSizes[pre.SelectedIndex][1]; busy = false;
                };
                EventHandler custom = delegate { if (!busy) pre.SelectedIndex = NewSizes.Length; };
                nw.ValueChanged += custom; nh.ValueChanged += custom;
                d.Finish(400);
                if (d.ShowDialog(this) != DialogResult.OK) return;
                CreateBlank((int)nw.Value, (int)nh.Value, col.Value);
            }
        }

        public void CreateBlank(int w, int h, Color background)
        {
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.Clear(background);
            Document nd; var it = LibraryStore.AddImage(bmp, out nd);
            OpenNew(it, nd);
            if (!Visible) Show();
        }

        /// <summary>File > Save As > project: an editable .scp copy (image + all objects) anywhere on disk.</summary>
        void SaveProjectAs()
        {
            if (doc == null) return;
            canvas.CommitEdit();
            using (var sfd = new SaveFileDialog { Filter = "RXCapture project (*.scp)|*.scp", DefaultExt = "scp", AddExtension = true, OverwritePrompt = true })
            {
                Directory.CreateDirectory(AppSettings.Current.SaveFolder);
                sfd.InitialDirectory = AppSettings.Current.SaveFolder;
                sfd.FileName = Path.GetFileNameWithoutExtension(AppSettings.Current.NewFileName("scp"));
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string keep = doc.ProjectPath;
                    doc.SaveProject(sfd.FileName);
                    doc.ProjectPath = keep;             // the library keeps its own copy
                    status.Hint(Loc.T("Saved") + ": " + sfd.FileName);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void ConvertImages() { using (var f = new ConvertImagesForm()) f.ShowDialog(this); }

        ContextMenuStrip ShareMenu()
        {
            var m = Theme.Menu();
            m.Items.Add(Theme.Item("Copy all to clipboard", "copy", (s, e) => CopyAll()));
            m.Items.Add(Theme.Item("Save As…", "save", (s, e) => SaveAs()));
            m.Items.Add(Theme.Item("Export as PDF…", "save", (s, e) => DoPdf()));
            m.Items.Add(Theme.Item("Email…", "email", (s, e) => DoEmail()));
            m.Items.Add(Theme.Item("Print…", "print", (s, e) => DoPrint()));
            m.Items.Add(Theme.Item("Open in Paint", "pen", (s, e) => DoPaint()));
            m.Items.Add(Theme.Item("Show in folder", "folder", (s, e) => DoReveal()));
            return m;
        }

        ContextMenuStrip RotateMenu()
        {
            var m = Theme.Menu();
            m.Items.Add(Theme.Item("Rotate right 90°", "rotate", (s, e) => { doc.RotateFlip(RotateFlipType.Rotate90FlipNone); canvas.ZoomFit(false); }));
            m.Items.Add(Theme.Item("Rotate left 90°", "rotate", (s, e) => { doc.RotateFlip(RotateFlipType.Rotate270FlipNone); canvas.ZoomFit(false); }));
            m.Items.Add(Theme.Item("Rotate 180°", "rotate", (s, e) => { doc.RotateFlip(RotateFlipType.Rotate180FlipNone); canvas.ZoomFit(false); }));
            return m;
        }

        ContextMenuStrip FlipMenu()
        {
            var m = Theme.Menu();
            m.Items.Add(Theme.Item("Flip horizontal", "flip", (s, e) => doc.RotateFlip(RotateFlipType.RotateNoneFlipX)));
            m.Items.Add(Theme.Item("Flip vertical", "flip", (s, e) => doc.RotateFlip(RotateFlipType.RotateNoneFlipY)));
            return m;
        }

        void ShowObjectMenu(Point p)
        {
            var m = Theme.Menu();
            bool sel = canvas.Selection.Count > 0;
            var cut = Theme.Item("Cut", "cutout", (s, e) => canvas.CutSelection()); cut.Enabled = sel; m.Items.Add(cut);
            var cp = Theme.Item("Copy", "copy", (s, e) => canvas.CopySelection()); cp.Enabled = sel; m.Items.Add(cp);
            var ps = Theme.Item("Paste", "paste", (s, e) => PasteSmart()); m.Items.Add(ps);
            var dup = Theme.Item("Duplicate", "copy", (s, e) => canvas.DuplicateSelection()); dup.Enabled = sel; m.Items.Add(dup);
            var del = Theme.Item("Delete", "trash", (s, e) => canvas.DeleteSelection()); del.Enabled = sel; m.Items.Add(del);
            if (SelectedStep() != null)
            {
                m.Items.Add(new ToolStripSeparator());
                m.Items.Add(Theme.Item("Edit step value…", "step", (s, e) => EditStepValue()));
                m.Items.Add(Theme.Item("Restart sequence here", "repeat", (s, e) => RestartSequence()));
            }
            m.Items.Add(new ToolStripSeparator());
            var ord = new ToolStripMenuItem(Loc.T("Arrange")) { ForeColor = Theme.Text, Enabled = sel };
            ord.DropDownItems.Add(Theme.Item("Bring to front", "up", (s, e) => canvas.ChangeOrder(0)));
            ord.DropDownItems.Add(Theme.Item("Bring forward", "up", (s, e) => canvas.ChangeOrder(2)));
            ord.DropDownItems.Add(Theme.Item("Send backward", "down", (s, e) => canvas.ChangeOrder(3)));
            ord.DropDownItems.Add(Theme.Item("Send to back", "down", (s, e) => canvas.ChangeOrder(1)));
            m.Items.Add(ord);
            m.Show(canvas, p);
        }

        // ------------------------------------------------------------------ commands

        void OpenFileDialog()
        {
            using (var ofd = new OpenFileDialog { Filter = "Images and projects|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.scp|All files|*.*", Multiselect = true })
                if (ofd.ShowDialog(this) == DialogResult.OK) foreach (var f in ofd.FileNames) OpenFile(f);
        }

        void PasteAsNew()
        {
            var b = Exporter.ClipboardImage();
            if (b == null) { MessageBox.Show(this, Loc.T("There is no image on the clipboard."), "RXCapture"); return; }
            Document d; var it = LibraryStore.AddImage(b, out d);
            OpenNew(it, d);
        }

        void PasteSmart()
        {
            if (doc == null) { PasteAsNew(); return; }
            if (canvas.HasObjectClipboard && Clipboard.GetDataObject() != null && !Clipboard.ContainsImage()) { canvas.PasteObjects(); return; }
            var b = Exporter.ClipboardImage();
            if (b != null) canvas.PlaceImage(b);
            else if (canvas.HasObjectClipboard) canvas.PasteObjects();
        }

        void CopySmart()
        {
            if (canvas.Selection.Count > 0) canvas.CopySelection(); else CopyAll();
        }

        void CopyAll()
        {
            if (doc == null) return;
            canvas.CommitEdit();
            using (var b = doc.Render()) Exporter.CopyToClipboard(b);
            status.Hint(Loc.T("Image copied to clipboard"));
        }

        /// <summary>A video (not an image document) is what the editor currently shows.</summary>
        bool VideoShown { get { return doc == null && item != null && item.IsVideo; } }   // HidePlayer clears item when the player closes
        bool CanSave { get { return doc != null || VideoShown; } }

        /// <summary>Writes the library video to <paramref name="dest"/> (a plain file copy: same format and quality).</summary>
        internal static void CopyVideoTo(LibItem it, string dest)
        {
            if (string.Equals(Path.GetFullPath(dest), Path.GetFullPath(it.File), StringComparison.OrdinalIgnoreCase)) return;   // already there
            File.Copy(it.File, dest, true);
        }

        /// <summary>Copies the video that is being shown to a file of the user's choice (same format as the recording).</summary>
        void SaveVideoAs()
        {
            var it = item;
            if (it == null || !File.Exists(it.File)) return;
            var cfg = AppSettings.Current;
            string ext = (it.Ext ?? "mp4").ToLowerInvariant();
            string label = ext == "mp4" ? "MP4 video" : ext == "avi" ? "AVI video" : ext == "gif" ? "GIF animation" : ext.ToUpperInvariant() + " video";
            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = label + " (*." + ext + ")|*." + ext;
                sfd.DefaultExt = ext; sfd.AddExtension = true; sfd.OverwritePrompt = true;
                Directory.CreateDirectory(cfg.SaveFolder);
                sfd.InitialDirectory = cfg.SaveFolder;
                sfd.FileName = Path.GetFileNameWithoutExtension(cfg.NewFileName(ext));
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                try
                {
                    string dest = sfd.FileName;
                    CopyVideoTo(it, dest);
                    cfg.SaveFolder = Path.GetDirectoryName(dest);
                    status.Hint(Loc.T("Saved") + ": " + dest);
                }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            }
        }

        void SaveAs()
        {
            if (VideoShown) { SaveVideoAs(); return; }
            if (doc == null) return;
            canvas.CommitEdit();
            var cfg = AppSettings.Current;
            using (var sfd = new SaveFileDialog())
            {
                sfd.Filter = Exporter.FileFilter;
                sfd.FilterIndex = Exporter.FilterIndexFor(cfg.Format);
                Directory.CreateDirectory(cfg.SaveFolder);
                sfd.InitialDirectory = cfg.SaveFolder;
                sfd.FileName = Path.GetFileNameWithoutExtension(cfg.NewFileName("png"));
                sfd.AddExtension = true;
                sfd.OverwritePrompt = true;
                if (sfd.ShowDialog(this) != DialogResult.OK) return;
                string path = sfd.FileName;
                if (Path.GetExtension(path).Length == 0) path += "." + Exporter.ExtForFilterIndex(sfd.FilterIndex);
                WriteExport(path);
            }
        }

        void WriteExport(string path)
        {
            try
            {
                using (var b = doc.Render()) Exporter.Save(b, path);
                doc.ExportPath = path;
                AppSettings.Current.SaveFolder = Path.GetDirectoryName(path);
                status.Hint(Loc.T("Saved") + ": " + path);
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        }

        void SaveQuick()
        {
            if (VideoShown) { SaveVideoAs(); return; }
            if (doc == null) return;
            canvas.CommitEdit();
            if (doc.ExportPath != null && Directory.Exists(Path.GetDirectoryName(doc.ExportPath))) WriteExport(doc.ExportPath);
            else SaveAs();
        }

        void DoPdf()
        {
            if (doc == null) return;
            using (var sfd = new SaveFileDialog { Filter = "PDF document (*.pdf)|*.pdf", FileName = Path.GetFileNameWithoutExtension(AppSettings.Current.NewFileName("pdf")) + ".pdf", InitialDirectory = AppSettings.Current.SaveFolder })
                if (sfd.ShowDialog(this) == DialogResult.OK) { canvas.CommitEdit(); using (var b = doc.Render()) Exporter.Save(b, sfd.FileName); status.Hint(Loc.T("Saved") + ": " + sfd.FileName); }
        }

        void DoPrint() { if (doc == null) return; canvas.CommitEdit(); using (var b = doc.Render()) Exporter.Print(b, this); }
        void DoPaint() { if (doc == null) return; canvas.CommitEdit(); using (var b = doc.Render()) Exporter.OpenInPaint(b); }

        void DoEmail()
        {
            if (doc == null) return;
            canvas.CommitEdit();
            using (var b = doc.Render())
                if (!Exporter.Email(b, "Screenshot")) MessageBox.Show(this, Loc.T("No e-mail program is configured (Simple MAPI)."), "RXCapture");
        }

        void DoReveal()
        {
            if (doc == null) return;
            if (doc.ExportPath != null && File.Exists(doc.ExportPath)) Exporter.Reveal(doc.ExportPath);
            else if (item != null) Exporter.Reveal(item.File);
        }

        void DeleteCurrent()
        {
            if (item == null) return;
            if (MessageBox.Show(this, Loc.T("Delete this capture from the library?"), "RXCapture", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            var old = item; item = null; doc = null;
            LibraryStore.Delete(old);
            var next = LibraryStore.List().FirstOrDefault(i => !i.IsVideo);
            if (next != null) OpenLibItem(next);
            else { canvas.SetDocument(null); UpdateTitle(); UpdateStatus(); ribbon.Invalidate(); }
        }

        // X button / menu in the library tray: the open item goes through DeleteCurrent so the canvas or player moves on cleanly
        void RemoveFromTray(LibItem it)
        {
            if (item != null && item.Id == it.Id) DeleteCurrent();
            else if (ThumbGrid.ConfirmDelete(this)) LibraryStore.Delete(it);
        }

        // several ticked thumbnails deleted at once (the tray has already asked for confirmation)
        void RemoveManyFromTray(List<LibItem> list)
        {
            bool currentGone = item != null && list.Exists(i => i.Id == item.Id);
            if (currentGone) { player.Stop(); player.Visible = false; item = null; doc = null; }
            LibraryStore.DeleteMany(list);
            if (!currentGone) return;
            var next = LibraryStore.List().FirstOrDefault(i => !i.IsVideo);
            if (next != null) OpenLibItem(next);
            else { canvas.SetDocument(null); UpdateTitle(); UpdateStatus(); ribbon.Invalidate(); }
        }

        void DoResize() { int w, h; if (Dlg.Resize(this, doc, out w, out h)) { doc.ResizeImage(w, h); canvas.ZoomFit(false); } }

        void DoCanvas()
        {
            int w, h, a; Color fill;
            if (Dlg.CanvasSize(this, doc, out w, out h, out a, out fill)) { doc.CanvasResize(w, h, a, fill); canvas.ZoomFit(false); }
        }

        void DoAdjust()
        {
            int br, co, sa, hu; float ga;
            if (Dlg.Adjust(this, doc.Base, out br, out co, out sa, out hu, out ga)) doc.ApplyToBase(b => Effects.Adjust(b, br, co, sa, hu, ga));
        }

        void DoBorder()
        {
            int w; Color c;
            using (var flat = doc.Render())
                if (!Dlg.Border(this, flat, out w, out c)) return;
            doc.ShadowOrFrame((b, off) => { off[0] = new Point(w, w); return Effects.Border(b, w, c); }, true);
            canvas.ZoomFit(false);
        }

        void DoShadow()
        {
            int dx, dy, blur, op; Color c;
            using (var flat = doc.Render())
                if (!Dlg.Shadow(this, flat, out dx, out dy, out blur, out c, out op)) return;
            doc.ShadowOrFrame((b, off) => { Point o; var r = Effects.DropShadow(b, dx, dy, blur, c, op, out o); off[0] = o; return r; }, true);
            canvas.ZoomFit(false);
        }

        void DoEdges()
        {
            bool t, r, b, l; int depth, tooth, style;
            using (var flat = doc.Render())
                if (!Dlg.Edges(this, flat, out t, out r, out b, out l, out depth, out tooth, out style)) return;
            doc.ApplyToFlattened(img => Effects.EdgeEffect(img, t, r, b, l, depth, tooth, style));
        }

        void DoRound()
        {
            int rad = 16;
            if (Dlg.Number(this, "Rounded corners", "Radius (px)", 1, 500, ref rad)) doc.ApplyToFlattened(b => Effects.RoundCorners(b, rad));
        }

        void DoWatermark()
        {
            string text; int size, op, anchor, margin; Color col; bool bold;
            using (var flat = doc.Render())
                if (!Dlg.Watermark(this, flat, out text, out size, out col, out op, out anchor, out margin, out bold)) return;
            doc.ApplyToFlattened(b => Effects.WatermarkText(b, text, "Segoe UI", size, col, op, anchor, margin, bold));
        }

        // ------------------------------------------------------------------ keyboard

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (canvas.IsEditing) return base.ProcessCmdKey(ref msg, keyData);
            switch (keyData)
            {
                case Keys.Control | Keys.Z: if (doc != null) doc.Undo(); return true;
                case Keys.Control | Keys.Y:
                case Keys.Control | Keys.Shift | Keys.Z: if (doc != null) doc.Redo(); return true;
                case Keys.Control | Keys.C: if (doc != null) CopySmart(); return true;
                case Keys.Control | Keys.X: if (doc != null) canvas.CutSelection(); return true;
                case Keys.Control | Keys.V: PasteSmart(); return true;
                case Keys.Control | Keys.A: canvas.SelectAll(); return true;
                case Keys.Control | Keys.D: canvas.DuplicateSelection(); return true;
                case Keys.Control | Keys.S: SaveQuick(); return true;
                case Keys.Control | Keys.N: NewImage(); return true;
                case Keys.Control | Keys.Shift | Keys.S: SaveAs(); return true;
                case Keys.Control | Keys.P: DoPrint(); return true;
                case Keys.Control | Keys.O: OpenFileDialog(); return true;
                case Keys.Control | Keys.D0: canvas.ZoomFit(true); return true;
                case Keys.Control | Keys.D1: canvas.SetZoom(1f, null); return true;
                case Keys.Control | Keys.Oemplus:
                case Keys.Control | Keys.Add: canvas.SetZoom(canvas.Zoom * 1.25f, null); return true;
                case Keys.Control | Keys.OemMinus:
                case Keys.Control | Keys.Subtract: canvas.SetZoom(canvas.Zoom / 1.25f, null); return true;
                case Keys.Delete: if (canvas.Selection.Count > 0) { canvas.DeleteSelection(); return true; } break;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }
    }

    /// <summary>Blue status bar: image size, selection, and the zoom slider.</summary>
    public class EditorStatus : Control
    {
        string size = "", sel = "", hint = "";
        float zoom = 1f;
        Rectangle plus, minus, slider, zoomText;
        bool dragging;
        readonly Timer hintTimer = new Timer { Interval = 4000 };

        public event Action<float> ZoomRequested;
        public event Action FitRequested;

        public EditorStatus()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Accent;
            hintTimer.Tick += (s, e) => { hintTimer.Stop(); hint = ""; Invalidate(); };
        }

        protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Height = (int)(23 * Theme.Scale(this)); }
        protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Height = (int)(23 * Theme.Scale(this)); }

        public void SetInfo(string size, string sel, float zoom) { this.size = size; this.sel = sel; this.zoom = zoom; Invalidate(); }
        public void Hint(string h) { hint = h; hintTimer.Stop(); hintTimer.Start(); Invalidate(); }

        static float ZoomToT(float z) { return (float)(Math.Log(z / 0.1) / Math.Log(80)); }
        static float TToZoom(float t) { return (float)(0.1 * Math.Pow(80, Math.Max(0, Math.Min(1, t)))); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            float s = Theme.Scale(this);
            g.Clear(Theme.Accent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // info icon
            var ic = new Rectangle((int)(8 * s), (int)((Height - 13 * s) / 2), (int)(13 * s), (int)(13 * s));
            using (var p = new Pen(Color.FromArgb(220, 255, 255, 255))) g.DrawEllipse(p, ic);
            TextRenderer.DrawText(g, "i", Font, ic, Color.White, TextFormatFlags.NoPadding | TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            int x = (int)(34 * s);
            TextRenderer.DrawText(g, size, Font, new Rectangle(x, 0, (int)(110 * s), Height), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            TextRenderer.DrawText(g, sel, Font, new Rectangle(x + (int)(120 * s), 0, (int)(200 * s), Height), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter);
            if (hint.Length > 0)
                TextRenderer.DrawText(g, hint, Font, new Rectangle(x + (int)(330 * s), 0, Width - (int)(600 * s), Height), Color.White, TextFormatFlags.NoPadding | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

            int right = Width - (int)(18 * s);
            plus = new Rectangle(right - (int)(16 * s), 0, (int)(16 * s), Height); right = plus.X - (int)(4 * s);
            slider = new Rectangle(right - (int)(110 * s), 0, (int)(110 * s), Height); right = slider.X - (int)(4 * s);
            minus = new Rectangle(right - (int)(16 * s), 0, (int)(16 * s), Height); right = minus.X - (int)(6 * s);
            zoomText = new Rectangle(right - (int)(60 * s), 0, (int)(60 * s), Height);
            TextRenderer.DrawText(g, (int)Math.Round(zoom * 100) + "% ▾", Font, zoomText, Color.White, TextFormatFlags.NoPadding | TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
            using (var p = new Pen(Color.White, 1.6f))
            {
                int cy = Height / 2;
                g.DrawLine(p, minus.X + 3 * s, cy, minus.Right - 3 * s, cy);
                g.DrawLine(p, plus.X + 3 * s, cy, plus.Right - 3 * s, cy); g.DrawLine(p, plus.X + plus.Width / 2f, cy - 5 * s, plus.X + plus.Width / 2f, cy + 5 * s);
                g.DrawLine(p, slider.X, cy, slider.Right, cy);
                float tx = slider.X + ZoomToT(zoom) * slider.Width;
                using (var b = new SolidBrush(Color.White)) g.FillRectangle(b, tx - 2 * s, cy - 7 * s, 4 * s, 14 * s);
            }
        }

        void SetFromX(int x)
        {
            float t = (float)(x - slider.X) / Math.Max(1, slider.Width);
            if (ZoomRequested != null) ZoomRequested(TToZoom(t));
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (slider.Contains(e.Location)) { dragging = true; SetFromX(e.X); }
            else if (plus.Contains(e.Location) && ZoomRequested != null) ZoomRequested(zoom * 1.25f);
            else if (minus.Contains(e.Location) && ZoomRequested != null) ZoomRequested(zoom / 1.25f);
            else if (zoomText.Contains(e.Location))
            {
                var m = Theme.Menu();
                foreach (var z in new[] { 25, 50, 75, 100, 150, 200, 400, 800 })
                {
                    int zz = z;
                    var it = new ToolStripMenuItem(z + "%") { ForeColor = Theme.Text };
                    it.Click += delegate { if (ZoomRequested != null) ZoomRequested(zz / 100f); };
                    m.Items.Add(it);
                }
                var fit = new ToolStripMenuItem(Loc.T("Fit to window")) { ForeColor = Theme.Text };
                fit.Click += delegate { if (FitRequested != null) FitRequested(); };
                m.Items.Add(fit);
                m.Show(this, new Point(zoomText.X, -m.Height + 0 > 0 ? 0 : 0), ToolStripDropDownDirection.AboveRight);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (dragging) SetFromX(e.X); }
        protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); dragging = false; }
    }
}
