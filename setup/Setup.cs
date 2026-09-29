using System;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("RXCapture Setup")]
[assembly: AssemblyDescription("Installer for RXCapture")]

namespace RXCaptureSetup
{
    /// <summary>One executable that installs RXCapture and, copied into the install folder as Uninstall.exe, removes it again.
    /// The application files travel inside this exe as the "payload.zip" resource. Per-user install, no administrator rights.</summary>
    static class Program
    {
        const string AppName = "RXCapture", ExeName = "RXCapture.exe", UninstallExe = "Uninstall.exe";
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RXCapture";
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        static readonly bool Vi = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi";
        internal static string T(string en, string vi) { return Vi ? vi : en; }

        internal static string Version { get { return Assembly.GetExecutingAssembly().GetName().Version.ToString(3); } }

        internal static string DefaultDir
        {
            get
            {
                // installing again over an existing copy keeps its folder
                try { using (var k = Registry.CurrentUser.OpenSubKey(UninstallKey)) { var v = k == null ? null : k.GetValue("InstallLocation") as string; if (!string.IsNullOrEmpty(v)) return v; } } catch { }
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppName);
            }
        }

        static string StartMenuLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppName + ".lnk"); } }
        static string DesktopLink { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppName + ".lnk"); } }

        static bool Has(string[] args, string name)
        {
            foreach (var a in args) if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string ArgValue(string[] args, string prefix)
        {
            foreach (var a in args)
                if (a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return a.Substring(prefix.Length).Trim('"');
            return null;
        }

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool silent = Has(args, "/S");
            if (Has(args, "/uninstall")) return Uninstall(args, silent);
            string dir = ArgValue(args, "/D=") ?? DefaultDir;
            if (silent)
            {
                try { Install(dir, !Has(args, "/nodesktop"), Has(args, "/startup") ? true : (bool?)null); if (Has(args, "/launch")) Launch(dir); return 0; }
                catch (Exception ex) { Console.Error.WriteLine(ex.Message); return 1; }
            }
            Application.Run(new SetupForm(dir));
            return 0;
        }

        // ------------------------------------------------------------------ install

        /// <param name="startWithWindows">true: start with Windows, false: do not, null: leave the current choice alone.</param>
        internal static string Install(string dir, bool desktopShortcut, bool? startWithWindows)
        {
            dir = Path.GetFullPath(dir.Trim().Trim('"'));
            if (Path.GetPathRoot(dir).Equals(dir, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException(T("Please choose a folder, not a whole drive.", "Hãy chọn một thư mục, không phải cả ổ đĩa."));
            StopRunning(dir);
            Directory.CreateDirectory(dir);

            string full = dir + Path.DirectorySeparatorChar;
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(s, ZipArchiveMode.Read))
                foreach (var e in zip.Entries)
                {
                    if (e.Name.Length == 0) continue;                                      // folder entry
                    string dest = Path.GetFullPath(Path.Combine(dir, e.FullName));
                    if (!dest.StartsWith(full, StringComparison.OrdinalIgnoreCase)) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(dest));
                    WithRetry(() => e.ExtractToFile(dest, true));
                }

            // this very exe becomes the uninstaller
            string self = Application.ExecutablePath, un = Path.Combine(dir, UninstallExe);
            if (!string.Equals(Path.GetFullPath(self), un, StringComparison.OrdinalIgnoreCase)) WithRetry(() => File.Copy(self, un, true));

            string exe = Path.Combine(dir, ExeName);
            MakeShortcut(StartMenuLink, exe, dir, T("Screen capture and image editor", "Chụp màn hình và chỉnh sửa ảnh"));
            if (desktopShortcut) MakeShortcut(DesktopLink, exe, dir, T("Screen capture and image editor", "Chụp màn hình và chỉnh sửa ảnh"));
            else TryDelete(DesktopLink);
            if (startWithWindows.HasValue) SetStartup(exe, startWithWindows.Value);

            long bytes = 0;
            foreach (var f in Directory.GetFiles(dir, "*", SearchOption.AllDirectories)) bytes += new FileInfo(f).Length;
            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppName);
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", AppName);
                k.SetValue("InstallLocation", dir);
                k.SetValue("DisplayIcon", exe + ",0");
                k.SetValue("UninstallString", "\"" + un + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + un + "\" /uninstall /S");
                k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            return exe;
        }

        internal static bool IsStartupOn()
        {
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey)) return k != null && k.GetValue(AppName) != null; } catch { return false; }
        }

        static void SetStartup(string exe, bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(AppName, "\"" + exe + "\" --minimized");
                    else k.DeleteValue(AppName, false);
                }
            }
            catch { }   // start-up is optional: never fail the installation over it
        }

        internal static void Launch(string dir)
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(dir, ExeName)) { WorkingDirectory = dir, UseShellExecute = true }); } catch { }
        }

        static void WithRetry(Action a)
        {
            for (int i = 0; ; i++)
            {
                try { a(); return; }
                catch (IOException) { if (i >= 8) throw; System.Threading.Thread.Sleep(300); }      // the old exe may still be closing
                catch (UnauthorizedAccessException) { if (i >= 8) throw; System.Threading.Thread.Sleep(300); }
            }
        }

        /// <summary>Ends the RXCapture that runs from <paramref name="dir"/> (copies started from elsewhere are left alone).</summary>
        static void StopRunning(string dir)
        {
            string exe = Path.Combine(dir, ExeName);
            foreach (var p in Process.GetProcessesByName("RXCapture"))
            {
                try
                {
                    string path = p.MainModule.FileName;
                    if (!string.Equals(path, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    p.Kill(); p.WaitForExit(4000);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }

        static void MakeShortcut(string lnk, string target, string workDir, string description)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lnk));
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                object shell = Activator.CreateInstance(t);
                object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
                Type st = sc.GetType();
                st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { target });
                st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { workDir });
                st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { description });
                st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { target + ",0" });
                st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
            }
            catch { }   // a missing shortcut must not fail the installation
        }

        static void TryDelete(string file) { try { if (File.Exists(file)) File.Delete(file); } catch { } }

        /// <summary>The temporary uninstaller copy deletes itself once it has exited.</summary>
        static void SelfDelete(string exe)
        {
            try { Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 >nul & del /f /q \"" + exe + "\"") { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden }); } catch { }
        }

        // ------------------------------------------------------------------ uninstall

        static int Uninstall(string[] args, bool silent)
        {
            string self = Application.ExecutablePath;
            string dir = ArgValue(args, "/_dir=");
            if (dir == null)
            {
                // started from the install folder: continue from a temporary copy so the folder itself can be deleted
                string tmp = Path.Combine(Path.GetTempPath(), "rxcapture-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 6) + ".exe");
                File.Copy(self, tmp, true);
                string folder = Path.GetDirectoryName(self);
                Process.Start(new ProcessStartInfo(tmp, "/uninstall /_dir=\"" + folder + "\"" + (silent ? " /S" : "") + (Has(args, "/removedata") ? " /removedata" : "")) { UseShellExecute = false });
                return 0;
            }

            bool removeData;
            if (!silent)
            {
                if (MessageBox.Show(T("Remove RXCapture from this computer?", "Gỡ RXCapture khỏi máy tính này?"), AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { SelfDelete(self); return 0; }
                removeData = MessageBox.Show(T("Also delete your settings and the library of captures (screenshots and videos kept inside RXCapture)?\r\n\r\nFiles you saved to other folders are not touched.",
                                                "Xóa luôn cài đặt và thư viện ảnh/video của RXCapture?\r\n\r\nCác tệp bạn đã lưu ở thư mục khác sẽ không bị đụng tới."),
                    AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) == DialogResult.Yes;
            }
            else removeData = Has(args, "/removedata");

            StopRunning(dir);
            TryDelete(StartMenuLink); TryDelete(DesktopLink);
            try { using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true)) { if (k != null) k.DeleteValue(AppName, false); } } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            for (int i = 0; i < 10 && Directory.Exists(dir); i++)
            {
                try { Directory.Delete(dir, true); } catch { System.Threading.Thread.Sleep(300); }
            }
            if (removeData)
            {
                foreach (var root in new[] { Environment.SpecialFolder.ApplicationData, Environment.SpecialFolder.LocalApplicationData })
                    try { string d = Path.Combine(Environment.GetFolderPath(root), AppName); if (Directory.Exists(d)) Directory.Delete(d, true); } catch { }
            }

            // this temporary copy deletes itself once it has exited
            SelfDelete(self);
            if (!silent) MessageBox.Show(T("RXCapture has been removed.", "Đã gỡ RXCapture."), AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }
    }

    /// <summary>The one-page installer window.</summary>
    class SetupForm : Form
    {
        readonly TextBox txtDir = new TextBox();
        readonly Button btnBrowse = new Button(), btnInstall = new Button(), btnCancel = new Button();
        readonly CheckBox chkDesktop = new CheckBox(), chkStartup = new CheckBox(), chkLaunch = new CheckBox();
        readonly Label lblTitle = new Label(), lblSub = new Label(), lblDir = new Label(), lblNote = new Label();
        string installedDir;

        static string T(string en, string vi) { return Program.T(en, vi); }

        public SetupForm(string dir)
        {
            AutoScaleDimensions = new SizeF(96f, 96f); AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9.5f);
            Text = "RXCapture " + Program.Version + " - " + T("Setup", "Cài đặt");
            FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 402);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = Color.FromArgb(37, 37, 40) };
            var logo = new PictureBox { SizeMode = PictureBoxSizeMode.Zoom, Bounds = new Rectangle(22, 20, 52, 52), BackColor = Color.Transparent };
            try { logo.Image = Icon.ExtractAssociatedIcon(Application.ExecutablePath).ToBitmap(); } catch { }
            lblTitle.Text = "RXCapture " + Program.Version; lblTitle.ForeColor = Color.White; lblTitle.Font = new Font("Segoe UI", 16f, FontStyle.Bold);
            lblTitle.AutoSize = false; lblTitle.Bounds = new Rectangle(88, 16, 450, 34);
            lblSub.Text = T("Screen capture, image editor and screen recorder", "Chụp màn hình, chỉnh sửa ảnh và quay video màn hình");
            lblSub.ForeColor = Color.FromArgb(200, 200, 205); lblSub.AutoSize = false; lblSub.Bounds = new Rectangle(90, 52, 450, 24);
            header.Controls.AddRange(new Control[] { logo, lblTitle, lblSub });

            lblDir.Text = T("Install location", "Thư mục cài đặt"); lblDir.AutoSize = true; lblDir.Location = new Point(22, 116);
            txtDir.Text = dir; txtDir.Bounds = new Rectangle(22, 140, 420, 26);
            btnBrowse.Text = T("Browse…", "Chọn…"); btnBrowse.Bounds = new Rectangle(452, 138, 86, 30);
            btnBrowse.Click += (s, e) =>
            {
                using (var f = new FolderBrowserDialog { SelectedPath = txtDir.Text, Description = T("Choose the folder to install RXCapture into", "Chọn thư mục để cài RXCapture") })
                    if (f.ShowDialog(this) == DialogResult.OK) txtDir.Text = Path.Combine(f.SelectedPath, f.SelectedPath.EndsWith("RXCapture", StringComparison.OrdinalIgnoreCase) ? "" : "RXCapture");
            };

            chkDesktop.Text = T("Create a desktop shortcut", "Tạo biểu tượng ở màn hình nền"); chkDesktop.Checked = true; chkDesktop.AutoSize = true; chkDesktop.Location = new Point(22, 188);
            chkLaunch.Text = T("Start RXCapture when setup finishes", "Chạy RXCapture khi cài đặt xong"); chkLaunch.Checked = true; chkLaunch.AutoSize = true; chkLaunch.Location = new Point(22, 248);
            chkStartup.Text = T("Start RXCapture when Windows starts", "Chạy RXCapture cùng Windows"); chkStartup.Checked = Program.IsStartupOn(); chkStartup.AutoSize = true; chkStartup.Location = new Point(22, 218);
            lblNote.Text = T("Installs for the current user only - no administrator rights needed. A Start menu shortcut is always created. Your captures and settings are kept when you update or uninstall (uninstall asks first).",
                             "Chỉ cài cho tài khoản hiện tại - không cần quyền quản trị. Luôn có biểu tượng trong menu Start. Ảnh chụp và cài đặt của bạn được giữ khi cập nhật hoặc gỡ (khi gỡ sẽ hỏi trước).");
            lblNote.ForeColor = Color.FromArgb(90, 90, 95); lblNote.AutoSize = false; lblNote.Bounds = new Rectangle(22, 284, 516, 54);

            btnInstall.Text = T("Install", "Cài đặt"); btnInstall.Bounds = new Rectangle(340, 352, 96, 34); btnInstall.Click += (s, e) => DoInstall();
            btnCancel.Text = T("Cancel", "Hủy"); btnCancel.Bounds = new Rectangle(442, 352, 96, 34); btnCancel.Click += (s, e) => Close();
            AcceptButton = btnInstall; CancelButton = btnCancel;

            Controls.AddRange(new Control[] { header, lblDir, txtDir, btnBrowse, chkDesktop, chkStartup, chkLaunch, lblNote, btnInstall, btnCancel });
        }

        void DoInstall()
        {
            if (installedDir != null) { if (chkLaunch.Checked) Program.Launch(installedDir); Close(); return; }   // "Finish"
            SetBusy(true);
            try
            {
                Application.DoEvents();
                Program.Install(txtDir.Text, chkDesktop.Checked, chkStartup.Checked);
                installedDir = Path.GetFullPath(txtDir.Text.Trim().Trim('"'));
                lblTitle.Text = T("RXCapture is installed", "Đã cài xong RXCapture");
                lblSub.Text = installedDir;
                txtDir.Visible = btnBrowse.Visible = lblDir.Visible = chkDesktop.Visible = chkStartup.Visible = false;
                lblNote.Text = T("Press Print Screen to capture. RXCapture lives in the system tray; open Settings there to change hotkeys and options.",
                                 "Nhấn Print Screen để chụp. RXCapture nằm ở khay hệ thống; mở Settings ở đó để đổi phím tắt và tùy chọn.");
                btnInstall.Text = T("Finish", "Hoàn tất"); btnCancel.Visible = false;
                SetBusy(false);
            }
            catch (Exception ex)
            {
                SetBusy(false);
                MessageBox.Show(this, ex.Message, "RXCapture", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void SetBusy(bool busy)
        {
            btnInstall.Enabled = btnCancel.Enabled = btnBrowse.Enabled = txtDir.Enabled = chkDesktop.Enabled = chkStartup.Enabled = chkLaunch.Enabled = !busy;
            Cursor = busy ? Cursors.WaitCursor : Cursors.Default;
        }
    }
}
