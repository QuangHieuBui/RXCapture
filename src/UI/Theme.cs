using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>Dark colour scheme modelled on the Snagit 12 editor.</summary>
    public static class Theme
    {
        public static readonly Color Back = Color.FromArgb(30, 30, 30);         // canvas / tray
        public static readonly Color TabBar = Color.FromArgb(31, 31, 31);
        public static readonly Color Ribbon = Color.FromArgb(45, 45, 48);
        public static readonly Color Border = Color.FromArgb(63, 63, 70);
        public static readonly Color Text = Color.FromArgb(232, 232, 232);
        public static readonly Color TextDim = Color.FromArgb(160, 160, 165);
        public static readonly Color Hover = Color.FromArgb(68, 68, 73);
        public static readonly Color Checked = Color.FromArgb(86, 86, 92);
        public static readonly Color Field = Color.FromArgb(51, 51, 55);
        public static readonly Color Accent = Color.FromArgb(0, 114, 198);
        public static readonly Color AccentLight = Color.FromArgb(28, 151, 234);
        public static readonly Color Red = Color.FromArgb(220, 38, 38);

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

        public static void DarkTitle(Form f)
        {
            try
            {
                int v = 1;
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref v, 4);
                int caption = Back.R | (Back.G << 8) | (Back.B << 16);
                DwmSetWindowAttribute(f.Handle, 35, ref caption, 4);   // caption colour (Win11)
            }
            catch { }
        }

        public static void DarkScrollbars(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
        }

        public static float Scale(Control c) { return Math.Max(1f, c.DeviceDpi / 96f); }

        public static void Style(Control root)
        {
            foreach (Control c in root.Controls)
            {
                StyleOne(c);
                if (c.HasChildren) Style(c);
            }
        }

        static void StyleOne(Control c)
        {
            if (c is Button)
            {
                var b = (Button)c;
                b.FlatStyle = FlatStyle.Flat; b.BackColor = Color.FromArgb(62, 62, 66); b.ForeColor = Text;
                b.FlatAppearance.BorderColor = Color.FromArgb(90, 90, 96);
                b.FlatAppearance.MouseOverBackColor = Color.FromArgb(78, 78, 84);
                b.UseVisualStyleBackColor = false;
            }
            else if (c is TextBox || c is NumericUpDown || c is ListBox)
            {
                c.BackColor = Field; c.ForeColor = Text;
                if (c is TextBox) ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
            }
            else if (c is ComboBox)
            {
                var cb = (ComboBox)c;
                cb.BackColor = Field; cb.ForeColor = Text; cb.FlatStyle = FlatStyle.Standard;
                if (cb.DropDownStyle == ComboBoxStyle.DropDownList) new ComboSkin(cb);                  // the system paints a flat drop-down list clipped and light blue: paint the closed box ourselves
                else { EventHandler dark = delegate { try { SetWindowTheme(cb.Handle, "DarkMode_CFD", null); } catch { } }; cb.HandleCreated += dark; if (cb.IsHandleCreated) dark(cb, EventArgs.Empty); }
            }
            else if (c is CheckBox || c is RadioButton)
            {
                c.ForeColor = Text; c.BackColor = Color.Transparent;
            }
            else if (c is Label || c is GroupBox || c is LinkLabel)
            {
                c.ForeColor = c is LinkLabel ? AccentLight : Text; c.BackColor = Color.Transparent;
                if (c is LinkLabel) { ((LinkLabel)c).LinkColor = AccentLight; ((LinkLabel)c).ActiveLinkColor = Color.White; }
            }
            else if (c is TrackBar)
            {
                c.BackColor = Ribbon;
            }
            else if (c is Panel || c is FlowLayoutPanel || c is TableLayoutPanel)
            {
                if (c.BackColor == SystemColors.Control) c.BackColor = Ribbon;
                c.ForeColor = Text;
            }
            else if (c is TabControl || c is TabPage)
            {
                c.BackColor = Ribbon; c.ForeColor = Text;
            }
        }

        public static GraphicsPath RoundRect(Rectangle r, int rad)
        {
            var p = new GraphicsPath();
            int d = rad * 2;
            if (d <= 0 || r.Width < d || r.Height < d) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static ContextMenuStrip Menu()
        {
            var m = new ContextMenuStrip();
            m.Renderer = new DarkRenderer();
            m.ShowImageMargin = true;
            m.BackColor = Color.FromArgb(37, 37, 38);
            m.ForeColor = Text;
            return m;
        }

        public static ToolStripMenuItem Item(string text, string icon, EventHandler click)
        {
            var it = new ToolStripMenuItem(Loc.T(text));
            if (icon != null) it.Image = Icons.Get(icon, 16, true);
            if (click != null) it.Click += click;
            it.ForeColor = Text;
            return it;
        }
    }

    public class DarkColors : ProfessionalColorTable
    {
        public override Color MenuItemSelected { get { return Color.FromArgb(70, 70, 76); } }
        public override Color MenuItemSelectedGradientBegin { get { return Color.FromArgb(70, 70, 76); } }
        public override Color MenuItemSelectedGradientEnd { get { return Color.FromArgb(70, 70, 76); } }
        public override Color MenuItemBorder { get { return Color.FromArgb(90, 90, 96); } }
        public override Color MenuBorder { get { return Color.FromArgb(70, 70, 75); } }
        public override Color ToolStripDropDownBackground { get { return Color.FromArgb(37, 37, 38); } }
        public override Color ImageMarginGradientBegin { get { return Color.FromArgb(37, 37, 38); } }
        public override Color ImageMarginGradientMiddle { get { return Color.FromArgb(37, 37, 38); } }
        public override Color ImageMarginGradientEnd { get { return Color.FromArgb(37, 37, 38); } }
        public override Color SeparatorDark { get { return Color.FromArgb(70, 70, 75); } }
        public override Color SeparatorLight { get { return Color.FromArgb(70, 70, 75); } }
        public override Color CheckBackground { get { return Color.FromArgb(0, 90, 158); } }
        public override Color CheckSelectedBackground { get { return Color.FromArgb(0, 100, 170); } }
        public override Color CheckPressedBackground { get { return Color.FromArgb(0, 100, 170); } }
        public override Color MenuItemPressedGradientBegin { get { return Color.FromArgb(60, 60, 64); } }
        public override Color MenuItemPressedGradientEnd { get { return Color.FromArgb(60, 60, 64); } }
    }

    public class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Color.FromArgb(110, 110, 115);
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Text;
            base.OnRenderArrow(e);
        }
    }

    /// <summary>Paints the closed box of a drop-down list in the dark theme. Windows draws it clipped and light blue (flat style) or with a
    /// white button (standard style); the list that opens below is left to Windows.</summary>
    public class ComboSkin : NativeWindow
    {
        const int WM_PAINT = 0x000F, WM_ERASEBKGND = 0x0014;
        [StructLayout(LayoutKind.Sequential)] struct PAINTSTRUCT { public IntPtr hdc; public bool fErase; public int l, t, r, b; public bool fRestore, fIncUpdate; [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] rgb; }
        [DllImport("user32.dll")] static extern IntPtr BeginPaint(IntPtr h, out PAINTSTRUCT ps);
        [DllImport("user32.dll")] static extern bool EndPaint(IntPtr h, ref PAINTSTRUCT ps);
        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr h, string app, string list);

        readonly ComboBox cb;
        bool hot;

        public ComboSkin(ComboBox combo)
        {
            cb = combo;
            cb.MouseEnter += delegate { hot = true; cb.Invalidate(); };
            cb.MouseLeave += delegate { hot = false; cb.Invalidate(); };
            cb.GotFocus += delegate { cb.Invalidate(); };
            cb.LostFocus += delegate { cb.Invalidate(); };
            cb.DropDown += delegate { cb.Invalidate(); };
            cb.DropDownClosed += delegate { cb.Invalidate(); };
            cb.SelectedIndexChanged += delegate { cb.Invalidate(); };
            cb.HandleCreated += delegate { Attach(); };
            cb.HandleDestroyed += delegate { ReleaseHandle(); };
            if (cb.IsHandleCreated) Attach();
        }

        void Attach()
        {
            AssignHandle(cb.Handle);
            try { SetWindowTheme(cb.Handle, "DarkMode_CFD", null); } catch { }     // the list that opens: dark scrollbar
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND) { m.Result = (IntPtr)1; return; }
            if (m.Msg == WM_PAINT)
            {
                PAINTSTRUCT ps;
                IntPtr hdc = BeginPaint(m.HWnd, out ps);
                try { using (var g = Graphics.FromHdc(hdc)) Paint(g); }
                finally { EndPaint(m.HWnd, ref ps); }
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        void Paint(Graphics g)
        {
            var r = cb.ClientRectangle;
            bool active = cb.Focused || cb.DroppedDown;
            using (var bg = new SolidBrush(Theme.Field)) g.FillRectangle(bg, r);
            var border = active ? Theme.AccentLight : (hot ? Theme.TextDim : Color.FromArgb(110, 110, 116));
            using (var p = new Pen(border)) g.DrawRectangle(p, 0, 0, r.Width - 1, r.Height - 1);

            string s = cb.SelectedItem == null ? cb.Text : cb.GetItemText(cb.SelectedItem);
            var tr = new Rectangle(6, 1, Math.Max(0, r.Width - 28), r.Height - 2);
            TextRenderer.DrawText(g, s, cb.Font, tr, cb.Enabled ? Theme.Text : Theme.TextDim,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            int x = r.Width - 13, y = r.Height / 2;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(active ? Theme.Text : Theme.TextDim, 1.4f)) g.DrawLines(p, new[] { new Point(x - 4, y - 2), new Point(x, y + 2), new Point(x + 4, y - 2) });
        }
    }

    /// <summary>Base class for dialogs so they share the dark look.</summary>
    public class DarkForm : Form
    {
        public DarkForm()
        {
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 9f);
            BackColor = Theme.Ribbon;
            ForeColor = Theme.Text;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitle(this);
        }

        protected override void OnLoad(EventArgs e)
        {
            Theme.Style(this);
            base.OnLoad(e);
        }
    }
}
