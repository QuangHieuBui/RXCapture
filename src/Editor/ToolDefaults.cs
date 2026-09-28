using System;
using System.Collections.Generic;
using System.Drawing;
using System.Xml.Linq;

namespace RXCapture
{
    /// <summary>One saved default style, stored in settings.xml as the serialised template annotation.</summary>
    public class ToolDefaultEntry
    {
        public string Tool;
        public string Xml;
    }

    /// <summary>
    /// The default properties (colours, widths, font, effects ...) each drawing tool starts with.
    /// Factory values are built in; anything the user configures is kept in settings.xml and survives restarts.
    /// </summary>
    public static class ToolDefaultsStore
    {
        /// <summary>Tools whose defaults can be configured in the dialog.</summary>
        public static readonly Tool[] Configurable = { Tool.Arrow, Tool.Line, Tool.Shape, Tool.Callout, Tool.Text, Tool.Step, Tool.Pen, Tool.Highlighter, Tool.Magnify };

        /// <summary>Every tool that starts from a template annotation.</summary>
        public static readonly Tool[] All = { Tool.Arrow, Tool.Line, Tool.Shape, Tool.Callout, Tool.Text, Tool.Step, Tool.Stamp, Tool.Pen, Tool.Highlighter, Tool.Blur, Tool.Magnify, Tool.Spotlight };

        public static event EventHandler Changed;

        static void Raise() { if (Changed != null) Changed(null, EventArgs.Empty); }

        public static Ann Factory(Tool t)
        {
            switch (t)
            {
                case Tool.Arrow: return Ann.Create(AnnKind.Arrow);
                case Tool.Line: return Ann.Create(AnnKind.Line);
                case Tool.Shape: return Ann.Create(AnnKind.Shape);
                case Tool.Callout: return Ann.Create(AnnKind.Callout);
                case Tool.Text: return Ann.Create(AnnKind.Text);
                case Tool.Step: return Ann.Create(AnnKind.Step);
                case Tool.Stamp: return Ann.Create(AnnKind.Stamp);
                case Tool.Pen: return Ann.Create(AnnKind.Pen);
                case Tool.Highlighter: { var hl = Ann.Create(AnnKind.Pen); hl.Highlighter = true; hl.Stroke = Color.FromArgb(255, 235, 59); hl.Width = 22; return hl; }
                case Tool.Blur: return Ann.Create(AnnKind.Blur);
                case Tool.Magnify: return Ann.Create(AnnKind.Magnify);
                case Tool.Spotlight: return Ann.Create(AnnKind.Spotlight);
            }
            return null;
        }

        static ToolDefaultEntry Find(Tool t)
        {
            var list = AppSettings.Current.ToolDefaults;
            if (list == null) return null;
            foreach (var e in list) if (e.Tool == t.ToString()) return e;
            return null;
        }

        public static bool IsCustom(Tool t) { return Find(t) != null; }

        /// <summary>The user's saved default for the tool, or the factory one.</summary>
        public static Ann Load(Tool t)
        {
            var f = Factory(t);
            var e = Find(t);
            if (e == null || f == null) return f;
            try
            {
                var a = Ann.FromXml(XElement.Parse(e.Xml));
                if (a.Kind != f.Kind) return f;
                a.Highlighter = f.Highlighter;
                return a;
            }
            catch { return f; }
        }

        /// <summary>Stores the style of a as the default for tool t (geometry and text are dropped).</summary>
        public static void Save(Tool t, Ann a)
        {
            var c = a.Clone();
            c.P1 = c.P2 = c.Tail = PointF.Empty;
            c.Pts = new List<PointF>();
            c.Img = null; c.Text = ""; c.Editing = false;
            var cfg = AppSettings.Current;
            if (cfg.ToolDefaults == null) cfg.ToolDefaults = new List<ToolDefaultEntry>();
            var e = Find(t);
            if (e == null) { e = new ToolDefaultEntry { Tool = t.ToString() }; cfg.ToolDefaults.Add(e); }
            e.Xml = c.ToXml().ToString(SaveOptions.DisableFormatting);
            cfg.Save();
            Raise();
        }

        public static void Reset(Tool t)
        {
            var cfg = AppSettings.Current;
            var e = Find(t);
            if (e == null) return;
            cfg.ToolDefaults.Remove(e);
            cfg.Save();
            Raise();
        }

        public static void ResetAll()
        {
            var cfg = AppSettings.Current;
            if (cfg.ToolDefaults == null || cfg.ToolDefaults.Count == 0) return;
            cfg.ToolDefaults.Clear();
            cfg.Save();
            Raise();
        }
    }
}
