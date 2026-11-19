using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;

namespace MineServer
{
    /// <summary>
    /// Modalità notte: "light" (chiaro), "dark" (scuro), "auto" (come Windows).
    /// Colora ricorsivamente le finestre; il tema si può cambiare senza riavviare.
    /// Un controllo con Tag = "keep" non viene toccato (console, anteprime, quadratini dei colori).
    /// </summary>
    public static class Theme
    {
        public static readonly Color Back = Color.FromArgb(32, 32, 32);
        public static readonly Color Surface = Color.FromArgb(43, 43, 43);
        public static readonly Color Input = Color.FromArgb(55, 55, 55);
        public static readonly Color Border = Color.FromArgb(85, 85, 85);
        public static readonly Color Fore = Color.FromArgb(232, 232, 232);
        public static readonly Color Dim = Color.FromArgb(165, 165, 165);
        public static readonly Color Link = Color.FromArgb(110, 175, 255);
        public static readonly Color Select = Color.FromArgb(0, 95, 184);

        public static bool Dark { get; private set; }

        class Orig
        {
            public Color Back, Fore;
            public bool BackSet, ForeSet;
            public FlatStyle? Flat;
            public bool? VisualBack, OwnerDraw;
        }

        static readonly Dictionary<Control, Orig> saved = new Dictionary<Control, Orig>();
        static readonly HashSet<Control> hooked = new HashSet<Control>();
        static bool adjusting;

        /// <summary>True se Windows usa il tema scuro per le app.</summary>
        public static bool WindowsIsDark()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    object v = k != null ? k.GetValue("AppsUseLightTheme") : null;
                    return v is int && (int)v == 0;
                }
            }
            catch { return false; }
        }

        public static bool Resolve(string mode)
        {
            if (mode == "dark") return true;
            if (mode == "light") return false;
            return WindowsIsDark();
        }

        // ------------------------------------------------------------ Windows (barra del titolo, barre di scorrimento)

        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);

        static void TitleBar(Form f, bool dark)
        {
            try
            {
                if (!f.IsHandleCreated) return;
                int v = dark ? 1 : 0;
                if (DwmSetWindowAttribute(f.Handle, 20, ref v, 4) != 0)      // Windows 11 / 10 20H1+
                    DwmSetWindowAttribute(f.Handle, 19, ref v, 4);          // Windows 10 più vecchi
            }
            catch { }
        }

        static void Scrollbars(Control c, bool dark)
        {
            try
            {
                if (c.IsHandleCreated) SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null);
            }
            catch { }
        }

        // ------------------------------------------------------------ colori leggibili

        static double Lum(Color c)
        {
            return (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) / 255.0;
        }

        /// <summary>Su sfondo scuro: il nero diventa testo chiaro, i colori scuri (rosso, verde...) vengono schiariti.</summary>
        public static Color Readable(Color c)
        {
            if (!Dark) return c;
            if (c.ToArgb() == SystemColors.ControlText.ToArgb() || c.ToArgb() == SystemColors.WindowText.ToArgb() || c.ToArgb() == Color.Black.ToArgb())
                return Fore;
            if (c.ToArgb() == SystemColors.GrayText.ToArgb()) return Dim;
            if (Lum(c) >= 0.5) return c;
            double k = 0.5;
            return Color.FromArgb(c.A, (int)(c.R + (255 - c.R) * k), (int)(c.G + (255 - c.G) * k), (int)(c.B + (255 - c.B) * k));
        }

        // ------------------------------------------------------------ applicazione

        public static void Apply(Control root, bool dark)
        {
            Dark = dark;
            var f = root as Form;
            if (f != null)
            {
                TitleBar(f, dark);
                if (!hooked.Contains(f))
                {
                    hooked.Add(f);
                    f.HandleCreated += (s, e) => TitleBar(f, Dark);
                }
            }
            Walk(root);
            root.Invalidate(true);
        }

        static void Walk(Control c)
        {
            if (!"keep".Equals(c.Tag as string))
            {
                Style(c);
                foreach (Control ch in c.Controls) Walk(ch);
            }
            if (!hooked.Contains(c))
            {
                hooked.Add(c);
                c.ControlAdded += (s, e) => { if (Dark) Walk(e.Control); };
                c.ForeColorChanged += (s, e) =>
                {
                    if (adjusting || !Dark || "keep".Equals(c.Tag as string)) return;
                    var r = Readable(c.ForeColor);
                    if (r == c.ForeColor) return;
                    adjusting = true;
                    try { c.ForeColor = r; } finally { adjusting = false; }
                };
                c.HandleCreated += (s, e) =>
                {
                    if (c is TextBoxBase || c is ListBox || c is ListView || c is DataGridView || c is TreeView || (c is ScrollableControl && ((ScrollableControl)c).AutoScroll))
                        Scrollbars(c, Dark);
                };
                c.Disposed += (s, e) => { saved.Remove(c); hooked.Remove(c); };
            }
        }

        static Orig Save(Control c)
        {
            Orig o;
            if (saved.TryGetValue(c, out o)) return o;
            o = new Orig();
            o.Back = c.BackColor;
            o.Fore = c.ForeColor;
            o.BackSet = c.BackColor != (c.Parent != null ? c.Parent.BackColor : SystemColors.Control) || c is TextBoxBase || c is ListBox || c is ListView;
            o.ForeSet = c.ForeColor != (c.Parent != null ? c.Parent.ForeColor : SystemColors.ControlText);
            var b = c as ButtonBase;
            if (b != null) o.Flat = b.FlatStyle;
            var cb = c as ComboBox;
            if (cb != null) o.Flat = cb.FlatStyle;
            var tp = c as TabPage;
            if (tp != null) o.VisualBack = tp.UseVisualStyleBackColor;
            var lv = c as ListView;
            if (lv != null) o.OwnerDraw = lv.OwnerDraw;
            saved[c] = o;
            return o;
        }

        static void Style(Control c)
        {
            var o = Save(c);
            adjusting = true;
            try
            {
                if (!Dark) { Restore(c, o); return; }

                Color fore = Readable(c.ForeColor);   // nero/predefinito -> chiaro, colori scuri -> schiariti
                if (c is Form || c is TabPage || c is Panel || c is GroupBox || c is TabControl || c is SplitContainer || c is UserControl)
                {
                    c.BackColor = c is Panel && c.Dock == DockStyle.Top ? Surface : Back;
                    c.ForeColor = fore;
                    var tp = c as TabPage;
                    if (tp != null) tp.UseVisualStyleBackColor = false;
                    var tc = c as ThemedTabControl;
                    if (tc != null) tc.SetDark(true);
                }
                else if (c is Button)
                {
                    var b = (Button)c;
                    b.FlatStyle = FlatStyle.Flat;
                    b.FlatAppearance.BorderColor = Border;
                    b.BackColor = Input;
                    b.ForeColor = fore;
                }
                else if (c is TextBoxBase || c is ListBox || c is NumericUpDown || c is ComboBox)
                {
                    c.BackColor = Input;
                    c.ForeColor = Fore;
                    var cb = c as ComboBox;
                    if (cb != null && cb.DrawMode == DrawMode.Normal) cb.FlatStyle = FlatStyle.Flat;
                    Scrollbars(c, true);
                }
                else if (c is ListView)
                {
                    var lv = (ListView)c;
                    lv.BackColor = Input;
                    lv.ForeColor = Fore;
                    HookListView(lv);
                    lv.OwnerDraw = true;
                    Scrollbars(c, true);
                }
                else if (c is DataGridView)
                {
                    var g = (DataGridView)c;
                    g.EnableHeadersVisualStyles = false;
                    g.BackgroundColor = Back;
                    g.GridColor = Border;
                    g.DefaultCellStyle.BackColor = Input;
                    g.DefaultCellStyle.ForeColor = Fore;
                    g.DefaultCellStyle.SelectionBackColor = Select;
                    g.DefaultCellStyle.SelectionForeColor = Color.White;
                    g.AlternatingRowsDefaultCellStyle.BackColor = Surface;
                    g.ColumnHeadersDefaultCellStyle.BackColor = Surface;
                    g.ColumnHeadersDefaultCellStyle.ForeColor = Fore;
                    g.RowHeadersDefaultCellStyle.BackColor = Surface;
                    g.RowHeadersDefaultCellStyle.ForeColor = Fore;
                    Scrollbars(c, true);
                }
                else if (c is LinkLabel)
                {
                    var l = (LinkLabel)c;
                    l.LinkColor = Link;
                    l.ActiveLinkColor = Link;
                    l.VisitedLinkColor = Link;
                    l.ForeColor = fore;
                }
                else if (c is Label || c is CheckBox || c is RadioButton)
                {
                    c.ForeColor = fore;
                }
            }
            finally { adjusting = false; }
        }

        static void Restore(Control c, Orig o)
        {
            if (o.BackSet) c.BackColor = o.Back; else c.ResetBackColor();
            if (o.ForeSet) c.ForeColor = o.Fore; else c.ResetForeColor();
            var b = c as ButtonBase;
            if (b != null && o.Flat.HasValue) b.FlatStyle = o.Flat.Value;
            var cb = c as ComboBox;
            if (cb != null && o.Flat.HasValue) cb.FlatStyle = o.Flat.Value;
            var tp = c as TabPage;
            if (tp != null && o.VisualBack.HasValue) tp.UseVisualStyleBackColor = o.VisualBack.Value;
            var lv = c as ListView;
            if (lv != null && o.OwnerDraw.HasValue) lv.OwnerDraw = o.OwnerDraw.Value;
            var tc = c as ThemedTabControl;
            if (tc != null) tc.SetDark(false);
            var g = c as DataGridView;
            if (g != null)
            {
                g.EnableHeadersVisualStyles = true;
                g.BackgroundColor = SystemColors.AppWorkspace;
                g.GridColor = SystemColors.ControlDark;
                g.DefaultCellStyle.BackColor = SystemColors.Window;
                g.DefaultCellStyle.ForeColor = SystemColors.ControlText;
                g.DefaultCellStyle.SelectionBackColor = SystemColors.Highlight;
                g.DefaultCellStyle.SelectionForeColor = SystemColors.HighlightText;
                g.AlternatingRowsDefaultCellStyle.BackColor = Color.Empty;
                g.ColumnHeadersDefaultCellStyle.BackColor = SystemColors.Control;
                g.ColumnHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
                g.RowHeadersDefaultCellStyle.BackColor = SystemColors.Control;
                g.RowHeadersDefaultCellStyle.ForeColor = SystemColors.ControlText;
            }
            var l = c as LinkLabel;
            if (l != null)
            {
                l.LinkColor = Color.FromArgb(0, 0, 255);
                l.ActiveLinkColor = Color.Red;
                l.VisitedLinkColor = Color.FromArgb(128, 0, 128);
            }
            if (c is TextBoxBase || c is ListBox || c is ListView || c is DataGridView) Scrollbars(c, false);
        }

        static readonly HashSet<ListView> lvHooked = new HashSet<ListView>();

        /// <summary>Intestazioni delle colonne scure (il resto lo disegna Windows come al solito).</summary>
        static void HookListView(ListView lv)
        {
            if (lvHooked.Contains(lv)) return;
            lvHooked.Add(lv);
            lv.DrawColumnHeader += (s, e) =>
            {
                if (!Dark) { e.DrawDefault = true; return; }
                using (var b = new SolidBrush(Surface)) e.Graphics.FillRectangle(b, e.Bounds);
                using (var p = new Pen(Border)) e.Graphics.DrawLine(p, e.Bounds.Right - 1, e.Bounds.Top + 3, e.Bounds.Right - 1, e.Bounds.Bottom - 4);
                TextRenderer.DrawText(e.Graphics, e.Header.Text, lv.Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                    Fore, TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            };
            lv.DrawItem += (s, e) => { e.DrawDefault = true; };
            lv.DrawSubItem += (s, e) => { e.DrawDefault = true; };
            lv.Disposed += (s, e) => lvHooked.Remove(lv);
        }
    }

    /// <summary>TabControl che in modalità notte disegna da sé le linguette (Windows le farebbe sempre chiare).</summary>
    public class ThemedTabControl : TabControl
    {
        bool dark;
        Font bold;

        public void SetDark(bool d)
        {
            if (dark == d) return;
            dark = d;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, d);
            UpdateStyles();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (!dark) { base.OnPaint(e); return; }
            var g = e.Graphics;
            g.Clear(Theme.Back);
            var page = DisplayRectangle;
            using (var p = new Pen(Theme.Border))
                g.DrawRectangle(p, page.X - 1, page.Y - 1, page.Width + 1, page.Height + 1);
            for (int i = 0; i < TabCount; i++)
            {
                var r = GetTabRect(i);
                bool sel = i == SelectedIndex;
                if (sel) r = new Rectangle(r.X, r.Y - 2, r.Width, r.Height + 3);
                using (var b = new SolidBrush(sel ? Theme.Input : Theme.Surface)) g.FillRectangle(b, r);
                using (var p = new Pen(Theme.Border)) g.DrawRectangle(p, r.X, r.Y, r.Width - 1, r.Height - 1);
                if (sel) using (var p = new Pen(Theme.Link, 2)) g.DrawLine(p, r.X + 1, r.Y + 1, r.Right - 2, r.Y + 1);
                if (bold == null || bold.Size != Font.Size) bold = new Font(Font, FontStyle.Bold);
                TextRenderer.DrawText(g, TabPages[i].Text, sel ? bold : Font, r,
                    sel ? Color.White : Theme.Fore, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            }
        }
    }
}
