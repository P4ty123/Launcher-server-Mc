using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MineServer
{
    /// <summary>
    /// "Ottimizza automaticamente": profili Leggero / Bilanciato / Qualità / Modpack pesante.
    /// Cambia server.properties e, su Paper, paper-world-defaults.yml, spigot.yml, bukkit.yml.
    /// La prima volta salva una copia dei file originali: "Ripristina" li rimette.
    /// </summary>
    public static class Optimizer
    {
        public static readonly string[] Keys = { "leggero", "bilanciato", "qualita", "modpack" };
        public static readonly string[] Labels =
        {
            "Leggero  —  PC poco potenti (es. Intel N95)",
            "Bilanciato  —  buon compromesso",
            "Qualità  —  PC potenti: mondo più lontano e più vivo",
            "Modpack pesante  —  tante mod (100+)"
        };

        const string BackupFolder = "mineserver-originali";
        static readonly string[] Files = { "server.properties", "spigot.yml", "bukkit.yml", Path.Combine("config", "paper-world-defaults.yml") };

        public static int CountMods(AppConfig c)
        {
            string d = Path.Combine(c.ServerDir, "mods");
            return Directory.Exists(d) ? Directory.GetFiles(d, "*.jar").Length : 0;
        }

        /// <summary>Profilo consigliato in base al PC (thread del processore) e al numero di mod.</summary>
        public static string Recommend(AppConfig c, int logicalCpus)
        {
            if (CountMods(c) >= 80) return "modpack";
            if (logicalCpus >= 8) return "qualita";
            if (logicalCpus >= 6) return "bilanciato";
            return "leggero";
        }

        /// <summary>RAM consigliata per il server (GB) in base alle mod; non viene cambiata da sola.</summary>
        public static string RamAdvice(AppConfig c)
        {
            int m = CountMods(c);
            if (m >= 150) return "8-10 GB (" + m + " mod)";
            if (m >= 50) return "6-8 GB (" + m + " mod)";
            if (m > 0) return "4-6 GB (" + m + " mod)";
            return c.ServerType == "Paper" ? "3-6 GB" : "3-4 GB";
        }

        public static bool HasBackup(AppConfig c)
        {
            return Directory.Exists(Path.Combine(c.ServerDir, BackupFolder));
        }

        static void BackupOnce(AppConfig c)
        {
            string b = Path.Combine(c.ServerDir, BackupFolder);
            if (Directory.Exists(b)) return;           // tengo sempre la copia del primo stato (quello "originale")
            Directory.CreateDirectory(b);
            foreach (var f in Files)
            {
                string src = Path.Combine(c.ServerDir, f);
                if (!File.Exists(src)) continue;
                string dst = Path.Combine(b, f);
                Directory.CreateDirectory(Path.GetDirectoryName(dst));
                File.Copy(src, dst, true);
            }
        }

        public static int Restore(AppConfig c)
        {
            string b = Path.Combine(c.ServerDir, BackupFolder);
            if (!Directory.Exists(b)) return 0;
            int n = 0;
            foreach (var f in Files)
            {
                string src = Path.Combine(b, f);
                if (!File.Exists(src)) continue;
                File.Copy(src, Path.Combine(c.ServerDir, f), true);
                n++;
            }
            Directory.Delete(b, true);
            return n;
        }

        // ------------------------------------------------------------ profili

        static Dictionary<string, string> PropsFor(string p)
        {
            var d = new Dictionary<string, string>();
            d["sync-chunk-writes"] = "false";
            d["network-compression-threshold"] = "256";
            d["allow-flight"] = "true";
            d["max-tick-time"] = "-1";
            if (p == "leggero") { d["view-distance"] = "7"; d["simulation-distance"] = "5"; d["entity-broadcast-range-percentage"] = "75"; }
            if (p == "bilanciato") { d["view-distance"] = "8"; d["simulation-distance"] = "6"; d["entity-broadcast-range-percentage"] = "85"; }
            if (p == "qualita") { d["view-distance"] = "12"; d["simulation-distance"] = "8"; d["entity-broadcast-range-percentage"] = "100"; }
            if (p == "modpack") { d["view-distance"] = "8"; d["simulation-distance"] = "5"; d["entity-broadcast-range-percentage"] = "80"; }
            return d;
        }

        /// <summary>(file, percorso yaml, valore) per Paper.</summary>
        static List<string[]> PaperFor(string p)
        {
            var l = new List<string[]>();
            string pw = Path.Combine("config", "paper-world-defaults.yml");
            l.Add(new[] { pw, "chunks.prevent-moving-into-unloaded-chunks", "true" });
            l.Add(new[] { pw, "hopper.disable-move-event", "true" });
            l.Add(new[] { pw, "environment.treasure-maps.find-already-discovered.villager-trade", "true" });
            l.Add(new[] { pw, "environment.treasure-maps.find-already-discovered.loot-tables", "true" });
            bool light = p == "leggero", mid = p == "bilanciato";
            if (light || mid)
            {
                l.Add(new[] { pw, "misc.redstone-implementation", "ALTERNATE_CURRENT" });
                l.Add(new[] { pw, "collisions.max-entity-collisions", "2" });
                l.Add(new[] { "spigot.yml", "world-settings.default.merge-radius.item", "3.5" });
                l.Add(new[] { "spigot.yml", "world-settings.default.merge-radius.exp", "4.0" });
                l.Add(new[] { "spigot.yml", "world-settings.default.entity-activation-range.animals", light ? "16" : "24" });
                l.Add(new[] { "spigot.yml", "world-settings.default.entity-activation-range.monsters", light ? "24" : "32" });
                l.Add(new[] { "spigot.yml", "world-settings.default.entity-activation-range.misc", light ? "8" : "12" });
                l.Add(new[] { "bukkit.yml", "spawn-limits.monsters", light ? "50" : "60" });
            }
            if (light)
            {
                l.Add(new[] { pw, "tick-rates.mob-spawner", "2" });
                l.Add(new[] { "bukkit.yml", "ticks-per.monster-spawns", "2" });
            }
            if (p == "qualita")
                l.Add(new[] { pw, "misc.redstone-implementation", "VANILLA" });
            return l;
        }

        /// <summary>Applica il profilo. Restituisce il riepilogo da mostrare.</summary>
        public static string Apply(AppConfig c, string profile)
        {
            if (!c.Installed) throw new Exception("Prima installa il server.");
            BackupOnce(c);
            var sb = new StringBuilder();
            int idx = Array.IndexOf(Keys, profile);
            sb.AppendLine("Profilo: " + (idx >= 0 ? Labels[idx].Split(new[] { "  —" }, StringSplitOptions.None)[0] : profile));

            foreach (var kv in PropsFor(profile)) McText.SetProp(c.ServerDir, kv.Key, kv.Value);
            var pr = PropsFor(profile);
            sb.AppendLine("• server.properties: distanza visiva " + pr["view-distance"] + ", simulazione " + pr["simulation-distance"] +
                          ", salvataggi veloci, il server non si chiude da solo nei momenti di lag");

            if (c.ServerType == "Paper")
            {
                int ok = 0, missingFile = 0;
                foreach (var t in PaperFor(profile))
                {
                    string f = Path.Combine(c.ServerDir, t[0]);
                    if (!File.Exists(f)) { missingFile++; continue; }
                    if (YamlSet(f, t[1], t[2])) ok++;
                }
                if (ok > 0) sb.AppendLine("• Paper: " + ok + " impostazioni (redstone, tramogge, mappe del tesoro, mob lontani, oggetti a terra...)");
                if (missingFile > 0) sb.AppendLine("• Paper: alcuni file nascono al primo avvio del server. Accendilo una volta e premi di nuovo \"Ottimizza\".");
            }
            if (profile == "modpack")
            {
                if (c.IdleMinutes < 30) c.IdleMinutes = 30;
                if (c.WakeMsg == AppConfig.DefWake)
                    c.WakeMsg = "&6Il server si sta accendendo... &eCi sono tante mod: riprova tra 3-4 minuti.";
                c.Save();
                sb.AppendLine("• Pausa automatica: dopo 30 minuti senza nessuno, e messaggio \"riprova tra 3-4 minuti\" (con tante mod l'avvio è lento)");
            }
            sb.AppendLine("• RAM consigliata per questo server: " + RamAdvice(c));
            var missing = MissingPerfMods(c);
            if (missing.Count > 0)
                sb.AppendLine("• Mod di ottimizzazione che potresti aggiungere (scheda Mod/Plugin): " + string.Join(", ", missing));
            sb.AppendLine();
            sb.AppendLine("Una copia dei file originali è salvata: con \"Ripristina\" torni com'eri.");
            return sb.ToString();
        }

        /// <summary>Mod di ottimizzazione note che non sono nella cartella mods (solo suggerimento).</summary>
        public static List<string> MissingPerfMods(AppConfig c)
        {
            var want = new List<string>();
            if (c.ServerType == "Fabric") want.AddRange(new[] { "lithium", "ferritecore", "modernfix" });
            else if (c.ServerType == "Forge" || c.ServerType == "NeoForge") want.AddRange(new[] { "ferritecore", "modernfix" });
            if (want.Count == 0) return want;
            string d = Path.Combine(c.ServerDir, "mods");
            var have = Directory.Exists(d)
                ? Directory.GetFiles(d, "*.jar").Select(f => Path.GetFileName(f).ToLowerInvariant().Replace("-", "").Replace("_", "")).ToList()
                : new List<string>();
            return want.Where(w => !have.Any(h => h.Contains(w))).ToList();
        }

        // ------------------------------------------------------------ YAML (solo valori già presenti)

        /// <summary>
        /// Cambia il valore di una chiave annidata ("a.b.c") in un file YAML semplice, mantenendo tutto il resto.
        /// Se la chiave non c'è non aggiunge niente. True se il file è stato cambiato o era già giusto.
        /// </summary>
        public static bool YamlSet(string file, string dotted, string value)
        {
            var path = dotted.Split('.');
            var lines = File.ReadAllLines(file, Encoding.UTF8).ToList();
            var stack = new List<KeyValuePair<int, string>>();   // (rientro, chiave)
            var rx = new Regex(@"^(\s*)([A-Za-z0-9_\-]+):(\s*)(.*)$");
            for (int i = 0; i < lines.Count; i++)
            {
                string l = lines[i];
                if (l.Trim().Length == 0 || l.TrimStart().StartsWith("#") || l.TrimStart().StartsWith("-")) continue;
                var m = rx.Match(l);
                if (!m.Success) continue;
                int ind = m.Groups[1].Value.Length;
                while (stack.Count > 0 && stack[stack.Count - 1].Key >= ind) stack.RemoveAt(stack.Count - 1);
                stack.Add(new KeyValuePair<int, string>(ind, m.Groups[2].Value));
                if (stack.Count != path.Length) continue;
                bool match = true;
                for (int k = 0; k < path.Length; k++) if (stack[k].Value != path[k]) { match = false; break; }
                if (!match) continue;
                string old = m.Groups[4].Value.Trim();
                if (old.Length == 0) return false;          // è una sezione, non un valore
                if (old.Trim('\'', '"') == value) return true;
                lines[i] = m.Groups[1].Value + m.Groups[2].Value + ": " + value;
                File.WriteAllLines(file, lines, new UTF8Encoding(false));
                return true;
            }
            return false;
        }
    }
}
