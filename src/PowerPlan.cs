using System;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace MineServer
{
    /// <summary>
    /// Piani energetici di Windows: "Prestazioni eccellenti" quando il server è acceso,
    /// "Risparmio di energia" quando è spento o in pausa. I piani mancanti vengono creati da soli.
    /// </summary>
    public static class PowerPlan
    {
        public const string Ultimate = "e9a42b02-d5df-448d-aa00-03f14749eb61";   // modello "Prestazioni eccellenti"
        public const string High = "8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c";
        public const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
        public const string Saver = "a1841308-3541-4f9b-a8e8-6e0a9b4a6dbe";

        static readonly Regex GuidRx = new Regex(@"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        static readonly object Gate = new object();

        static string Run(string args, out int code)
        {
            code = -1;
            try
            {
                var psi = new ProcessStartInfo("powercfg.exe", args);
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                psi.RedirectStandardOutput = true;
                psi.RedirectStandardError = true;
                using (var p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                    p.WaitForExit(15000);
                    code = p.ExitCode;
                    return o;
                }
            }
            catch (Exception ex) { return ex.Message; }
        }

        static string Run(string args)
        {
            int c;
            return Run(args, out c);
        }

        public static string Active()
        {
            var m = GuidRx.Match(Run("/getactivescheme"));
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }

        /// <summary>Righe di "powercfg /list": una per piano, con GUID e nome.</summary>
        static string[] ListLines()
        {
            return Run("/list").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        }

        public static bool Exists(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            foreach (var l in ListLines())
                if (l.IndexOf(guid, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Cerca un piano per nome (italiano o inglese), utile se l'utente lo aveva già creato.</summary>
        static string FindByName(params string[] names)
        {
            foreach (var l in ListLines())
            {
                var m = GuidRx.Match(l);
                if (!m.Success) continue;
                foreach (var n in names)
                    if (l.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0) return m.Value.ToLowerInvariant();
            }
            return null;
        }

        /// <summary>Crea una copia del piano modello e ne restituisce il GUID (null se Windows rifiuta).</summary>
        static string Duplicate(string template)
        {
            int code;
            string o = Run("/duplicatescheme " + template, out code);
            if (code != 0) return null;
            foreach (Match m in GuidRx.Matches(o))
                if (!m.Value.Equals(template, StringComparison.OrdinalIgnoreCase)) return m.Value.ToLowerInvariant();
            return null;
        }

        /// <summary>Piano per il server acceso: Prestazioni eccellenti (creato se manca), altrimenti Prestazioni elevate.</summary>
        public static string EnsurePerformance(GlobalConfig cfg, Action<string> log)
        {
            lock (Gate)
            {
                if (Exists(cfg.PowerPerfGuid)) return cfg.PowerPerfGuid;
                string g = Exists(Ultimate) ? Ultimate : FindByName("Prestazioni eccellenti", "Ultimate Performance");
                if (g == null)
                {
                    g = Duplicate(Ultimate);
                    if (g != null) log("Creato il piano energetico 'Prestazioni eccellenti'.");
                }
                if (g == null)
                {
                    g = Exists(High) ? High : FindByName("Prestazioni elevate", "High performance");
                    if (g == null) g = Duplicate(High);
                    if (g != null) log("Windows non permette 'Prestazioni eccellenti': uso 'Prestazioni elevate'.");
                }
                if (g == null) return null;
                NoSleep(g);
                cfg.PowerPerfGuid = g;
                cfg.Save();
                return g;
            }
        }

        /// <summary>Piano per il server spento: Risparmio di energia (creato se manca, es. Atlas OS), altrimenti Bilanciato.</summary>
        public static string EnsureSaver(GlobalConfig cfg, Action<string> log)
        {
            lock (Gate)
            {
                if (Exists(cfg.PowerSaverGuid)) return cfg.PowerSaverGuid;
                string g = Exists(Saver) ? Saver : FindByName("Risparmio di energia", "Power saver");
                if (g == null)
                {
                    g = Duplicate(Saver);
                    if (g != null) log("Creato il piano energetico 'Risparmio di energia'.");
                }
                if (g == null)
                {
                    g = Exists(Balanced) ? Balanced : FindByName("Bilanciato", "Balanced");
                    if (g != null) log("Windows non permette 'Risparmio di energia': uso 'Bilanciato'.");
                }
                if (g == null) return null;
                NoSleep(g);
                cfg.PowerSaverGuid = g;
                cfg.Save();
                return g;
            }
        }

        /// <summary>Niente sospensione né ibernazione con il PC alla corrente: il PC deve restare raggiungibile.</summary>
        static void NoSleep(string guid)
        {
            Run("/setacvalueindex " + guid + " SUB_SLEEP STANDBYIDLE 0");
            Run("/setacvalueindex " + guid + " SUB_SLEEP HIBERNATEIDLE 0");
            Run("/setacvalueindex " + guid + " SUB_DISK DISKIDLE 0");
        }

        public static bool SetActive(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return false;
            lock (Gate)
            {
                int code;
                Run("/setactive " + guid, out code);
                return code == 0;
            }
        }

        /// <summary>Nome leggibile del piano (dall'elenco di Windows).</summary>
        public static string NameOf(string guid)
        {
            if (string.IsNullOrEmpty(guid)) return "?";
            foreach (var l in ListLines())
            {
                if (l.IndexOf(guid, StringComparison.OrdinalIgnoreCase) < 0) continue;
                int a = l.IndexOf('('), b = l.LastIndexOf(')');
                if (a >= 0 && b > a) return l.Substring(a + 1, b - a - 1);
            }
            return guid;
        }
    }
}
