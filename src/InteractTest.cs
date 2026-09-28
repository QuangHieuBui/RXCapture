using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;

namespace RXCapture
{
    /// <summary>Drives the canvas with synthetic mouse messages (RXCapture.exe --interact [log]) to verify every tool.</summary>
    static class InteractTest
    {
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        static CanvasControl c;
        static Document d;
        static StringBuilder log = new StringBuilder();
        static int failed;
        static string logPathG;

        static IntPtr LP(Point p) { return (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)); }
        static Point S(float x, float y) { return Point.Round(c.ToScreenF(new PointF(x, y))); }

        static void Down(Point p) { SendMessage(c.Handle, 0x200, IntPtr.Zero, LP(p)); SendMessage(c.Handle, 0x201, (IntPtr)1, LP(p)); Application.DoEvents(); }
        static void Move(Point p) { SendMessage(c.Handle, 0x200, (IntPtr)1, LP(p)); Application.DoEvents(); }
        static void Up(Point p) { SendMessage(c.Handle, 0x202, IntPtr.Zero, LP(p)); Application.DoEvents(); }

        static void Drag(float x1, float y1, float x2, float y2)
        {
            var a = S(x1, y1); var b = S(x2, y2);
            Down(a);
            int steps = 6;
            for (int i = 1; i <= steps; i++) Move(new Point(a.X + (b.X - a.X) * i / steps, a.Y + (b.Y - a.Y) * i / steps));
            Up(b);
        }

        static void Click(float x, float y) { var p = S(x, y); Down(p); Up(p); }

        static void Expect(string name, bool ok, string info = "")
        {
            log.AppendLine((ok ? "PASS " : "FAIL ") + name + (info.Length > 0 ? "  [" + info + "]" : ""));
            if (!ok) failed++;
        }

        static void TypeText(string text)
        {
            var tb = c.Controls.OfType<TextBox>().FirstOrDefault();
            if (tb == null) return;
            tb.Text = text;
            Application.DoEvents();
        }

        public static int Run(string logPath)
        {
            logPathG = logPath;
            var ed = new EditorForm();
            ed.StartPosition = FormStartPosition.Manual; ed.Location = new Point(30, 20); ed.Size = new Size(1500, 900);
            ed.Show();
            for (int i = 0; i < 20; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }

            Document doc0;
            var it = LibraryStore.AddImage(SelfTest.Sample(900, 600), out doc0);
            ed.OpenNew(it, doc0);
            c = ed.Canvas; d = ed.Doc;
            c.SetZoom(1f, null);
            Application.DoEvents();
            try { ClickThrough(ed, true); Sequence(ed); Overlap(ed); ClickThrough(ed, false); RealMouse(ed); }
            catch (Exception ex) { failed++; log.AppendLine("FAIL exception: " + ex); }

            // final screenshot
            try
            {
                for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                using (var b = ScreenGrabber.Grab(ed.Bounds)) b.Save(Path.ChangeExtension(logPath ?? "interact.log", ".png"), ImageFormat.Png);
            }
            catch { }
            LibraryStore.Delete(it);
            log.AppendLine(failed == 0 ? "ALL INTERACTION TESTS PASSED" : failed + " FAILED");
            File.WriteAllText(logPath ?? "interact.log", log.ToString());
            ed.Close();
            return failed == 0 ? 0 : 1;
        }

        static Ann Filled(AnnKind k, float x1, float y1, float x2, float y2, Color fill)
        {
            var a = Ann.Create(k); a.P1 = new PointF(x1, y1); a.P2 = new PointF(x2, y2); a.Fill = fill; a.Width = 3; return a;
        }

        static Ann Picked() { return c.Selection.Count == 1 ? c.Selection[0] : null; }

        /// <summary>Overlapping objects: hit priority, z-order, drawing tools and moving must all respect the stacking order.</summary>
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")] static extern int SetWindowLong(IntPtr h, int i, int v);
        [DllImport("user32.dll")] static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

        /// <summary>
        /// While the synthetic-message tests run, let the physical mouse pass through the editor window, so a real mouse
        /// move by whoever is at the keyboard cannot slip into a drag that the test is simulating.
        /// </summary>
        static void ClickThrough(EditorForm ed, bool on)
        {
            const int GWL_EXSTYLE = -20, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20;
            int ex = GetWindowLong(ed.Handle, GWL_EXSTYLE);
            ex = on ? (ex | WS_EX_LAYERED | WS_EX_TRANSPARENT) : (ex & ~WS_EX_TRANSPARENT);
            SetWindowLong(ed.Handle, GWL_EXSTYLE, ex);
            if (on) SetLayeredWindowAttributes(ed.Handle, 0, 255, 2);
            Pump(100);
        }

        // ---- real mouse events (not messages posted to the canvas): they go through the same window hit-testing a user's mouse does
        [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] static extern void mouse_event(uint f, int dx, int dy, uint d, IntPtr e);

        static void Pump(int ms) { var t = DateTime.Now; while ((DateTime.Now - t).TotalMilliseconds < ms) { Application.DoEvents(); System.Threading.Thread.Sleep(10); } }

        static void RealDrag(Point a, Point b)
        {
            SetCursorPos(a.X, a.Y); Pump(60);
            mouse_event(2, 0, 0, 0, IntPtr.Zero); Pump(40);
            for (int i = 1; i <= 8; i++) { SetCursorPos(a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8); Pump(25); }
            mouse_event(4, 0, 0, 0, IntPtr.Zero); Pump(80);
        }

        static Point Scr(float x, float y) { return c.PointToScreen(S(x, y)); }

        static void RealMouse(EditorForm ed)
        {
            ed.TopMost = true; ed.Activate(); Pump(300);
            log.AppendLine("INFO real mouse: editor " + ed.Bounds + " state " + ed.WindowState + ", canvas screen rect " + c.RectangleToScreen(c.ClientRectangle) + ", press point " + Scr(560, 470) + ", zoom " + c.Zoom);
            c.SetSelection(new List<Ann>()); c.CurrentTool = Tool.Callout;
            RealDrag(Scr(560, 470), Scr(780, 560));
            Expect("real mouse: callout created and editing", c.IsEditing && d.Items.Last().Kind == AnnKind.Callout, "items " + d.Items.Count);
            TypeText("Hello"); c.CommitEdit(); Pump(100);
            var co = d.Items.Last();
            var r0 = co.Rect; var t0 = co.Tail;
            Expect("real mouse: created callout keeps its tail tip at the press point", Math.Abs(t0.X - 560) < 14 && Math.Abs(t0.Y - 470) < 14, "tail " + t0 + " rect " + r0);
            // move it with the Select tool
            c.CurrentTool = Tool.Select;
            RealDrag(Scr(r0.X + r0.Width / 2, r0.Y + r0.Height / 2), Scr(r0.X + r0.Width / 2 + 90, r0.Y + r0.Height / 2 + 60));
            var co2 = d.Items.Last();
            Expect("real mouse: Select tool drags the callout", Math.Abs(co2.Rect.X - (r0.X + 90)) < 4 && Math.Abs(co2.Rect.Y - (r0.Y + 60)) < 4, "rect " + r0 + " -> " + co2.Rect);
            // move it again while the Callout tool is still active
            c.CurrentTool = Tool.Callout;
            var r1 = co2.Rect; int cnt = d.Items.Count;
            RealDrag(Scr(r1.X + r1.Width / 2, r1.Y + r1.Height / 2), Scr(r1.X + r1.Width / 2 - 40, r1.Y + r1.Height / 2 + 30));
            var co3 = d.Items.Last();
            Expect("real mouse: Callout tool drags an existing callout too", d.Items.Count == cnt && Math.Abs(co3.Rect.X - (r1.X - 40)) < 4 && Math.Abs(co3.Rect.Y - (r1.Y + 30)) < 4, "rect " + r1 + " -> " + co3.Rect + ", items " + d.Items.Count + " vs " + cnt);
            d.Items.Remove(d.Items.Last()); d.Raise(); c.SetSelection(new List<Ann>());
        }

        static void Overlap(EditorForm ed)
        {
            Document nd; var it = LibraryStore.AddImage(SelfTest.Sample(700, 500), out nd);
            ed.OpenNew(it, nd); d = ed.Doc; c.SetZoom(1f, null); c.CurrentTool = Tool.Select;
            var A = Filled(AnnKind.Shape, 100, 100, 300, 250, Color.FromArgb(255, 60, 90, 220));    // bottom
            var B = Filled(AnnKind.Shape, 200, 150, 400, 300, Color.FromArgb(255, 60, 180, 90));    // overlaps A
            var C = Ann.Create(AnnKind.Step); C.P1 = new PointF(233, 183); C.P2 = new PointF(267, 217);   // sits on A and B
            var D = Ann.Create(AnnKind.Callout); D.P1 = new PointF(350, 260); D.P2 = new PointF(500, 330); D.Tail = new PointF(380, 380); D.Text = "x";   // overlaps B corner
            d.Push(); d.Items.Add(A); d.Items.Add(B); d.Items.Add(C); d.Items.Add(D); d.Raise();

            Click(120, 120); Expect("overlap: only A under the point -> A", Picked() == A);
            Click(250, 170); Expect("overlap: A and B under the point -> upper B", Picked() == B);
            Click(250, 200); Expect("overlap: step sits above both -> step", Picked() == C);
            Click(360, 285); Expect("overlap: callout over B corner -> callout", Picked() == D);
            Click(390, 200); Expect("overlap: right part of B (no A) -> B", Picked() == B);

            // z-order: send B to back, the same spot must now pick A
            Click(390, 200); c.ChangeOrder(1);
            Click(250, 170); Expect("z-order: after sending B to back the same point picks A", Picked() == A);
            c.ChangeOrder(0);   // bring A to front
            Click(250, 170); Expect("z-order: A brought to front -> A", Picked() == A);
            d.Undo(); d.Undo();
            A = d.Items[0]; B = d.Items[1]; C = d.Items[2]; D = d.Items[3];   // undo restores clones

            // hollow shape on top: interior click must fall through, outline click must hit it
            var H = Filled(AnnKind.Shape, 140, 120, 280, 235, Color.Transparent);
            d.Push(); d.Items.Add(H); d.Raise();
            Click(160, 200); var inner = Picked();
            Expect("hollow shape: interior click falls through to the object below", inner != null && inner != H, inner == null ? "none" : inner.Kind.ToString());
            Click(210, 120); Expect("hollow shape: clicking its outline selects it", Picked() == H);

            // with a drawing tool active the same priority applies and only the top object moves
            c.CurrentTool = Tool.Callout;
            var pb = C.P1; var pa = A.P1; var pbB = B.P1;
            Drag(250, 200, 270, 215);
            Expect("overlap + drawing tool: drag picks and moves only the top object (step)", Math.Abs(C.P1.X - (pb.X + 20)) < 3 && A.P1 == pa && B.P1 == pbB && d.Items.Count == 5, "step dx=" + (C.P1.X - pb.X));
            c.CurrentTool = Tool.Arrow;
            Drag(390, 200, 395, 205);
            Expect("overlap + arrow tool: drag on B moves B, not a new arrow", d.Items.Count == 5 && Math.Abs(B.P1.X - (pbB.X + 5)) < 3, "B dx=" + (B.P1.X - pbB.X));
            c.CurrentTool = Tool.Blur;
            Drag(120, 120, 130, 125);
            Expect("overlap + blur tool: drag on A moves A", d.Items.Count == 5 && Math.Abs(A.P1.X - (pa.X + 10)) < 3, "A dx=" + (A.P1.X - pa.X));

            // marquee over everything selects all; duplicate creates offset copies on top
            c.CurrentTool = Tool.Select;
            Drag(20, 20, 600, 450);
            Expect("overlap: marquee selects all five", c.Selection.Count == 5, c.Selection.Count + " selected");
            c.DuplicateSelection();
            Expect("overlap: duplicate makes 5 more, on top, selected", d.Items.Count == 10 && c.Selection.Count == 5 && d.Items.IndexOf(c.Selection[0]) >= 5);
            var dupTop = d.Items[8];   // copy of the callout
            c.SetSelection(new List<Ann>());
            Click(dupTop.Rect.X + dupTop.Rect.Width / 2, dupTop.Rect.Y + dupTop.Rect.Height / 2);
            Expect("overlap: clicking where original and copy overlap picks the copy (upper)", Picked() != null && d.Items.IndexOf(Picked()) >= 5);
            d.Undo(); d.Undo();
            Expect("overlap: undo restores the stack", d.Items.Count >= 5);
            LibraryStore.Delete(it);
        }

        static void Sequence(EditorForm ed)
        {
            // --- drawing tools
            c.CurrentTool = Tool.Arrow; Drag(80, 80, 300, 200);
            Expect("arrow created", d.Items.Count == 1 && d.Items[0].Kind == AnnKind.Arrow && Math.Abs(d.Items[0].P2.X - 300) < 3, d.Items.Count + " items");

            c.CurrentTool = Tool.Shape; Drag(350, 60, 600, 160);
            Expect("shape created", d.Items.Count == 2 && d.Items[1].Kind == AnnKind.Shape);

            c.CurrentTool = Tool.Line; Drag(50, 400, 250, 420);
            Expect("line created", d.Items.Count == 3);

            c.CurrentTool = Tool.Step; Click(700, 100); Click(760, 100);
            Expect("steps auto-number", d.Items.Count == 5 && d.Items[3].Number == 1 && d.Items[4].Number == 2, d.Items.Count + " items");
            {
                var s1 = d.Items[3];
                Expect("default step size is 40 px", Math.Abs(s1.Rect.Width - 40) < 0.5f && Math.Abs(s1.Rect.Height - 40) < 0.5f, s1.Rect.ToString());
                d.RenumberFrom(s1, 10, true);
                Expect("edit value: the following steps continue from the new number", d.Items[3].Number == 10 && d.Items[4].Number == 11, d.Items[3].Number + "," + d.Items[4].Number);
                d.RestartSequenceAt(d.Items[4]);
                Expect("restart sequence: the chosen step becomes 1", d.Items[4].Number == 1 && d.Items[3].Number == 10);
                d.Undo(); d.Undo();
                Expect("renumbering can be undone", d.Items[3].Number == 1 && d.Items[4].Number == 2, d.Items[3].Number + "," + d.Items[4].Number);
                d.StepNext = 1;
                c.CurrentTool = Tool.Step; Click(820, 100); Click(880, 100);
                Expect("restart sequence: the next step added is 1 and it counts on from there", d.Items[d.Items.Count - 2].Number == 1 && d.Items[d.Items.Count - 1].Number == 2, d.Items[d.Items.Count - 2].Number + "," + d.Items[d.Items.Count - 1].Number);
                d.Items.RemoveRange(d.Items.Count - 2, 2); d.Raise();
            }

            c.CurrentTool = Tool.Text; Click(100, 300);
            Expect("text tool starts inline edit", c.IsEditing);
            TypeText("Hello world");
            c.CommitEdit();
            var t = d.Items.LastOrDefault();
            Expect("text committed", t != null && t.Kind == AnnKind.Text && t.Text == "Hello world" && t.Rect.Width > 40, t == null ? "null" : t.Text + " " + t.Rect);

            c.CurrentTool = Tool.Text; Click(500, 500);
            c.CommitEdit();
            Expect("empty text is discarded", d.Items.Count == 6, d.Items.Count + " items");

            c.SetSelection(new List<Ann>()); c.CurrentTool = Tool.Callout; Drag(400, 250, 640, 320);
            Expect("callout starts edit", c.IsEditing);
            TypeText("Look here!");
            c.CommitEdit();
            var co = d.Items.Last();
            Expect("callout: tail tip is the press point, the dragged box stretches to the release point",
                co.Kind == AnnKind.Callout && co.Text == "Look here!" && Math.Abs(co.Tail.X - 400) < 1.5f && Math.Abs(co.Tail.Y - 250) < 1.5f && Math.Abs(co.Rect.Right - 640) < 1.5f && Math.Abs(co.Rect.Bottom - 320) < 1.5f && !co.AutoSize,
                co.Kind + " " + co.Text + " tail " + co.Tail + " rect " + co.Rect);
            {
                Expect("default callout text: Arial, red, 20px", co.FontName == "Arial" && co.FontSize == 20 && co.TextColor.R > 200 && co.TextColor.G < 90, co.FontName + " " + co.FontSize + " " + co.TextColor);
                // a plain click: the tail shows straight away at the click point, the body rests above-right of it
                c.SetSelection(new List<Ann>()); c.CurrentTool = Tool.Callout; Click(150, 500);
                var cc = d.Items.Last();
                bool shows = cc.Kind == AnnKind.Callout && Math.Abs(cc.Tail.X - 150) < 1.5f && Math.Abs(cc.Tail.Y - 500) < 1.5f && cc.Rect.Bottom < 500 - 20 && cc.Rect.Left > 150 && cc.Rect.Width > 150 && cc.AutoSize;
                Expect("callout: click sets the tail tip and shows the body above it", shows, "tail " + cc.Tail + " rect " + cc.Rect);
                c.CommitEdit();
                d.Items.Remove(cc); d.Raise();

                {
                    // pressing on the visible tail (even away from the straight line to the tip) must grab the callout, not start a new one
                    var wc = Ann.Create(AnnKind.Callout); wc.P1 = new PointF(300, 200); wc.P2 = new PointF(500, 260); wc.Tail = new PointF(250, 400); wc.Text = "x"; wc.AutoSize = false;
                    d.Items.Add(wc); d.Raise();
                    c.SetSelection(new System.Collections.Generic.List<Ann>()); c.CurrentTool = Tool.Callout;
                    int n0 = d.Items.Count;
                    Click(340, 275);
                    Expect("callout tool: pressing on the tail wedge selects the callout, no new callout", d.Items.Count == n0 && c.Selection.Count == 1 && ReferenceEquals(c.Selection[0], wc), d.Items.Count + " vs " + n0);
                    c.SetSelection(new System.Collections.Generic.List<Ann>());
                    Click(250, 398);
                    Expect("callout tool: pressing on the tail tip selects the callout, no new callout", d.Items.Count == n0 && c.Selection.Count == 1 && ReferenceEquals(c.Selection[0], wc), d.Items.Count + " vs " + n0);
                    c.SetSelection(new System.Collections.Generic.List<Ann>());
                    Click(600, 420);   // clearly off the callout: a click still starts a new one
                    Expect("callout tool: pressing on empty canvas still creates a callout", d.Items.Count == n0 + 1 && d.Items.Last().Kind == AnnKind.Callout && !ReferenceEquals(d.Items.Last(), wc));
                    // just added a callout: a click on empty canvas deselects it (no new callout); only the next click adds one
                    TypeText("t"); c.CommitEdit();
                    int n1 = d.Items.Count;
                    Click(760, 500);
                    Expect("callout tool: a click on empty canvas after adding a callout only deselects it", d.Items.Count == n1 && c.Selection.Count == 0 && !c.IsEditing, d.Items.Count + " vs " + n1 + ", selected " + c.Selection.Count);
                    Click(760, 500);
                    Expect("callout tool: the next click on empty canvas adds a new callout", d.Items.Count == n1 + 1 && d.Items.Last().Kind == AnnKind.Callout && c.IsEditing, d.Items.Count + " vs " + (n1 + 1));
                    c.CommitEdit();
                    var extra = d.Items.Last(); if (!ReferenceEquals(extra, wc) && extra.Kind == AnnKind.Callout && extra.Text.Length == 0) d.Items.Remove(extra);
                    d.Items.RemoveAll(a => a.Kind == AnnKind.Callout && a.Text != "Look here!"); d.Raise(); c.SetSelection(new System.Collections.Generic.List<Ann>());
                }

                // the box changes size while the mouse moves; the tail tip stays put
                c.SetSelection(new List<Ann>()); c.CurrentTool = Tool.Callout;
                Down(S(300, 450));
                { var l0 = d.Items.Last(); Expect("callout: right after the press only the tail tip exists, the box has no size yet", l0.Kind == AnnKind.Callout && l0.Rect.Width < 1 && l0.Rect.Height < 1 && Math.Abs(l0.Tail.X - 300) < 1.5f, "rect " + l0.Rect); }
                Move(S(420, 420)); var w1 = d.Items.Last().Rect.Width; var h1 = d.Items.Last().Rect.Height;
                Move(S(560, 380)); var live = d.Items.Last(); var w2 = live.Rect.Width; var h2 = live.Rect.Height; var tl = live.Tail;
                Up(S(560, 380));
                Expect("callout: the box grows while dragging and the tail tip stays fixed", w2 > w1 + 60 && h2 >= h1 && Math.Abs(tl.X - 300) < 1.5f && Math.Abs(tl.Y - 450) < 1.5f,
                    "w " + w1 + " -> " + w2 + ", h " + h1 + " -> " + h2 + ", tail " + tl);
                c.CommitEdit();
                var lastCo = d.Items.Last(); if (lastCo.Kind == AnnKind.Callout && lastCo.Text.Length == 0) { d.Items.Remove(lastCo); d.Raise(); }
            }

            {
                // moving a callout moves box and tail together (Alt = box only); dragging the tip moves only the tip
                var t0 = co.Tail; var r0 = co.Rect;
                c.CurrentTool = Tool.Select;
                Drag(r0.X + r0.Width / 2, r0.Y + r0.Height / 2, r0.X + r0.Width / 2 + 60, r0.Y + r0.Height / 2 + 40);
                var co2 = d.Items.Last();
                Expect("callout: dragging it moves the whole callout, tail included", Math.Abs(co2.Rect.X - (r0.X + 60)) < 3 && Math.Abs(co2.Rect.Y - (r0.Y + 40)) < 3 && Math.Abs(co2.Tail.X - (t0.X + 60)) < 3 && Math.Abs(co2.Tail.Y - (t0.Y + 40)) < 3,
                    "rect " + co2.Rect + " tail " + co2.Tail + " (was " + t0 + ")");
                var tip = co2.Tail; var rb = co2.Rect;
                Drag(tip.X, tip.Y, tip.X + 30, tip.Y + 20);
                var co3 = d.Items.Last();
                Expect("callout: dragging the tail tip moves only the tip", Math.Abs(co3.Tail.X - (tip.X + 30)) < 3 && Math.Abs(co3.Tail.Y - (tip.Y + 20)) < 3 && Math.Abs(co3.Rect.X - rb.X) < 0.5f && Math.Abs(co3.Rect.Width - rb.Width) < 0.5f, "tail " + co3.Tail + " rect " + co3.Rect);
                var tipNow = co3.Tail; float w3 = co3.Rect.Width;
                Drag(co3.Rect.Right, co3.Rect.Y + co3.Rect.Height / 2, co3.Rect.Right + 40, co3.Rect.Y + co3.Rect.Height / 2);   // right-middle handle
                var co4 = d.Items.Last();
                Expect("callout: resizing keeps the tail tip fixed", Math.Abs(co4.Tail.X - tipNow.X) < 0.5f && Math.Abs(co4.Tail.Y - tipNow.Y) < 0.5f && co4.Rect.Width > w3 + 30, "width " + w3 + " -> " + co4.Rect.Width);
                {
                    // Alt-move: box only (checked on the model, the harness cannot hold Alt)
                    var probe = co4.Clone(); var pt = probe.Tail; var pr = probe.Rect;
                    probe.MoveBody(25, 15);
                    Expect("callout: MoveBody moves the box and keeps the tail tip", Math.Abs(probe.Rect.X - (pr.X + 25)) < 0.01f && Math.Abs(probe.Tail.X - pt.X) < 0.01f && Math.Abs(probe.Tail.Y - pt.Y) < 0.01f);

                    // extra tail handles: 9 slides the tail along its side, 10 widens the base
                    var hs = co4.Handles();
                    Expect("callout: has tail base and width handles", hs.Length == 11 && hs[9].X > -1e5f && hs[10].X > -1e5f, "handles " + hs.Length);
                    bool horiz = Math.Abs(hs[10].X - hs[9].X) > Math.Abs(hs[10].Y - hs[9].Y);
                    var tail9 = co4.Tail; var rect9 = co4.Rect; var base0 = hs[9];
                    if (horiz) Drag(hs[9].X, hs[9].Y, hs[9].X + 22, hs[9].Y); else Drag(hs[9].X, hs[9].Y, hs[9].X, hs[9].Y + 22);
                    var cb = d.Items.Last(); var hb = cb.Handles();
                    float moved = horiz ? hb[9].X - base0.X : hb[9].Y - base0.Y;
                    Expect("callout: base handle slides the tail along the box edge, tip and box stay", Math.Abs(moved) > 4 && Math.Abs(cb.Tail.X - tail9.X) < 0.5f && Math.Abs(cb.Tail.Y - tail9.Y) < 0.5f && Math.Abs(cb.Rect.Width - rect9.Width) < 0.5f && cb.TailPos >= 0, "moved " + moved + ", TailPos " + cb.TailPos);
                    float tw0 = cb.TailW;
                    var end0 = hb[10];
                    if (horiz) Drag(end0.X, end0.Y, end0.X + 12, end0.Y); else Drag(end0.X, end0.Y, end0.X, end0.Y + 12);
                    var cwid = d.Items.Last();
                    Expect("callout: width handle changes the tail base width", cwid.TailW > 0 && Math.Abs(cwid.TailW - (horiz ? Math.Abs(hb[10].X + 12 - hb[9].X) : Math.Abs(hb[10].Y + 12 - hb[9].Y))) < 6 && Math.Abs(cwid.Tail.X - tail9.X) < 0.5f, "TailW " + cwid.TailW);
                    using (var flat = d.Render()) Expect("callout: renders with a customised tail", flat.Width > 0);
                    var rt = Ann.FromXml(cwid.ToXml());
                    Expect("callout: tail shape survives save / load", Math.Abs(rt.TailPos - cwid.TailPos) < 0.0001f && Math.Abs(rt.TailW - cwid.TailW) < 0.0001f, rt.TailPos + "," + rt.TailW);
                    d.Undo(); d.Undo();
                }
                d.Undo(); d.Undo(); d.Undo();
                d.Items.RemoveAll(a => a.Kind == AnnKind.Callout); d.Raise();       // the wide dragged callout would sit in the way of the later tests
            }

            c.CurrentTool = Tool.Blur; Drag(60, 40, 200, 70);
            var bl = d.Items.Last();
            Expect("blur region added", bl.Kind == AnnKind.Blur);
            using (var flat = d.Render())
            {
                // the sample has a gradient with text; pixelating should make neighbouring pixels identical in blocks
                var a = flat.GetPixel(100, 50); var b = flat.GetPixel(101, 50);
                Expect("blur actually changes pixels", a == b, a + " vs " + b);
            }

            c.CurrentTool = Tool.Highlighter; Drag(120, 520, 420, 530);
            Expect("highlighter stroke", d.Items.Last().Kind == AnnKind.Pen && d.Items.Last().Highlighter && d.Items.Last().Pts.Count > 2);

            c.CurrentTool = Tool.Stamp; Click(800, 500);
            Expect("stamp placed", d.Items.Last().Kind == AnnKind.Stamp && d.Items.Last().Rect.Width > 30);

            c.CurrentTool = Tool.Magnify; Drag(650, 350, 800, 450);
            Expect("magnify added", d.Items.Last().Kind == AnnKind.Magnify);

            c.CurrentTool = Tool.Spotlight; Drag(20, 20, 40, 40); // tiny but > 4px → keep
            int before = d.Items.Count;
            c.CurrentTool = Tool.Arrow; Click(200, 200);   // a click with a drawing tool without drag must not leave a stray arrow
            Expect("click without drag removes zero-size arrow", d.Items.Count == before, d.Items.Count + " vs " + before);

            // drawing tool still active: pressing on an existing object must select+move it, not create a new one
            c.CurrentTool = Tool.Callout;
            int cnt = d.Items.Count; var stampObj = d.Items.First(a => a.Kind == AnnKind.Stamp); float sx0 = stampObj.P1.X;
            Drag(stampObj.Rect.X + 20, stampObj.Rect.Y + 20, stampObj.Rect.X + 50, stampObj.Rect.Y + 20);
            Expect("drawing tool: drag existing object moves it", d.Items.Count == cnt && Math.Abs(stampObj.P1.X - (sx0 + 30)) < 3 && c.Selection.Contains(stampObj), d.Items.Count + " vs " + cnt + ", dx=" + (stampObj.P1.X - sx0));

            // --- select / move / resize
            c.CurrentTool = Tool.Select;
            Click(200, 140);   // on the arrow's body (arrow from 80,80 to 300,200: midpoint 190,140)
            Expect("select by clicking the arrow", c.Selection.Count == 1 && c.Selection[0].Kind == AnnKind.Arrow, c.Selection.Count + " sel");
            var arrow = c.Selection.Count > 0 ? c.Selection[0] : d.Items[0];
            float x0 = arrow.P1.X;
            Drag(190, 140, 210, 160);
            Expect("move selected object", Math.Abs(arrow.P1.X - (x0 + 20)) < 3, x0 + " -> " + arrow.P1.X);

            Click(475, 60);   // shape top edge
            var shape = d.Items[1];
            Expect("select shape by outline", c.Selection.Count == 1 && c.Selection[0] == shape);
            float w0 = shape.Rect.Width;
            var br = S(shape.Rect.Right, shape.Rect.Bottom);
            Down(br); Move(new Point(br.X + 30, br.Y + 20)); Up(new Point(br.X + 30, br.Y + 20));
            Expect("resize via corner handle", Math.Abs(shape.Rect.Width - (w0 + 30)) < 3, w0 + " -> " + shape.Rect.Width);

            // marquee
            Drag(30, 380, 280, 440);
            Expect("marquee selects the line", c.Selection.Count >= 1 && c.Selection.Any(a => a.Kind == AnnKind.Line));
            c.DeleteSelection();
            int afterDel = d.Items.Count;
            d.Undo();
            Expect("undo delete", d.Items.Count == afterDel + 1);
            d.Redo();
            Expect("redo delete", d.Items.Count == afterDel);

            // style change on selection
            Click(475, 60);
            c.ApplyProp(a => a.Stroke = Color.Blue);
            Expect("apply property to selection", d.Items[1].Stroke.ToArgb() == Color.Blue.ToArgb());

            for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
            try { using (var b = ScreenGrabber.Grab(ed.Bounds)) b.Save(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(logPathG ?? "x.log")), "interact_mid.png"), ImageFormat.Png); } catch { }

            foreach (var tl in new[] { Tool.Fill, Tool.Eraser, Tool.CutOut, Tool.Crop, Tool.Step, Tool.Blur })
            {
                c.CurrentTool = tl;
                stampObj = d.Items.First(a => a.Kind == AnnKind.Stamp);
                float bx = stampObj.P1.X; int n0 = d.Items.Count; var wasBase = d.Base;
                Drag(stampObj.Rect.X + 20, stampObj.Rect.Y + 20, stampObj.Rect.X + 35, stampObj.Rect.Y + 25);
                Expect(tl + " tool: dragging an existing object moves it", d.Items.Count == n0 && Math.Abs(stampObj.P1.X - (bx + 15)) < 3 && ReferenceEquals(wasBase, d.Base) && !c.HasCrop, "dx=" + (stampObj.P1.X - bx) + " items " + d.Items.Count + "/" + n0);
            }
            c.CurrentTool = Tool.Select;

            // --- destructive tools
            c.CurrentTool = Tool.Fill; c.FillColor = Color.Lime; Click(880, 20);
            { var px = d.Base.GetPixel(880, 20); Expect("fill tool", px.G > 200 && px.R < 100, px.ToString()); }

            c.CurrentTool = Tool.Eraser; c.EraserSize = 30; Drag(700, 560, 780, 560);
            Expect("eraser makes transparent pixels", d.Base.GetPixel(740, 560).A == 0, d.Base.GetPixel(740, 560).ToString());

            int w = d.Width;
            c.CurrentTool = Tool.CutOut; Drag(300, 590, 360, 592);   // dominant horizontal drag → vertical strip removed
            Expect("cut out vertical strip", d.Width == w - 60, w + " -> " + d.Width);

            c.CurrentTool = Tool.Crop; Drag(50, 50, 450, 350);
            Expect("crop selection made", c.HasCrop);
            c.CommitCrop();
            Expect("crop applied", d.Width == 400 && d.Height == 300, d.Width + "x" + d.Height);

            // canvas edge drag (right-middle handle)
            c.CurrentTool = Tool.Select;
            var rm = S(d.Width, d.Height / 2f);
            int cw = d.Width;
            Down(rm); Move(new Point(rm.X + 25, rm.Y)); Move(new Point(rm.X + 50, rm.Y)); Up(new Point(rm.X + 50, rm.Y));
            Expect("drag canvas edge to grow image", d.Width == cw + 50, cw + " -> " + d.Width);

            // zoom
            c.SetZoom(2f, null);
            Expect("zoom 200%", Math.Abs(c.Zoom - 2f) < 0.01f);
            c.ZoomFit(false);

            // export roundtrip of the edited document
            string p = Path.Combine(Path.GetTempPath(), "rxcapture_interact.png");
            using (var b = d.Render()) Exporter.Save(b, p);
            Expect("export edited image", new FileInfo(p).Length > 1000);
            c.CurrentTool = Tool.Select;
        }
    }
}

namespace RXCapture
{
    /// <summary>RXCapture.exe --overlaytest [log]: drives the region overlay with synthetic input on the real multi-monitor desktop.</summary>
    static class OverlayTest
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        static IntPtr LP(Point p) { return (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)); }

        public static int Run(string logPath)
        {
            var log = new System.Text.StringBuilder();
            int failed = 0;
            Action<string, bool, string> expect = delegate(string n, bool ok, string i) { log.AppendLine((ok ? "PASS " : "FAIL ") + n + "  [" + i + "]"); if (!ok) failed++; };

            var vs = ScreenGrabber.VirtualScreen;
            log.AppendLine("virtual screen " + vs);
            foreach (var s in Screen.AllScreens) log.AppendLine("  monitor " + s.Bounds + " primary=" + s.Primary);

            // choose two client-space regions: one on the left monitor (negative X in screen space) and one on the primary
            var cases = new List<Rectangle>();
            var left = Screen.AllScreens.OrderBy(s => s.Bounds.X).First().Bounds;
            var prim = Screen.PrimaryScreen.Bounds;
            cases.Add(new Rectangle(left.X + 200, left.Y + 150, 480, 300));
            cases.Add(new Rectangle(prim.X + 300, prim.Y + 200, 640, 360));
            if (left.Right == prim.X) cases.Add(new Rectangle(left.Right - 200, prim.Y + 300, 400, 200));   // spanning both monitors

            foreach (var region in cases)
            {
                Bitmap frozen = ScreenGrabber.Grab(vs);
                var tops = WindowFinder.Snapshot(IntPtr.Zero);
                var timer = new System.Windows.Forms.Timer { Interval = 700 };
                int stage = 0;
                var a = new Point(region.X - vs.X, region.Y - vs.Y);
                var b = new Point(region.Right - vs.X, region.Bottom - vs.Y);
                timer.Tick += delegate
                {
                    var f = Form.ActiveForm;
                    if (f == null || !(f is RegionOverlay)) { if (++stage > 12) { timer.Stop(); foreach (Form x in Application.OpenForms) if (x is RegionOverlay) x.Close(); } return; }
                    timer.Interval = 250;
                    switch (stage++)
                    {
                        case 0:
                            SendMessage(f.Handle, 0x200, IntPtr.Zero, LP(a));
                            SendMessage(f.Handle, 0x201, (IntPtr)1, LP(a));
                            for (int i = 1; i <= 8; i++) SendMessage(f.Handle, 0x200, (IntPtr)1, LP(new Point(a.X + (b.X - a.X) * i / 8, a.Y + (b.Y - a.Y) * i / 8)));
                            SendMessage(f.Handle, 0x202, IntPtr.Zero, LP(b));
                            break;
                        case 2:
                            try { using (var shot = ScreenGrabber.Grab(Rectangle.Intersect(vs, new Rectangle(region.X - 60, region.Y - 60, region.Width + 120, region.Height + 160)))) shot.Save(Path.ChangeExtension(logPath ?? "overlay.log", null) + "_" + cases.IndexOf(region) + ".png", ImageFormat.Png); } catch { }
                            SendMessage(f.Handle, 0x100, (IntPtr)13, IntPtr.Zero);   // Enter
                            break;
                    }
                };
                timer.Start();
                var res = RegionOverlay.Pick(frozen, vs, tops, OverlayMode.Region, null);
                timer.Stop(); timer.Dispose();
                expect("overlay returns a result for " + region, res != null, res == null ? "null" : res.Rect.ToString());
                if (res != null)
                {
                    expect("selected rectangle matches drag " + region, res.Rect == region, res.Rect + " vs " + region);
                    var rel = new Rectangle(res.Rect.X - vs.X, res.Rect.Y - vs.Y, res.Rect.Width, res.Rect.Height);
                    using (var crop = ScreenGrabber.Crop(frozen, rel))
                    using (var live = ScreenGrabber.Grab(res.Rect))
                    {
                        // desktop may have tiny changes (clock, cursor); compare a coarse checksum
                        long diff = 0; int n = 0;
                        for (int y = 0; y < crop.Height; y += 9) for (int x = 0; x < crop.Width; x += 9) { var p1 = crop.GetPixel(x, y); var p2 = live.GetPixel(x, y); diff += Math.Abs(p1.R - p2.R) + Math.Abs(p1.G - p2.G) + Math.Abs(p1.B - p2.B); n++; }
                        expect("frozen crop equals live desktop " + region, diff / (double)n < 12, "avg diff " + (diff / (double)n).ToString("0.0"));
                    }
                }
                frozen.Dispose();
            }
            log.AppendLine(failed == 0 ? "ALL OVERLAY TESTS PASSED" : failed + " FAILED");
            File.WriteAllText(logPath ?? "overlay.log", log.ToString());
            return failed == 0 ? 0 : 1;
        }
    }
}

namespace RXCapture
{
    /// <summary>RXCapture.exe --videotest [log]: records ~3 seconds through the real recorder UI in AVI and GIF mode.</summary>
    static class VideoTest
    {

        /// <summary>Trims 1s..2s out of the recording and checks the result is a valid ~1 s MP4 whose first frame matches the source.</summary>
        static int TrimCheck(LibItem v, string fmt, StringBuilder log)
        {
            string dst = Path.Combine(Path.GetTempPath(), "rxcapture_trimtest.mp4");
            Bitmap first; TimeSpan kept;
            bool ok = Mp4Writer.Trim(v.File, dst, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), out first, out kept);
            double secs = 0;
            if (ok)
            {
                var b = File.ReadAllBytes(dst);
                for (int i = 0; i < b.Length - 24; i++)
                    if (b[i] == 'm' && b[i + 1] == 'v' && b[i + 2] == 'h' && b[i + 3] == 'd')
                    {
                        Func<int, uint> u = o => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
                        secs = (double)u(i + 20) / u(i + 16); break;
                    }
            }
            int dist = 999;
            if (ok && first != null)
                using (var thumb = new Bitmap(v.ThumbFile))
                {
                    Color a = thumb.GetPixel(thumb.Width / 2, thumb.Height / 2), c = first.GetPixel(first.Width / 2, first.Height / 2);
                    dist = Math.Abs(a.R - c.R) + Math.Abs(a.G - c.G) + Math.Abs(a.B - c.B);
                }
            if (first != null) first.Dispose();
            try { File.Delete(dst); } catch { }
            bool good = ok && secs > 0.8 && secs < 1.3 && dist <= 60;
            log.AppendLine((good ? "PASS " : "FAIL ") + fmt + " trim 1s-2s -> mp4 of " + secs.ToString("0.00") + "s (ok " + ok + ", first-frame colour diff " + dist + ")");
            return good ? 0 : 1;
        }

        /// <summary>Opens the recorded file in the editor's player panel and checks it really plays (duration + a drawn frame).</summary>
        static int PlaybackCheck(LibItem v, string fmt, StringBuilder log)
        {
            var prim = Screen.PrimaryScreen.Bounds;
            var host = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, TopMost = true, ShowInTaskbar = false, Bounds = new Rectangle(prim.X + 100, prim.Y + 100, 900, 560) };
            var player = new VideoPlayerPanel();
            host.Controls.Add(player);
            host.Show(); player.Visible = true;
            player.Open(v.File, v.Ext);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 8000 && !player.IsOpened && !player.Failed) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
            while (sw.ElapsedMilliseconds < 9000 ) { Application.DoEvents(); System.Threading.Thread.Sleep(30); if (sw.ElapsedMilliseconds > 1500) break; }   // let a frame render
            int failed = 0;
            bool opened = player.IsOpened && !player.Failed && player.Duration.TotalSeconds >= 1.5;
            log.AppendLine((opened ? "PASS " : "FAIL ") + fmt + " plays in the editor player (opened " + player.IsOpened + ", duration " + player.Duration.TotalSeconds.ToString("0.0") + "s)");
            if (!opened) failed++;
            // the stage must show the video, not the black background: the centre pixel has to match the first frame (the library thumbnail)
            int dist = 999; string got = "?", want = "?";
            try
            {
                using (var thumb = new Bitmap(v.ThumbFile))
                using (var shot = ScreenGrabber.Grab(new Rectangle(host.Left + 450, host.Top + 250, 1, 1)))
                {
                    Color a = thumb.GetPixel(thumb.Width / 2, thumb.Height / 2), b = shot.GetPixel(0, 0);
                    want = a.R + "," + a.G + "," + a.B; got = b.R + "," + b.G + "," + b.B;
                    dist = Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
                }
            }
            catch { }
            bool drawn = dist <= 60;
            log.AppendLine((drawn ? "PASS " : "FAIL ") + fmt + " player draws the video frame (centre " + got + " vs first frame " + want + ")");
            if (!drawn) failed++;
            // dragging the grips of the green span on the seek bar changes the kept range
            {
                var flg = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
                var seekBar = (Control)typeof(VideoPlayerPanel).GetField("seek", flg).GetValue(player);
                Application.DoEvents();
                Action<string, int> send = (name, x) =>
                    typeof(Control).GetMethod(name, flg).Invoke(seekBar, new object[] { new MouseEventArgs(MouseButtons.Left, 1, x, seekBar.Height / 2, 0) });
                int l = 8, r = seekBar.Width - 8;
                int x25 = l + (r - l) / 4, x75 = l + (r - l) * 3 / 4;
                send("OnMouseDown", l); send("OnMouseMove", x25); send("OnMouseUp", x25);          // start grip -> 25 %
                send("OnMouseDown", r); send("OnMouseMove", x75); send("OnMouseUp", x75);          // end grip   -> 75 %
                double d = player.Duration.TotalSeconds;
                double s0 = player.TrimStart.TotalSeconds / d, e0 = player.TrimEnd.TotalSeconds / d;
                bool gripOk = Math.Abs(s0 - 0.25) < 0.04 && Math.Abs(e0 - 0.75) < 0.04;
                log.AppendLine((gripOk ? "PASS " : "FAIL ") + fmt + " dragging the green span grips sets the kept range (" + s0.ToString("0.00") + " - " + e0.ToString("0.00") + " of the video)");
                if (!gripOk) failed++;
            }
            player.Stop();
            bool released = true;
            try { using (var fs = new FileStream(v.File, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { } } catch { released = false; }
            log.AppendLine((released ? "PASS " : "FAIL ") + fmt + " player releases the file when stopped");
            if (!released) failed++;
            host.Close();
            return failed;
        }
        public static int Run(string logPath)
        {
            var log = new System.Text.StringBuilder();
            int failed = 0;
            App.AppIcon = SystemIcons.Application;
            foreach (var fmt in new[] { "avi", "gif", "mp4" })
            {
                AppSettings.Current.VideoFormat = fmt;
                int before = LibraryStore.List().Count(i => i.IsVideo);
                var prim = Screen.PrimaryScreen.Bounds;
                var region = new Rectangle(prim.X + 200, prim.Y + 200, 640, 360);
                var t = new System.Windows.Forms.Timer { Interval = 1000 };
                int tick = 0;
                t.Tick += delegate
                {
                    var rf = Application.OpenForms.OfType<RecorderForm>().FirstOrDefault();
                    if (rf == null) return;
                    tick++;
                    if (tick == 1) rf.RecClicked();          // starts the 3-2-1 countdown
                    if (tick == 7) { rf.StopClicked(); t.Stop(); }
                };
                t.Start();
                VideoRecorder.Run(region);
                t.Stop();
                var vids = LibraryStore.List().Where(i => i.IsVideo).ToList();
                bool added = vids.Count == before + 1;
                log.AppendLine((added ? "PASS " : "FAIL ") + "video recorded as " + fmt + " (library videos " + before + " -> " + vids.Count + ")");
                if (!added) { failed++; continue; }
                var v = vids[0];
                var fi = new FileInfo(v.File);
                bool okExt = v.Ext == fmt && fi.Length > (fmt == "avi" ? 5000 : 500);   // a still screen makes tiny GIF/MP4 files (identical frames merge)
                log.AppendLine((okExt ? "PASS " : "FAIL ") + fmt + " file " + fi.Name + " " + fi.Length + " bytes, duration label " + v.DurationSec + "s");
                if (!okExt) failed++;
                if (fmt == "avi")
                {
                    int n = 0; foreach (var f in AviReader.Frames(v.File)) n++;
                    bool ok = n >= 20;
                    log.AppendLine((ok ? "PASS " : "FAIL ") + "avi frames = " + n + " (15 fps, ~3 s expected ≥ 30)");
                    if (!ok) failed++;
                }
                else if (fmt == "gif")
                {
                    using (var im = Image.FromFile(v.File))
                    {
                        int n = im.GetFrameCount(FrameDimension.Time);
                        bool ok = n >= 1;
                        log.AppendLine((ok ? "PASS " : "FAIL ") + "gif " + im.Width + "x" + im.Height + " frames=" + n);
                        if (!ok) failed++;
                    }
                }
                else
                {
                    // ISO base media file: 'ftyp' box first and a 'moov' box somewhere; frame size is 640x360 (even)
                    byte[] head = File.ReadAllBytes(v.File);
                    string all = System.Text.Encoding.ASCII.GetString(head);
                    bool ok = head.Length > 12 && all.Substring(4, 4) == "ftyp" && all.Contains("moov") && all.Contains("avc1");
                    log.AppendLine((ok ? "PASS " : "FAIL ") + "mp4 container has ftyp/moov/avc1 (H.264)");
                    if (!ok) failed++;
                }
                if (fmt != "gif") { failed += PlaybackCheck(v, fmt, log); failed += TrimCheck(v, fmt, log); }
                LibraryStore.Delete(v);
            }
            log.AppendLine(failed == 0 ? "ALL VIDEO TESTS PASSED" : failed + " FAILED");
            File.WriteAllText(logPath ?? "video.log", log.ToString());
            return failed == 0 ? 0 : 1;
        }
    }
}

namespace RXCapture
{
    /// <summary>RXCapture.exe --scrolltest file.png x,y,w,h : scroll-captures the given screen region.</summary>
    static class ScrollTest
    {
        public static int Run(string[] args)
        {
            var p = args[2].Split(',');
            var r = new Rectangle(int.Parse(p[0]), int.Parse(p[1]), int.Parse(p[2]), int.Parse(p[3]));
            using (var bmp = ScrollCapture.Run(r, CursorSnap.Take()))
            {
                if (bmp == null) return 1;
                bmp.Save(args[1], ImageFormat.Png);
                File.WriteAllText(args[1] + ".txt", bmp.Width + "x" + bmp.Height);
            }
            return 0;
        }
    }
}

namespace RXCapture
{
    /// <summary>RXCapture.exe --windowtest [log]: captures a window that is partly covered by another one (PrintWindow path).</summary>
    static class WindowTest
    {
        public static int Run(string logPath)
        {
            var log = new System.Text.StringBuilder(); int failed = 0;
            var prim = Screen.PrimaryScreen.Bounds;
            var target = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, BackColor = Color.FromArgb(30, 160, 60), ShowInTaskbar = false, TopMost = true, Text = "wt-target" };
            target.Bounds = new Rectangle(prim.X + 300, prim.Y + 300, 500, 300);
            var cover = new Form { FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, BackColor = Color.FromArgb(200, 30, 30), ShowInTaskbar = false, TopMost = true, Text = "wt-cover" };
            cover.Bounds = new Rectangle(prim.X + 500, prim.Y + 350, 400, 150);   // covers the right/bottom part of the target
            target.Show(); Application.DoEvents(); cover.Show(); Application.DoEvents();
            for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
            var tops = WindowFinder.Snapshot(IntPtr.Zero);
            var w = tops.Find(t => t.Handle == target.Handle);
            log.AppendLine((w != null ? "PASS" : "FAIL") + " target window is in the window snapshot"); if (w == null) failed++;
            if (w != null)
            {
                bool covered = CaptureManager.IsCovered(w, tops);
                log.AppendLine((covered ? "PASS" : "FAIL") + " IsCovered detects the overlapping window"); if (!covered) failed++;
                using (var direct = ScreenGrabber.Grab(w.Bounds))
                using (var full = ScreenGrabber.CaptureWindowContent(w.Handle, w.Bounds))
                {
                    var dp = direct.GetPixel(400, 100);      // inside the covered part: the red cover
                    log.AppendLine((dp.R > 150 && dp.G < 80 ? "PASS" : "FAIL") + " plain screen crop shows the covering window " + dp);
                    if (full == null) { failed++; log.AppendLine("FAIL PrintWindow returned no image"); }
                    else
                    {
                        var fp = full.GetPixel(400, 100);
                        bool ok = full.Width == w.Bounds.Width && full.Height == w.Bounds.Height && fp.G > 120 && fp.R < 80;
                        log.AppendLine((ok ? "PASS" : "FAIL") + " PrintWindow shows the real window content " + fp + " size " + full.Size);
                        if (!ok) failed++;
                    }
                }
            }
            cover.Close(); target.Close();
            log.AppendLine(failed == 0 ? "ALL WINDOW TESTS PASSED" : failed + " FAILED");
            File.WriteAllText(logPath ?? "window.log", log.ToString());
            return failed == 0 ? 0 : 1;
        }
    }
}
