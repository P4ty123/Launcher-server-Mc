using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MineServer
{
    /// <summary>Una mod trovata nella cartella mods.</summary>
    public class ModInfo
    {
        public string File;        // nome del file (.jar)
        public string Id = "";     // id della mod (dai metadati)
        public string Name = "";
        public string Loader = ""; // fabric, quilt, forge, neoforge
        public bool ClientOnly;
        public bool ServerOk;      // qualcuno (metadati o Modrinth) dice esplicitamente che serve/va bene sul server
        public string Reason = "";
        public string Sha1 = "";

        public string Label
        {
            get { return (string.IsNullOrEmpty(Name) ? File : Name + "  (" + File + ")") + (Reason.Length > 0 ? "  —  " + Reason : ""); }
        }
    }

    /// <summary>
    /// Trova le mod "solo client" in una cartella mods (anche mod messe a mano), in tre modi:
    /// 1) metadati della mod (Fabric/Quilt: environment = client; Forge/NeoForge: clientSideOnly = true),
    /// 2) Modrinth, cercando il file per impronta SHA-1 (vale anche per molte mod scaricate da CurseForge),
    /// 3) elenco di mod note solo client.
    /// Le mod non vengono mai cancellate: si spostano in "mods-disattivate" e si possono rimettere.
    /// </summary>
    public static class ModCheck
    {
        public const string DisabledFolder = "mods-disattivate";

        /// <summary>Mod che girano solo sul gioco (grafica, interfaccia, suoni): sul server sono inutili o lo fanno crashare.</summary>
        static readonly HashSet<string> KnownClient = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "oculus", "iris", "sodium", "embeddium", "rubidium", "magnesium", "optifine", "optifabric",
            "sodium_extra", "sodiumextra", "rubidium_extra", "embeddiumplus", "reeses_sodium_options", "reeses-sodium-options",
            "textrues_embeddium_options", "indium", "entityculling", "notenoughanimations", "betterf3", "modmenu",
            "immediatelyfast", "moreculling", "continuity", "lambdynlights", "dynamiclightsreforged", "dynamic_lights_reforged",
            "ryoamiclights", "zoomify", "ok_zoomer", "okzoomer", "citresewn", "enhancedblockentities", "dynamic_fps", "dynamicfps",
            "fpsreducer", "chat_heads", "chatheads", "legendarytooltips", "presencefootsteps", "sound_physics_remastered",
            "soundphysics", "fancymenu", "drippyloadingscreen", "loadingbackgrounds", "blur", "toastcontrol", "cullleaves",
            "betterfoliage", "mousetweaks", "controlling", "itemborders", "visuality", "fallingleaves", "freecam", "torohealth",
            "neat", "catalogue", "ding", "smoothscroll", "nofog", "clearskies", "betteradvancements", "betterbiomeblend",
            "entity_model_features", "entity_texture_features", "skinlayers3d", "3dskinlayers", "waveycapes", "capes",
            "screenshot_viewer", "betterpingdisplay", "farsight", "oculusparticlefix"
        };

        public static string DisabledDir(string serverDir)
        {
            return Path.Combine(serverDir, DisabledFolder);
        }

        // ------------------------------------------------------------ lettura dei metadati

        static string ReadEntry(ZipArchive z, string name)
        {
            var e = z.GetEntry(name);
            if (e == null) return null;
            using (var sr = new StreamReader(e.Open(), Encoding.UTF8)) return sr.ReadToEnd();
        }

        static readonly Regex TomlStr = new Regex(@"^\s*(\w+)\s*=\s*""([^""]*)""", RegexOptions.Multiline);

        public static ModInfo Read(string jar)
        {
            return Read(jar, true);
        }

        /// <summary>hash = false: niente impronta SHA-1 (più veloce, basta per leggere id e nome).</summary>
        public static ModInfo Read(string jar, bool hash)
        {
            var m = new ModInfo();
            m.File = Path.GetFileName(jar);
            if (hash) try { m.Sha1 = Sha1(jar); } catch { }
            try
            {
                using (var z = ZipFile.OpenRead(jar))
                {
                    string fab = ReadEntry(z, "fabric.mod.json");
                    if (fab != null)
                    {
                        var o = Net.ParseJson(fab);
                        m.Loader = "fabric";
                        m.Id = J.S(J.P(o, "id")) ?? "";
                        m.Name = J.S(J.P(o, "name")) ?? "";
                        string env = J.S(J.P(o, "environment")) ?? "*";
                        if (env == "client") { m.ClientOnly = true; m.Reason = "la mod dice di essere solo per il gioco (client)"; }
                        else m.ServerOk = true;
                        return m;
                    }
                    string quilt = ReadEntry(z, "quilt.mod.json");
                    if (quilt != null)
                    {
                        var o = Net.ParseJson(quilt);
                        m.Loader = "quilt";
                        m.Id = J.S(J.P(o, "quilt_loader", "id")) ?? "";
                        m.Name = J.S(J.P(o, "quilt_loader", "metadata", "name")) ?? "";
                        string env = J.S(J.P(o, "minecraft", "environment")) ?? "*";
                        if (env == "client") { m.ClientOnly = true; m.Reason = "la mod dice di essere solo per il gioco (client)"; }
                        else m.ServerOk = true;
                        return m;
                    }
                    string toml = ReadEntry(z, "META-INF/neoforge.mods.toml");
                    m.Loader = toml != null ? "neoforge" : "forge";
                    if (toml == null) toml = ReadEntry(z, "META-INF/mods.toml");
                    if (toml != null)
                    {
                        // primo [[mods]]: modId e displayName
                        int mi = toml.IndexOf("[[mods]]", StringComparison.Ordinal);
                        string head = mi >= 0 ? toml.Substring(0, mi) : toml;
                        string modsPart = mi >= 0 ? toml.Substring(mi) : "";
                        int next = modsPart.IndexOf("[[", 8, StringComparison.Ordinal);
                        if (next > 0) modsPart = modsPart.Substring(0, next);
                        foreach (Match x in TomlStr.Matches(modsPart))
                        {
                            if (x.Groups[1].Value == "modId") m.Id = x.Groups[2].Value;
                            if (x.Groups[1].Value == "displayName") m.Name = x.Groups[2].Value;
                        }
                        if (Regex.IsMatch(head, @"^\s*clientSideOnly\s*=\s*true", RegexOptions.Multiline))
                        {
                            m.ClientOnly = true;
                            m.Reason = "la mod dice di essere solo per il gioco (clientSideOnly)";
                        }
                    }
                }
            }
            catch { }
            if (string.IsNullOrEmpty(m.Name) && m.Id.Length > 0) m.Name = m.Id;
            return m;
        }

        public static string Sha1(string file)
        {
            using (var s = System.IO.File.OpenRead(file))
            using (var h = SHA1.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        // ------------------------------------------------------------ controllo completo

        /// <summary>Legge tutte le mod di una cartella e segna quelle solo client. online = chiedi anche a Modrinth.</summary>
        public static List<ModInfo> Scan(string modsDir, bool online, Action<string> log)
        {
            var list = new List<ModInfo>();
            if (!Directory.Exists(modsDir)) return list;
            foreach (var f in Directory.GetFiles(modsDir, "*.jar")) list.Add(Read(f));
            if (log != null) log("Lette " + list.Count + " mod.");

            if (online)
            {
                try { AskModrinth(list, log); }
                catch (Exception ex) { if (log != null) log("Modrinth non raggiungibile (" + ex.Message + "): uso solo metadati ed elenco."); }
            }

            foreach (var m in list)
            {
                if (m.ClientOnly || m.ServerOk) continue;
                string id = m.Id ?? "";
                string fn = Path.GetFileNameWithoutExtension(m.File).ToLowerInvariant();
                bool known = id.Length > 0 ? KnownClient.Contains(id)
                                           : KnownClient.Any(k => fn.StartsWith(k + "-") || fn.StartsWith(k + "_") || fn == k);
                if (known) { m.ClientOnly = true; m.Reason = "mod nota solo per il gioco (client)"; }
            }
            return list;
        }

        /// <summary>Modrinth: dall'impronta del file al progetto, poi "server_side". unsupported = solo client.</summary>
        static void AskModrinth(List<ModInfo> list, Action<string> log)
        {
            var todo = list.Where(m => !m.ClientOnly && m.Sha1.Length == 40).ToList();
            if (todo.Count == 0) return;
            var projectOf = new Dictionary<string, string>();   // sha1 -> project id
            for (int i = 0; i < todo.Count; i += 100)
            {
                var batch = todo.Skip(i).Take(100).Select(m => m.Sha1).ToList();
                string body = "{\"hashes\":[" + string.Join(",", batch.Select(h => "\"" + h + "\"")) + "],\"algorithm\":\"sha1\"}";
                var res = J.O(Net.ParseJson(Net.PostString("https://api.modrinth.com/v2/version_files", body, null)));
                if (res == null) continue;
                foreach (var kv in res)
                {
                    string pid = J.S(J.P(kv.Value, "project_id"));
                    if (pid != null) projectOf[kv.Key.ToLowerInvariant()] = pid;
                }
            }
            var ids = projectOf.Values.Distinct().ToList();
            var side = new Dictionary<string, string>();
            for (int i = 0; i < ids.Count; i += 80)
            {
                var batch = ids.Skip(i).Take(80).ToList();
                string q = "[" + string.Join(",", batch.Select(x => "\"" + x + "\"")) + "]";
                foreach (var p in J.A(Net.GetJson("https://api.modrinth.com/v2/projects?ids=" + Uri.EscapeDataString(q))) ?? new object[0])
                {
                    string id = J.S(J.P(p, "id"));
                    if (id != null) side[id] = J.S(J.P(p, "server_side")) ?? "";
                }
            }
            int found = 0;
            foreach (var m in todo)
            {
                string pid, ss;
                if (!projectOf.TryGetValue(m.Sha1, out pid) || !side.TryGetValue(pid, out ss)) continue;
                found++;
                if (ss == "unsupported") { m.ClientOnly = true; m.Reason = "Modrinth: non serve sul server"; }
                else if (ss == "required" || ss == "optional") m.ServerOk = true;
            }
            if (log != null) log("Modrinth ha riconosciuto " + found + " mod su " + todo.Count + ".");
        }

        // ------------------------------------------------------------ disattiva / rimetti

        /// <summary>Sposta le mod in mods-disattivate. Restituisce quante ne ha spostate.</summary>
        public static int Disable(string serverDir, IEnumerable<string> files)
        {
            string from = Path.Combine(serverDir, "mods"), to = DisabledDir(serverDir);
            Directory.CreateDirectory(to);
            int n = 0;
            foreach (var f in files)
            {
                string src = Path.Combine(from, f), dst = Path.Combine(to, f);
                if (!System.IO.File.Exists(src)) continue;
                if (System.IO.File.Exists(dst)) System.IO.File.Delete(dst);
                System.IO.File.Move(src, dst);
                n++;
            }
            return n;
        }

        public static int Enable(string serverDir, IEnumerable<string> files)
        {
            string from = DisabledDir(serverDir), to = Path.Combine(serverDir, "mods");
            Directory.CreateDirectory(to);
            int n = 0;
            foreach (var f in files)
            {
                string src = Path.Combine(from, f), dst = Path.Combine(to, f);
                if (!System.IO.File.Exists(src)) continue;
                if (System.IO.File.Exists(dst)) System.IO.File.Delete(dst);
                System.IO.File.Move(src, dst);
                n++;
            }
            return n;
        }

        public static string[] DisabledFiles(string serverDir)
        {
            string d = DisabledDir(serverDir);
            return Directory.Exists(d) ? Directory.GetFiles(d, "*.jar").Select(Path.GetFileName).OrderBy(x => x).ToArray() : new string[0];
        }

        // ------------------------------------------------------------ crash all'avvio

        static readonly Regex ClientCrash = new Regex(@"invalid dist DEDICATED_SERVER|net[/.]minecraft[/.]client[/.]|Environment type CLIENT|client-only|NoClassDefFoundError: net/minecraft/client",
                                                      RegexOptions.IgnoreCase);
        static readonly Regex JarInLine = new Regex(@"mods[\\/]([^\\/\s""'\]\)]+?\.jar)", RegexOptions.IgnoreCase);
        static readonly Regex ModIdInLine = new Regex(@"(?:TRANSFORMER/|ModID:\s*|mod ['""]?|Mod ID:\s*)([a-z0-9_\-]{2,64})(?:@|['""\s,]|$)", RegexOptions.IgnoreCase);

        /// <summary>
        /// Dopo un crash all'avvio: se il log parla di classi "client" sul server, restituisce le mod sospette
        /// (nomi dei file). Lista vuota = il crash non sembra causato da una mod solo client.
        /// </summary>
        public static List<string> SuspectsFromCrash(IList<string> lines, string serverDir)
        {
            var res = new List<string>();
            if (lines == null || !lines.Any(l => ClientCrash.IsMatch(l))) return res;
            string modsDir = Path.Combine(serverDir, "mods");
            if (!Directory.Exists(modsDir)) return res;
            var files = Directory.GetFiles(modsDir, "*.jar").Select(Path.GetFileName).ToList();
            var byId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var info = Read(Path.Combine(modsDir, f), false);
                if (info.Id.Length > 0 && !byId.ContainsKey(info.Id)) byId[info.Id] = f;
            }
            // guardo le righe vicine all'errore
            for (int i = 0; i < lines.Count; i++)
            {
                if (!ClientCrash.IsMatch(lines[i])) continue;
                for (int j = Math.Max(0, i - 15); j < Math.Min(lines.Count, i + 40); j++)
                {
                    foreach (Match m in JarInLine.Matches(lines[j]))
                    {
                        string f = files.FirstOrDefault(x => x.Equals(m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                        if (f != null && !res.Contains(f)) res.Add(f);
                    }
                    foreach (Match m in ModIdInLine.Matches(lines[j]))
                    {
                        string id = m.Groups[1].Value;
                        if (id == "minecraft" || id == "forge" || id == "neoforge" || id == "fml" || id == "mixin") continue;
                        string f;
                        if (byId.TryGetValue(id, out f) && !res.Contains(f)) res.Add(f);
                    }
                }
            }
            return res;
        }
    }
}
