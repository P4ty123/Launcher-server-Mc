using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace MineServer
{
    /// <summary>
    /// Testi colorati di Minecraft. Nel programma si scrivono con "&amp;" (es. &amp;6 = oro, &amp;l = grassetto),
    /// nel server diventano "§" oppure componenti JSON per tellraw.
    /// </summary>
    public static class McText
    {
        public const string ColorCodes = "0123456789abcdef";
        public const string AllCodes = "0123456789abcdefklmnor";

        public static readonly Color[] Colors =
        {
            Color.FromArgb(0, 0, 0), Color.FromArgb(0, 0, 170), Color.FromArgb(0, 170, 0), Color.FromArgb(0, 170, 170),
            Color.FromArgb(170, 0, 0), Color.FromArgb(170, 0, 170), Color.FromArgb(255, 170, 0), Color.FromArgb(170, 170, 170),
            Color.FromArgb(85, 85, 85), Color.FromArgb(85, 85, 255), Color.FromArgb(85, 255, 85), Color.FromArgb(85, 255, 255),
            Color.FromArgb(255, 85, 85), Color.FromArgb(255, 85, 255), Color.FromArgb(255, 255, 85), Color.FromArgb(255, 255, 255)
        };

        public static readonly string[] ColorNames =
        {
            "black", "dark_blue", "dark_green", "dark_aqua", "dark_red", "dark_purple", "gold", "gray",
            "dark_gray", "blue", "green", "aqua", "red", "light_purple", "yellow", "white"
        };

        public static readonly string[] ColorLabels =
        {
            "Nero", "Blu scuro", "Verde scuro", "Azzurro scuro", "Rosso scuro", "Viola", "Oro", "Grigio",
            "Grigio scuro", "Blu", "Verde", "Azzurro", "Rosso", "Rosa", "Giallo", "Bianco"
        };

        /// <summary>Un pezzo di testo con il suo stile.</summary>
        public class Run
        {
            public string Text;
            public int Color = -1;      // indice in Colors, -1 = colore predefinito
            public bool Bold, Italic, Underline, Strike, Magic;
        }

        static bool IsCode(char c)
        {
            return AllCodes.IndexOf(char.ToLowerInvariant(c)) >= 0;
        }

        /// <summary>Divide il testo in pezzi con lo stesso stile, come fa Minecraft (un colore azzera gli stili).</summary>
        public static List<Run> Parse(string text, char marker)
        {
            var list = new List<Run>();
            var cur = new Run();
            var sb = new StringBuilder();
            text = text ?? "";
            for (int i = 0; i < text.Length; i++)
            {
                char ch = text[i];
                if ((ch == marker || ch == '§') && i + 1 < text.Length && IsCode(text[i + 1]))
                {
                    if (sb.Length > 0)
                    {
                        cur.Text = sb.ToString();
                        list.Add(cur);
                        var next = new Run();
                        next.Color = cur.Color; next.Bold = cur.Bold; next.Italic = cur.Italic;
                        next.Underline = cur.Underline; next.Strike = cur.Strike; next.Magic = cur.Magic;
                        cur = next;
                        sb.Length = 0;
                    }
                    char c = char.ToLowerInvariant(text[++i]);
                    int ci = ColorCodes.IndexOf(c);
                    if (ci >= 0 || c == 'r')
                    {
                        cur.Color = ci;
                        cur.Bold = cur.Italic = cur.Underline = cur.Strike = cur.Magic = false;
                    }
                    else if (c == 'l') cur.Bold = true;
                    else if (c == 'o') cur.Italic = true;
                    else if (c == 'n') cur.Underline = true;
                    else if (c == 'm') cur.Strike = true;
                    else if (c == 'k') cur.Magic = true;
                    continue;
                }
                sb.Append(ch);
            }
            if (sb.Length > 0)
            {
                cur.Text = sb.ToString();
                list.Add(cur);
            }
            return list;
        }

        /// <summary>"&amp;6Ciao" -> "§6Ciao" (solo dove "&amp;" è seguito da un codice valido).</summary>
        public static string ToSection(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";
            var sb = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '&' && i + 1 < text.Length && IsCode(text[i + 1])) sb.Append('§');
                else sb.Append(text[i]);
            }
            return sb.ToString();
        }

        public static string FromSection(string text)
        {
            return (text ?? "").Replace('§', '&');
        }

        public static string Plain(string text)
        {
            var sb = new StringBuilder();
            foreach (var r in Parse(text, '&')) sb.Append(r.Text);
            return sb.ToString();
        }

        /// <summary>Sostituisce {giocatore}, {online}, {max}.</summary>
        public static string Fill(string text, string player, string online, string max)
        {
            return (text ?? "").Replace("{giocatore}", player ?? "").Replace("{online}", online ?? "?").Replace("{max}", max ?? "?");
        }

        static string JsonString(string s)
        {
            var sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c < 32) sb.Append(' ');
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        /// <summary>Componente di testo per /tellraw (va bene sia come JSON sia come SNBT delle versioni nuove).</summary>
        public static string ToComponent(string ampText)
        {
            var sb = new StringBuilder("[\"\"");
            foreach (var r in Parse(ampText, '&'))
            {
                sb.Append(",{\"text\":").Append(JsonString(r.Text));
                if (r.Color >= 0) sb.Append(",\"color\":\"").Append(ColorNames[r.Color]).Append('"');
                if (r.Bold) sb.Append(",\"bold\":true");
                if (r.Italic) sb.Append(",\"italic\":true");
                if (r.Underline) sb.Append(",\"underlined\":true");
                if (r.Strike) sb.Append(",\"strikethrough\":true");
                if (r.Magic) sb.Append(",\"obfuscated\":true");
                sb.Append('}');
            }
            return sb.Append(']').ToString();
        }

        // ------------------------------------------------------------ server.properties

        /// <summary>Valore per server.properties: "\" e a capo protetti, caratteri non ASCII (anche §) come \uXXXX.</summary>
        public static string PropEscape(string v)
        {
            var sb = new StringBuilder();
            foreach (char c in v ?? "")
            {
                if (c == '\\') sb.Append("\\\\");
                else if (c == '\n') sb.Append("\\n");
                else if (c == '\r') continue;
                else if (c < 32 || c > 126) sb.Append("\\u").Append(((int)c).ToString("X4"));
                else sb.Append(c);
            }
            return sb.ToString();
        }

        public static string PropUnescape(string v)
        {
            var sb = new StringBuilder();
            v = v ?? "";
            for (int i = 0; i < v.Length; i++)
            {
                char c = v[i];
                if (c != '\\' || i + 1 >= v.Length) { sb.Append(c); continue; }
                char n = v[++i];
                if (n == 'n') sb.Append('\n');
                else if (n == 't') sb.Append('\t');
                else if (n == 'r') { }
                else if (n == 'u' && i + 4 < v.Length)
                {
                    int code;
                    if (int.TryParse(v.Substring(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                    {
                        sb.Append((char)code);
                        i += 4;
                    }
                    else sb.Append(n);
                }
                else sb.Append(n);
            }
            return sb.ToString();
        }

        /// <summary>Imposta una voce di server.properties lasciando com'è tutto il resto.</summary>
        public static void SetProp(string serverDir, string key, string rawValue)
        {
            var list = Props.Read(serverDir);
            bool found = false;
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Key != key) continue;
                list[i] = new KeyValuePair<string, string>(key, rawValue);
                found = true;
            }
            if (!found) list.Add(new KeyValuePair<string, string>(key, rawValue));
            Props.Write(serverDir, list);
        }

        /// <summary>Descrizione nella lista server: due righe scritte con "&amp;".</summary>
        public static void SetMotd(string serverDir, string line1, string line2)
        {
            string m = ToSection((line1 ?? "").Trim());
            string l2 = ToSection((line2 ?? "").Trim());
            if (l2.Length > 0) m += "\n" + l2;
            SetProp(serverDir, "motd", PropEscape(m));
        }

        public static void GetMotd(string serverDir, out string line1, out string line2)
        {
            string m = PropUnescape(Props.Get(serverDir, "motd") ?? "");
            int i = m.IndexOf('\n');
            line1 = FromSection(i >= 0 ? m.Substring(0, i) : m);
            line2 = FromSection(i >= 0 ? m.Substring(i + 1).Replace("\n", " ") : "");
        }

        // ------------------------------------------------------------ logo

        public static string IconPath(string serverDir)
        {
            return Path.Combine(serverDir, "server-icon.png");
        }

        /// <summary>Ritaglia al centro (quadrato) e rimpicciolisce a 64x64, il formato che chiede Minecraft.</summary>
        public static Bitmap MakeIcon(Image src)
        {
            int side = Math.Min(src.Width, src.Height);
            var crop = new Rectangle((src.Width - side) / 2, (src.Height - side) / 2, side, side);
            var bmp = new Bitmap(64, 64, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                bool tiny = side <= 32;   // pixel art: niente sfumature
                g.InterpolationMode = tiny ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.Clear(Color.Transparent);
                using (var ia = new ImageAttributes())
                {
                    ia.SetWrapMode(WrapMode.TileFlipXY);
                    g.DrawImage(src, new Rectangle(0, 0, 64, 64), crop.X, crop.Y, crop.Width, crop.Height, GraphicsUnit.Pixel, ia);
                }
            }
            return bmp;
        }

        /// <summary>Carica un'immagine senza tenere bloccato il file.</summary>
        public static Bitmap LoadImage(string path)
        {
            using (var ms = new MemoryStream(File.ReadAllBytes(path)))
            using (var img = Image.FromStream(ms))
                return new Bitmap(img);
        }

        // ------------------------------------------------------------ disegno (anteprime)

        /// <summary>Disegna una riga colorata come in Minecraft (con l'ombra). Restituisce la larghezza.</summary>
        public static int Draw(Graphics g, string text, Font baseFont, int x, int y, Color defColor, Dictionary<FontStyle, Font> cache)
        {
            int x0 = x;
            const TextFormatFlags fl = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;
            foreach (var r in Parse(text, '&'))
            {
                if (string.IsNullOrEmpty(r.Text)) continue;
                var st = FontStyle.Regular;
                if (r.Bold) st |= FontStyle.Bold;
                if (r.Italic) st |= FontStyle.Italic;
                if (r.Underline) st |= FontStyle.Underline;
                if (r.Strike) st |= FontStyle.Strikeout;
                Font f;
                if (!cache.TryGetValue(st, out f)) { f = new Font(baseFont, st); cache[st] = f; }
                string t = r.Magic ? new string('▒', r.Text.Length) : r.Text;
                Color c = r.Color >= 0 ? Colors[r.Color] : defColor;
                Color sh = Color.FromArgb(c.R / 4, c.G / 4, c.B / 4);
                TextRenderer.DrawText(g, t, f, new Point(x + 2, y + 2), sh, fl);
                TextRenderer.DrawText(g, t, f, new Point(x, y), c, fl);
                x += TextRenderer.MeasureText(g, t, f, new Size(int.MaxValue, int.MaxValue), fl).Width;
            }
            return x - x0;
        }
    }

    /// <summary>Anteprima della voce nella lista Multigiocatore.</summary>
    public class ServerListPreview : Control
    {
        public string Line1 = "", Line2 = "", Players = "0/20";
        public Image Icon;
        readonly Font mono = new Font("Consolas", 11F, FontStyle.Bold);
        readonly Font small = new Font("Segoe UI", 8.5F);
        readonly Dictionary<FontStyle, Font> cache = new Dictionary<FontStyle, Font>();

        public ServerListPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 128;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            // sfondo a "terra" come il menu del gioco
            using (var b1 = new SolidBrush(Color.FromArgb(48, 36, 25)))
            using (var b2 = new SolidBrush(Color.FromArgb(56, 42, 29)))
            {
                g.FillRectangle(b1, ClientRectangle);
                for (int yy = 0; yy < Height; yy += 16)
                    for (int xx = ((yy / 16) % 2) * 16; xx < Width; xx += 32)
                        g.FillRectangle(b2, xx, yy, 16, 16);
            }
            TextRenderer.DrawText(g, "Anteprima: si aggiorna mentre scrivi", small, new Point(10, 6), Color.FromArgb(200, 200, 200));

            int w = Math.Min(Width - 20, 640);
            var box = new Rectangle((Width - w) / 2, 28, w, 80);
            using (var bb = new SolidBrush(Color.FromArgb(20, 20, 20))) g.FillRectangle(bb, box);
            using (var pen = new Pen(Color.FromArgb(160, 160, 160), 2)) g.DrawRectangle(pen, box);

            var ir = new Rectangle(box.X + 8, box.Y + 8, 64, 64);
            if (Icon != null)
            {
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.DrawImage(Icon, ir);
            }
            else
            {
                using (var nb = new SolidBrush(Color.FromArgb(70, 70, 70))) g.FillRectangle(nb, ir);
                TextRenderer.DrawText(g, "nessun\nlogo", small, ir, Color.Silver, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            int tx = ir.Right + 12;
            McText.Draw(g, "&fServer di casa", mono, tx, box.Y + 6, Color.White, cache);
            var ps = TextRenderer.MeasureText(g, Players, mono);
            int px = box.Right - ps.Width - 26;
            McText.Draw(g, "&7" + Players, mono, px, box.Y + 6, Color.Gray, cache);
            using (var gb = new SolidBrush(Color.FromArgb(85, 255, 85)))
                for (int i = 0; i < 5; i++) g.FillRectangle(gb, box.Right - 20 + i * 3, box.Y + 18 - i * 2 - 2, 2, i * 2 + 4);

            var clip = g.Clip;
            g.SetClip(new Rectangle(tx, box.Y, box.Right - tx - 6, box.Height));
            McText.Draw(g, Line1, mono, tx, box.Y + 30, Color.FromArgb(170, 170, 170), cache);
            McText.Draw(g, Line2, mono, tx, box.Y + 52, Color.FromArgb(170, 170, 170), cache);
            g.Clip = clip;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var f in cache.Values) f.Dispose();
                mono.Dispose();
                small.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>Anteprima dei messaggi in chat.</summary>
    public class ChatPreview : Control
    {
        public string[] Lines = new string[0];
        readonly Font mono = new Font("Consolas", 11F, FontStyle.Bold);
        readonly Dictionary<FontStyle, Font> cache = new Dictionary<FontStyle, Font>();

        public ChatPreview()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            using (var b = new SolidBrush(Color.FromArgb(70, 72, 70))) g.FillRectangle(b, ClientRectangle);
            int y = 5;
            foreach (var l in Lines)
            {
                if (string.IsNullOrEmpty(l)) continue;
                McText.Draw(g, l, mono, 8, y, Color.White, cache);
                y += 21;
            }
            if (y == 5) TextRenderer.DrawText(g, "(nessun messaggio)", mono, new Point(8, 5), Color.Silver);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var f in cache.Values) f.Dispose();
                mono.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
