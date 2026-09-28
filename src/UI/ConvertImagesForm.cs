using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>File > Convert Images: converts a batch of images (or .scp projects) to PNG / JPEG / BMP / GIF / TIFF / PDF.</summary>
    public class ConvertImagesForm : DarkForm
    {
        static readonly string[] Exts = { "png", "jpg", "bmp", "gif", "tif", "pdf" };
        readonly ListBox list = new ListBox { HorizontalScrollbar = true, SelectionMode = SelectionMode.MultiExtended };
        readonly ComboBox cbFormat = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 200 };
        readonly NumericUpDown nQuality = new NumericUpDown { Minimum = 1, Maximum = 100, Value = 92, Width = 70 };
        readonly RadioButton rbSame = new RadioButton { Text = Loc.T("Same folder as the source image"), Checked = true, AutoSize = true };
        readonly RadioButton rbFolder = new RadioButton { Text = Loc.T("Folder:"), AutoSize = true };
        readonly TextBox tbFolder = new TextBox { Enabled = false };
        readonly Button bBrowse = new Button { Text = "…", Width = 34, Enabled = false };
        readonly Label lblStatus = new Label { AutoSize = false };
        readonly ProgressBar bar = new ProgressBar { Minimum = 0, Maximum = 100 };
        readonly Button bConvert = new Button { Text = Loc.T("Convert") }, bClose = new Button { Text = Loc.T("Close"), DialogResult = DialogResult.Cancel };
        readonly List<string> files = new List<string>();

        public ConvertImagesForm()
        {
            Text = Loc.T("Convert Images");
            ClientSize = new Size(520, 430);
            CancelButton = bClose;

            Controls.Add(new Label { Text = Loc.T("Images to convert"), Left = 14, Top = 12, Width = 300, Height = 20 });
            list.SetBounds(14, 34, 400, 170);
            Controls.Add(list);
            var bAdd = new Button { Text = Loc.T("Add…"), Left = 424, Top = 34, Width = 82, Height = 28 };
            var bRemove = new Button { Text = Loc.T("Remove"), Left = 424, Top = 68, Width = 82, Height = 28 };
            var bClear = new Button { Text = Loc.T("Clear"), Left = 424, Top = 102, Width = 82, Height = 28 };
            Controls.Add(bAdd); Controls.Add(bRemove); Controls.Add(bClear);
            bAdd.Click += (s, e) => AddFiles();
            bRemove.Click += (s, e) => { var sel = new List<int>(); foreach (int i in list.SelectedIndices) sel.Add(i); sel.Sort(); for (int k = sel.Count - 1; k >= 0; k--) { files.RemoveAt(sel[k]); list.Items.RemoveAt(sel[k]); } UpdateUi(); };
            bClear.Click += (s, e) => { files.Clear(); list.Items.Clear(); UpdateUi(); };
            list.AllowDrop = true;
            list.DragEnter += (s, e) => { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            list.DragDrop += (s, e) => { var fs = e.Data.GetData(DataFormats.FileDrop) as string[]; if (fs != null) AddPaths(fs); };

            Controls.Add(new Label { Text = Loc.T("Convert to"), Left = 14, Top = 220, Width = 100, Height = 20 });
            cbFormat.Left = 120; cbFormat.Top = 216;
            foreach (var t in new[] { "PNG", "JPEG", "BMP", "GIF", "TIFF", "PDF" }) cbFormat.Items.Add(t);
            cbFormat.SelectedIndex = Math.Max(0, Array.IndexOf(Exts, AppSettings.Current.Format));
            Controls.Add(cbFormat);
            Controls.Add(new Label { Text = Loc.T("JPEG quality"), Left = 336, Top = 220, Width = 84, Height = 20 });
            nQuality.Left = 424; nQuality.Top = 216; nQuality.Value = Math.Max(1, Math.Min(100, AppSettings.Current.JpegQuality));
            Controls.Add(nQuality);
            cbFormat.SelectedIndexChanged += (s, e) => nQuality.Enabled = cbFormat.SelectedIndex == 1;
            nQuality.Enabled = cbFormat.SelectedIndex == 1;

            Controls.Add(new Label { Text = Loc.T("Save to"), Left = 14, Top = 258, Width = 100, Height = 20 });
            rbSame.Left = 120; rbSame.Top = 256;
            rbFolder.Left = 120; rbFolder.Top = 284;
            tbFolder.SetBounds(196, 282, 268, 24); tbFolder.Text = AppSettings.Current.SaveFolder;
            bBrowse.Left = 470; bBrowse.Top = 281; bBrowse.Height = 26;
            Controls.Add(rbSame); Controls.Add(rbFolder); Controls.Add(tbFolder); Controls.Add(bBrowse);
            rbFolder.CheckedChanged += (s, e) => { tbFolder.Enabled = bBrowse.Enabled = rbFolder.Checked; };
            bBrowse.Click += (s, e) =>
            {
                using (var fb = new FolderBrowserDialog { SelectedPath = tbFolder.Text }) if (fb.ShowDialog(this) == DialogResult.OK) tbFolder.Text = fb.SelectedPath;
            };

            bar.SetBounds(14, 330, 492, 16); Controls.Add(bar);
            lblStatus.SetBounds(14, 350, 492, 20); lblStatus.ForeColor = Theme.TextDim; Controls.Add(lblStatus);
            bConvert.SetBounds(316, 384, 92, 30); bClose.SetBounds(414, 384, 92, 30);
            Controls.Add(bConvert); Controls.Add(bClose);
            bConvert.Click += (s, e) => Convert();
            UpdateUi();
        }

        /// <summary>Used by the automated tests: adds the files, picks the format / folder, converts and returns the status line.</summary>
        public string TestRun(IEnumerable<string> paths, int formatIndex, string outDir)
        {
            AddPaths(paths);
            cbFormat.SelectedIndex = formatIndex;
            if (outDir != null) { rbFolder.Checked = true; tbFolder.Text = outDir; }
            Convert();
            return lblStatus.Text;
        }

        void UpdateUi()
        {
            bConvert.Enabled = files.Count > 0;
            lblStatus.Text = files.Count == 0 ? Loc.T("Add images or drop files here.") : files.Count + " " + Loc.T("file(s)");
        }

        void AddFiles()
        {
            using (var ofd = new OpenFileDialog { Multiselect = true, Filter = "Images and projects|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.ico;*.scp|All files|*.*" })
                if (ofd.ShowDialog(this) == DialogResult.OK) AddPaths(ofd.FileNames);
        }

        void AddPaths(IEnumerable<string> paths)
        {
            foreach (var p in paths)
                if (File.Exists(p) && !files.Contains(p)) { files.Add(p); list.Items.Add(p); }
            UpdateUi();
        }

        static Bitmap LoadImage(string path)
        {
            if (Path.GetExtension(path).ToLowerInvariant() == ".scp") return Document.LoadProject(path).Render();
            using (var im = Image.FromFile(path)) return Effects.ToArgb(im);
        }

        void Convert()
        {
            string ext = Exts[cbFormat.SelectedIndex];
            string outDir = rbFolder.Checked ? tbFolder.Text : null;
            var cfg = AppSettings.Current;
            int oldQ = cfg.JpegQuality; cfg.JpegQuality = (int)nQuality.Value;
            int ok = 0, fail = 0; string firstErr = null;
            bConvert.Enabled = false; bar.Value = 0;
            try
            {
                for (int i = 0; i < files.Count; i++)
                {
                    lblStatus.Text = Path.GetFileName(files[i]); lblStatus.Refresh();
                    try
                    {
                        string dir = outDir ?? Path.GetDirectoryName(files[i]);
                        string target = Path.Combine(dir, Path.GetFileNameWithoutExtension(files[i]) + "." + ext);
                        if (string.Equals(Path.GetFullPath(target), Path.GetFullPath(files[i]), StringComparison.OrdinalIgnoreCase))
                            target = Path.Combine(dir, Path.GetFileNameWithoutExtension(files[i]) + "_converted." + ext);
                        int n = 2;
                        while (File.Exists(target) && !string.Equals(Path.GetFullPath(target), Path.GetFullPath(files[i]), StringComparison.OrdinalIgnoreCase))
                            target = Path.Combine(dir, Path.GetFileNameWithoutExtension(files[i]) + "_" + (n++) + "." + ext);
                        using (var bmp = LoadImage(files[i])) Exporter.Save(bmp, target);
                        ok++;
                    }
                    catch (Exception ex) { fail++; if (firstErr == null) firstErr = Path.GetFileName(files[i]) + ": " + ex.Message; }
                    bar.Value = (i + 1) * 100 / files.Count;
                }
            }
            finally { cfg.JpegQuality = oldQ; bConvert.Enabled = true; }
            lblStatus.Text = Loc.T("Converted") + " " + ok + "/" + files.Count + (fail > 0 ? "  -  " + fail + " " + Loc.T("failed") + " (" + firstErr + ")" : "");
        }
    }
}
